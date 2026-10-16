using BlazorStudio.Core.Build;

namespace BlazorStudio.App.Services;

public enum RunState { Idle, Building, Running }

public enum BuildAction { Build, Rebuild, Clean, CleanBuild, Restore }

public enum OutputKind { Normal, Error, Warning, Success, Command, Muted }

public sealed record OutputLine(string Text, OutputKind Kind, DateTime Time);

/// <summary>dotnet build / watch / run und die Ausgabe dazu.</summary>
public sealed class RunService
{
    private const int MaxLines = 4000;
    private readonly Workspace _ws;
    private readonly SettingsService _settings;
    private readonly Localizer _l;
    private readonly IHostBridge _host;
    private readonly List<OutputLine> _lines = new();
    private readonly List<BuildDiagnostic> _diagnostics = new();
    private ProcessRunner? _runner;
    private ProcessRunner? _buildRunner;

    public RunService(Workspace ws, SettingsService settings, Localizer l, IHostBridge host)
    {
        _ws = ws;
        _settings = settings;
        _l = l;
        _host = host;
    }

    public RunState State { get; private set; } = RunState.Idle;

    public string? Url { get; private set; }

    public bool HotReload { get; private set; }

    public IReadOnlyList<OutputLine> Lines => _lines;

    public IReadOnlyList<BuildDiagnostic> Diagnostics => _diagnostics;

