using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using BlazorStudio.Deploy;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Runtime;

namespace BlazorStudio.Agent;

/// <summary>
/// Speicheranalyse laufender .NET-Prozesse mit ClrMD. Der Prozess wird per Prozess-Snapshot
/// (PssCaptureSnapshot) nur Millisekunden angehalten; ausgewertet wird die Kopie. Die Typ-Statistik
/// wird gespeichert, die Kopie bleibt für Objekt-Details eine Weile offen (begrenzt, da sie RAM kostet).
/// Ablage: &lt;DataDirectory&gt;\memory\&lt;Id&gt;.json und &lt;Id&gt;.types.json.
/// </summary>
public sealed class MemoryService : IDisposable
{
    private const int MaxOpenSessions = 2;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(20);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _dir;
    private readonly IisManager _iis;
    private readonly ServiceManager _services;
    private readonly ILogger<MemoryService> _log;
    private readonly ConcurrentDictionary<string, MemorySnapshotInfo> _infos = new();
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly Timer _cleanup;

    public MemoryService(AgentOptions options, IisManager iis, ServiceManager services, ILogger<MemoryService> log)
    {
        _dir = Path.Combine(options.DataDirectory, "memory");
        _iis = iis;
        _services = services;
        _log = log;
        Directory.CreateDirectory(_dir);
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json").Where(f => !f.EndsWith(".types.json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                if (JsonSerializer.Deserialize<MemorySnapshotInfo>(File.ReadAllText(file), Json) is not { } info) continue;
                if (info.State == "running")
                {
                    info.State = "failed";
                    info.Error = "Der Agent wurde während der Analyse neu gestartet.";
                }
                info.DetailsAvailable = false;
                _infos[info.Id] = info;
            }
            catch (Exception e) when (e is JsonException or IOException) { }
        }
        _cleanup = new Timer(_ => CloseIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    // ---------------- Prozesse ----------------

    /// <summary>.NET-Prozesse, die sich analysieren lassen: IIS-Worker, verwaltete Dienste und andere .NET-Prozesse.</summary>
    public List<MemoryProcess> Processes()
    {
        var labels = new Dictionary<int, string>();
        if (IisManager.IisInstalled)
        {
            try
            {
                foreach (var pool in _iis.GetPools())
                foreach (var w in pool.WorkerProcesses)
                    labels[w.ProcessId] = $"App-Pool {pool.Name}";
            }
            catch (Exception e) when (e is IisUnavailableException or COMException or UnauthorizedAccessException) { }
        }
        try
        {
            foreach (var s in _services.List(false))
                if (s.ProcessId is > 0 and var pid)
                    labels[pid] = $"Dienst {s.Name}";
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or UnauthorizedAccessException) { }

        var pids = new HashSet<int>(labels.Keys);
        try
        {
            foreach (var pid in DiagnosticsClient.GetPublishedProcesses()) pids.Add(pid);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        pids.Remove(Environment.ProcessId);

        // Out-of-process-Hosting: die eigentliche App ist ein Kindprozess von w3wp.exe.
        var parents = ParentProcesses();
        var result = new List<MemoryProcess>();
        foreach (var pid in pids)
        {
            Process p;
            try
            {
                p = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                continue;
            }
            using (p)
            {
                var label = labels.GetValueOrDefault(pid);
                if (label is null && parents.TryGetValue(pid, out var parent) && labels.TryGetValue(parent, out var parentLabel))
                    label = parentLabel + " (out-of-process)";
                var item = new MemoryProcess { ProcessId = pid, Name = SafeName(p), Label = label ?? "" };
                try
                {
                    item.WorkingSetBytes = p.WorkingSet64;
                    item.Is32Bit = Environment.Is64BitOperatingSystem && IsWow64Process(p.Handle, out var wow) && wow;
                }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { }
                result.Add(item);
            }
        }
        return result.OrderBy(r => r.Label.Length == 0).ThenBy(r => r.Label).ThenBy(r => r.Name).ThenBy(r => r.ProcessId).ToList();
    }

    private static string SafeName(Process p)
    {
        try
        {
            return p.ProcessName;
        }
        catch (InvalidOperationException)
        {
            return "?";
        }
    }

    // ---------------- Snapshots ----------------

    public List<MemorySnapshotInfo> List() =>
        _infos.Values.Select(Fresh).OrderByDescending(i => i.CreatedAt).ToList();

    public MemorySnapshotInfo Get(string id) =>
        _infos.TryGetValue(id, out var info) ? Fresh(info) : throw new NotFoundException($"Snapshot {id} gibt es nicht.");

    private MemorySnapshotInfo Fresh(MemorySnapshotInfo info)
    {
        info.DetailsAvailable = _sessions.ContainsKey(info.Id);
        return info;
    }

    public MemorySnapshotInfo Start(MemorySnapshotRequest request)
    {
        if (request.ProcessId <= 0) throw new ArgumentException("Prozess-ID fehlt.");
        if (_infos.Values.Any(i => i.State == "running")) throw new InvalidOperationException("Es läuft bereits eine Speicheranalyse – bitte warten.");
        if (request.ProcessId == Environment.ProcessId) throw new ArgumentException("Der Agent kann sich nicht selbst analysieren.");
        var pid = OutOfProcessChild(request.ProcessId) ?? request.ProcessId;
        Process p;
        try
        {
            p = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            throw new NotFoundException($"Prozess {pid} läuft nicht (mehr).");
        }
        var info = new MemorySnapshotInfo
        {
            Id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + pid,
            ProcessId = pid,
            CreatedAt = DateTime.UtcNow,
            Label = request.Label?.Trim() ?? "",
            Step = "Snapshot wird erstellt …",
        };
        using (p)
        {
            info.ProcessName = SafeName(p);
            try
            {
                info.WorkingSetBytes = p.WorkingSet64;
            }
            catch (InvalidOperationException) { }
        }
        _infos[info.Id] = info;
        Save(info);
        _ = Task.Run(() => Analyze(info));
        return info;
    }

    /// <summary>
    /// ASP.NET Core „out-of-process“: w3wp.exe selbst enthält kein .NET, die App läuft als Kindprozess
    /// (dotnet.exe bzw. App.exe). Dann wird dieser analysiert.
    /// </summary>
    private static int? OutOfProcessChild(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.ProcessName.Equals("w3wp", StringComparison.OrdinalIgnoreCase)) return null;
            var dotnet = DiagnosticsClient.GetPublishedProcesses().ToHashSet();
            if (dotnet.Contains(pid)) return null;
            foreach (var (child, parent) in ParentProcesses())
                if (parent == pid && dotnet.Contains(child)) return child;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException) { }
        return null;
    }

    public void Delete(string id)
    {
        if (!_infos.TryGetValue(id, out var info)) throw new NotFoundException($"Snapshot {id} gibt es nicht.");
        if (info.State == "running") throw new InvalidOperationException("Die Analyse läuft noch.");
        _infos.TryRemove(id, out _);
        if (_sessions.TryRemove(id, out var s)) s.Dispose();
        foreach (var f in new[] { InfoPath(id), TypesPath(id) })
            if (File.Exists(f)) File.Delete(f);
    }

    public List<MemoryTypeStat> Types(string id)
    {
        var info = Get(id);
        if (!info.IsReady) throw new InvalidOperationException("Der Snapshot ist noch nicht fertig.");
        var path = TypesPath(id);
        return File.Exists(path) ? JsonSerializer.Deserialize<List<MemoryTypeStat>>(File.ReadAllText(path), Json) ?? new() : new();
    }

    private string InfoPath(string id) => Path.Combine(_dir, id + ".json");
    private string TypesPath(string id) => Path.Combine(_dir, id + ".types.json");

    private void Save(MemorySnapshotInfo info)
    {
        try
        {
            File.WriteAllText(InfoPath(info.Id), JsonSerializer.Serialize(info, Json));
        }
        catch (IOException e)
        {
            _log.LogWarning(e, "Snapshot {Id} konnte nicht gespeichert werden", info.Id);
        }
    }

    private void Analyze(MemorySnapshotInfo info)
    {
        DataTarget? target = null;
        var total = Stopwatch.StartNew();
        try
        {
            var pause = Stopwatch.StartNew();
            target = DataTarget.CreateSnapshotAndAttach(info.ProcessId);
            info.PauseMs = pause.ElapsedMilliseconds;
            if (target.ClrVersions.IsDefaultOrEmpty)
                throw new InvalidOperationException("In diesem Prozess ist keine .NET-Laufzeit geladen.");
            var clr = target.ClrVersions[0];
            info.Runtime = $"{clr.Flavor} {clr.Version}";
            info.Step = "Heap wird gelesen …";
            Save(info);
            var runtime = clr.CreateRuntime();
            var heap = runtime.Heap;
            if (!heap.CanWalkHeap)
                throw new InvalidOperationException("Der Heap ist gerade nicht lesbar (der GC lief im Moment des Snapshots) – bitte erneut versuchen.");

            var stats = new Dictionary<ulong, (ClrType Type, long Count, long Bytes, long Max)>();
            long objects = 0, bytes = 0;
            foreach (var seg in heap.Segments)
            {
                foreach (var obj in seg.EnumerateObjects(carefully: false))
                {
                    if (obj.Type is not { } type) continue;
                    var size = (long)obj.Size;
                    if (type.IsFree)
                    {
                        info.FreeBytes += size;
                        continue;
                    }
                    objects++;
                    bytes += size;
                    switch (seg.GetGeneration(obj.Address))
                    {
                        case Generation.Generation0: info.Gen0Bytes += size; break;
                        case Generation.Generation1: info.Gen1Bytes += size; break;
                        case Generation.Generation2: info.Gen2Bytes += size; break;
                        case Generation.Large: info.LargeBytes += size; break;
                        case Generation.Pinned: info.PinnedBytes += size; break;
                    }
                    stats.TryGetValue(type.MethodTable, out var s);
                    stats[type.MethodTable] = (type, s.Count + 1, s.Bytes + size, Math.Max(s.Max, size));
                    if (objects % 200_000 == 0)
                    {
                        info.Step = $"Heap wird gelesen … {objects:N0} Objekte";
                        info.HeapBytes = bytes;
                    }
                }
            }
            var types = stats.Values
                .GroupBy(s => s.Type.Name ?? $"<unbekannt 0x{s.Type.MethodTable:X}>")
                .Select(g => new MemoryTypeStat { Type = g.Key, Count = g.Sum(x => x.Count), Bytes = g.Sum(x => x.Bytes), MaxBytes = g.Max(x => x.Max) })
                .OrderByDescending(t => t.Bytes)
                .ToList();
            File.WriteAllText(TypesPath(info.Id), JsonSerializer.Serialize(types, Json));
            info.HeapBytes = bytes;
            info.ObjectCount = objects;
            info.TypeCount = types.Count;
            info.AnalysisMs = total.ElapsedMilliseconds;
            info.Step = null;
            info.State = "ready";

            foreach (var old in _sessions.Values.OrderBy(s => s.LastUsed).Take(Math.Max(0, _sessions.Count - MaxOpenSessions + 1)).ToList())
                if (_sessions.TryRemove(old.Id, out var removed)) removed.Dispose();
            _sessions[info.Id] = new Session(info.Id, target, runtime);
            target = null;
            _log.LogInformation("Speicheranalyse {Id}: {Process} ({Pid}), {Objects} Objekte, {Bytes} Bytes, Pause {Pause} ms",
                info.Id, info.ProcessName, info.ProcessId, objects, bytes, info.PauseMs);
        }
        catch (Exception e)
        {
            info.State = "failed";
            info.Step = null;
            info.Error = Explain(e, info);
            _log.LogWarning(e, "Speicheranalyse {Id} fehlgeschlagen", info.Id);
        }
        finally
        {
            target?.Dispose();
            Save(info);
        }
    }

    private static string Explain(Exception e, MemorySnapshotInfo info) => e switch
    {
        ArgumentException when !ProcessAlive(info.ProcessId) => $"Prozess {info.ProcessId} läuft nicht mehr.",
        UnauthorizedAccessException or Win32Exception { NativeErrorCode: 5 } =>
            "Zugriff verweigert – der Agent muss als LocalSystem oder Administrator laufen, um andere Prozesse zu analysieren.",
        _ when e.Message.Contains("architecture", StringComparison.OrdinalIgnoreCase) || e is BadImageFormatException =>
            "32-Bit-Prozesse kann der (64-Bit-)Agent nicht analysieren. Tipp: „32-Bit-Anwendungen aktivieren“ im App-Pool ausschalten.",
        FileNotFoundException f when f.FileName?.Contains("dac", StringComparison.OrdinalIgnoreCase) == true =>
            "Die Diagnose-Bibliothek der .NET-Laufzeit (mscordaccore.dll) wurde nicht gefunden: " + e.Message,
        _ => e.Message,
    };

    private static bool ProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    // ---------------- Details (aus der offenen Kopie) ----------------

    private Session Open(string id)
    {
        Get(id);
        if (!_sessions.TryGetValue(id, out var s))
            throw new InvalidOperationException("Die Details dieses Snapshots sind nicht mehr geladen (nach 20 Minuten ohne Zugriff oder bei mehr als zwei offenen Snapshots wird die Kopie freigegeben). Bitte einen neuen Snapshot erstellen.");
        s.LastUsed = DateTime.UtcNow;
        return s;
    }

    /// <summary>Instanzen eines Typs, größte zuerst.</summary>
    public List<MemoryObjectRow> Instances(string id, string type, int skip, int take)
    {
        if (string.IsNullOrWhiteSpace(type)) throw new ArgumentException("Typ fehlt.");
        var s = Open(id);
        take = Math.Clamp(take, 1, 500);
        skip = Math.Max(0, skip);
        lock (s.Lock)
        {
            var found = new List<(ulong Address, ulong Size, ClrSegment Seg)>();
            foreach (var seg in s.Heap.Segments)
            foreach (var obj in seg.EnumerateObjects(carefully: false))
            {
                if (obj.Type?.Name != type) continue;
                found.Add((obj.Address, obj.Size, seg));
                if (found.Count >= 100_000) break;
            }
            return found.OrderByDescending(f => f.Size).ThenBy(f => f.Address).Skip(skip).Take(take).Select(f =>
            {
                var obj = s.Heap.GetObject(f.Address);
                return new MemoryObjectRow { Address = f.Address, Type = type, Size = (long)f.Size, Generation = GenerationName(f.Seg.GetGeneration(f.Address)), Preview = Preview(obj) };
            }).ToList();
        }
    }

    public MemoryObjectDetail Object(string id, ulong address, int from, bool reachable)
    {
        var s = Open(id);
        lock (s.Lock)
        {
            var obj = s.Heap.GetObject(address);
            if (!obj.IsValid || obj.Type is not { } type) throw new NotFoundException($"An Adresse 0x{address:X} liegt kein gültiges Objekt.");
            var detail = new MemoryObjectDetail
            {
                Address = address,
                Type = type.Name ?? "?",
                Size = (long)obj.Size,
                Generation = s.Heap.GetSegmentByAddress(address) is { } seg ? GenerationName(seg.GetGeneration(address)) : "",
            };
            if (type.IsString)
            {
                detail.Text = obj.AsString(20_000);
                detail.Length = obj.ReadField<int>("_stringLength");
            }
            else if (type.IsArray)
            {
                var array = obj.AsArray();
                detail.Length = array.Length;
                detail.ElementsFrom = Math.Clamp(from, 0, Math.Max(0, array.Length - 1));
                var component = type.ComponentType;
                for (var i = detail.ElementsFrom; i < Math.Min(array.Length, detail.ElementsFrom + 200); i++)
                {
                    var addr = type.GetArrayElementAddress(address, i);
                    detail.Elements.Add(Describe(s, $"[{i}]", component, addr, component is null || !component.IsValueType, depth: 0, out _));
                }
            }
            else
            {
                foreach (var field in type.Fields)
                {
                    var addr = field.GetAddress(address);
                    detail.Fields.Add(Describe(s, field.Name ?? "?", field.Type, addr, field.IsObjectReference, depth: 0, out var nested));
                    detail.Fields.AddRange(nested);
                }
            }
            if (reachable) Reachable(s, obj, detail);
            return detail;
        }
    }

    /// <summary>Wie viel ab diesem Objekt erreichbar ist (Obergrenze dessen, was es festhält).</summary>
    private static void Reachable(Session s, ClrObject start, MemoryObjectDetail detail)
    {
        var seen = new HashSet<ulong> { start.Address };
        var queue = new Queue<ClrObject>();
        queue.Enqueue(start);
        long bytes = 0;
        var watch = Stopwatch.StartNew();
        while (queue.Count > 0)
        {
            if (seen.Count > 500_000 || watch.ElapsedMilliseconds > 10_000)
            {
                detail.ReachableTruncated = true;
                break;
            }
            var obj = queue.Dequeue();
            bytes += (long)obj.Size;
            if (!obj.ContainsPointers) continue;
            foreach (var child in obj.EnumerateReferences(carefully: false, considerDependantHandles: false))
                if (child.IsValid && seen.Add(child.Address)) queue.Enqueue(child);
        }
        detail.ReachableBytes = bytes;
        detail.ReachableObjects = seen.Count;
    }

    /// <summary>Wer hält das Objekt fest? Wurzel-Pfade (GC-Root → … → Objekt).</summary>
    public MemoryRootResult Roots(string id, ulong address)
    {
        var s = Open(id);
        lock (s.Lock)
        {
            var target = s.Heap.GetObject(address);
            if (!target.IsValid) throw new NotFoundException($"An Adresse 0x{address:X} liegt kein gültiges Objekt.");
            var result = new MemoryRootResult();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var gcroot = new GCRoot(s.Heap, new[] { address });
            var seenRoots = new HashSet<ulong>();
            try
            {
                foreach (var (root, chain) in gcroot.EnumerateRootPaths(cts.Token))
                {
                    var path = new MemoryRootPath { RootKind = RootName(root) };
                    for (var link = chain; link is not null; link = link.Next)
                    {
                        var obj = s.Heap.GetObject(link.Object);
                        path.Steps.Add(new MemoryPathStep { Address = link.Object, Type = obj.Type?.Name ?? "?" });
                    }
                    for (var i = 1; i < path.Steps.Count; i++)
                        path.Steps[i].Via = Via(s.Heap.GetObject(path.Steps[i - 1].Address), path.Steps[i].Address);
                    // Pro erstem Objekt (meist dieselbe Wurzel über mehrere Handles) nur ein Pfad.
                    if (path.Steps.Count > 0 && !seenRoots.Add(path.Steps[0].Address)) continue;
                    result.Paths.Add(path);
                    if (result.Paths.Count >= 8)
                    {
                        result.Truncated = true;
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                result.Truncated = true;
            }
            return result;
        }
    }

    private static string RootName(ClrRoot root)
    {
        var name = root.RootKind.ToString();
        if (root is ClrStackRoot { StackFrame: { } frame })
            name += ": " + (frame.Method?.Signature ?? frame.ToString());
        if (root.IsPinned) name += " (pinned)";
        return name;
    }

    private static string? Via(ClrObject parent, ulong child)
    {
        if (!parent.IsValid) return null;
        foreach (var r in parent.EnumerateReferencesWithFields(carefully: false, considerDependantHandles: true))
        {
            if (r.Object.Address != child) continue;
            if (r.IsDependentHandle) return "(ConditionalWeakTable)";
            if (r.IsArrayElement) return "[]";
            var name = r.Field?.Name;
            for (var inner = r.InnerField; inner is { } f; inner = f.InnerField)
                name += "." + f.Field?.Name;
            return name;
        }
        return null;
    }

    // ---------------- Werte lesen ----------------

    private static MemoryField Describe(Session s, string name, ClrType? type, ulong address, bool isReference, int depth, out List<MemoryField> nested)
    {
        nested = new();
        var field = new MemoryField { Name = name, Type = type?.Name ?? (isReference ? "object" : "?") };
        var reader = s.Target.DataReader;
        try
        {
            if (isReference)
            {
                var r = reader.ReadPointer(address);
                if (r == 0)
                {
                    field.Value = "null";
                    return field;
                }
                var obj = s.Heap.GetObject(r);
                field.Reference = r;
                field.Type = obj.Type?.Name ?? field.Type;
                field.Value = Preview(obj) ?? field.Type;
                return field;
            }
            if (type is null) return field;
            if (type.IsEnum || type.IsPrimitive || type.ElementType is not (ClrElementType.Struct or ClrElementType.GenericInstantiation))
            {
                field.Value = Primitive(reader, type.IsEnum ? type.AsEnum().ElementType : type.ElementType, address);
                if (type.IsEnum && field.Value is { } raw)
                    field.Value = EnumName(type, raw) is { } en ? $"{en} ({raw})" : raw;
                return field;
            }
            if (WellKnown(reader, type.Name, address) is { } known)
            {
                field.Value = known;
                return field;
            }
            field.Value = "{…}";
            if (depth > 0) return field;
            foreach (var sub in type.Fields)
            {
                var item = Describe(s, name + "." + sub.Name, sub.Type, sub.GetAddress(address, interior: true), sub.IsObjectReference, depth + 1, out _);
                nested.Add(item);
            }
            return field;
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IndexOutOfRangeException or NullReferenceException)
        {
            field.Value = "<nicht lesbar>";
            return field;
        }
    }

    private static string? EnumName(ClrType type, string raw)
    {
        try
        {
            foreach (var (n, v) in type.AsEnum().EnumerateValues())
                if (Convert.ToString(v, CultureInfo.InvariantCulture) == raw) return n;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or FormatException) { }
        return null;
    }

    private static string? Primitive(IDataReader r, ClrElementType et, ulong a) => et switch
    {
        ClrElementType.Boolean => r.Read<byte>(a) != 0 ? "true" : "false",
        ClrElementType.Char => $"'{(char)r.Read<ushort>(a)}'",
        ClrElementType.Int8 => r.Read<sbyte>(a).ToString(CultureInfo.InvariantCulture),
        ClrElementType.UInt8 => r.Read<byte>(a).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Int16 => r.Read<short>(a).ToString(CultureInfo.InvariantCulture),
        ClrElementType.UInt16 => r.Read<ushort>(a).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Int32 => r.Read<int>(a).ToString(CultureInfo.InvariantCulture),
        ClrElementType.UInt32 => r.Read<uint>(a).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Int64 => r.Read<long>(a).ToString(CultureInfo.InvariantCulture),
        ClrElementType.UInt64 => r.Read<ulong>(a).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Float => r.Read<float>(a).ToString(CultureInfo.InvariantCulture),
        ClrElementType.Double => r.Read<double>(a).ToString(CultureInfo.InvariantCulture),
        ClrElementType.NativeInt or ClrElementType.NativeUInt or ClrElementType.Pointer or ClrElementType.FunctionPointer => $"0x{r.ReadPointer(a):X}",
        _ => null,
    };

    private static string? WellKnown(IDataReader r, string? type, ulong a)
    {
        switch (type)
        {
            case "System.DateTime":
                var data = r.Read<ulong>(a);
                var ticks = (long)(data & 0x3FFFFFFFFFFFFFFF);
                var kind = (data >> 62) switch { 1 => " UTC", 2 => " lokal", _ => "" };
                return ticks is >= 0 and <= 3155378975999999999
                    ? new DateTime(ticks).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + kind
                    : ticks.ToString(CultureInfo.InvariantCulture);
            case "System.TimeSpan":
                return TimeSpan.FromTicks(r.Read<long>(a)).ToString("c", CultureInfo.InvariantCulture);
            case "System.Guid":
                Span<byte> g = stackalloc byte[16];
                return r.Read(a, g) == 16 ? new Guid(g).ToString() : null;
            case "System.Decimal":
                Span<int> d = stackalloc int[4];
                if (r.Read(a, MemoryMarshal.AsBytes(d)) != 16) return null;
                // Laufzeit-Layout: _flags, _hi32, _lo64 (lo, mid)
                try
                {
                    return new decimal(d[2], d[3], d[1], (d[0] & int.MinValue) != 0, (byte)((d[0] >> 16) & 0xFF)).ToString(CultureInfo.InvariantCulture);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
            case "System.DateTimeOffset":
                var dt = r.Read<ulong>(a) & 0x3FFFFFFFFFFFFFFF;
                var offset = r.Read<short>(a + 8);
                return (long)dt <= 3155378975999999999
                    ? new DateTime((long)dt).AddMinutes(offset).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + $" {(offset < 0 ? "-" : "+")}{TimeSpan.FromMinutes(Math.Abs(offset)):hh\\:mm}"
                    : null;
            default:
                return null;
        }
    }

    private static string? Preview(ClrObject obj)
    {
        if (!obj.IsValid || obj.Type is not { } type) return null;
        if (type.IsString)
        {
            var text = obj.AsString(120) ?? "";
            return "\"" + text.Replace("\r", "\\r").Replace("\n", "\\n") + (text.Length >= 120 ? "…\"" : "\"");
        }
        if (type.IsArray) return $"Länge {obj.AsArray().Length:N0}";
        if (obj.IsBoxedValue && type.IsPrimitive)
        {
            try
            {
                var reader = type.Heap.Runtime.DataTarget.DataReader;
                return Primitive(reader, type.ElementType, obj.Address + (ulong)reader.PointerSize);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
        return null;
    }

    private static string GenerationName(Generation g) => g switch
    {
        Generation.Generation0 => "0",
        Generation.Generation1 => "1",
        Generation.Generation2 => "2",
        Generation.Large => "LOH",
        Generation.Pinned => "POH",
        Generation.Frozen => "Frozen",
        _ => "?",
    };

    // ---------------- Aufräumen ----------------

    private void CloseIdle()
    {
        foreach (var s in _sessions.Values)
            if (DateTime.UtcNow - s.LastUsed > IdleTimeout && _sessions.TryRemove(s.Id, out var removed))
            {
                removed.Dispose();
                _log.LogInformation("Speicheranalyse {Id}: Kopie nach Leerlauf freigegeben", s.Id);
            }
    }

    public void Dispose()
    {
        _cleanup.Dispose();
        foreach (var s in _sessions.Values) s.Dispose();
        _sessions.Clear();
    }

    private sealed class Session(string id, DataTarget target, ClrRuntime runtime) : IDisposable
    {
        public string Id { get; } = id;
        public DataTarget Target { get; } = target;
        public ClrHeap Heap { get; } = runtime.Heap;
        public object Lock { get; } = new();
        public DateTime LastUsed { get; set; } = DateTime.UtcNow;

        public void Dispose()
        {
            lock (Lock)
            {
                runtime.Dispose();
                Target.Dispose();
            }
        }
    }

    // ---------------- Win32 ----------------

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool wow64);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    /// <summary>Prozess-ID → Elternprozess-ID.</summary>
    private static Dictionary<int, int> ParentProcesses()
    {
        var map = new Dictionary<int, int>();
        if (!OperatingSystem.IsWindows()) return map;
        var snapshot = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return map;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (var ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
                map[(int)entry.ProcessId] = (int)entry.ParentProcessId;
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return map;
    }
}
