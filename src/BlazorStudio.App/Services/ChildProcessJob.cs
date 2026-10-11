using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BlazorStudio.App.Services;

/// <summary>
/// Windows-Job-Objekt mit „kill on close“: Prozesse, die Blazor Studio startet (z. B. dotnet watch des Live-Designers
/// samt der App darunter), werden automatisch beendet, wenn Blazor Studio endet – auch bei Absturz oder hartem Beenden.
/// </summary>
public static class ChildProcessJob
{
    private static readonly Lazy<IntPtr> Job = new(Create);

    public static void Add(int processId)
    {
        if (Job.Value == IntPtr.Zero) return;
        try
        {
            using var p = Process.GetProcessById(processId);
            AssignProcessToJobObject(Job.Value, p.Handle);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static IntPtr Create()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = { LimitFlags = 0x2000 } }; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(job, 9 /* JobObjectExtendedLimitInformation */, ptr, (uint)size)) return IntPtr.Zero;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        return job; // Handle bleibt bis Prozessende offen – beim Schließen beendet Windows alle Prozesse im Job
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
