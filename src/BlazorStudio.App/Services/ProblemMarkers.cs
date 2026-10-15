using BlazorStudio.Core.Build;
using BlazorStudio.Core.Project;

namespace BlazorStudio.App.Services;

/// <summary>
/// Welche Dateien haben Fehler? Für die rote Markierung im Projektmappen-Explorer (Datei, Ordner, Projekt) wie in Rider.
/// Quellen: lösungsweite Roslyn-Analyse im Hintergrund, die Live-Prüfung des offenen Editors und der letzte Build.
/// </summary>
public sealed class ProblemMarkers : IDisposable
{
    private readonly Workspace _ws;
    private readonly RunService _run;
    private readonly IHostBridge _host;
    private readonly object _gate = new();

    /// <summary>Fehler aus der Analyse (lösungsweit oder Editor) je Datei.</summary>
    private Dictionary<string, int> _analysis = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fehler aus dem letzten Build je Datei.</summary>
    private Dictionary<string, int> _build = new(StringComparer.OrdinalIgnoreCase);

    private DateTime _buildTime;
    private int _buildSignature;

    /// <summary>Zusammengeführt: Datei → Fehler, Ordner → Fehler darunter.</summary>
    private Dictionary<string, int> _files = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, int> _dirs = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _scan;
    private string? _root;

    public ProblemMarkers(Workspace ws, RunService run, IHostBridge host)
    {
        _ws = ws;
        _run = run;
        _host = host;
        _ws.Changed += OnWorkspaceChanged;
        _ws.FileChangedOnDisk += OnFileChanged;
        _run.Changed += OnRunChanged;
    }

    public event Action? Changed;

    /// <summary>Anzahl Fehler einer Datei (0 = keine).</summary>
    public int ErrorsIn(string file)
    {
        lock (_gate) return _files.GetValueOrDefault(Norm(file));
    }

    /// <summary>Anzahl Fehler in allen Dateien unterhalb eines Ordners (auch Projekt- oder Projektmappenordner).</summary>
    public int ErrorsUnder(string directory)
    {
        lock (_gate) return _dirs.GetValueOrDefault(Norm(directory));
    }

    public int Total
    {
        get { lock (_gate) return _files.Values.Sum(); }
    }

    private static string Norm(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    // ------------------------------------------------------------------
    // Eingänge
    // ------------------------------------------------------------------

    /// <summary>Ergebnis der Live-Prüfung des Editors (aktueller, ungespeicherter Text).</summary>
    public void ReportLive(string file, int errors)
    {
        var key = Norm(file);
        lock (_gate)
        {
            if (_analysis.GetValueOrDefault(key) == errors) return;
            if (errors > 0) _analysis[key] = errors;
            else _analysis.Remove(key);
        }
        Recompute();
    }

    private void OnWorkspaceChanged()
    {
        // Neues Projekt geöffnet → alles verwerfen und einmal komplett prüfen
        var root = _ws.RootDirectory;
        if (string.Equals(root, _root, StringComparison.OrdinalIgnoreCase)) return;
        _root = root;
        lock (_gate)
        {
            _analysis = new(StringComparer.OrdinalIgnoreCase);
            _build = new(StringComparer.OrdinalIgnoreCase);
        }
        Recompute();
        if (root is not null) ScheduleScan(1500);
    }

    private void OnFileChanged(string path)
    {
        // Gespeichert (oder von außen geändert): Andere Dateien können dadurch Fehler bekommen oder verlieren
        if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            ScheduleScan(1500);
    }

    private void OnRunChanged()
    {
        var list = _run.Diagnostics;
        var signature = list.Count == 0 ? 0 : HashCode.Combine(list.Count, list[0], list[^1]);
        if (signature == _buildSignature) return;
        _buildSignature = signature;
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in list.Where(d => d.Severity == DiagnosticSeverity.Error && d.File.Length > 0))
        {
            var key = Norm(d.File);
            map[key] = map.GetValueOrDefault(key) + 1;
        }
        lock (_gate)
        {
            _build = map;
            _buildTime = DateTime.UtcNow;
        }
        Recompute();
    }

    // ------------------------------------------------------------------
    // Lösungsweite Analyse
    // ------------------------------------------------------------------