    public int ErrorCount => _diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);

    public int WarningCount => _diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);

    public event Action? Changed;

    /// <summary>Wird bei jeder neuen Zeile ausgelöst (für Auto-Scroll).</summary>
    public event Action? OutputAdded;

    public void ClearOutput()
    {
        _lines.Clear();
        Changed?.Invoke();
    }

    public void Write(string text, OutputKind kind = OutputKind.Normal)
    {
        _ = _host.InvokeOnUiAsync(() =>
        {
            _lines.Add(new OutputLine(text, kind, DateTime.Now));
            if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
            OutputAdded?.Invoke();
        });
    }

    private void OnProcessLine(string line, bool isError)
    {
        var diag = ProcessRunner.ParseDiagnostic(line);
        var kind = diag?.Severity switch
        {
            DiagnosticSeverity.Error => OutputKind.Error,
            DiagnosticSeverity.Warning => OutputKind.Warning,
            _ => isError ? OutputKind.Error : Classify(line),
        };
        Write(line, kind);
        var url = ProcessRunner.ParseListeningUrl(line);
        _ = _host.InvokeOnUiAsync(() =>
        {
            if (diag is not null && !_diagnostics.Any(d => d.File == diag.File && d.Line == diag.Line && d.Code == diag.Code))
                _diagnostics.Add(diag);
            if (url is not null && (Url is null || url.StartsWith("https", StringComparison.OrdinalIgnoreCase)))
                Url = url.Replace("0.0.0.0", "localhost").Replace("[::]", "localhost");
            if (diag is not null || url is not null) Changed?.Invoke();
        });
    }

    private static OutputKind Classify(string line)
    {
        var l = line.TrimStart();
        if (l.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase) || l.Contains("Hot reload", StringComparison.OrdinalIgnoreCase) && l.Contains("applied", StringComparison.OrdinalIgnoreCase)) return OutputKind.Success;
        if (l.StartsWith("dotnet watch", StringComparison.OrdinalIgnoreCase)) return OutputKind.Muted;
        if (l.Contains("Build FAILED", StringComparison.OrdinalIgnoreCase)) return OutputKind.Error;
        return OutputKind.Normal;
    }

    private string? ProjectFile => _ws.Project?.ProjectFile;

    public async Task<bool> BuildAsync()
    {
        if (ProjectFile is null || State == RunState.Building) return false;
        _ws.SaveAll();
        var wasRunning = State;
        State = RunState.Building;
        _diagnostics.Clear();
        Changed?.Invoke();
        Write("❯ dotnet build", OutputKind.Command);
        _buildRunner = new ProcessRunner();
        _buildRunner.Output += OnProcessLine;
        int code;
        try
        {
            code = await _buildRunner.RunAsync("dotnet", new[] { "build", ProjectFile, "-nologo", "-v", "q", "-clp:NoSummary" }, Path.GetDirectoryName(ProjectFile)!);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Write(_l["run.dotnetMissing"], OutputKind.Error);
            code = -1;
        }
        finally
        {
            _buildRunner.Dispose();
            _buildRunner = null;
        }
        Write(code == 0 ? "✓ " + _l["run.buildOk"] : "✗ " + _l["run.buildFailed"], code == 0 ? OutputKind.Success : OutputKind.Error);
        State = wasRunning == RunState.Running && _runner?.IsRunning == true ? RunState.Running : RunState.Idle;
        Changed?.Invoke();
        return code == 0;
    }

    /// <summary>Startet die App mit dotnet watch (Hot Reload) oder dotnet run.</summary>
    public async Task StartAsync(bool? hotReload = null)
    {
        if (ProjectFile is null || _runner?.IsRunning == true) return;
        _ws.SaveAll();
        HotReload = hotReload ?? _settings.Current.UseHotReload;
        _diagnostics.Clear();
        Url = null;
        State = RunState.Running;
        Changed?.Invoke();

        var dir = Path.GetDirectoryName(ProjectFile)!;
        var args = HotReload
            ? new[] { "watch", "--non-interactive", "--project", ProjectFile }
            : new[] { "run", "--project", ProjectFile };
        Write("❯ dotnet " + string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)), OutputKind.Command);

        _runner?.Dispose();
        var runner = _runner = new ProcessRunner();
        runner.Output += OnProcessLine;
        runner.Exited += code => _ = _host.InvokeOnUiAsync(() => OnRunnerExited(runner, code));
        try
        {
            var env = new Dictionary<string, string> { ["DOTNET_WATCH_RESTART_ON_RUDE_EDIT"] = "true", ["DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER"] = "1" };
            _ = runner.RunAsync("dotnet", args, dir, env);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Write(_l["run.dotnetMissing"], OutputKind.Error);
            State = RunState.Idle;
            Changed?.Invoke();
        }
        await Task.CompletedTask;
    }

    /// <summary>Ende des Laufs – nur für den aktuellen Runner (ein spät gemeldetes Ende eines alten Laufs darf den neuen nicht beenden).</summary>
    private void OnRunnerExited(ProcessRunner runner, int? code)
    {
        if (!ReferenceEquals(runner, _runner) || State != RunState.Running) return;
        Write(code is { } c ? _l.F("run.exited", c) : _l["run.stopped"], code is null or 0 ? OutputKind.Muted : OutputKind.Warning);
        State = RunState.Idle;
        Url = null;
        Changed?.Invoke();
    }

    public void Stop()
    {
        var runner = _runner;
        runner?.Stop();
        _buildRunner?.Stop();
        if (runner is null) return;
        // Sicherheitsnetz: Kommt das Ende nicht (hängende Pipes), nach kurzer Zeit trotzdem auf „bereit“ –
        // sonst ließe sich Run erst nach einem Neustart von Studio wieder drücken
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 30 && runner.IsRunning; i++) await Task.Delay(100);
            await Task.Delay(1500);
            await _host.InvokeOnUiAsync(() => OnRunnerExited(runner, null));
        });
    }

    public async Task RestartAsync()
    {
        var hot = HotReload;
        Stop();
        for (var i = 0; i < 40 && _runner?.IsRunning == true; i++) await Task.Delay(100);
        await StartAsync(hot);
    }

    public void StopAll()
    {
        _runner?.Dispose();
        _buildRunner?.Dispose();
    }

    /// <summary>Für den Debugger: Url aus launchSettings übernehmen.</summary>
    public void SetUrl(string? url)
    {
        Url = url;
        Changed?.Invoke();
    }

    public void ClearDiagnostics()
    {
        _diagnostics.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// Build-Aktion für ein Projekt (.csproj) oder die ganze Solution (.sln/.slnx), wie im Kontextmenü von Rider/VS.
    /// Fehler und Warnungen landen im Problems-Panel.
    /// </summary>
    public async Task<bool> RunBuildActionAsync(string target, BuildAction action)
    {
        if (State == RunState.Building) return false;
        _ws.SaveAll();
        var wasRunning = State;
        State = RunState.Building;
        _diagnostics.Clear();
        Changed?.Invoke();
        var name = Path.GetFileName(target);
        var steps = action switch
        {
            BuildAction.Build => new[] { new[] { "build", target, "-nologo", "-v", "q", "-clp:NoSummary" } },
            BuildAction.Rebuild => new[] { new[] { "build", target, "--no-incremental", "-nologo", "-v", "q", "-clp:NoSummary" } },
            BuildAction.Clean => new[] { new[] { "clean", target, "-nologo", "-v", "q" } },
            BuildAction.CleanBuild => new[] { new[] { "clean", target, "-nologo", "-v", "q" }, new[] { "build", target, "-nologo", "-v", "q", "-clp:NoSummary" } },
            BuildAction.Restore => new[] { new[] { "restore", target, "-nologo", "-v", "q" } },
            _ => Array.Empty<string[]>(),
        };
        var code = 0;
        var started = DateTime.Now;
        try
        {
            foreach (var args in steps)
            {
                Write("❯ dotnet " + string.Join(' ', args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a)), OutputKind.Command);
                _buildRunner = new ProcessRunner();
                _buildRunner.Output += OnProcessLine;
                try
                {
                    code = await _buildRunner.RunAsync("dotnet", args, Path.GetDirectoryName(target)!);
                }
                finally
                {
                    _buildRunner.Dispose();
                    _buildRunner = null;
                }
                if (code != 0) break;
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Write(_l["run.dotnetMissing"], OutputKind.Error);
            code = -1;
        }
        var secs = (DateTime.Now - started).TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture);
        var label = _l["build." + action];
        Write(code == 0 ? $"✓ {label}: {name} ({secs} s)" : $"✗ {label}: {name} – {_l["run.buildFailed"]}", code == 0 ? OutputKind.Success : OutputKind.Error);
        State = wasRunning == RunState.Running && _runner?.IsRunning == true ? RunState.Running : RunState.Idle;
        Changed?.Invoke();
        return code == 0;
    }

    /// <summary>Befehl im Projektordner ausführen (z. B. dotnet new) und Ausgabe zeigen.</summary>
    public async Task<int> ExecAsync(string workingDir, params string[] args)
    {
        Write("❯ dotnet " + string.Join(' ', args), OutputKind.Command);
        using var r = new ProcessRunner();
        r.Output += OnProcessLine;
        try
        {
            return await r.RunAsync("dotnet", args, workingDir);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Write(_l["run.dotnetMissing"], OutputKind.Error);
            return -1;
        }
    }
}
