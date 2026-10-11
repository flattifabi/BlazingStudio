using BlazorStudio.Core.Highlighting;
using BlazorStudio.Core.Project;
using BlazorStudio.Core.Razor;
using BlazorStudio.Core.Search;
using BlazorStudio.App.Components;

namespace BlazorStudio.App.Services;

/// <summary>
/// Gehe zu Definition (F12), Verwendungen (Shift+F12), Implementierungen (Strg+F12) und Umbenennen (F2)
/// für .cs- und .razor-Dateien – über den Roslyn-Index des Workspace.
/// </summary>
public sealed class NavigationService
{
    private readonly Workspace _ws;
    private readonly UiService _ui;
    private readonly Localizer _l;

    public NavigationService(Workspace ws, UiService ui, Localizer l)
    {
        _ws = ws;
        _ui = ui;
        _l = l;
    }

    /// <summary>Position im Roslyn-Dokument zur Editor-Position (bei .razor über die Projektion).</summary>
    private async Task<(string Path, string Code, int Position)?> TargetAsync(DocumentSession doc, string text, int offset)
    {
        await _ws.EnsureHighlighterAsync();
        if (doc.Kind == DocumentKind.CSharp) return (doc.Path, text, offset);
        if (doc.Kind != DocumentKind.Razor) return null;
        var (cls, ns, usings) = _ws.RazorContextFor(doc);
        var proj = RazorCSharpProjection.Project(text, WordEnd(text, offset), cls, ns, usings, _ws.IsCSharpParameter);
        if (proj is null) return null;
        // Projektion bis zum Wortende – Roslyn findet den Bezeichner direkt vor der Position
        await _ws.Highlighter.UpsertRazorAsync(doc.Path, proj);
        return (SemanticHighlighter.GeneratedPath(doc.Path), proj.Code, proj.Position);
    }

    private static int WordEnd(string text, int offset)
    {
        var e = Math.Clamp(offset, 0, text.Length);
        while (e < text.Length && (char.IsLetterOrDigit(text[e]) || text[e] == '_')) e++;
        return e;
    }

    private static string? WordAt(string text, int offset)
    {
        var s = Math.Clamp(offset, 0, text.Length);
        var e = s;
        while (s > 0 && (char.IsLetterOrDigit(text[s - 1]) || text[s - 1] == '_')) s--;
        while (e < text.Length && (char.IsLetterOrDigit(text[e]) || text[e] == '_')) e++;
        return e > s ? text[s..e] : null;
    }

    // ------------------------------------------------------------------

    public async Task GoToDefinitionAsync(DocumentSession doc, string text, int offset)
    {
        try
        {
            var t = await TargetAsync(doc, text, offset);
            if (t is null)
            {
                // Markup: <Komponente> → .razor-Datei der Komponente
                if (doc.Kind == DocumentKind.Razor && TagAt(text, offset) is { } tag)
                {
                    if (FindComponentFile(tag) is { } file)
                    {
                        _ws.OpenAt(file, 0, 0);
                        return;
                    }
                    // Komponente aus einer Bibliothek (MudButton …) bzw. in C# definiert: über Roslyn auflösen
                    var (_, ns, usings) = _ws.RazorContextFor(doc);
                    var probe = string.Concat(usings.Where(u => u.Length > 0).Distinct().Select(u => "using " + u.TrimEnd(';') + ";\n"))
                        + "namespace " + ns + "\n{\nclass __BsNav\n{\n";
                    var pos = probe.Length;
                    probe += tag + " __x;\n}\n}\n";
                    var tr = await _ws.Highlighter.DefinitionAsync(doc.Path + ".__nav.cs", probe, pos + 1);
                    if (tr.Locations.Count > 0)
                    {
                        _ws.OpenAt(tr.Locations[0].Path, tr.Locations[0].Offset, tr.Locations[0].Length);
                        return;
                    }
                    if (tr.External is { } ext)
                    {
                        _ui.ShowToast(_l.F("nav.external", tr.Symbol ?? tag, ext), ToastKind.Info, 6000);
                        return;
                    }
                }
                _ui.ShowToast(_l["nav.noSymbol"], ToastKind.Info);
                return;
            }
            var r = await _ws.Highlighter.DefinitionAsync(t.Value.Path, t.Value.Code, t.Value.Position);
            if (r.Locations.Count == 0)
            {
                if (r.Symbol is { } sym && r.Kind is "class" && FindComponentFile(sym) is { } comp)
                {
                    _ws.OpenAt(comp, 0, 0);
                    return;
                }
                _ui.ShowToast(r.External is { } ext ? _l.F("nav.external", r.Symbol ?? "", ext) : _l["nav.noSymbol"], ToastKind.Info, 6000);
                return;
            }
            if (r.Locations.Count == 1)
            {
                var loc = r.Locations[0];
                _ws.OpenAt(loc.Path, loc.Offset, loc.Length);
                return;
            }
            // partielle Klassen o. Ä.: Auswahl in der Trefferliste
            FindPanel.ShowResults(_l.F("nav.definitions", r.Symbol ?? ""), ToHits(r.Locations));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _ui.ShowToast(e.Message, ToastKind.Error);
        }
    }

