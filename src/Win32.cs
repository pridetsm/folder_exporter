// Win32.cs - native interop. Directory enumeration uses FindFirstFileEx so that
// size + timestamps + attributes arrive with the directory listing itself; that
// avoids a separate stat/open per file, which is what makes naive .NET folder
// walkers so expensive on large trees.
using System;
using System.Runtime.InteropServices;

namespace FolderExporter
{
    internal static class Win32
    {
        public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
        public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;
        public const uint FILE_ATTRIBUTE_HIDDEN = 0x00000002;
        public const uint FILE_ATTRIBUTE_SYSTEM = 0x00000004;

        public const int ERROR_FILE_NOT_FOUND = 2;
        public const int ERROR_PATH_NOT_FOUND = 3;
        public const int ERROR_ACCESS_DENIED = 5;
        public const int ERROR_NO_MORE_FILES = 18;

        // FindFirstFileEx tuning constants.
        private const int FindExInfoBasic = 1;        // skip 8.3 alternate names
        private const int FindExSearchNameMatch = 0;
        private const int FIND_FIRST_EX_LARGE_FETCH = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;

            public long ToLong()
            {
                return ((long)dwHighDateTime << 32) | (uint)dwLowDateTime;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WIN32_FIND_DATAW
        {
            public uint dwFileAttributes;
            public FILETIME ftCreationTime;
            public FILETIME ftLastAccessTime;
            public FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string cAlternateFileName;

            public long Size
            {
                get { return ((long)nFileSizeHigh << 32) | (uint)nFileSizeLow; }
            }

            public bool IsDirectory
            {
                get { return (dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0; }
            }

            public bool IsReparsePoint
            {
                get { return (dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0; }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WIN32_FILE_ATTRIBUTE_DATA
        {
            public uint dwFileAttributes;
            public FILETIME ftCreationTime;
            public FILETIME ftLastAccessTime;
            public FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileExW(
            string lpFileName, int fInfoLevelId, out WIN32_FIND_DATAW lpFindFileData,
            int fSearchOp, IntPtr lpSearchFilter, int dwAdditionalFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATAW lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FindClose(IntPtr hFindFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileAttributesExW(
            string lpFileName, int fInfoLevelId, out WIN32_FILE_ATTRIBUTE_DATA lpFileInformation);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceExW(
            string lpDirectoryName, out ulong lpFreeBytesAvailable,
            out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr min, IntPtr max);

        private const uint PROCESS_MODE_BACKGROUND_BEGIN = 0x00100000;
        private const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;

        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        /// <summary>Opens a find handle for "dir\*". Returns INVALID_HANDLE_VALUE on failure.</summary>
        public static IntPtr FindFirst(string directory, out WIN32_FIND_DATAW data)
        {
            string pattern = LongPath(directory);
            if (!pattern.EndsWith("\\")) pattern += "\\";
            pattern += "*";
            return FindFirstFileExW(pattern, FindExInfoBasic, out data,
                FindExSearchNameMatch, IntPtr.Zero, FIND_FIRST_EX_LARGE_FETCH);
        }

        /// <summary>Stats a single path without the MAX_PATH limit. false if missing/denied.</summary>
        public static bool TryGetAttributes(string path, out WIN32_FILE_ATTRIBUTE_DATA data)
        {
            return GetFileAttributesExW(LongPath(path), 0 /* GetFileExInfoStandard */, out data);
        }

        public static bool TryGetDiskSpace(string anyPathOnVolume, out long freeBytes, out long totalBytes)
        {
            ulong free, total, totalFree;
            freeBytes = 0; totalBytes = 0;
            string dir = anyPathOnVolume;
            if (!dir.EndsWith("\\")) dir += "\\";
            if (!GetDiskFreeSpaceExW(dir, out free, out total, out totalFree)) return false;
            freeBytes = (long)totalFree;
            totalBytes = (long)total;
            return true;
        }

        /// <summary>
        /// Puts the process into background mode: low CPU priority AND low disk I/O
        /// priority, so scans yield to anything else touching the disk.
        /// </summary>
        public static bool EnterBackgroundMode()
        {
            IntPtr me = GetCurrentProcess();
            if (SetPriorityClass(me, PROCESS_MODE_BACKGROUND_BEGIN)) return true;
            // Background mode is per-process and can fail if already applied;
            // fall back to plain below-normal CPU priority.
            return SetPriorityClass(me, BELOW_NORMAL_PRIORITY_CLASS);
        }

        /// <summary>Hands trimmable pages back to the OS after a scan.</summary>
        public static void TrimWorkingSet()
        {
            try { SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1)); }
            catch { }
        }

        /// <summary>Prefixes \\?\ so paths beyond MAX_PATH (260) still enumerate.</summary>
        public static string LongPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path.Substring(2);
            if (path.Length >= 2 && path[1] == ':') return @"\\?\" + path;
            return path; // relative or device path: leave alone
        }

        public static double FileTimeToUnix(FILETIME ft)
        {
            long t = ft.ToLong();
            if (t <= 0) return 0;
            // FILETIME epoch 1601-01-01 -> Unix epoch 1970-01-01 is 11644473600 seconds.
            return (t / 10000000.0) - 11644473600.0;
        }

        // ------------------------------------------------------------- job objects
        //
        // Used by the job scheduler to kill a script's whole process tree on
        // timeout. Killing just the immediate child (e.g. powershell.exe) leaves
        // anything it spawned running; a job object with KILL_ON_JOB_CLOSE takes
        // down every process still assigned to it - including grandchildren -
        // the instant the last handle to the job closes.

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
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            IntPtr hJob, int JobObjectInfoClass,
            ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        /// <summary>
        /// Creates an unnamed job object that kills every process still assigned to
        /// it as soon as its last handle closes. Returns IntPtr.Zero on failure (an
        /// older/locked-down system, or a policy blocking job object creation) - the
        /// caller falls back to killing just the immediate child process.
        /// </summary>
        public static IntPtr CreateKillOnCloseJob()
        {
            IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            uint size = (uint)Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, size))
            {
                CloseHandle(job);
                return IntPtr.Zero;
            }
            return job;
        }
    }
}