    public void ScheduleScan(int delayMs)
    {
        _scan?.Cancel();
        var cts = _scan = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, cts.Token);
                await _ws.EnsureHighlighterAsync();
                var result = await ScanAsync(cts.Token);
                if (cts.IsCancellationRequested) return;
                lock (_gate) _analysis = result;
                Recompute();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Die Markierung ist eine Hilfe – Fehler der Analyse dürfen nichts stören
            }
        });
    }

    private async Task<Dictionary<string, int>> ScanAsync(CancellationToken ct)
    {
        var dirs = _ws.Solution?.AllProjects.Where(p => p.Project is not null && !_ws.IsUnloaded(p.Path)).Select(p => Path.GetDirectoryName(p.Path)!).ToList()
                   ?? (_ws.RootDirectory is { } r ? new List<string> { r } : new List<string>());
        var open = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var markupErrors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await _host.InvokeOnUiAsync(() =>
        {
            foreach (var d in _ws.Documents)
            {
                open[d.Path] = d.Text;
                // Markup-Fehler offener .razor-Dateien (Razor-Parser) zählen mit
                if (d.Kind == DocumentKind.Razor && d.Diagnostics.Count(x => x.IsError) is > 0 and var n) markupErrors[d.Path] = n;
            }
        });
        var razor = dirs.SelectMany(ProjectIndex.EnumerateFiles)
            .Where(f => f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).Equals("_Imports.razor", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(1500).ToList();

        // 1. Projektionen der .razor-Dateien auf den aktuellen Stand bringen (ohne Änderung kostenlos)
        var projections = new List<(string Path, string Text, BlazorStudio.Core.Razor.RazorProjection Projection)>();
        foreach (var f in razor)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var text = open.TryGetValue(f, out var t) ? t : Workspace.ReadText(f);
                if (text is null) continue;
                var (cls, ns, usings) = _ws.RazorContextFor(f);
                var proj = BlazorStudio.Core.Razor.RazorCSharpProjection.ClassFor(text, cls, ns, usings);
                await _ws.Highlighter.UpsertRazorAsync(f, proj);
                projections.Add((f, text, proj));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
            }
        }

        // 2. Alle .cs-Dateien
        var result = await _ws.Highlighter.DiagnoseAllCSharpAsync(ct);

        // 3. @code-Blöcke der .razor-Dateien
        foreach (var (path, text, proj) in projections)
        {
            ct.ThrowIfCancellationRequested();
            var n = (await _ws.Highlighter.DiagnoseRazorAsync(path, text, proj, ct)).Count;
            n += markupErrors.GetValueOrDefault(path);
            if (n > 0) result[Norm(path)] = n;
        }
        return result.Where(kv => File.Exists(kv.Key)).ToDictionary(kv => Norm(kv.Key), kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------

    private void Recompute()
    {
        Dictionary<string, int> analysis, build;
        DateTime buildTime;
        lock (_gate)
        {
            analysis = _analysis;
            build = _build;
            buildTime = _buildTime;
        }
        var files = new Dictionary<string, int>(analysis, StringComparer.OrdinalIgnoreCase);
        foreach (var (file, n) in build)
        {
            // Build-Fehler gelten nur, solange die Datei seit dem Build nicht geändert wurde
            DateTime written;
            try { written = File.GetLastWriteTimeUtc(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if (written > buildTime) continue;
            files[file] = Math.Max(files.GetValueOrDefault(file), n);
        }
        var dirs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, n) in files)
        {
            for (var d = Path.GetDirectoryName(file); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
                dirs[d] = dirs.GetValueOrDefault(d) + n;
        }
        bool same;
        lock (_gate)
        {
            same = files.Count == _files.Count && files.All(kv => _files.GetValueOrDefault(kv.Key) == kv.Value);
            _files = files;
            _dirs = dirs;
        }
        if (!same) _ = _host.InvokeOnUiAsync(() => Changed?.Invoke());
    }

    public void Dispose()
    {
        _scan?.Cancel();
        _ws.Changed -= OnWorkspaceChanged;
        _ws.FileChangedOnDisk -= OnFileChanged;
        _run.Changed -= OnRunChanged;
    }
}
