using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BlazorStudio.Agent;

/// <summary>Misst alle 2 Sekunden CPU/Arbeitsspeicher des Servers und der IIS-Arbeitsprozesse (w3wp).</summary>
public sealed class SystemSampler : BackgroundService
{
    public const int HistoryLength = 90;

    private readonly object _gate = new();
    private readonly Queue<double> _cpu = new();
    private readonly Queue<double> _mem = new();
    private Dictionary<int, ProcessSample> _processes = new();
    private ulong _lastIdle, _lastKernel, _lastUser;

    public double CpuPercent { get; private set; }

    public long MemoryUsedBytes { get; private set; }

    public long MemoryTotalBytes { get; private set; }

    public sealed record ProcessSample(int Pid, double CpuPercent, long WorkingSet, long PrivateBytes, int Threads, DateTime? StartTime, TimeSpan TotalCpu, DateTime At);

    public (List<double> Cpu, List<double> Memory) History()
    {
        lock (_gate) return (_cpu.ToList(), _mem.ToList());
    }

    public ProcessSample? Process(int pid)
    {
        lock (_gate) return _processes.GetValueOrDefault(pid);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                Sample();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Messung ist Komfort – darf den Dienst nie beenden
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private void Sample()
    {
        if (GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
        {
            ulong idle = idleFt.Value, kernel = kernelFt.Value, user = userFt.Value;
            if (_lastKernel != 0)
            {
                var total = (kernel - _lastKernel) + (user - _lastUser);
                var busy = total - (idle - _lastIdle);
                CpuPercent = total == 0 ? 0 : Math.Round(Math.Clamp(busy * 100.0 / total, 0, 100), 1);
            }
            _lastIdle = idle;
            _lastKernel = kernel;
            _lastUser = user;
        }

        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref status))
        {
            MemoryTotalBytes = (long)status.TotalPhys;
            MemoryUsedBytes = (long)(status.TotalPhys - status.AvailPhys);
        }

        var now = DateTime.UtcNow;
        var next = new Dictionary<int, ProcessSample>();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("w3wp"))
        {
            using (p)
            {
                try
                {
                    var cpuTime = p.TotalProcessorTime;
                    double cpu = 0;
                    if (_processes.TryGetValue(p.Id, out var prev))
                    {
                        var wall = (now - prev.At).TotalMilliseconds;
                        if (wall > 0) cpu = Math.Round(Math.Clamp((cpuTime - prev.TotalCpu).TotalMilliseconds * 100.0 / (wall * Environment.ProcessorCount), 0, 100), 1);
                    }
                    DateTime? start = null;
                    try { start = p.StartTime.ToUniversalTime(); } catch (Win32Exception) { }
                    next[p.Id] = new ProcessSample(p.Id, cpu, p.WorkingSet64, p.PrivateMemorySize64, p.Threads.Count, start, cpuTime, now);
                }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception)
                {
                    // Prozess wurde inzwischen beendet
                }
            }
        }

        lock (_gate)
        {
            _processes = next;
            Push(_cpu, CpuPercent);
            Push(_mem, MemoryTotalBytes == 0 ? 0 : Math.Round(MemoryUsedBytes * 100.0 / MemoryTotalBytes, 1));
        }
    }

    private static void Push(Queue<double> q, double v)
    {
        q.Enqueue(v);
        while (q.Count > HistoryLength) q.Dequeue();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;

        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