    public async Task FindUsagesAsync(DocumentSession doc, string text, int offset)
    {
        try
        {
            var t = await TargetAsync(doc, text, offset);
            if (t is null) { _ui.ShowToast(_l["nav.noSymbol"], ToastKind.Info); return; }
            var r = await _ws.Highlighter.ReferencesAsync(t.Value.Path, t.Value.Code, t.Value.Position);
            if (r.Symbol is null) { _ui.ShowToast(_l["nav.noSymbol"], ToastKind.Info); return; }
            var locs = r.Locations.ToList();
            // Verwendungen im Markup der zugehörigen Komponente (@Total, @onclick="Add" …)
            foreach (var razor in OwningRazorFiles(r))
            {
                var rt = TextOf(razor);
                if (rt is null) continue;
                var regions = RazorCSharpProjection.CodeRegions(rt);
                foreach (var hit in RazorMarkupUsages.Find(rt, r.Symbol))
                    if (!regions.Any(g => hit >= g.Start && hit < g.End) && !locs.Any(l => l.Path.Equals(razor, StringComparison.OrdinalIgnoreCase) && l.Offset == hit))
                        locs.Add(new NavLocation(razor, hit, r.Symbol.Length));
            }
            FindPanel.ShowResults(_l.F("nav.usages", r.Symbol), ToHits(locs));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _ui.ShowToast(e.Message, ToastKind.Error);
        }
    }

    public async Task GoToImplementationAsync(DocumentSession doc, string text, int offset)
    {
        try
        {
            var t = await TargetAsync(doc, text, offset);
            if (t is null) { _ui.ShowToast(_l["nav.noSymbol"], ToastKind.Info); return; }
            var r = await _ws.Highlighter.ImplementationsAsync(t.Value.Path, t.Value.Code, t.Value.Position);
            if (r.Symbol is null) { _ui.ShowToast(_l["nav.noSymbol"], ToastKind.Info); return; }
            if (r.Locations.Count == 0) { _ui.ShowToast(_l.F("nav.noImpl", r.Symbol), ToastKind.Info); return; }
            if (r.Locations.Count == 1)
            {
                _ws.OpenAt(r.Locations[0].Path, r.Locations[0].Offset, r.Locations[0].Length);
                return;
            }
            FindPanel.ShowResults(_l.F("nav.implementations", r.Symbol), ToHits(r.Locations));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _ui.ShowToast(e.Message, ToastKind.Error);
        }
    }

