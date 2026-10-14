using System.Reflection;
using System.Text.RegularExpressions;
using BlazorStudio.Core.Razor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;

namespace BlazorStudio.Core.Highlighting;

/// <summary>Eintrag im Strg+.-Menü.</summary>
/// <param name="Kind">"quickfix" (behebt einen Fehler) oder "refactor".</param>
public sealed record CodeActionItem(int Id, string Title, string Kind, bool IsPreferred);

/// <summary>Ergebnis einer Aktion: Änderungen am Editor-Text (Positionen im Originaltext) oder eine Fehlermeldung.</summary>
public sealed record CodeActionResult(IReadOnlyList<TextEditAt> Edits, string? Error = null);

/// <summary>
/// Schnellaktionen wie in Rider/VS (Strg+.): Roslyns Code-Fixes („Interface implementieren“, „Abstrakte Klasse
/// implementieren“, „using hinzufügen“, „Methode erzeugen“ …) und Refactorings („Konstruktor erzeugen“,
/// „Überschreibungen erzeugen“, „Variable einführen“ …). Funktioniert in .cs-Dateien und in den C#-Teilen von .razor.
/// </summary>
public sealed partial class SemanticHighlighter
{
    private static readonly Lazy<(IReadOnlyList<CodeFixProvider> Fixes, IReadOnlyList<CodeRefactoringProvider> Refactorings)> Providers = new(LoadProviders);

    private readonly Dictionary<int, (CodeAction Action, Document Document, string? RazorPath)> _lastActions = new();
    private int _nextActionId;

    /// <summary>Titel, die nur Analyzer-Konfiguration betreffen – im Editor ohne .editorconfig-Verwaltung nutzlos.</summary>
    private static readonly string[] HiddenTitles = { "Suppress", "Configure", "Unterdrücken", "Konfigurieren", "Fix all", "Alle ", "Move type to", "Rename file to" };

