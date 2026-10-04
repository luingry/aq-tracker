using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AqTracker.Core;

// Ties a child process tree to this process through a Windows job object with
// KILL_ON_JOB_CLOSE: when the tracker exits (normally, crashes or is ended by
// Windows shutdown) the kernel terminates every process in the job. Ending the
// tree never starts a new process, so it is safe while the session is ending —
// launching taskkill.exe at that point fails with 0xc0000142 (see ERRORS.md).
public sealed class ChildProcessJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    // Lets the child explicitly opt out with CREATE_BREAKAWAY_FROM_JOB instead of
    // failing CreateProcess, matching the behavior it had outside a job.
    private const uint JobObjectLimitBreakawayOk = 0x800;
    private IntPtr _handle;

    private ChildProcessJob(IntPtr handle) => _handle = handle;

    public static ChildProcessJob Create()
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new JobObjectExtendedLimitInfo { BasicLimitInformation = new JobObjectBasicLimitInfo { LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitBreakawayOk } };
        var length = Marshal.SizeOf<JobObjectExtendedLimitInfo>();
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(limits, buffer, false);
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, buffer, (uint)length))
            {
                var error = Marshal.GetLastWin32Error();
                CloseHandle(handle);
                throw new Win32Exception(error);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return new ChildProcessJob(handle);
    }

    public void Assign(Process process)
    {
        if (_handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(ChildProcessJob));
        if (!AssignProcessToJobObject(_handle, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    // Terminates every process in the job immediately; closing the handle alone
    // would do the same, this just makes the intent and timing explicit.
    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle == IntPtr.Zero) return;
        TerminateJobObject(handle, 1);
        CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInfo
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
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInfo
    {
        public JobObjectBasicLimitInfo BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
