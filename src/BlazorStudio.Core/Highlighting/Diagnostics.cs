using System.Text.RegularExpressions;
using BlazorStudio.Core.Razor;
using Microsoft.CodeAnalysis;

namespace BlazorStudio.Core.Highlighting;

/// <summary>C#-Fehler aus Roslyn, Position im Originaltext (.cs bzw. .razor).</summary>
public sealed record CodeDiagnostic(int Start, int End, string Code, string Message);

/// <summary>
/// Rote Unterstreichung beim Tippen wie in VS/Rider: Compilerfehler aus dem Roslyn-Workspace.
/// Der Workspace ist eine Annäherung (alle Projekte in einer Compilation, Referenzen des Startprojekts, keine
/// Source-Generatoren) – Fehler, die nur daraus entstehen können, werden nicht gemeldet; der Build zeigt sie exakt.
/// </summary>
public sealed partial class SemanticHighlighter
{
    /// <summary>
    /// Fehler, die aus der Annäherung stammen können: fehlende Referenzen, doppelte Typen über Projektgrenzen,
    /// mehrere Program.cs, abweichende Basisklassen zwischen .razor und .razor.cs.
    /// </summary>
    /// <summary>Hinweise, dass der Datei Referenzen fehlen (Typ, Namespace oder Assembly nicht gefunden).</summary>
    private static readonly HashSet<string> MissingReference = new(StringComparer.Ordinal)
    {
        "CS0246", "CS0234", "CS0400", "CS0012", "CS0518", "CS0656", "CS1069", "CS8179",
    };

    private static readonly HashSet<string> Unreliable = new(StringComparer.Ordinal)
    {
        "CS0246", "CS0234", "CS0400", "CS0012", "CS0518", "CS0656", "CS1069", "CS8179",       // Typ/Namespace/Assembly fehlt
        "CS0101", "CS0102", "CS0111", "CS0121", "CS0229", "CS0104", "CS0433", "CS0436",       // doppelt/mehrdeutig
        "CS0260", "CS0262", "CS0263", "CS0264", "CS0115", "CS0534", "CS0535", "CS0738",       // partial/Basisklasse
        "CS8802", "CS7022", "CS0017", "CS8803",                                              // Top-Level-Statements mehrfach
        "CS0759", "CS8795", "CS8796", "CS9248",                                              // partial-Member aus Generatoren
    };

    /// <summary>Compilerfehler einer .cs-Datei (aktueller Editor-Text).</summary>
    public async Task<IReadOnlyList<CodeDiagnostic>> DiagnoseCSharpAsync(string path, string text, CancellationToken ct = default)
    {
        var doc = await UpsertAsync(path, text);
        if (doc is null) return Array.Empty<CodeDiagnostic>();
        var model = await doc.GetSemanticModelAsync(ct);
        if (model is null) return Array.Empty<CodeDiagnostic>();
        return Filter(await RelevantAsync(model, ct))
            .Select(d => new CodeDiagnostic(d.Location.SourceSpan.Start, d.Location.SourceSpan.End, d.Id, d.GetMessage()))
            .ToList();
    }

    /// <summary>
    /// Compilerfehler der @code-Blöcke und @inject-Zeilen einer .razor-Datei. C# aus dem Markup wird nicht geprüft –
    /// dort erzeugt erst der Razor-Compiler den echten Code (Lambdas, @bind, RenderFragment-Kontexte).
    /// </summary>
    public async Task<IReadOnlyList<CodeDiagnostic>> DiagnoseRazorAsync(string razorPath, string text, RazorProjection projection, CancellationToken ct = default)
    {
        if (projection.Map is null) return Array.Empty<CodeDiagnostic>();
        await UpsertRazorAsync(razorPath, projection);
        var doc = Current(GeneratedPath(razorPath));
        if (doc is null) return Array.Empty<CodeDiagnostic>();
        var model = await doc.GetSemanticModelAsync(ct);
        if (model is null) return Array.Empty<CodeDiagnostic>();

        var markupStart = projection.Code.IndexOf("private void __BsMarkup()", StringComparison.Ordinal);
        if (markupStart < 0) markupStart = projection.Code.Length;
        // @code-Blöcke mit Razor-Markup (RenderFragment-Vorlagen) sind kein reines C# – dort nichts melden
        var skip = projection.Map.Where(m => m.Gen < markupStart && HasMarkup(text.Substring(m.Orig, Math.Min(m.Len, text.Length - m.Orig))))
            .Select(m => (m.Gen, m.Gen + m.Len)).ToList();

        var result = new List<CodeDiagnostic>();
        foreach (var d in Filter(await RelevantAsync(model, ct)))
        {
            var span = d.Location.SourceSpan;
            if (span.Start >= markupStart || skip.Any(s => span.Start >= s.Item1 && span.Start <= s.Item2)) continue;
            if (projection.ToOriginal(span.Start) is not int start) continue;
            var end = projection.ToOriginal(span.End) ?? start + span.Length;
            result.Add(new CodeDiagnostic(start, Math.Max(end, start), d.Id, d.GetMessage()));
        }
        return result;
    }