    public async Task RenameAsync(DocumentSession doc, string text, int offset)
    {
        try
        {
            var t = await TargetAsync(doc, text, offset);
            var old = WordAt(text, offset);
            if (t is null || old is null) { _ui.ShowToast(_l["nav.noSymbol"], ToastKind.Info); return; }
            var req = new DialogRequest
            {
                Title = _l["nav.rename"],
                Message = _l.F("nav.renameText", old),
                InputValue = old,
                OkText = _l["nav.rename"],
                CancelText = _l["action.cancel"],
            };
            var dr = await _ui.ShowDialogAsync(req);
            if (dr != DialogResult.Ok) return;
            var name = req.InputValue?.Trim() ?? "";
            if (name.Length == 0 || name == old) return;

            // Projektion neu setzen (der Dialog kann eine Weile offen gewesen sein)
            t = await TargetAsync(doc, doc.Text, offset);
            if (t is null) return;
            var (error, oldName, edits) = await _ws.Highlighter.RenameAsync(t.Value.Path, t.Value.Code, t.Value.Position, name);
            if (error is not null)
            {
                _ui.ShowToast(error switch
                {
                    "invalid" => _l.F("nav.renameInvalid", name),
                    "metadata" => _l.F("nav.renameMetadata", oldName ?? old),
                    _ => _l["nav.noSymbol"],
                }, ToastKind.Warning, 5000);
                return;
            }

            // Markup-Verwendungen in den zugehörigen .razor-Dateien mit umbenennen
            var byFile = edits.GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.SelectMany(e => e.Edits).ToList(), StringComparer.OrdinalIgnoreCase);
            var defs = await _ws.Highlighter.DefinitionAsync(t.Value.Path, t.Value.Code, t.Value.Position);
            foreach (var razor in OwningRazorFiles(defs))
            {
                var rt = TextOf(razor);
                if (rt is null || oldName is null) continue;
                var regions = RazorCSharpProjection.CodeRegions(rt);
                if (!byFile.TryGetValue(razor, out var list)) byFile[razor] = list = new List<TextEditAt>();
                foreach (var hit in RazorMarkupUsages.Find(rt, oldName))
                    if (!regions.Any(g => hit >= g.Start && hit < g.End) && !list.Any(e => e.Offset == hit))
                        list.Add(new TextEditAt(hit, oldName.Length, name));
            }

            var total = 0;
            var files = 0;
            foreach (var (file, list) in byFile)
            {
                if (list.Count == 0) continue;
                var open = _ws.Documents.FirstOrDefault(d => d.Path.Equals(file, StringComparison.OrdinalIgnoreCase));
                var content = open?.Text ?? FileSearch.ReadText(file);
                if (content is null) continue;
                var updated = Apply(content, list);
                if (open is not null)
                {
                    open.SetText(updated, ChangeOrigin.External);
                }
                else
                {
                    var bom = File.ReadAllBytes(file).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF });
                    await File.WriteAllTextAsync(file, updated, new System.Text.UTF8Encoding(bom));
                }
                await ReindexAsync(file, updated);
                total += list.Count;
                files++;
            }
            _ui.ShowToast(_l.F("nav.renamed", oldName ?? old, name, total, files), ToastKind.Success);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _ui.ShowToast(e.Message, ToastKind.Error);
        }
    }

    private async Task ReindexAsync(string file, string text)
    {
        if (file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
        {
            var (cls, ns, usings) = _ws.RazorContextFor(file);
            await _ws.Highlighter.UpsertRazorAsync(file, RazorCSharpProjection.ClassFor(text, cls, ns, usings));
        }
        else if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            await _ws.Highlighter.ClassifyCSharpAsync(file, text);
        }
    }

    private static string Apply(string text, IEnumerable<TextEditAt> edits)
    {
        foreach (var e in edits.GroupBy(e => e.Offset).Select(g => g.First()).OrderByDescending(e => e.Offset))
        {
            if (e.Offset < 0 || e.Offset + e.Length > text.Length) continue;
            text = text.Remove(e.Offset, e.Length).Insert(e.Offset, e.NewText);
        }
        return text;
    }

    /// <summary>.razor-Dateien, deren Klasse das Symbol enthält (Definition in @code oder in Datei.razor.cs).</summary>
    private static IEnumerable<string> OwningRazorFiles(NavResult r)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in r.Locations.Where(l => l.IsDefinition))
        {
            if (l.Path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)) set.Add(l.Path);
            else if (l.Path.EndsWith(".razor.cs", StringComparison.OrdinalIgnoreCase) && File.Exists(l.Path[..^3])) set.Add(l.Path[..^3]);
        }
        return set;
    }

    private string? TextOf(string path) =>
        _ws.Documents.FirstOrDefault(d => d.Path.Equals(path, StringComparison.OrdinalIgnoreCase))?.Text ?? FileSearch.ReadText(path);

    /// <summary>Tag-Name, wenn der Cursor auf &lt;Name oder &lt;/Name steht.</summary>
    private static string? TagAt(string text, int offset)
    {
        var word = WordAt(text, offset);
        if (word is null || !char.IsUpper(word[0])) return null;
        var s = Math.Clamp(offset, 0, text.Length);
        while (s > 0 && (char.IsLetterOrDigit(text[s - 1]) || text[s - 1] == '_' || text[s - 1] == '.')) s--;
        if (s > 0 && text[s - 1] == '/') s--;
        return s > 0 && text[s - 1] == '<' ? word : null;
    }

    private string? FindComponentFile(string name)
    {
        var roots = _ws.Solution is { } sln
            ? sln.AllProjects.Where(p => p.Project is not null).Select(p => Path.GetDirectoryName(p.Path)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : _ws.RootDirectory is { } r ? new List<string> { r } : new List<string>();
        foreach (var root in roots)
            foreach (var f in ProjectIndex.EnumerateFiles(root))
                if (Path.GetFileName(f).Equals(name + ".razor", StringComparison.OrdinalIgnoreCase)) return f;
        return null;
    }

    /// <summary>Navigationsziele als Trefferliste (Zeile, Spalte, Zeilentext).</summary>
    private List<SearchHit> ToHits(IEnumerable<NavLocation> locations)
    {
        var hits = new List<SearchHit>();
        var cache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in locations.DistinctBy(l => (l.Path.ToLowerInvariant(), l.Offset)))
        {
            if (!cache.TryGetValue(l.Path, out var text)) cache[l.Path] = text = TextOf(l.Path);
            if (text is null) continue;
            var offset = Math.Clamp(l.Offset, 0, text.Length);
            var lineStart = text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
            if (offset == 0) lineStart = 0;
            var lineEnd = text.IndexOf('\n', offset);
            if (lineEnd < 0) lineEnd = text.Length;
            var line = 1;
            for (var i = 0; i < lineStart; i++) if (text[i] == '\n') line++;
            var lineText = text[lineStart..lineEnd].TrimEnd('\r');
            hits.Add(new SearchHit(l.Path, line, offset - lineStart + 1, offset, Math.Min(l.Length, Math.Max(0, lineText.Length - (offset - lineStart))), lineText));
        }
        return hits.OrderBy(h => h.File, StringComparer.OrdinalIgnoreCase).ThenBy(h => h.Offset).ToList();
    }
}
