using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using BlazorStudio.Core.Razor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;

namespace BlazorStudio.Core.Highlighting;

/// <summary>Fundstelle in einer echten Datei (.cs oder .razor), Position im Originaltext.</summary>
public sealed record NavLocation(string Path, int Offset, int Length, bool IsDefinition = false);

/// <summary>Ergebnis einer Navigation: Fundstellen, Symbolname und – bei Symbolen aus DLLs – eine Beschreibung.</summary>
public sealed record NavResult(IReadOnlyList<NavLocation> Locations, string? Symbol, string? Kind = null, string? External = null, string? ContainingType = null);

public sealed record TextEditAt(int Offset, int Length, string NewText);

public sealed record FileEdits(string Path, IReadOnlyList<TextEditAt> Edits);

/// <summary>
/// Navigation wie in Rider/VS: Gehe zu Definition (F12), Implementierungen (Strg+F12), Verwendungen (Shift+F12),
/// Umbenennen (F2). .razor-Dateien sind über ihre @code-Blöcke als generierte Dokumente im Workspace;
/// Positionen werden über die Abschnitts-Zuordnung auf die .razor-Datei zurückgerechnet.
/// </summary>
public sealed partial class SemanticHighlighter
{
    private readonly ConcurrentDictionary<string, (string RazorPath, RazorProjection Projection)> _generated = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pfad des generierten Dokuments zu einer .razor-Datei.</summary>
    public static string GeneratedPath(string razorPath) => razorPath + ".__g.cs";

    /// <summary>Generiertes Dokument für eine .razor-Datei setzen (Projektion + Zuordnung).</summary>
    public async Task UpsertRazorAsync(string razorPath, RazorProjection projection)
    {
        var gen = GeneratedPath(razorPath);
        _generated[gen] = (razorPath, projection);
        await UpsertAsync(gen, projection.Code);
    }

    /// <summary>Alle .razor-Dateien des Projekts einmalig indizieren (damit Verwendungen/Umbenennen sie finden).</summary>
    public async Task IndexRazorAsync(IEnumerable<(string Path, RazorProjection Projection)> files)
    {
        foreach (var (path, proj) in files)
        {
            if (_generated.ContainsKey(GeneratedPath(path))) continue;
            await UpsertRazorAsync(path, proj);
        }
    }

    /// <summary>Klassifiziert eine .razor-Datei über ihre Projektion (Farben der @code-Blöcke).</summary>
    public async Task<IReadOnlyList<(int Start, int Length, int Type, int Mods)>> ClassifyRazorAsync(string razorPath, RazorProjection projection)
    {
        await UpsertRazorAsync(razorPath, projection);
        var doc = Current(GeneratedPath(razorPath));
        if (doc is null || projection.Map is null) return Array.Empty<(int, int, int, int)>();
        var result = new List<(int, int, int, int)>();
        foreach (var (gen, orig, len) in projection.Map)
            foreach (var t in await ClassifyAsync(doc, gen, len, orig - gen)) result.Add(t);
        return result;
    }

    private Document? Current(string path) =>
        _workspace is not null && _docs.TryGetValue(path, out var id) ? _workspace.CurrentSolution.GetDocument(id) : null;

    // ------------------------------------------------------------------

