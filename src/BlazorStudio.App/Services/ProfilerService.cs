using System.Text.RegularExpressions;
using BlazorStudio.Core.Profiling;
using BlazorStudio.Core.Project;

namespace BlazorStudio.App.Services;

/// <summary>CPU- und Speicheranalyse der laufenden App (EventPipe).</summary>
public sealed partial class ProfilerService : IDisposable
{
    public const int HistoryLength = 90;

    private readonly Workspace _ws;
    private readonly UiService _ui;
    private readonly Localizer _l;
    private readonly IHostBridge _host;
    private readonly Dictionary<string, List<double>> _history = new();
    private readonly Dictionary<string, (string File, int Line)?> _sourceCache = new();
    private LiveCounters? _live;
    private CancellationTokenSource? _recordCts;

    public ProfilerService(Workspace ws, UiService ui, Localizer l, IHostBridge host)
    {
        _ws = ws;
        _ui = ui;
        _l = l;
        _host = host;
    }

    public IReadOnlyList<DotNetProcess> Processes { get; private set; } = Array.Empty<DotNetProcess>();

    public int? AttachedPid => _live?.ProcessId;

    public string? AttachedName { get; private set; }

    public bool IsRecording { get; private set; }

    public DateTime? RecordingUntil { get; private set; }

    public bool IsAnalyzing { get; private set; }

    public ProfileReport? Report { get; private set; }

    public string? LastTrace { get; private set; }

    public string? Error { get; private set; }

    public event Action? Changed;

    /// <summary>Wird einmal pro Live-Zähler-Takt ausgelöst (gedrosselt).</summary>
    public event Action? CountersUpdated;

    public IReadOnlyDictionary<string, List<double>> History => _history;

    public double? Latest(string counter) => _history.TryGetValue(counter, out var h) && h.Count > 0 ? h[^1] : null;

