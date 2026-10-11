using BlazorStudio.Core.Editing;
using BlazorStudio.Core.Model;
using BlazorStudio.Core.Razor;

namespace BlazorStudio.App.Services;

public enum DocumentKind { Razor, CSharp, Css, JavaScript, Json, Html, Xml, Markdown, Sql, Text }

public enum ViewMode { Design, Split, Code }

public enum ChangeOrigin { Load, Design, Code, Undo, External }

/// <summary>Eine geöffnete Datei. Für .razor zusätzlich der Designer-Zustand.</summary>
public sealed class DocumentSession
{
    private const int MaxUndo = 200;
    private readonly Stack<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();
    private DateTime _lastPush = DateTime.MinValue;
    private string? _lastMergeKey;

    private sealed record Snapshot(string Text, List<int>? SelectionPath);

    public DocumentSession(string path, string text)
    {
        Path = path;
        Kind = KindFor(path);
        Text = text;
        SavedText = text;
        if (Kind == DocumentKind.Razor) Reparse();
        View = Kind == DocumentKind.Razor ? ViewMode.Split : ViewMode.Code;
    }

    public string Path { get; private set; }

    public string Name => System.IO.Path.GetFileName(Path);

    public DocumentKind Kind { get; }

    public string Text { get; private set; }

    public string SavedText { get; private set; }

    public bool IsDirty => Text != SavedText;

    public int Version { get; private set; }

    public DateTime LastWriteOnDisk { get; set; }

    // ---------------- Razor ----------------

    public RazorDocument? Doc { get; private set; }

    public Dictionary<string, (int Start, int End)> Spans { get; private set; } = new();

    public IReadOnlyList<RazorDiagnostic> Diagnostics { get; private set; } = Array.Empty<RazorDiagnostic>();

    public bool HasErrors => Diagnostics.Any(d => d.IsError);

    public string? SelectedId { get; private set; }

    public DesignNode? Selected => Doc?.Find(SelectedId);

    public ViewMode View { get; set; }

    /// <summary>Breite der Vorschau in px; 0 = volle Breite.</summary>
    public int DeviceWidth { get; set; }

    /// <summary>"desktop", "tablet", "phone" oder "custom" (Breakpoint gewählt).</summary>
    public string DevicePreset { get; set; } = "desktop";

    public bool DeviceLandscape { get; set; }

    /// <summary>"" (normal), "hover", "focus", "active", "disabled".</summary>
    public string DesignState { get; set; } = "";

    public int Zoom { get; set; } = 100;

    /// <summary>Zeile, die der Code-Editor anzeigen soll (z. B. aus Problemen / Debugger).</summary>
    public int? RevealLine { get; set; }

    /// <summary>Bereich (Offset, Länge), der im Editor markiert und angezeigt werden soll.</summary>
    public (int Offset, int Length)? RevealSelection { get; set; }

    public event Action<ChangeOrigin>? Changed;

    public event Action? SelectionChanged;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public string MonacoLanguage => Kind switch
    {
        DocumentKind.Razor => "razor",
        DocumentKind.CSharp => "csharp",
        DocumentKind.Css => System.IO.Path.GetExtension(Path).Equals(".scss", StringComparison.OrdinalIgnoreCase) ? "scss" : "css",
        DocumentKind.JavaScript => System.IO.Path.GetExtension(Path).Equals(".ts", StringComparison.OrdinalIgnoreCase) ? "typescript" : "javascript",
        DocumentKind.Json => "json",
        DocumentKind.Html => "html",
        DocumentKind.Xml => "xml",
        DocumentKind.Markdown => "markdown",
        DocumentKind.Sql => "sql",
        _ => "plaintext",
    };

    public static DocumentKind KindFor(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".razor" => DocumentKind.Razor,
        ".cs" => DocumentKind.CSharp,
        ".css" or ".scss" => DocumentKind.Css,
        ".js" or ".mjs" or ".ts" => DocumentKind.JavaScript,
        ".json" => DocumentKind.Json,
        ".html" or ".htm" or ".cshtml" => DocumentKind.Html,
        ".csproj" or ".props" or ".targets" or ".xml" or ".slnx" or ".config" or ".resx" => DocumentKind.Xml,
        ".md" => DocumentKind.Markdown,
        ".sql" => DocumentKind.Sql,
        _ => DocumentKind.Text,
    };

