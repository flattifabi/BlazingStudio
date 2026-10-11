using BlazorStudio.Core.Build;
using BlazorStudio.Core.Debugging;

namespace BlazorStudio.App.Services;

public sealed record WatchItem(string Expression, EvaluationResult? Result);

/// <summary>Debuggen der Blazor-App über netcoredbg (DAP).</summary>
public sealed class DebugService
{
    private readonly Workspace _ws;
    private readonly RunService _run;
    private readonly SettingsService _settings;
    private readonly Localizer _l;
    private readonly UiService _ui;
    private readonly IHostBridge _host;
    private readonly List<WatchItem> _watches = new();

    public DebugService(Workspace ws, RunService run, SettingsService settings, Localizer l, UiService ui, IHostBridge host)
    {
        _ws = ws;
        _run = run;
        _settings = settings;
        _l = l;
        _ui = ui;
        _host = host;
        Session.Changed += () => _ = _host.InvokeOnUiAsync(OnSessionChanged);
        Session.Output += (cat, text) =>
        {
            if (cat == "adapter") return;
            _run.Write(text, cat == "stderr" ? OutputKind.Error : OutputKind.Normal);
            var url = ProcessRunner.ParseListeningUrl(text);
            if (url is not null) _ = _host.InvokeOnUiAsync(() => _run.SetUrl(url.Replace("0.0.0.0", "localhost")));
        };
    }

    public DebugSession Session { get; } = new();

    public DebugState State => Session.State;

    public bool IsActive => Session.IsActive;

    public bool IsStopped => Session.State == DebugState.Stopped;

    public IReadOnlyList<WatchItem> Watches => _watches;

    public event Action? Changed;

    private StackFrameInfo? _lastShownFrame;

    private void OnSessionChanged()
    {
        if (Session.State == DebugState.Stopped && Session.CurrentFrame is { File: not null } f && f != _lastShownFrame)
        {
            _lastShownFrame = f;
            if (File.Exists(f.File)) _ws.Open(f.File, f.Line);
            _ = RefreshWatchesAsync();
        }
        if (Session.State != DebugState.Stopped) _lastShownFrame = null;
        if (Session.State == DebugState.Exited) _run.SetUrl(null);
        Changed?.Invoke();
    }

    /// <summary>netcoredbg suchen: Einstellung, %LOCALAPPDATA%\BlazorStudio\netcoredbg, PATH.</summary>
    public string? FindDebugger()
    {
        var candidates = new List<string?>
        {
            _settings.Current.DebuggerPath,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlazorStudio", "netcoredbg", "netcoredbg.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".netcoredbg", "netcoredbg.exe"),
            Path.Combine(AppContext.BaseDirectory, "netcoredbg", "netcoredbg.exe"),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length > 0) candidates.Add(Path.Combine(dir.Trim(), "netcoredbg.exe"));
        }
        return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c));
    }

    public async Task StartAsync()
    {
        var csproj = _ws.Project?.ProjectFile;
        if (csproj is null || IsActive) return;

        var debugger = FindDebugger();
        if (debugger is null)
        {
            await _ui.ShowDialogAsync(new DialogRequest
            {
                Title = _l["debug.missingTitle"],
                Message = _l["debug.missingText"],
                OkText = _l["action.ok"],
            });
            _ui.OpenOverlay("settings");
            return;
        }

        if (ProjectInfo.IsWebAssembly(csproj))
        {
            _ui.ShowToast(_l["debug.wasmHint"], ToastKind.Warning, 7000);
        }

        if (_run.State == RunState.Running) _run.Stop();
        if (!await _run.BuildAsync()) return;

        var dll = ProjectInfo.OutputDll(csproj);
        if (dll is null || !File.Exists(dll))
        {
            _ui.ShowToast(_l["debug.noDll"], ToastKind.Error);
            return;
        }

        var profile = ProjectInfo.LaunchProfiles(csproj).FirstOrDefault(p => p.Name.Contains("https", StringComparison.OrdinalIgnoreCase))
                      ?? ProjectInfo.LaunchProfiles(csproj).FirstOrDefault();
        var env = new Dictionary<string, string>(profile?.Environment ?? new Dictionary<string, string>());
        env.TryAdd("ASPNETCORE_ENVIRONMENT", "Development");
        if (profile?.ApplicationUrl is { } urls) env["ASPNETCORE_URLS"] = urls;

        _run.Write("❯ netcoredbg " + Path.GetFileName(dll), OutputKind.Command);
        try
        {
            await Session.StartAsync(debugger, dll, Path.GetDirectoryName(csproj)!, env);
            var first = profile?.ApplicationUrl?.Split(';').FirstOrDefault(u => u.StartsWith("https", StringComparison.OrdinalIgnoreCase))
                        ?? profile?.ApplicationUrl?.Split(';').FirstOrDefault();
            if (first is not null) _run.SetUrl(first);
        }
        catch (Exception e) when (e is DapException or TimeoutException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _run.Write(e.Message, OutputKind.Error);
            _ui.ShowToast(_l.F("debug.startFailed", e.Message), ToastKind.Error, 7000);
            await Session.StopAsync();
        }
        Changed?.Invoke();
    }

    public async Task StopAsync()
    {
        if (Session.State is DebugState.Idle or DebugState.Exited) return;
        await Session.StopAsync();
        _run.SetUrl(null);
        Changed?.Invoke();
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
    }

    public Task ToggleBreakpointAsync(string file, int line) => Session.ToggleBreakpointAsync(file, line);

    public void AddWatch(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return;
        _watches.Add(new WatchItem(expression.Trim(), null));
        _ = RefreshWatchesAsync();
        Changed?.Invoke();
    }

    public void RemoveWatch(WatchItem item)
    {
        _watches.Remove(item);
        Changed?.Invoke();
    }

    public async Task RefreshWatchesAsync()
    {
        if (!IsStopped) return;
        for (var i = 0; i < _watches.Count; i++)
            _watches[i] = _watches[i] with { Result = await Session.EvaluateAsync(_watches[i].Expression) };
        Changed?.Invoke();
    }
}