    /// <summary>Alle C#-Fix- und Refactoring-Provider aus den Roslyn-Features-Assemblys (wie in VS per MEF exportiert).</summary>
    private static (IReadOnlyList<CodeFixProvider>, IReadOnlyList<CodeRefactoringProvider>) LoadProviders()
    {
        var fixes = new List<CodeFixProvider>();
        var refactorings = new List<CodeRefactoringProvider>();
        foreach (var asm in MefHostServices.DefaultAssemblies)
        {
            Type?[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }
            foreach (var t in types)
            {
                if (t is null || t.IsAbstract) continue;
                var fix = t.GetCustomAttribute<ExportCodeFixProviderAttribute>();
                var refactoring = t.GetCustomAttribute<ExportCodeRefactoringProviderAttribute>();
                if (fix is null && refactoring is null) continue;
                var languages = fix?.Languages ?? refactoring!.Languages;
                if (!languages.Contains(LanguageNames.CSharp)) continue;
                var ctor = t.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes);
                if (ctor is null) continue;
                try
                {
                    switch (ctor.Invoke(null))
                    {
                        case CodeFixProvider f: fixes.Add(f); break;
                        case CodeRefactoringProvider r: refactorings.Add(r); break;
                    }
                }
                catch (TargetInvocationException)
                {
                    // Provider braucht MEF-Importe – auslassen
                }
            }
        }
        return (fixes, refactorings);
    }

    /// <summary>
    /// Aktionen an [start, end) einer .cs-Datei bzw. (mit <paramref name="projection"/>) einer .razor-Datei.
    /// <paramref name="withRefactorings"/>: auch Refactorings (nur bei Strg+., die Glühbirne zeigt nur Fixes).
    /// </summary>
    public async Task<IReadOnlyList<CodeActionItem>> GetCodeActionsAsync(string path, string text, int start, int end, bool withRefactorings,
        RazorProjection? projection = null, CancellationToken ct = default)
    {
        Document? doc;
        string? razorPath = null;
        if (projection is not null)
        {
            if (projection.Map is null) return Array.Empty<CodeActionItem>();
            await UpsertRazorAsync(path, projection);
            doc = Current(GeneratedPath(path));
            razorPath = path;
            var gs = ToGenerated(projection, start);
            var ge = ToGenerated(projection, end);
            if (gs is null) return Array.Empty<CodeActionItem>();
            start = gs.Value;
            end = ge ?? gs.Value;
        }
        else
        {
            doc = await UpsertAsync(path, text);
        }
        if (doc is null) return Array.Empty<CodeActionItem>();

        var source = await doc.GetTextAsync(ct);
        start = Math.Clamp(start, 0, source.Length);
        end = Math.Clamp(Math.Max(start, end), 0, source.Length);
        var span = TextSpan.FromBounds(start, end);
        // Fehler der ganzen Zeile berücksichtigen (wie die Glühbirne in Rider)
        var line = source.Lines.GetLineFromPosition(start);
        var lineSpan = TextSpan.FromBounds(line.Start, Math.Max(line.End, end));

        var (fixProviders, refactoringProviders) = Providers.Value;
        var found = new List<(CodeAction Action, string Kind, bool Preferred)>();

        var model = await doc.GetSemanticModelAsync(ct);
        if (model is not null)
        {
            var diagnostics = model.GetDiagnostics(lineSpan, ct)
                .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning && d.Location.IsInSource)
                .ToList();
            foreach (var d in diagnostics)
            {
                foreach (var p in fixProviders.Where(p => p.FixableDiagnosticIds.Contains(d.Id)))
                {
                    try
                    {
                        await p.RegisterCodeFixesAsync(new CodeFixContext(doc, d, (a, _) => found.Add((a, "quickfix", d.Severity == DiagnosticSeverity.Error)), ct));
                    }
                    catch (Exception e) when (e is not OperationCanceledException && e is not OutOfMemoryException)
                    {
                        // Ein fehlerhafter Provider darf das Menü nicht leeren
                    }
                }
            }
        }

        if (withRefactorings)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(3));
            foreach (var p in refactoringProviders)
            {
                if (budget.IsCancellationRequested) break;
                try
                {
                    await p.ComputeRefactoringsAsync(new CodeRefactoringContext(doc, span, a => found.Add((a, "refactor", false)), budget.Token));
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                }
            }
        }

        _lastActions.Clear();
        var items = new List<CodeActionItem>();
        var titles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (action, kind, preferred) in found)
        {
            foreach (var (title, leaf) in Flatten(action, null))
            {
                // Aktionen mit VS-Dialog (Mitglieder auswählen, Interface extrahieren …) und Datei-Operationen gehen hier nicht
                if (leaf is CodeActionWithOptions || HiddenTitles.Any(h => title.StartsWith(h, StringComparison.OrdinalIgnoreCase)) || !titles.Add(title)) continue;
                var id = ++_nextActionId;
                _lastActions[id] = (leaf, doc, razorPath);
                items.Add(new CodeActionItem(id, title, kind, preferred && kind == "quickfix"));
                if (items.Count >= 60) break;
            }
        }
        // Fehlerbehebungen vor Refactorings, „Implementieren“ ganz oben
        return items.OrderBy(i => i.Kind == "quickfix" ? 0 : 1).ThenBy(i => i.IsPreferred ? 0 : 1).ToList();
    }

    /// <summary>Verschachtelte Aktionen („Using hinzufügen“ → mehrere Namespaces) als „Gruppe: Eintrag“.</summary>
    private static IEnumerable<(string Title, CodeAction Leaf)> Flatten(CodeAction action, string? parent)
    {
        var title = parent is null ? action.Title : parent + ": " + action.Title;
        if (action.NestedActions.Length == 0)
        {
            yield return (title, action);
            yield break;
        }
        foreach (var child in action.NestedActions)
            foreach (var x in Flatten(child, action.NestedActions.Length == 1 && parent is null ? null : title))
                yield return x;
    }

    /// <summary>Wendet eine Aktion aus der letzten Liste an und liefert die Änderungen am Editor-Text.</summary>
    /// <param name="editorText">Aktueller Text im Editor (für .razor: wohin neue @using-Zeilen kommen).</param>
    public async Task<CodeActionResult> ApplyCodeActionAsync(int id, string editorText, CancellationToken ct = default)
    {
        if (!_lastActions.TryGetValue(id, out var entry)) return new CodeActionResult(Array.Empty<TextEditAt>(), "Die Aktion ist veraltet – bitte erneut Strg+. drücken.");
        var (action, doc, razorPath) = entry;
        var operations = await action.GetOperationsAsync(ct);
        var apply = operations.OfType<ApplyChangesOperation>().FirstOrDefault();
        if (apply is null) return new CodeActionResult(Array.Empty<TextEditAt>(), "Diese Aktion wird im Editor nicht unterstützt.");

        var changes = apply.ChangedSolution.GetChanges(doc.Project.Solution);
        var projectChanges = changes.GetProjectChanges().ToList();
        var otherDocs = projectChanges.SelectMany(p => p.GetChangedDocuments()).Where(d => d != doc.Id).ToList();
        var added = projectChanges.SelectMany(p => p.GetAddedDocuments()).ToList();
        if (added.Count > 0 || otherDocs.Count > 0)
            return new CodeActionResult(Array.Empty<TextEditAt>(), "Diese Aktion ändert weitere Dateien – das wird hier noch nicht unterstützt.");

        var newDoc = apply.ChangedSolution.GetDocument(doc.Id);
        if (newDoc is null) return new CodeActionResult(Array.Empty<TextEditAt>(), "Diese Aktion wird im Editor nicht unterstützt.");
        var textChanges = (await newDoc.GetTextChangesAsync(doc, ct)).ToList();
        if (textChanges.Count == 0) return new CodeActionResult(Array.Empty<TextEditAt>(), "Diese Aktion ändert nichts am Text und wird im Editor nicht unterstützt.");
        if (razorPath is null)
            return new CodeActionResult(textChanges.Select(c => new TextEditAt(c.Span.Start, c.Span.Length, c.NewText ?? "")).ToList());

        // .razor: Änderungen auf die Originaldatei zurückrechnen
        if (!_generated.TryGetValue(GeneratedPath(razorPath), out var gen)) return new CodeActionResult(Array.Empty<TextEditAt>(), "Projektion fehlt.");
        var projection = gen.Projection;
        var generated = (await doc.GetTextAsync(ct)).ToString();
        var edits = new List<TextEditAt>();
        var codeChanges = new List<TextChange>();
        foreach (var c in textChanges)
        {
            var newText = c.NewText ?? "";
            // using-Direktiven gehören in .razor als @using an den Anfang
            var usings = UsingLines().Matches(newText);
            if (usings.Count > 0 && ToOriginal(projection, c.Span.Start) is null && UsingLines().Replace(newText, "").Trim().Length == 0)
            {
                foreach (Match m in usings)
                {
                    var (at, insert) = RazorCSharpProjection.UsingInsertion(editorText, m.Groups[1].Value);
                    if (!editorText.Contains("@using " + m.Groups[1].Value, StringComparison.Ordinal)) edits.Add(new TextEditAt(at, 0, insert));
                }
                continue;
            }
            codeChanges.Add(c);
        }
        if (codeChanges.Count > 0)
        {
            // Roslyn ersetzt gern mehr als nötig (generierte Zeilenumbrüche usw.) – Gesamtunterschied bestimmen
            var updated = SourceText.From(generated).WithChanges(codeChanges).ToString();
            var c = Minimize(generated, updated);
            var s = ToOriginal(projection, c.Span.Start);
            var e = ToOriginal(projection, c.Span.End);
            if (s is null || e is null || e < s)
                return new CodeActionResult(Array.Empty<TextEditAt>(), "Diese Aktion betrifft generierten Code der Komponente und kann in .razor nicht angewendet werden.");
            edits.Add(new TextEditAt(s.Value, e.Value - s.Value, c.NewText ?? ""));
        }
        return new CodeActionResult(edits);
    }

    /// <summary>Kleinste einzelne Änderung, die <paramref name="oldText"/> in <paramref name="newText"/> überführt.</summary>
    public static TextChange Minimize(string oldText, string newText)
    {
        var prefix = 0;
        while (prefix < oldText.Length && prefix < newText.Length && oldText[prefix] == newText[prefix]) prefix++;
        var suffix = 0;
        while (suffix < oldText.Length - prefix && suffix < newText.Length - prefix && oldText[^(suffix + 1)] == newText[^(suffix + 1)]) suffix++;
        return new TextChange(new TextSpan(prefix, oldText.Length - prefix - suffix), newText.Substring(prefix, newText.Length - prefix - suffix));
    }

    [GeneratedRegex(@"^\s*using\s+([A-Za-z_][\w.]*)\s*;\s*$", RegexOptions.Multiline)]
    private static partial Regex UsingLines();

    /// <summary>Generierte Position → Original (Grenzen eines Abschnitts eingeschlossen).</summary>
    private static int? ToOriginal(RazorProjection p, int gen) => p.ToOriginal(gen);

    /// <summary>Original (.razor) → generierte Position.</summary>
    private static int? ToGenerated(RazorProjection p, int orig)
    {
        if (p.Map is null) return null;
        foreach (var (g, o, l) in p.Map)
            if (orig >= o && orig <= o + l) return g + (orig - o);
        return null;
    }
}
