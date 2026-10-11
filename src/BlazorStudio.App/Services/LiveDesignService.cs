using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using BlazorStudio.Core.Build;
using BlazorStudio.Core.Design;
using BlazorStudio.Core.Model;

namespace BlazorStudio.App.Services;

public enum LiveDesignState { Off, Starting, Ready, Rebuilding, Failed }

/// <summary>
/// Echter WYSIWYG-Designer: Das Startprojekt läuft als Schatten-Kopie (mit unsichtbaren Markierungen) über
/// dotnet watch; der Design-Tab zeigt diese echte App, Änderungen kommen per Hot Reload an.
/// </summary>
public sealed partial class LiveDesignService : IDisposable
{
    private readonly Workspace _ws;
    private readonly SettingsService _settings;
    private readonly IHostBridge _host;
    private readonly List<string> _log = new();
    private readonly List<BuildDiagnostic> _errors = new();
    private readonly HashSet<string> _overridden = new(StringComparer.OrdinalIgnoreCase);
    private ProcessRunner? _runner;
    private ShadowProject? _shadow;
    private CancellationTokenSource? _syncDebounce;
    private string? _bridge;

    public LiveDesignService(Workspace ws, SettingsService settings, IHostBridge host)
    {
        _ws = ws;
        _settings = settings;
        _host = host;
        _ws.DocumentContentChanged += OnDocumentChanged;
        _ws.Changed += OnWorkspaceChanged;
        _ws.FileChangedOnDisk += FileChangedOnDisk;
    }

    public event Action? Changed;

    /// <summary>Wird ausgelöst, wenn Hot Reload eine Änderung übernommen hat (Designer kann Auswahl auffrischen).</summary>
    public event Action? Reloaded;

    /// <summary>Der Designer soll die Seite aktualisieren (statische Seiten rendern Hot-Reload-Änderungen nicht von selbst).</summary>
    public event Action? RefreshRequested;

    private CancellationTokenSource? _refreshTimer;
    private string? _logFile;