    public void Rename(string newPath) => Path = newPath;

    // ------------------------------------------------------------------

    /// <summary>Text kommt aus dem Code-Editor oder von außen.</summary>
    public void SetText(string text, ChangeOrigin origin)
    {
        if (text == Text) return;
        if (origin == ChangeOrigin.Code) PushUndo("code", TimeSpan.FromMilliseconds(1200));
        else if (origin == ChangeOrigin.External) { _undo.Clear(); _redo.Clear(); }
        var path = Selected is { } sel ? DocumentEditor.PathOf(sel) : null;
        Text = text;
        Version++;
        if (Kind == DocumentKind.Razor)
        {
            Reparse();
            var node = DocumentEditor.FromPath(Doc!, path);
            SelectedId = node?.Id;
        }
        Changed?.Invoke(origin);
        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Änderung aus dem Designer. mergeKey fasst schnelle Folgeänderungen (z. B. Tippen in einem Feld)
    /// zu einem Rückgängig-Schritt zusammen.
    /// </summary>
    public bool Apply(Action<RazorDocument> change, string? mergeKey = null)
    {
        if (Doc is null) return false;
        PushUndo(mergeKey, TimeSpan.FromMilliseconds(1000));
        change(Doc);
        Serialize();
        Changed?.Invoke(ChangeOrigin.Design);
        return true;
    }

    /// <summary>Wie Apply, ändert aber nur die @code-Blöcke (z. B. neue Methode).</summary>
    public void ReplaceCodeBlocks(IReadOnlyList<string> blocks)
    {
        Apply(d =>
        {
            d.CodeBlocks.Clear();
            d.CodeBlocks.AddRange(blocks);
        });
    }

    private void Serialize()
    {
        var w = RazorWriter.Write(Doc!);
        Text = w.Text;
        Spans = w.Spans;
        Version++;
    }

    private void Reparse()
    {
        var r = RazorParser.Parse(Text);
        Doc = r.Document;
        Diagnostics = r.Diagnostics;
        Spans = DocumentEditor.SpansFromSource(Doc);
    }

    public void Select(string? id)
    {
        if (id == SelectedId) return;
        SelectedId = Doc?.Find(id) is null ? null : id;
        SelectionChanged?.Invoke();
    }

    public void SelectNode(DesignNode? node) => Select(node?.Id);

    // ------------------------------------------------------------------

    private void PushUndo(string? mergeKey, TimeSpan window)
    {
        var now = DateTime.UtcNow;
        if (mergeKey is not null && mergeKey == _lastMergeKey && now - _lastPush < window && _undo.Count > 0)
        {
            _lastPush = now;
            return;
        }
        _undo.Push(new Snapshot(Text, Selected is { } s ? DocumentEditor.PathOf(s) : null));
        if (_undo.Count > MaxUndo)
        {
            var keep = _undo.Take(MaxUndo).Reverse().ToList();
            _undo.Clear();
            foreach (var k in keep) _undo.Push(k);
        }
        _redo.Clear();
        _lastPush = now;
        _lastMergeKey = mergeKey;
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(new Snapshot(Text, Selected is { } s ? DocumentEditor.PathOf(s) : null));
        Restore(_undo.Pop());
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(new Snapshot(Text, Selected is { } s ? DocumentEditor.PathOf(s) : null));
        Restore(_redo.Pop());
    }

    private void Restore(Snapshot s)
    {
        _lastMergeKey = null;
        Text = s.Text;
        Version++;
        if (Kind == DocumentKind.Razor)
        {
            Reparse();
            SelectedId = DocumentEditor.FromPath(Doc!, s.SelectionPath)?.Id;
        }
        Changed?.Invoke(ChangeOrigin.Undo);
        SelectionChanged?.Invoke();
    }

    public void MarkSaved()
    {
        SavedText = Text;
        Changed?.Invoke(ChangeOrigin.Load);
    }

    /// <summary>Zeile (1-basiert) zu einem Offset.</summary>
    public int LineOf(int offset)
    {
        var line = 1;
        for (var i = 0; i < Math.Min(offset, Text.Length); i++) if (Text[i] == '\n') line++;
        return line;
    }
}
