using System.Text.Json.Nodes;

namespace BlazorStudio.Core.Debugging;

public enum DebugState { Idle, Starting, Running, Stopped, Exited }

public sealed record StackFrameInfo(int Id, string Name, string? File, int Line, int Column);

public sealed record VariableInfo(string Name, string Value, string? Type, int VariablesReference, string? EvaluateName)
{
    public bool HasChildren => VariablesReference > 0;
}

public sealed record EvaluationResult(string Value, string? Type, int VariablesReference, string? Error = null);

/// <summary>Steuert eine Debug-Sitzung über DAP (Haltepunkte, Schritte, Variablen).</summary>
public sealed class DebugSession : IAsyncDisposable
{
    private readonly Dictionary<string, SortedSet<int>> _breakpoints = new(StringComparer.OrdinalIgnoreCase);
    private DapClient? _client;
    private TaskCompletionSource? _initialized;

    public event Action? Changed;

    /// <summary>(Kategorie, Text) – stdout/stderr/console des Debuggees.</summary>
    public event Action<string, string>? Output;

    public DebugState State { get; private set; } = DebugState.Idle;

    public int? StoppedThreadId { get; private set; }

    public string? StopReason { get; private set; }

    public IReadOnlyList<StackFrameInfo> Frames { get; private set; } = Array.Empty<StackFrameInfo>();

    public StackFrameInfo? CurrentFrame { get; private set; }

    public IReadOnlyList<VariableInfo> Locals { get; private set; } = Array.Empty<VariableInfo>();

    public IReadOnlyDictionary<string, SortedSet<int>> Breakpoints => _breakpoints;

    public bool IsActive => State is DebugState.Starting or DebugState.Running or DebugState.Stopped;

    public bool HasBreakpoint(string file, int line) => _breakpoints.TryGetValue(file, out var s) && s.Contains(line);

    public async Task ToggleBreakpointAsync(string file, int line)
    {
        if (!_breakpoints.TryGetValue(file, out var set)) _breakpoints[file] = set = new SortedSet<int>();
        if (!set.Remove(line)) set.Add(line);
        if (set.Count == 0) _breakpoints.Remove(file);
        Changed?.Invoke();
        if (IsActive) await SendBreakpointsAsync(file);
    }

    public async Task StartAsync(string adapterPath, string programDll, string workingDirectory, IDictionary<string, string> environment, IEnumerable<string>? programArgs = null)
    {
        if (IsActive) throw new InvalidOperationException("Debug session already active.");
        State = DebugState.Starting;
        Frames = Array.Empty<StackFrameInfo>();
        Locals = Array.Empty<VariableInfo>();
        CurrentFrame = null;
        Changed?.Invoke();

        _client = new DapClient();
        _client.EventReceived += OnEvent;
        _client.Log += l => Output?.Invoke("adapter", l);
        _initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.Start(adapterPath, new[] { "--interpreter=vscode" }, workingDirectory);

        await _client.RequestAsync("initialize", new JsonObject
        {
            ["clientID"] = "blazor-studio",
            ["clientName"] = "Blazor Studio",
            ["adapterID"] = "coreclr",
            ["linesStartAt1"] = true,
            ["columnsStartAt1"] = true,
            ["pathFormat"] = "path",
            ["supportsVariableType"] = true,
            ["supportsRunInTerminalRequest"] = false,
            ["locale"] = "de",
        });

        var env = new JsonObject();
        foreach (var kv in environment) env[kv.Key] = kv.Value;
        var args = new JsonArray();
        foreach (var a in programArgs ?? Array.Empty<string>()) args.Add(a);

        var launch = _client.RequestAsync("launch", new JsonObject
        {
            ["name"] = "Blazor Studio",
            ["type"] = "coreclr",
            ["request"] = "launch",
            ["program"] = programDll,
            ["cwd"] = workingDirectory,
            ["env"] = env,
            ["args"] = args,
            ["stopAtEntry"] = false,
            ["justMyCode"] = true,
            ["console"] = "internalConsole",
        }, 60000);

        await Task.WhenAny(_initialized.Task, Task.Delay(15000));
        foreach (var file in _breakpoints.Keys.ToList()) await SendBreakpointsAsync(file);
        try
        {
            await _client.RequestAsync("configurationDone");
        }
        catch (DapException) { }
        await launch;
        if (State == DebugState.Starting) State = DebugState.Running;
        Changed?.Invoke();
    }

    private async Task SendBreakpointsAsync(string file)
    {
        if (_client is null) return;
        var lines = _breakpoints.TryGetValue(file, out var s) ? s.ToList() : new List<int>();
        var bps = new JsonArray();
        foreach (var l in lines) bps.Add(new JsonObject { ["line"] = l });
        try
        {
            await _client.RequestAsync("setBreakpoints", new JsonObject
            {
                ["source"] = new JsonObject { ["path"] = file, ["name"] = Path.GetFileName(file) },
                ["breakpoints"] = bps,
                ["sourceModified"] = false,
            });
        }
        catch (DapException e)
        {
            Output?.Invoke("console", e.Message);
        }
    }