    /// <summary>
    /// Lösungsweite Analyse (wie Rider „Solution-wide analysis“): Fehleranzahl je .cs-Datei im aktuellen Stand des
    /// Workspace (geöffnete Dateien mit Editor-Text). Generierte Dateien und obj/ werden übersprungen.
    /// .razor-Dateien prüft der Aufrufer mit <see cref="DiagnoseRazorAsync"/>.
    /// </summary>
    public async Task<Dictionary<string, int>> DiagnoseAllCSharpAsync(CancellationToken ct = default)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (_workspace is null) return result;
        List<KeyValuePair<string, DocumentId>> docs;
        await _lock.WaitAsync(ct);
        try
        {
            docs = _docs.ToList();
        }
        finally
        {
            _lock.Release();
        }
        var solution = _workspace.CurrentSolution;
        var sep = Path.DirectorySeparatorChar;
        foreach (var (path, id) in docs)
        {
            ct.ThrowIfCancellationRequested();
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".__razor.cs", StringComparison.OrdinalIgnoreCase) || _generated.ContainsKey(path)
                || path.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase) || path.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase))
                continue;
            if (solution.GetDocument(id) is not { } doc || await doc.GetSemanticModelAsync(ct) is not { } model) continue;
            var count = Filter(await RelevantAsync(model, ct)).Count();
            if (count > 0) result[path] = count;
        }
        return result;
    }

    /// <summary>
    /// Alle Fehler – oder nur Syntaxfehler, wenn der Datei Referenzen fehlen: Dann sind geerbte Member, Extension-Methoden
    /// usw. nicht auflösbar und jede Namens-Prüfung wäre ein Fehlalarm (z. B. Typen aus einer RCL, die der Workspace nicht kennt).
    /// </summary>
    private static async Task<IEnumerable<Diagnostic>> RelevantAsync(SemanticModel model, CancellationToken ct)
    {
        var root = await model.SyntaxTree.GetRootAsync(ct);
        var all = model.GetDiagnostics(cancellationToken: ct).Where(d => !FromIncompleteType(d, model, root, ct)).ToList();
        var incomplete = all.Any(d => MissingReference.Contains(d.Id) || LooksLikeMissingType(d, root))
                         || root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>()
                             .Any(t => model.GetDeclaredSymbol(t, ct) is INamedTypeSymbol { BaseType: { } b } && HasErrorType(b));
        return incomplete ? model.SyntaxTree.GetDiagnostics(ct) : all;
    }

    /// <summary><c>ProtectedData.Protect(…)</c> ohne Referenz: CS0103 auf einem großgeschriebenen Namen vor einem Punkt ist ein fehlender Typ.</summary>
    private static bool LooksLikeMissingType(Diagnostic d, SyntaxNode root)
    {
        if (d.Id != "CS0103") return false;
        var node = root.FindNode(d.Location.SourceSpan, getInnermostNodeForTie: true);
        return node is Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax { Parent: Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax ma } id
               && ma.Expression == id && char.IsUpper(id.Identifier.ValueText.FirstOrDefault());
    }

    private static readonly HashSet<string> MemberErrors = new(StringComparer.Ordinal) { "CS0117", "CS1061", "CS1501", "CS1503", "CS7036", "CS1929" };

    /// <summary>
    /// „X enthält keine Definition für Y“, wenn X in einer Datei steht, die Roslyn nicht vollständig lesen kann
    /// (z. B. .razor mit Markup in @code) – dann fehlt Y nur in der Annäherung.
    /// </summary>
    private static bool FromIncompleteType(Diagnostic d, SemanticModel model, SyntaxNode root, CancellationToken ct)
    {
        if (!MemberErrors.Contains(d.Id)) return false;
        var node = root.FindNode(d.Location.SourceSpan, getInnermostNodeForTie: true);
        var access = node.AncestorsAndSelf().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax>().FirstOrDefault();
        ITypeSymbol? type = access is null
            ? model.GetEnclosingSymbol(d.Location.SourceSpan.Start, ct)?.ContainingType
            : model.GetTypeInfo(access.Expression, ct).Type ?? model.GetSymbolInfo(access.Expression, ct).Symbol as ITypeSymbol;
        return type is not null && type.DeclaringSyntaxReferences.Any(r =>
            r.SyntaxTree.GetDiagnostics(ct).Any(x => x.Severity == DiagnosticSeverity.Error));
    }

    private static bool HasErrorType(INamedTypeSymbol? t)
    {
        for (; t is not null; t = t.BaseType)
            if (t.TypeKind == TypeKind.Error || t.TypeArguments.Any(a => a.TypeKind == TypeKind.Error)) return true;
        return false;
    }

    private static IEnumerable<Diagnostic> Filter(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error && d.Location.IsInSource && !Unreliable.Contains(d.Id))
            .GroupBy(d => (d.Location.SourceSpan.Start, d.Id)).Select(g => g.First())
            .Take(200);

    private static bool HasMarkup(string code) => code.Contains("@<", StringComparison.Ordinal) || MarkupLine().IsMatch(code);

    [GeneratedRegex(@"^\s*</?[A-Za-z][\w.:-]*[\s/>]", RegexOptions.Multiline)]
    private static partial Regex MarkupLine();
}