    /// <param name="path">.cs-Pfad bzw. bei .razor <see cref="GeneratedPath"/> (Projektion vorher über <see cref="UpsertRazorAsync"/> gesetzt).</param>
    public async Task<NavResult> DefinitionAsync(string path, string text, int position, CancellationToken ct = default)
    {
        var (symbol, solution) = await SymbolAtAsync(path, text, position, ct);
        if (symbol is null || solution is null) return new NavResult(Array.Empty<NavLocation>(), null);
        var locs = symbol.Locations.Where(l => l.IsInSource).Select(l => Map(l, isDefinition: true)).Where(l => l is not null).Select(l => l!).ToList();
        string? external = null;
        if (locs.Count == 0)
            external = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) + (symbol.ContainingAssembly is { } a ? "  ·  " + a.Identity.Name + " " + a.Identity.Version : "");
        return new NavResult(locs, symbol.Name, KindOf(symbol), external, symbol.ContainingType?.Name);
    }

    public async Task<NavResult> ImplementationsAsync(string path, string text, int position, CancellationToken ct = default)
    {
        var (symbol, solution) = await SymbolAtAsync(path, text, position, ct);
        if (symbol is null || solution is null) return new NavResult(Array.Empty<NavLocation>(), null);
        var found = new List<ISymbol>();
        if (symbol is INamedTypeSymbol { TypeKind: TypeKind.Interface } iface)
            found.AddRange(await SymbolFinder.FindImplementationsAsync(iface, solution, cancellationToken: ct));
        else if (symbol is INamedTypeSymbol cls)
            found.AddRange(await SymbolFinder.FindDerivedClassesAsync(cls, solution, cancellationToken: ct));
        else
        {
            found.AddRange(await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: ct));
            found.AddRange(await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: ct));
        }
        var locs = found.SelectMany(s => s.Locations).Where(l => l.IsInSource).Select(l => Map(l, true)).Where(l => l is not null).Select(l => l!).Distinct().ToList();
        return new NavResult(locs, symbol.Name, KindOf(symbol), null, symbol.ContainingType?.Name);
    }

    public async Task<NavResult> ReferencesAsync(string path, string text, int position, CancellationToken ct = default)
    {
        var (symbol, solution) = await SymbolAtAsync(path, text, position, ct);
        if (symbol is null || solution is null) return new NavResult(Array.Empty<NavLocation>(), null);
        var refs = await SymbolFinder.FindReferencesAsync(symbol, solution, ct);
        var list = new List<NavLocation>();
        foreach (var r in refs)
        {
            // Accessoren o. Ä. melden die Definition ein zweites Mal – nur die des gesuchten Symbols
            if (SymbolEqualityComparer.Default.Equals(r.Definition.OriginalDefinition, symbol))
                foreach (var d in r.Definition.Locations.Where(l => l.IsInSource)) if (Map(d, true) is { } m) list.Add(m);
            foreach (var l in r.Locations) if (Map(l.Location, false) is { } m) list.Add(m);
        }
        return new NavResult(list.Distinct().OrderBy(l => l.Path, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Offset).ToList(), symbol.Name, KindOf(symbol), null, symbol.ContainingType?.Name);
    }

    /// <summary>Umbenennen über alle Dateien. Liefert Änderungen im Originaltext (auch für .razor @code-Blöcke).</summary>
    public async Task<(string? Error, string? OldName, IReadOnlyList<FileEdits> Edits)> RenameAsync(string path, string text, int position, string newName, CancellationToken ct = default)
    {
        if (!Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(newName)) return ("invalid", null, Array.Empty<FileEdits>());
        var (symbol, solution) = await SymbolAtAsync(path, text, position, ct);
        if (symbol is null || solution is null) return ("nosymbol", null, Array.Empty<FileEdits>());
        if (!symbol.Locations.Any(l => l.IsInSource)) return ("metadata", symbol.Name, Array.Empty<FileEdits>());
        var renamed = await Renamer.RenameSymbolAsync(solution, symbol, new SymbolRenameOptions(), newName, ct);
        var edits = new List<FileEdits>();
        foreach (var change in renamed.GetChanges(solution).GetProjectChanges())
        {
            foreach (var id in change.GetChangedDocuments())
            {
                var oldDoc = solution.GetDocument(id);
                var newDoc = renamed.GetDocument(id);
                if (oldDoc?.FilePath is not { } file || newDoc is null) continue;
                var changes = (await newDoc.GetTextChangesAsync(oldDoc, ct)).ToList();
                if (_generated.TryGetValue(file, out var g))
                {
                    var mapped = new List<TextEditAt>();
                    foreach (var c in changes)
                    {
                        var start = g.Projection.ToOriginal(c.Span.Start);
                        var end = g.Projection.ToOriginal(c.Span.End);
                        if (start is null || end is null || end < start) continue;
                        mapped.Add(new TextEditAt(start.Value, end.Value - start.Value, c.NewText ?? ""));
                    }
                    if (mapped.Count > 0) edits.Add(new FileEdits(g.RazorPath, mapped));
                }
                else if (!file.Contains(".__", StringComparison.Ordinal))
                {
                    edits.Add(new FileEdits(file, changes.Select(c => new TextEditAt(c.Span.Start, c.Span.Length, c.NewText ?? "")).ToList()));
                }
            }
        }
        return (null, symbol.Name, edits);
    }

    private async Task<(ISymbol? Symbol, Microsoft.CodeAnalysis.Solution? Solution)> SymbolAtAsync(string path, string text, int position, CancellationToken ct)
    {
        var doc = await UpsertAsync(path, text);
        if (doc is null) return (null, null);
        position = Math.Clamp(position, 0, text.Length);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(doc, position, ct);
        if (symbol is null && position > 0) symbol = await SymbolFinder.FindSymbolAtPositionAsync(doc, position - 1, ct);
        // Konstruktoraufruf → Typ; Methodengruppe → Originaldefinition
        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor && ctor.IsImplicitlyDeclared) symbol = ctor.ContainingType;
        if (symbol is IMethodSymbol { ReducedFrom: { } reduced }) symbol = reduced;
        return (symbol?.OriginalDefinition, doc.Project.Solution);
    }

    private NavLocation? Map(Location loc, bool isDefinition)
    {
        var file = loc.SourceTree?.FilePath;
        if (string.IsNullOrEmpty(file)) return null;
        var span = loc.SourceSpan;
        if (_generated.TryGetValue(file, out var g))
        {
            var start = g.Projection.ToOriginal(span.Start);
            return start is null ? null : new NavLocation(g.RazorPath, start.Value, span.Length, isDefinition);
        }
        if (file.Contains(".__", StringComparison.Ordinal) || file.EndsWith(".GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase)) return null;
        return new NavLocation(file, span.Start, span.Length, isDefinition);
    }

    private static string KindOf(ISymbol s) => s switch
    {
        INamedTypeSymbol t => t.TypeKind.ToString().ToLowerInvariant(),
        IMethodSymbol => "method",
        IPropertySymbol => "property",
        IFieldSymbol => "field",
        IEventSymbol => "event",
        ILocalSymbol => "local",
        IParameterSymbol => "parameter",
        INamespaceSymbol => "namespace",
        _ => s.Kind.ToString().ToLowerInvariant(),
    };
}

/// <summary>Verwendungen eines Namens im Razor-Markup (außerhalb von @code): @name, @(…), @onclick="Name", Value="@x" …</summary>
public static partial class RazorMarkupUsages
{
    public static IReadOnlyList<int> Find(string text, string name)
    {
        var result = new List<int>();
        if (string.IsNullOrEmpty(name)) return result;
        var code = RazorCSharpProjection.CodeRegions(text);
        var word = new Regex(@"(?<![\w.])" + Regex.Escape(name) + @"(?!\w)");
        foreach (var (start, end, _) in RazorCSharpProjection.MarkupSpans(text))
        {
            if (code.Any(c => start >= c.Start && start < c.End)) continue;
            foreach (Match m in word.Matches(text[start..end]))
                result.Add(start + m.Index);
        }
        return result.Distinct().OrderBy(x => x).ToList();
    }
}