    /// <summary>Assemblynamen aller Projekte der Mappe = „eigener Code“.</summary>
    public HashSet<string> UserModules =>
        (_ws.Solution?.AllProjects.Select(p => p.Project?.AssemblyName ?? p.Name) ?? Enumerable.Empty<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public void RefreshProcesses()
    {
        Processes = ProcessDiscovery.List();
        Changed?.Invoke();
    }

    /// <summary>Prozess des Startprojekts (Name = AssemblyName bzw. dotnet mit DLL).</summary>
    public DotNetProcess? Suggested
    {
        get
        {
            var asm = _ws.StartupItem?.Project?.AssemblyName ?? _ws.Project?.Name;
            if (asm is null) return Processes.FirstOrDefault();
            return Processes.FirstOrDefault(p => p.Name.Equals(asm, StringComparison.OrdinalIgnoreCase))
                   ?? Processes.FirstOrDefault(p => p.MainModule?.Contains(asm, StringComparison.OrdinalIgnoreCase) == true && p.Name != "dotnet")
                   ?? Processes.FirstOrDefault(p => !p.Name.Equals("dotnet", StringComparison.OrdinalIgnoreCase) && !p.Name.Contains("BlazorStudio", StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Attach(DotNetProcess p)
    {
        Detach();
        Error = null;
        _history.Clear();
        _live = new LiveCounters();
        var lastTick = DateTime.MinValue;
        _live.Sample += (name, value) =>
        {
            lock (_history)
            {
                if (!_history.TryGetValue(name, out var h)) _history[name] = h = new List<double>();
                h.Add(value);
                if (h.Count > HistoryLength) h.RemoveAt(0);
            }
            if (DateTime.UtcNow - lastTick > TimeSpan.FromMilliseconds(700))
            {
                lastTick = DateTime.UtcNow;
                _ = _host.InvokeOnUiAsync(() => CountersUpdated?.Invoke());
            }
        };
        _live.Stopped += msg => _ = _host.InvokeOnUiAsync(() =>
        {
            if (msg.Length > 0) Error = msg;
            Changed?.Invoke();
        });
        try
        {
            _live.Start(p.Id);
            AttachedName = $"{p.Name} ({p.Id})";
        }
        catch (Exception e)
        {
            Error = e.Message;
            _live = null;
        }
        Changed?.Invoke();
    }

    public void Detach()
    {
        _live?.Dispose();
        _live = null;
        AttachedName = null;
        Changed?.Invoke();
    }

    public async Task RecordAsync(int seconds, bool cpu, bool allocations, bool hideIdle)
    {
        if (AttachedPid is not { } pid || IsRecording) return;
        Error = null;
        IsRecording = true;
        RecordingUntil = DateTime.Now.AddSeconds(seconds);
        _recordCts = new CancellationTokenSource();
        Changed?.Invoke();
        var dir = Path.Combine(Path.GetTempPath(), "BlazorStudio", "traces");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"trace-{pid}-{DateTime.Now:yyyyMMdd-HHmmss}.nettrace");
        try
        {
            await Task.Run(() => TraceRecorder.RecordAsync(pid, TimeSpan.FromSeconds(seconds), cpu, allocations, file, _recordCts.Token));
            IsRecording = false;
            IsAnalyzing = true;
            Changed?.Invoke();
            var modules = UserModules;
            Report = await Task.Run(() => TraceRecorder.Analyze(file, modules, hideIdle));
            LastTrace = file;
        }
        catch (Exception e)
        {
            Error = e.Message;
            _ui.ShowToast(_l.F("prof.recordFailed", e.Message), ToastKind.Error, 7000);
        }
        finally
        {
            IsRecording = false;
            IsAnalyzing = false;
            RecordingUntil = null;
            Changed?.Invoke();
        }
    }

    public void StopRecording() => _recordCts?.Cancel();

    public async Task ReanalyzeAsync(bool hideIdle)
    {
        if (LastTrace is null || IsAnalyzing) return;
        IsAnalyzing = true;
        Changed?.Invoke();
        var modules = UserModules;
        try { Report = await Task.Run(() => TraceRecorder.Analyze(LastTrace, modules, hideIdle)); }
        catch (Exception e) { Error = e.Message; }
        IsAnalyzing = false;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------
    // Methode → Quelltext
    // ------------------------------------------------------------------

    /// <summary>Sucht die Deklaration einer Methode wie „Shop.Pages.Home.AddToCart(class Product)“ in der Mappe.</summary>
    public (string File, int Line)? Locate(string fullMethod)
    {
        if (_sourceCache.TryGetValue(fullMethod, out var cached)) return cached;
        var m = MethodRegex().Match(fullMethod);
        (string, int)? result = null;
        if (m.Success && _ws.RootDirectory is { } root)
        {
            var type = m.Groups["type"].Value;
            var cls = type.Split('.', '+').Last();
            var method = m.Groups["method"].Value;
            // Lambdas/State-Machines: <AddAsync>b__0 / <AddAsync>d__5 → AddAsync
            var inner = InnerNameRegex().Match(method + " " + cls);
            if (inner.Success) method = inner.Groups[1].Value;
            if (cls.StartsWith('<')) cls = type.Split('.', '+').Reverse().Skip(1).FirstOrDefault() ?? cls;
            if (method is ".ctor") method = cls;
            var files = ProjectIndex.EnumerateFiles(root).Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase));
            var decl = new Regex($@"\b{Regex.Escape(method)}\s*(<[^>]*>)?\s*\(");
            foreach (var f in files.OrderBy(f => Path.GetFileNameWithoutExtension(f).StartsWith(cls, StringComparison.OrdinalIgnoreCase) ? 0 : 1))
            {
                var isComponent = f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) && Path.GetFileNameWithoutExtension(f).Equals(cls, StringComparison.OrdinalIgnoreCase);
                var text = Workspace.ReadText(f);
                if (text is null) continue;
                if (!isComponent && !Regex.IsMatch(text, $@"\b(class|record|struct)\s+{Regex.Escape(cls)}\b")) continue;
                var match = decl.Matches(text).FirstOrDefault(x => LooksLikeDeclaration(text, x.Index));
                if (match is null) continue;
                result = (f, text.Take(match.Index).Count(c => c == '\n') + 1);
                break;
            }
        }
        _sourceCache[fullMethod] = result;
        return result;
    }

    private static bool LooksLikeDeclaration(string text, int index)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var prefix = text[lineStart..index];
        return Regex.IsMatch(prefix, @"\b(void|Task|async|public|private|protected|internal|static|override|[A-Z]\w*(<.*>)?\??)\s+$");
    }

    [GeneratedRegex(@"^(?<type>[\w.+`<>\[\]]+)\.(?<method>[\w<>`.]+)\(")]
    private static partial Regex MethodRegex();

    [GeneratedRegex(@"<(\w+)>")]
    private static partial Regex InnerNameRegex();

    public void Dispose() => Detach();
}