    /// <summary>Nach einer Änderung zweimal auffrischen: schnell (meist fertig) und zur Sicherheit etwas später.</summary>
    private void ScheduleRefresh()
    {
        _refreshTimer?.Cancel();
        var cts = _refreshTimer = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(900, cts.Token);
                await _host.InvokeOnUiAsync(() => RefreshRequested?.Invoke());
                await Task.Delay(1300, cts.Token);
                await _host.InvokeOnUiAsync(() => RefreshRequested?.Invoke());
            }
            catch (TaskCanceledException)
            {
            }
        });
    }

    public LiveDesignState State { get; private set; } = LiveDesignState.Off;

    /// <summary>Basis-URL der laufenden Schatten-App, z. B. http://127.0.0.1:52344.</summary>
    public string? BaseUrl { get; private set; }

    public string? ProjectFile => _shadow?.ProjectFile;

    public IReadOnlyList<BuildDiagnostic> Errors => _errors;

    public IReadOnlyList<string> Log => _log;

    public bool Enabled => _settings.Current.LiveDesign;

    /// <summary>Klicks gehen an die App (Formulare ausprobieren) statt an den Designer.</summary>
    public bool Interact { get; private set; }

    public void SetInteract(bool on)
    {
        Interact = on;
        Changed?.Invoke();
    }

    /// <summary>Seiten mit Layout (MainLayout, Navigation …) oder nur die Seite selbst anzeigen.</summary>
    public bool ShowLayout => _settings.Current.LiveShowLayout;

    public void SetShowLayout(bool on)
    {
        _settings.Current.LiveShowLayout = on;
        _settings.Save();
        Changed?.Invoke();
    }

    public void SetEnabled(bool on)
    {
        _settings.Current.LiveDesign = on;
        _settings.Save();
        if (on) EnsureStarted();
        else Stop();
        Changed?.Invoke();
    }

    /// <summary>Für den aktuellen Projektstand starten (falls noch nicht läuft).</summary>
    public void EnsureStarted()
    {
        if (!Enabled) return;
        var project = _ws.Project?.ProjectFile;
        if (project is null || !File.Exists(project)) return;
        if (_runner?.IsRunning == true && string.Equals(_shadow?.ProjectFile, Path.GetFullPath(project), StringComparison.OrdinalIgnoreCase)) return;
        _ = StartAsync(project);
    }

    public void Restart()
    {
        Stop();
        EnsureStarted();
    }

    private async Task StartAsync(string projectFile)
    {
        Stop();
        _shadow = new ShadowProject(projectFile)
        {
            BridgeScriptContent = _bridge ??= LoadBridge(),
            PreviewRenderMode = DetectRenderMode(Path.GetDirectoryName(projectFile)!),
        };
        State = LiveDesignState.Starting;
        _errors.Clear();
        _log.Clear();
        Changed?.Invoke();

        var shadow = _shadow;
        _logFile = shadow.ShadowDir + ".watch.log"; // außerhalb des Ordners, sonst beobachtet dotnet watch seine eigene Logdatei
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(shadow.ShadowDir);
                File.WriteAllText(_logFile, $"[{DateTime.Now:HH:mm:ss}] Live-Designer startet für {projectFile}{Environment.NewLine}");
                shadow.SyncAll(CurrentOverrides(shadow));
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Fail(e.Message);
            return;
        }
        if (_shadow != shadow) return;

        KillStale(shadow);
        var port = FreePort();
        BaseUrl = "http://127.0.0.1:" + port;
        _runner = new ProcessRunner();
        _runner.Output += OnOutput;
        var env = new Dictionary<string, string>
        {
            ["DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER"] = "1",
            ["DOTNET_WATCH_RESTART_ON_RUDE_EDIT"] = "1",
            ["DOTNET_WATCH_SUPPRESS_EMOJIS"] = "1",
            ["DOTNET_WATCH_SUPPRESS_MSBUILD_INCREMENTALISM"] = "0",
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["ASPNETCORE_URLS"] = BaseUrl,
            ["DOTNET_CLI_UI_LANGUAGE"] = "en",
            ["BLAZORSTUDIO_DESIGN"] = "1",
        };
        // Nach einem Wiederherstellungsversuch ohne gemeinsamen Compiler-Server (VBCSCompiler) bauen – der hängt sonst mitunter
        if (_noSharedCompilation) env["UseSharedCompilation"] = "false";
        var runner = _runner;
        var task = runner.RunAsync("dotnet", new[] { "watch", "run", "--non-interactive", "--no-launch-profile", "--project", shadow.ShadowProjectFile, "--urls", BaseUrl }, shadow.ShadowDir, env);
        // PID merken, damit eine verwaiste Instanz (Absturz, hart beendet) beim nächsten Start aufgeräumt wird
        if (runner.ProcessId is { } watchPid)
        {
            ChildProcessJob.Add(watchPid); // stirbt mit Blazor Studio, samt der App darunter
            try { File.WriteAllText(PidFile(shadow), watchPid.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            catch (IOException) { }
        }
        _ = task
            .ContinueWith(t =>
            {
                if (_runner != runner) return;
                _ = _host.InvokeOnUiAsync(() =>
                {
                    if (State != LiveDesignState.Off) Fail("dotnet watch beendet (Code " + (t.IsCompletedSuccessfully ? t.Result : -1) + ")");
                });
            }, TaskScheduler.Default);
    }

    public void Stop()
    {
        _runner?.Dispose();
        _runner = null;
        BaseUrl = null;
        if (State != LiveDesignState.Off)
        {
            State = LiveDesignState.Off;
            Changed?.Invoke();
        }
    }

    private void Fail(string message)
    {
        _log.Add(message);
        State = LiveDesignState.Failed;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------
    // Ausgabe von dotnet watch auswerten
    // ------------------------------------------------------------------

    private void OnOutput(string line, bool isError)
    {
        _ = _host.InvokeOnUiAsync(() =>
        {
            _log.Add(line);
            if (_log.Count > 600) _log.RemoveRange(0, 200);
            try { if (_logFile is not null) File.AppendAllText(_logFile, line + Environment.NewLine); }
            catch (IOException) { }
            if (!_objRetried && IsCorruptBuildState(line) && _shadow is { } broken)
            {
                // obj/ kaputt (z. B. Blazor Studio während eines Builds beendet) - einmal leeren und neu starten
                _objRetried = true;
                _noSharedCompilation = true;
                _ = RecoverAsync(broken);
                return;
            }
            var diag = ProcessRunner.ParseDiagnostic(line);
            if (diag is { Severity: DiagnosticSeverity.Error } && !_errors.Any(e => e.File == diag.File && e.Line == diag.Line && e.Code == diag.Code))
            {
                _errors.Add(diag);
                // Fehlerzeilen kommen nach der Meldung "Unable to apply changes" - Banner aktualisieren
                if (State == LiveDesignState.Failed) Changed?.Invoke();
            }

            var l = line.Trim();
            if (l.Contains("Now listening on", StringComparison.OrdinalIgnoreCase) || l.Contains("Application started", StringComparison.OrdinalIgnoreCase))
            {
                var wasRestart = State is LiveDesignState.Rebuilding or LiveDesignState.Failed;
                _errors.Clear();
                _objRetried = false;
                Set(LiveDesignState.Ready);
                if (wasRestart) RefreshRequested?.Invoke();
            }
            else if (l.Contains("File updated", StringComparison.OrdinalIgnoreCase))
            {
                // neuer Versuch: alte Fehler gelten nicht mehr
                _errors.Clear();
            }
            else if (l.Contains("Hot reload succeeded", StringComparison.OrdinalIgnoreCase) || l.Contains("Hot reload of changes succeeded", StringComparison.OrdinalIgnoreCase)
                     || l.Contains("Hot reload change handled", StringComparison.OrdinalIgnoreCase)
                     || l.Contains("changes applied", StringComparison.OrdinalIgnoreCase) || l.Contains("No C# changes to apply", StringComparison.OrdinalIgnoreCase))
            {
                _errors.Clear();
                Set(LiveDesignState.Ready);
                Reloaded?.Invoke();
                RefreshRequested?.Invoke();
            }
            else if (l.Contains("Fix the error to continue", StringComparison.OrdinalIgnoreCase) || l.Contains("Build failed", StringComparison.OrdinalIgnoreCase)
                     || l.Contains("Unable to apply changes", StringComparison.OrdinalIgnoreCase)
                     || l.Contains("Waiting for a file to change before restarting", StringComparison.OrdinalIgnoreCase) && _errors.Count > 0)
            {
                Set(LiveDesignState.Failed);
            }
            else if (l.Contains("Building", StringComparison.OrdinalIgnoreCase) && State == LiveDesignState.Ready || l.Contains("Restarting", StringComparison.OrdinalIgnoreCase))
            {
                Set(LiveDesignState.Rebuilding);
            }
        });
    }

    private bool _objRetried;
    private bool _noSharedCompilation;

    /// <summary>Fehler, die nicht am Code liegen, sondern an halb geschriebenen Build-Dateien im Spiegel (CSC : error CS2001 … obj\…).</summary>
    private static bool IsCorruptBuildState(string line) =>
        CorruptBuildRx().IsMatch(line) && (line.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase) || line.Contains("/obj/", StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"\berror (CS2001|CS0006|MSB3030|MSB3491|MSB4018|NETSDK1004)\b")]
    private static partial Regex CorruptBuildRx();

    private async Task RecoverAsync(ShadowProject shadow)
    {
        _log.Add("Build-Zwischenstand beschädigt – obj wird geleert und der Live-Designer neu gestartet …");
        Stop();
        await Task.Run(() =>
        {
            var obj = Path.Combine(shadow.ShadowDir, "obj");
            for (var i = 0; i < 5 && Directory.Exists(obj); i++)
            {
                try { Directory.Delete(obj, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(400); }
            }
        });
        EnsureStarted();
    }

    private void Set(LiveDesignState s)
    {
        if (State == s && s != LiveDesignState.Failed) return;
        State = s;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------
    // Abgleich mit Editor-Inhalten
    // ------------------------------------------------------------------

    /// <summary>Präfix der Markierungs-Ids für eine Datei.</summary>
    public string? PrefixFor(string path) => _shadow is { } s && s.Contains(path) ? DesignInstrumenter.PrefixFor(s.Relative(path)) : null;

    private Dictionary<string, string> CurrentOverrides(ShadowProject shadow)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in _ws.Documents.ToList())
        {
            if (!shadow.Contains(d.Path)) continue;
            map[d.Path] = ShadowText(shadow, d);
            _overridden.Add(d.Path);
        }
        return map;
    }

    /// <summary>Text, der in die Schatten-Kopie geht: bei .razor instrumentiert, sonst der Editor-Inhalt.</summary>
    private string ShadowText(ShadowProject shadow, DocumentSession d)
    {
        if (d.Kind != DocumentKind.Razor || d.Doc is null || d.HasErrors) return d.Text;
        try
        {
            return DesignInstrumenter.Instrument(d.Text, d.Doc, d.Spans, DesignInstrumenter.PrefixFor(shadow.Relative(d.Path)), RenderFragmentParameters);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return d.Text;
        }
    }

    private IReadOnlyCollection<string>? RenderFragmentParameters(string component) => _ws.FragmentsOf(component);

    private void OnDocumentChanged(DocumentSession doc)
    {
        if (_shadow is null || !_shadow.Contains(doc.Path)) return;
        // kurz entprellen – Tippen erzeugt viele Änderungen
        _syncDebounce?.Cancel();
        var cts = _syncDebounce = new CancellationTokenSource();
        var shadow = _shadow;
        _ = Task.Delay(140, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            _ = _host.InvokeOnUiAsync(() =>
            {
                if (_shadow != shadow) return;
                var text = ShadowText(shadow, doc);
                _overridden.Add(doc.Path);
                _ = Task.Run(() =>
                {
                    if (shadow.WriteText(doc.Path, text)) _ = _host.InvokeOnUiAsync(ScheduleRefresh);
                });
            });
        }, TaskScheduler.Default);
    }

    private void OnWorkspaceChanged()
    {
        if (_shadow is null) return;
        // Projektwechsel → neu starten
        if (_ws.Project?.ProjectFile is { } pf && !string.Equals(Path.GetFullPath(pf), _shadow.ProjectFile, StringComparison.OrdinalIgnoreCase))
        {
            if (Enabled && _runner is not null) Restart();
            return;
        }
        if (_ws.Project is null)
        {
            Stop();
            return;
        }
        // Geschlossene Dokumente wieder auf den Stand der Festplatte bringen
        var open = _ws.Documents.Select(d => d.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _overridden.Where(p => !open.Contains(p)).ToList())
        {
            _overridden.Remove(p);
            var shadow = _shadow;
            _ = Task.Run(() => shadow.Restore(p));
        }
    }

    /// <summary>Datei auf der Festplatte geändert (nicht über den Editor) – in den Spiegel übernehmen.</summary>
    public void FileChangedOnDisk(string path)
    {
        if (_shadow is null || _overridden.Contains(path) || !_shadow.Contains(path)) return;
        var shadow = _shadow;
        _ = Task.Run(() =>
        {
            if (shadow.Restore(path)) _ = _host.InvokeOnUiAsync(ScheduleRefresh);
        });
    }

    // ------------------------------------------------------------------
    // URLs
    // ------------------------------------------------------------------

    /// <summary>URL, unter der ein Dokument im Live-Designer angezeigt wird (Seite über ihre Route, sonst Vorschau-Seite).</summary>
    public string? UrlFor(DocumentSession doc)
    {
        if (BaseUrl is null || doc.Doc is null) return null;
        if (doc.Doc.PageRoute is { } route && ShowLayout) return BaseUrl + SampleRoute(route);
        // ohne Layout: die Seite als einzelne Komponente rendern (Provider der Bibliotheken bleiben aktiv)
        var (cls, ns, _) = _ws.RazorContextFor(doc);
        return BaseUrl + "/_bs/preview?c=" + Uri.EscapeDataString(ns + "." + cls);
    }

    /// <summary>URL für eine Komponenten-Vorschau (Tooltips in der Toolbox).</summary>
    public string? PreviewUrl(string fullTypeName, IReadOnlyDictionary<string, string>? parameters = null, string? text = null)
    {
        if (BaseUrl is null || State != LiveDesignState.Ready) return null;
        var url = BaseUrl + "/_bs/preview?c=" + Uri.EscapeDataString(fullTypeName);
        if (parameters is { Count: > 0 })
        {
            var json = System.Text.Json.JsonSerializer.Serialize(parameters);
            url += "&p=" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        if (!string.IsNullOrEmpty(text)) url += "&t=" + Uri.EscapeDataString(text);
        return url;
    }

    /// <summary>Route mit Beispielwerten für Parameter: /product/{id:int} → /product/1.</summary>
    public static string SampleRoute(string route)
    {
        var r = RouteParam().Replace(route, m =>
        {
            var constraint = m.Groups["c"].Value;
            if (m.Value.Contains('?')) return "";
            return constraint switch
            {
                "int" or "long" or "decimal" or "double" or "float" => "1",
                "bool" => "true",
                "guid" => Guid.Empty.ToString(),
                "datetime" => "2026-01-01",
                _ => "beispiel",
            };
        });
        r = r.Replace("//", "/");
        return r.Length > 1 ? r.TrimEnd('/') : "/";
    }

    [GeneratedRegex(@"\{\*?(?<n>\w+)(?::(?<c>\w+))?[^}]*\}")]
    private static partial Regex RouteParam();

    private static string? DetectRenderMode(string projectDir)
    {
        try
        {
            var program = Path.Combine(projectDir, "Program.cs");
            var text = File.Exists(program) ? File.ReadAllText(program) : "";
            if (text.Contains("AddInteractiveServerComponents", StringComparison.Ordinal)) return "Microsoft.AspNetCore.Components.Web.RenderMode.InteractiveServer";
        }
        catch (IOException) { }
        return null;
    }

    private static string PidFile(ShadowProject shadow) => shadow.ShadowDir + ".watch.pid";

    private static void KillStale(ShadowProject shadow)
    {
        // Alles, was noch aus dem Schatten-Ordner läuft (z. B. die App nach einem Absturz), beenden – sonst ist die exe gesperrt
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                if (p.ProcessName is "dotnet" or "Idle" or "System") continue;
                var file = p.MainModule?.FileName;
                if (file is not null && file.StartsWith(shadow.ShadowDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) p.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
            }
            finally
            {
                p.Dispose();
            }
        }
        try
        {
            var file = PidFile(shadow);
            if (!File.Exists(file) || !int.TryParse(File.ReadAllText(file), out var pid)) return;
            File.Delete(file);
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            if (p.ProcessName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)) p.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string LoadBridge()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "wwwroot", "js", "design-bridge.js");
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    public void Dispose()
    {
        _ws.DocumentContentChanged -= OnDocumentChanged;
        _ws.Changed -= OnWorkspaceChanged;
        _ws.FileChangedOnDisk -= FileChangedOnDisk;
        Stop();
    }
}