    private void OnEvent(string ev, JsonObject body)
    {
        switch (ev)
        {
            case "initialized":
                _initialized?.TrySetResult();
                break;
            case "stopped":
                StoppedThreadId = body["threadId"]?.GetValue<int>();
                StopReason = body["reason"]?.GetValue<string>();
                State = DebugState.Stopped;
                _ = LoadStoppedStateAsync();
                break;
            case "continued":
                State = DebugState.Running;
                Frames = Array.Empty<StackFrameInfo>();
                CurrentFrame = null;
                Locals = Array.Empty<VariableInfo>();
                Changed?.Invoke();
                break;
            case "output":
                var cat = body["category"]?.GetValue<string>() ?? "console";
                var text = body["output"]?.GetValue<string>() ?? "";
                if (cat != "telemetry") Output?.Invoke(cat, text.TrimEnd('\r', '\n'));
                break;
            case "terminated":
            case "exited":
            case "adapterExited":
                if (State != DebugState.Exited)
                {
                    State = DebugState.Exited;
                    Frames = Array.Empty<StackFrameInfo>();
                    CurrentFrame = null;
                    Locals = Array.Empty<VariableInfo>();
                    Changed?.Invoke();
                }
                break;
        }
    }

    private async Task LoadStoppedStateAsync()
    {
        if (_client is null || StoppedThreadId is null) return;
        try
        {
            var st = await _client.RequestAsync("stackTrace", new JsonObject { ["threadId"] = StoppedThreadId, ["startFrame"] = 0, ["levels"] = 50 });
            var frames = new List<StackFrameInfo>();
            foreach (var f in st["stackFrames"]?.AsArray() ?? new JsonArray())
            {
                if (f is not JsonObject fo) continue;
                frames.Add(new StackFrameInfo(
                    fo["id"]?.GetValue<int>() ?? 0,
                    fo["name"]?.GetValue<string>() ?? "?",
                    fo["source"]?["path"]?.GetValue<string>(),
                    fo["line"]?.GetValue<int>() ?? 0,
                    fo["column"]?.GetValue<int>() ?? 0));
            }
            Frames = frames;
            await SelectFrameAsync(frames.FirstOrDefault(f => f.File is not null) ?? frames.FirstOrDefault());
        }
        catch (Exception e) when (e is DapException or TimeoutException or IOException)
        {
            Output?.Invoke("console", e.Message);
            Changed?.Invoke();
        }
    }

    public async Task SelectFrameAsync(StackFrameInfo? frame)
    {
        CurrentFrame = frame;
        Locals = Array.Empty<VariableInfo>();
        if (frame is not null && _client is not null)
        {
            var scopes = await _client.RequestAsync("scopes", new JsonObject { ["frameId"] = frame.Id });
            var first = scopes["scopes"]?.AsArray().OfType<JsonObject>().FirstOrDefault();
            var reference = first?["variablesReference"]?.GetValue<int>() ?? 0;
            if (reference > 0) Locals = await GetVariablesAsync(reference);
        }
        Changed?.Invoke();
    }

    public async Task<IReadOnlyList<VariableInfo>> GetVariablesAsync(int reference)
    {
        if (_client is null) return Array.Empty<VariableInfo>();
        var body = await _client.RequestAsync("variables", new JsonObject { ["variablesReference"] = reference });
        return (body["variables"]?.AsArray() ?? new JsonArray()).OfType<JsonObject>().Select(v => new VariableInfo(
            v["name"]?.GetValue<string>() ?? "",
            v["value"]?.GetValue<string>() ?? "",
            v["type"]?.GetValue<string>(),
            v["variablesReference"]?.GetValue<int>() ?? 0,
            v["evaluateName"]?.GetValue<string>())).ToList();
    }

    public async Task<EvaluationResult> EvaluateAsync(string expression, string context = "watch")
    {
        if (_client is null || State != DebugState.Stopped) return new EvaluationResult("", null, 0, "not stopped");
        try
        {
            var args = new JsonObject { ["expression"] = expression, ["context"] = context };
            if (CurrentFrame is not null) args["frameId"] = CurrentFrame.Id;
            var r = await _client.RequestAsync("evaluate", args, 10000);
            return new EvaluationResult(r["result"]?.GetValue<string>() ?? "", r["type"]?.GetValue<string>(), r["variablesReference"]?.GetValue<int>() ?? 0);
        }
        catch (Exception e) when (e is DapException or TimeoutException)
        {
            return new EvaluationResult("", null, 0, e.Message);
        }
    }

    public Task ContinueAsync() => ThreadCommand("continue");

    public Task StepOverAsync() => ThreadCommand("next");

    public Task StepIntoAsync() => ThreadCommand("stepIn");

    public Task StepOutAsync() => ThreadCommand("stepOut");

    public Task PauseAsync() => ThreadCommand("pause", requireStopped: false);

    private async Task ThreadCommand(string command, bool requireStopped = true)
    {
        if (_client is null) return;
        if (requireStopped && State != DebugState.Stopped) return;
        var thread = StoppedThreadId ?? 0;
        if (command == "pause" && thread == 0)
        {
            var threads = await _client.RequestAsync("threads");
            thread = threads["threads"]?.AsArray().OfType<JsonObject>().FirstOrDefault()?["id"]?.GetValue<int>() ?? 1;
        }
        try
        {
            await _client.RequestAsync(command, new JsonObject { ["threadId"] = thread });
            if (command != "pause")
            {
                State = DebugState.Running;
                Frames = Array.Empty<StackFrameInfo>();
                CurrentFrame = null;
                Locals = Array.Empty<VariableInfo>();
                Changed?.Invoke();
            }
        }
        catch (DapException e)
        {
            Output?.Invoke("console", e.Message);
        }
    }

    public async Task StopAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
        State = DebugState.Exited;
        Frames = Array.Empty<StackFrameInfo>();
        CurrentFrame = null;
        Locals = Array.Empty<VariableInfo>();
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
