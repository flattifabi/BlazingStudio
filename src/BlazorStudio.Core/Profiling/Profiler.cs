using System.Diagnostics;
using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace BlazorStudio.Core.Profiling;

public sealed record DotNetProcess(int Id, string Name, string? MainModule, DateTime? StartTime);

/// <summary>Laufende .NET-Prozesse, die Diagnose (EventPipe) erlauben.</summary>
public static class ProcessDiscovery
{
    public static IReadOnlyList<DotNetProcess> List()
    {
        var list = new List<DotNetProcess>();
        IEnumerable<int> ids;
        try { ids = DiagnosticsClient.GetPublishedProcesses(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return list; }
        var self = Environment.ProcessId;
        foreach (var id in ids)
        {
            if (id == self) continue;
            try
            {
                using var p = Process.GetProcessById(id);
                string? module = null;
                DateTime? start = null;
                try { module = p.MainModule?.FileName; start = p.StartTime; } catch (Exception) { }
                list.Add(new DotNetProcess(id, p.ProcessName, module, start));
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        return list.OrderByDescending(p => p.StartTime).ToList();
    }
}

// ======================================================================
// Live-Zähler (System.Runtime EventCounters)
// ======================================================================

public sealed class LiveCounters : IDisposable
{
    private EventPipeSession? _session;
    private Task? _reader;

    /// <summary>(Zählername, Wert) – ca. einmal pro Sekunde und Zähler.</summary>
    public event Action<string, double>? Sample;

    public event Action<string>? Stopped;

    public int? ProcessId { get; private set; }

    public bool IsRunning => _session is not null;

    public void Start(int pid, int intervalSeconds = 1)
    {
        Stop();
        var client = new DiagnosticsClient(pid);
        var providers = new[]
        {
            new EventPipeProvider("System.Runtime", EventLevel.Informational, 0,
                new Dictionary<string, string> { ["EventCounterIntervalSec"] = intervalSeconds.ToString() }),
            new EventPipeProvider("Microsoft.AspNetCore.Hosting", EventLevel.Informational, 0,
                new Dictionary<string, string> { ["EventCounterIntervalSec"] = intervalSeconds.ToString() }),
        };
        _session = client.StartEventPipeSession(providers, requestRundown: false, circularBufferMB: 16);
        ProcessId = pid;
        var session = _session;
        _reader = Task.Run(() =>
        {
            try
            {
                using var source = new EventPipeEventSource(session.EventStream);
                source.Dynamic.All += OnEvent;
                source.Process();
            }
            catch (Exception e)
            {
                Stopped?.Invoke(e.Message);
                return;
            }
            Stopped?.Invoke("");
        });
    }

    private void OnEvent(TraceEvent e)
    {
        if (e.EventName != "EventCounters") return;
        try
        {
            if (e.PayloadValue(0) is not IDictionary<string, object> outer) return;
            if (!outer.TryGetValue("Payload", out var inner) || inner is not IDictionary<string, object> payload) return;
            var name = payload.TryGetValue("Name", out var n) ? n?.ToString() ?? "" : "";
            double value;
            if (payload.TryGetValue("Increment", out var inc)) value = Convert.ToDouble(inc);
            else if (payload.TryGetValue("Mean", out var mean)) value = Convert.ToDouble(mean);
            else return;
            Sample?.Invoke(name, value);
        }
        catch (InvalidCastException) { }
        catch (FormatException) { }
    }

    public void Stop()
    {
        try { _session?.Stop(); }
        catch (Exception) { }
        _session?.Dispose();
        _session = null;
        ProcessId = null;
    }

    public void Dispose() => Stop();
}

// ======================================================================
// Aufzeichnung (CPU-Samples + Allokationen) und Auswertung
// ======================================================================

public sealed record MethodStat(string Method, string Module, double SelfPercent, double TotalPercent, int SelfSamples, int TotalSamples, bool IsUserCode);

public sealed class CallNode
{
    public required string Method { get; init; }

    public required string Module { get; init; }

    public bool IsUserCode { get; init; }

    public int Samples { get; set; }

    public int SelfSamples { get; set; }

    public Dictionary<string, CallNode> Children { get; } = new();
}

public sealed record AllocationStat(string Name, long Bytes, int Count, IReadOnlyList<(string Type, long Bytes)> TopTypes);

public sealed record GcSummary(int Gen0, int Gen1, int Gen2, double PauseMs, double MaxPauseMs, long? HeapAfterBytes);

public sealed class ProfileReport
{
    public double DurationSeconds { get; init; }

    public int TotalSamples { get; set; }

    public int IdleSamples { get; set; }

    public List<MethodStat> Hotspots { get; } = new();

    /// <summary>Last, zugeordnet dem tiefsten eigenen Methodenaufruf – „wovon kommt das“.</summary>
    public List<MethodStat> UserAttribution { get; } = new();

    public CallNode Root { get; } = new() { Method = "Alle Threads", Module = "" };

    public List<AllocationStat> AllocationsByType { get; } = new();

    public List<AllocationStat> AllocationsByMethod { get; } = new();

    public long SampledAllocatedBytes { get; set; }

    public GcSummary Gc { get; set; } = new(0, 0, 0, 0, 0, null);

    public int ExceptionCount { get; set; }

    public List<(string Type, int Count)> Exceptions { get; } = new();
}

public static class TraceRecorder
{
    private const long GcKeyword = 0x1;
    private const long LoaderKeyword = 0x8;
    private const long JitKeyword = 0x10;
    private const long ContentionKeyword = 0x4000;
    private const long ExceptionKeyword = 0x8000;
    private const long StackKeyword = 0x40000000;

    /// <summary>Zeichnet eine .nettrace-Datei auf.</summary>
    public static async Task RecordAsync(int pid, TimeSpan duration, bool cpu, bool allocations, string outputFile, CancellationToken ct = default)
    {
        var providers = new List<EventPipeProvider>();
        if (cpu) providers.Add(new EventPipeProvider("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational));
        var keywords = LoaderKeyword | JitKeyword | ExceptionKeyword | ContentionKeyword | StackKeyword | GcKeyword;
        providers.Add(new EventPipeProvider("Microsoft-Windows-DotNETRuntime", allocations ? EventLevel.Verbose : EventLevel.Informational, keywords));

        var client = new DiagnosticsClient(pid);
        using var session = client.StartEventPipeSession(providers, requestRundown: true, circularBufferMB: 256);
        await using var file = File.Create(outputFile);
        var copy = session.EventStream.CopyToAsync(file, CancellationToken.None);
        try
        {
            await Task.Delay(duration, ct);
        }
        catch (TaskCanceledException) { }
        try { await session.StopAsync(CancellationToken.None); }
        catch (Exception) { session.Stop(); }
        await copy;
    }

    private static readonly string[] IdleMarkers =
    {
        "WaitHandle.Wait", "WaitOneNoCheck", "Monitor.Wait", "Thread.Sleep", "LowLevelLifoSemaphore.Wait", "SemaphoreSlim.Wait",
        "ManualResetEventSlim.Wait", "Interop+Kernel32.WaitFor", "WaitForSingleObject", "WaitForMultipleObjects", "PortableThreadPool+GateThread",
        "Task.SpinThenBlockingWait", "Task.InternalWaitCore", "SocketAsyncEngine.EventLoop", "Interop+Sys.WaitForSocketEvents",
        "LowLevelLifoSemaphore.WaitForSignal", "Thread.SleepInternal", "Monitor.ReliableEnter", "PollGC", "WaitSubsystem",
        "System.Threading.ThreadPoolWorkQueue.Dispatch", "ThreadPoolWorker", "WorkerThread.WorkerThreadStart",
        "IOCompletionPoller.Poll", "GetQueuedCompletionStatus", "Thread.Join", "TimerQueue.TimerThread",
    };

    /// <summary>Wertet eine Aufzeichnung aus. userModules: Assemblynamen des eigenen Codes.</summary>
    public static ProfileReport Analyze(string nettrace, ISet<string> userModules, bool hideIdle = true)
    {
        var etlx = TraceLog.CreateFromEventPipeDataFile(nettrace, null, new TraceLogOptions { ContinueOnError = true });
        using var log = new TraceLog(etlx);

        var report = new ProfileReport { DurationSeconds = log.SessionDuration.TotalSeconds };
        var self = new Dictionary<string, (string Module, int Count, bool User)>();
        var total = new Dictionary<string, int>();
        var attributed = new Dictionary<string, (string Module, int Count)>();
        var allocType = new Dictionary<string, (long Bytes, int Count)>();
        var allocMethod = new Dictionary<string, (long Bytes, int Count, Dictionary<string, long> Types)>();
        var exceptions = new Dictionary<string, int>();
        int gen0 = 0, gen1 = 0, gen2 = 0;
        double pauseMs = 0, maxPause = 0;
        double? suspendStart = null;
        long? heapAfter = null;
        var considered = 0;

        bool IsUser(string module) => userModules.Contains(module) || userModules.Contains(Path.GetFileNameWithoutExtension(module));

        foreach (var e in log.Events)
        {
            if (e.ProviderName == "Microsoft-DotNETCore-SampleProfiler")
            {
                var stack = e.CallStack();
                if (stack is null) continue;
                report.TotalSamples++;
                var frames = Frames(stack);
                if (frames.Count == 0) continue;
                if (hideIdle && frames.Take(12).Any(f => IdleMarkers.Any(m => f.Method.Contains(m, StringComparison.Ordinal))))
                {
                    report.IdleSamples++;
                    continue;
                }
                considered++;

                var leaf = frames[0];
                self[leaf.Method] = (leaf.Module, (self.TryGetValue(leaf.Method, out var s) ? s.Count : 0) + 1, IsUser(leaf.Module));
                foreach (var m in frames.Select(f => f.Method).Distinct())
                    total[m] = total.GetValueOrDefault(m) + 1;

                var user = frames.FirstOrDefault(f => IsUser(f.Module));
                var key = user.Method ?? leaf.Method;
                attributed[key] = (user.Module ?? leaf.Module, attributed.GetValueOrDefault(key).Count + 1);

                // Top-down-Baum: vom äußersten Aufruf nach innen
                var node = report.Root;
                node.Samples++;
                for (var i = frames.Count - 1; i >= 0 && frames.Count - i <= 80; i--)
                {
                    var f = frames[i];
                    if (!node.Children.TryGetValue(f.Method, out var child))
                    {
                        child = new CallNode { Method = f.Method, Module = f.Module, IsUserCode = IsUser(f.Module) };
                        node.Children[f.Method] = child;
                    }
                    child.Samples++;
                    if (i == 0) child.SelfSamples++;
                    node = child;
                }
                continue;
            }

            switch (e)
            {
                case GCAllocationTickTraceData a:
                {
                    var type = string.IsNullOrEmpty(a.TypeName) ? "?" : a.TypeName;
                    var bytes = a.AllocationAmount64;
                    report.SampledAllocatedBytes += bytes;
                    var t = allocType.GetValueOrDefault(type);
                    allocType[type] = (t.Bytes + bytes, t.Count + 1);
                    var frames = a.CallStack() is { } st ? Frames(st) : new List<(string Method, string Module)>();
                    var user = frames.FirstOrDefault(f => IsUser(f.Module));
                    var site = user.Method ?? (frames.Count > 0 ? "[" + frames[0].Module + "] " + frames[0].Method : "?");
                    if (!allocMethod.TryGetValue(site, out var am)) am = (0, 0, new Dictionary<string, long>());
                    am.Types[type] = am.Types.GetValueOrDefault(type) + bytes;
                    allocMethod[site] = (am.Bytes + bytes, am.Count + 1, am.Types);
                    break;
                }
                case GCStartTraceData gs:
                    if (gs.Depth == 0) gen0++;
                    else if (gs.Depth == 1) gen1++;
                    else gen2++;
                    break;
                case GCSuspendEETraceData:
                    suspendStart = e.TimeStampRelativeMSec;
                    break;
                case GCNoUserDataTraceData when e.EventName.Contains("RestartEEStop", StringComparison.Ordinal) && suspendStart is not null:
                    var p = e.TimeStampRelativeMSec - suspendStart.Value;
                    pauseMs += p;
                    maxPause = Math.Max(maxPause, p);
                    suspendStart = null;
                    break;
                case GCHeapStatsTraceData hs:
                    heapAfter = hs.GenerationSize0 + hs.GenerationSize1 + hs.GenerationSize2 + hs.GenerationSize3 + hs.GenerationSize4;
                    break;
                case ExceptionTraceData ex:
                    report.ExceptionCount++;
                    exceptions[ex.ExceptionType] = exceptions.GetValueOrDefault(ex.ExceptionType) + 1;
                    break;
            }
        }

        double Pct(int n) => considered == 0 ? 0 : 100.0 * n / considered;
        foreach (var (method, data) in self.OrderByDescending(x => x.Value.Count).Take(300))
            report.Hotspots.Add(new MethodStat(method, data.Module, Pct(data.Count), Pct(total.GetValueOrDefault(method)), data.Count, total.GetValueOrDefault(method), data.User));
        // Methoden mit hoher Gesamtzeit ohne eigene Samples (Aufrufer) ebenfalls aufnehmen
        foreach (var (method, count) in total.OrderByDescending(x => x.Value).Take(300))
        {
            if (self.ContainsKey(method)) continue;
            var module = FindModule(report.Root, method) ?? "";
            report.Hotspots.Add(new MethodStat(method, module, 0, Pct(count), 0, count, IsUser(module)));
        }
        foreach (var (method, data) in attributed.OrderByDescending(x => x.Value.Count).Take(200))
            report.UserAttribution.Add(new MethodStat(method, data.Module, Pct(data.Count), Pct(data.Count), data.Count, data.Count, IsUser(data.Module)));
        foreach (var (type, data) in allocType.OrderByDescending(x => x.Value.Bytes).Take(200))
            report.AllocationsByType.Add(new AllocationStat(type, data.Bytes, data.Count, Array.Empty<(string, long)>()));
        foreach (var (method, data) in allocMethod.OrderByDescending(x => x.Value.Bytes).Take(200))
            report.AllocationsByMethod.Add(new AllocationStat(method, data.Bytes, data.Count,
                data.Types.OrderByDescending(t => t.Value).Take(5).Select(t => (t.Key, t.Value)).ToList()));
        foreach (var (type, count) in exceptions.OrderByDescending(x => x.Value)) report.Exceptions.Add((type, count));
        report.Gc = new GcSummary(gen0, gen1, gen2, pauseMs, maxPause, heapAfter);
        report.TotalSamples = Math.Max(report.TotalSamples, considered);
        Prune(report.Root, Math.Max(1, considered / 400));
        return report;
    }

    private static List<(string Method, string Module)> Frames(TraceCallStack stack)
    {
        var list = new List<(string, string)>();
        for (var s = stack; s is not null && list.Count < 200; s = s.Caller)
        {
            var ca = s.CodeAddress;
            var module = ca.ModuleName ?? "?";
            var method = ca.FullMethodName;
            if (string.IsNullOrEmpty(method)) method = "?!0x" + ca.Address.ToString("x");
            list.Add((method, module));
        }
        // unaufgelöste native Frames am Rand entfernen
        while (list.Count > 0 && list[0].Item1.StartsWith("?!", StringComparison.Ordinal) && list.Count > 1) list.RemoveAt(0);
        return list;
    }

    private static string? FindModule(CallNode node, string method)
    {
        if (node.Method == method) return node.Module;
        foreach (var c in node.Children.Values)
        {
            var m = FindModule(c, method);
            if (m is not null) return m;
        }
        return null;
    }

    private static void Prune(CallNode node, int minSamples)
    {
        foreach (var key in node.Children.Where(c => c.Value.Samples < minSamples).Select(c => c.Key).ToList()) node.Children.Remove(key);
        foreach (var c in node.Children.Values) Prune(c, minSamples);
    }
}
