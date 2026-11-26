using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;

namespace AvaloniaVS.Services
{
    /// <summary>
    /// A Windows job object that kills every previewer process when Visual Studio exits,
    /// including when devenv crashes or is killed, so no orphaned hosts are left running.
    /// </summary>
    /// <remarks>
    /// The job handle is never closed on purpose: Windows closes it when devenv's process
    /// ends, and <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> then terminates the children.
    /// </remarks>
    internal static class ChildProcessJob
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        private static readonly Lazy<IntPtr> s_job = new Lazy<IntPtr>(CreateJob);

        /// <summary>
        /// Adds <paramref name="process"/> to the job. Failure is logged and ignored: the
        /// previewer still works, it just won't be cleaned up if VS crashes.
        /// </summary>
        public static void TryAssign(Process process, ILogger log)
        {
            try
            {
                var job = s_job.Value;

                if (job == IntPtr.Zero || !AssignProcessToJobObject(job, process.Handle))
                {
                    log.Debug(
                        "Could not add previewer process to job object: {Error}",
                        new Win32Exception(Marshal.GetLastWin32Error()).Message);
                }
            }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
            {
                // The process may already have exited.
                log.Debug(ex, "Could not add previewer process to job object");
            }
        }

        private static IntPtr CreateJob()
        {
            var job = CreateJobObject(IntPtr.Zero, null);

            if (job == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                },
            };

            var length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            var ptr = Marshal.AllocHGlobal(length);

            try
            {
                Marshal.StructureToPtr(info, ptr, false);

                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)length))
                {
                    CloseHandle(job);
                    return IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            return job;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

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
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
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
    }
}
