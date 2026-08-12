// Scanner.cs - the directory walker and per-target state machine.
//
// Design notes:
//  * Traversal is iterative (explicit stack), so a 5000-deep tree cannot blow
//    the managed stack.
//  * Every metric is derived from the WIN32_FIND_DATA already returned by the
//    directory listing - the scanner never opens a file.
//  * Add/remove detection compares the current set of paths against the previous
//    one. In "hash" mode we keep only a 64-bit FNV-1a hash per file (~24 bytes
//    of set overhead per file) instead of the full string.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace FolderExporter
{
    /// <summary>Immutable result of one scan, handed to the metrics renderer.</summary>
    public sealed class ScanResult
    {
        public string TargetName;
        public string Path;
        public Dictionary<string, string> ExtraLabels;

        public bool Exists;
        public bool Success;
        public string ErrorMessage = "";

        public long SizeBytes;
        public long FileCount;
        public long DirectoryCount;

        public double FolderCreatedTs;
        public double FolderModifiedTs;

        public double OldestFileTs;
        public double NewestFileTs;
        public string OldestFileName = "";
        public string NewestFileName = "";

        public long LargestFileBytes;
        public string LargestFileName = "";

        public double[] AgeBucketBounds;
        public long[] AgeBucketCounts;   // per-bucket, made cumulative at render time
        public double AgeSumSeconds;
        public long AgeObservations;     // files that had a usable timestamp

        public long ScanErrors;
        public double ScanDurationSeconds;
        public double ScanTimestamp;
        public bool TimedOut;

        public int AddedLastScan;
        public int RemovedLastScan;

        // Cumulative, carried across scans.
        public long FilesAddedTotal;
        public long FilesRemovedTotal;
        public long ScansTotal;
        public long ScanErrorsTotal;

        public double LastAddedTs;
        public double LastRemovedTs;
        public string LastAddedFile = "";
        public string LastRemovedFile = "";

        // Event-driven throughput, from the watcher rather than the scan. These count
        // every file that arrived or left, including ones whose whole life fell between
        // two scans and which the scan-based counters above cannot see.
        public long EventsArrived;
        public long EventsDeparted;
        public long EventsLost;
        public bool WatcherActive;
        public bool WatchEnabled;

        public long TrackedFiles;
        public bool TrackingTruncated;
        public bool TrackingEnabled;
        public bool ExposeNames;

        public bool HasDisk;
        public string Volume = "";
        public long DiskFreeBytes;
        public long DiskTotalBytes;

        public List<ExtensionStat> Extensions;
    }

    public sealed class ExtensionStat
    {
        public string Extension;
        public long Files;
        public long Bytes;
    }

    /// <summary>Holds everything that must survive between scans for one target.</summary>
    public sealed class TargetState
    {
        public readonly FolderConfig Config;
        private HashSet<long> _knownHashes;
        private Dictionary<long, string> _knownNames;
        private bool _primed;

        public long FilesAddedTotal;
        public long FilesRemovedTotal;
        public long ScansTotal;
        public long ScanErrorsTotal;
        public double LastAddedTs;
        public double LastRemovedTs;
        public string LastAddedFile = "";
        public string LastRemovedFile = "";

        /// <summary>Event-driven arrival/departure counting. Null when watch_events is off.</summary>
        public FolderWatcher Watcher;

        private volatile ScanResult _last;
        public ScanResult Last { get { return _last; } }

        public TargetState(FolderConfig cfg)
        {
            Config = cfg;
            if (cfg.TrackChanges)
            {
                if (cfg.ChangeTrackingMode == "name") _knownNames = new Dictionary<long, string>();
                else _knownHashes = new HashSet<long>();
            }
        }

        public void Publish(ScanResult r) { _last = r; }

        internal HashSet<long> KnownHashes { get { return _knownHashes; } set { _knownHashes = value; } }
        internal Dictionary<long, string> KnownNames { get { return _knownNames; } set { _knownNames = value; } }
        internal bool IsPrimed { get { return _primed; } set { _primed = value; } }
    }

    public sealed class Scanner
    {
        private readonly Config _cfg;
        private readonly Logger _log;

        public Scanner(Config cfg, Logger log)
        {
            _cfg = cfg;
            _log = log;
        }

        public ScanResult Scan(TargetState state, CancellationToken cancel)
        {
            FolderConfig t = state.Config;
            var sw = Stopwatch.StartNew();
            var r = new ScanResult();
            r.TargetName = t.Name;
            r.Path = t.Path;
            r.ExtraLabels = t.Labels;
            r.TrackingEnabled = t.TrackChanges;
            r.WatchEnabled = t.WatchEvents;
            if (state.Watcher != null)
            {
                r.EventsArrived = state.Watcher.Arrived;
                r.EventsDeparted = state.Watcher.Departed;
                r.EventsLost = state.Watcher.Lost;
                r.WatcherActive = state.Watcher.Active;
            }
            r.ExposeNames = t.ExposeFilenameLabels;
            r.AgeBucketBounds = _cfg.FileAgeBucketsSeconds;
            r.AgeBucketCounts = new long[_cfg.FileAgeBucketsSeconds.Length];
            r.ScanTimestamp = Now();

            Win32.WIN32_FILE_ATTRIBUTE_DATA rootAttr;
            if (!Win32.TryGetAttributes(t.Path, out rootAttr) ||
                (rootAttr.dwFileAttributes & Win32.FILE_ATTRIBUTE_DIRECTORY) == 0)
            {
                r.Exists = false;
                r.Success = false;
                r.ErrorMessage = "path does not exist or is not a directory";
                state.ScansTotal++;
                Carry(state, r);
                r.ScanDurationSeconds = sw.Elapsed.TotalSeconds;
                _log.Debug("target " + t.Name + ": path missing (" + t.Path + ")");
                return r;
            }

            r.Exists = true;
            r.FolderCreatedTs = Win32.FileTimeToUnix(rootAttr.ftCreationTime);
            r.FolderModifiedTs = Win32.FileTimeToUnix(rootAttr.ftLastWriteTime);

            bool tracking = t.TrackChanges;
            bool nameMode = tracking && t.ChangeTrackingMode == "name";
            HashSet<long> newHashes = tracking && !nameMode ? new HashSet<long>() : null;
            Dictionary<long, string> newNames = nameMode ? new Dictionary<long, string>() : null;

            int added = 0;
            double newestAddedTs = 0;
            string newestAddedName = null;

            Dictionary<string, ExtensionStat> exts = t.ExtensionMetrics
                ? new Dictionary<string, ExtensionStat>(StringComparer.OrdinalIgnoreCase)
                : null;

            double oldest = double.MaxValue, newest = double.MinValue;
            long throttleCounter = 0;
            long timeoutTicks = _cfg.ScanTimeoutSeconds > 0
                ? (long)_cfg.ScanTimeoutSeconds * Stopwatch.Frequency
                : long.MaxValue;

            var stack = new Stack<DirFrame>();
            stack.Push(new DirFrame(t.Path, "", 0));

            var find = new Win32.WIN32_FIND_DATAW();

            while (stack.Count > 0)
            {
                if (cancel.IsCancellationRequested) break;
                if (sw.ElapsedTicks > timeoutTicks)
                {
                    r.TimedOut = true;
                    r.ScanErrors++;
                    _log.Warn("target " + t.Name + ": scan timed out after " + _cfg.ScanTimeoutSeconds + "s");
                    break;
                }

                DirFrame frame = stack.Pop();
                IntPtr h = Win32.FindFirst(frame.FullPath, out find);
                if (h == Win32.INVALID_HANDLE_VALUE)
                {
                    int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    if (err != Win32.ERROR_FILE_NOT_FOUND && err != Win32.ERROR_NO_MORE_FILES)
                    {
                        r.ScanErrors++;
                        _log.Debug("target " + t.Name + ": cannot enumerate " + frame.FullPath + " (win32 error " + err + ")");
                    }
                    continue;
                }

                try
                {
                    do
                    {
                        string name = find.cFileName;
                        if (name == "." || name == "..") continue;

                        bool isDir = find.IsDirectory;

                        if (t.SkipHidden && (find.dwFileAttributes & Win32.FILE_ATTRIBUTE_HIDDEN) != 0) continue;
                        if (t.SkipSystem && (find.dwFileAttributes & Win32.FILE_ATTRIBUTE_SYSTEM) != 0) continue;

                        if (isDir)
                        {
                            if (Matches(t.ExcludeDirRx, name)) continue;
                            r.DirectoryCount++;
                            if (find.IsReparsePoint && !t.FollowReparsePoints) continue; // junction/symlink loop guard
                            if (!t.Recursive) continue;
                            if (t.MaxDepth > 0 && frame.Depth + 1 >= t.MaxDepth) continue;
                            stack.Push(new DirFrame(
                                Combine(frame.FullPath, name),
                                frame.RelPath.Length == 0 ? name : frame.RelPath + "\\" + name,
                                frame.Depth + 1));
                            continue;
                        }

                        // ---- file ----
                        if (t.IncludeRx != null && !Matches(t.IncludeRx, name)) continue;
                        if (Matches(t.ExcludeRx, name)) continue;

                        long size = find.Size;
                        r.FileCount++;
                        r.SizeBytes += size;

                        double ts = t.AgeBasis == "create"
                            ? Win32.FileTimeToUnix(find.ftCreationTime)
                            : Win32.FileTimeToUnix(find.ftLastWriteTime);

                        if (ts > 0)
                        {
                            if (ts < oldest) { oldest = ts; r.OldestFileName = name; }
                            if (ts > newest) { newest = ts; r.NewestFileName = name; }
                            double age = r.ScanTimestamp - ts;
                            if (age < 0) age = 0;
                            r.AgeSumSeconds += age;
                            r.AgeObservations++;
                            var bounds = r.AgeBucketBounds;
                            for (int b = 0; b < bounds.Length; b++)
                                if (age <= bounds[b]) { r.AgeBucketCounts[b]++; break; }
                        }

                        if (size > r.LargestFileBytes)
                        {
                            r.LargestFileBytes = size;
                            r.LargestFileName = name;
                        }

                        if (exts != null)
                        {
                            string ext = ExtensionOf(name);
                            ExtensionStat es;
                            if (!exts.TryGetValue(ext, out es))
                            {
                                es = new ExtensionStat();
                                es.Extension = ext;
                                exts[ext] = es;
                            }
                            es.Files++;
                            es.Bytes += size;
                        }

                        if (tracking)
                        {
                            string rel = frame.RelPath.Length == 0 ? name : frame.RelPath + "\\" + name;
                            long h64 = Fnv1a64(rel);
                            bool isNew;
                            if (nameMode)
                            {
                                if (newNames.Count < t.MaxTrackedFiles) newNames[h64] = rel;
                                else r.TrackingTruncated = true;
                                isNew = state.KnownNames == null || !state.KnownNames.ContainsKey(h64);
                            }
                            else
                            {
                                if (newHashes.Count < t.MaxTrackedFiles) newHashes.Add(h64);
                                else r.TrackingTruncated = true;
                                isNew = state.KnownHashes == null || !state.KnownHashes.Contains(h64);
                            }
                            if (isNew && (state.IsPrimed || t.CountInitialFilesAsAdded))
                            {
                                added++;
                                if (ts >= newestAddedTs) { newestAddedTs = ts; newestAddedName = rel; }
                            }
                        }

                        if (_cfg.ThrottleEveryFiles > 0 && ++throttleCounter >= _cfg.ThrottleEveryFiles)
                        {
                            throttleCounter = 0;
                            Thread.Sleep(_cfg.ThrottleSleepMs);
                        }
                    }
                    while (Win32.FindNextFileW(h, out find));
                }
                finally
                {
                    Win32.FindClose(h);
                }
            }

            // ---- change accounting ----
            if (tracking)
            {
                int newCount = nameMode ? newNames.Count : newHashes.Count;
                int oldCount = nameMode
                    ? (state.KnownNames == null ? 0 : state.KnownNames.Count)
                    : (state.KnownHashes == null ? 0 : state.KnownHashes.Count);

                int removed = 0;
                if (state.IsPrimed)
                {
                    // matched = files present in both sets; removed = old - matched.
                    int matched = newCount - added;
                    if (matched < 0) matched = 0;
                    removed = oldCount - matched;
                    if (removed < 0) removed = 0;

                    if (removed > 0 && nameMode && state.KnownNames != null)
                    {
                        foreach (var kv in state.KnownNames)
                        {
                            if (!newNames.ContainsKey(kv.Key)) { state.LastRemovedFile = kv.Value; break; }
                        }
                    }
                }

                if (added > 0)
                {
                    state.FilesAddedTotal += added;
                    state.LastAddedTs = r.ScanTimestamp;
                    if (newestAddedName != null) state.LastAddedFile = newestAddedName;
                }
                if (removed > 0)
                {
                    state.FilesRemovedTotal += removed;
                    state.LastRemovedTs = r.ScanTimestamp;
                }

                r.AddedLastScan = added;
                r.RemovedLastScan = removed;
                r.TrackedFiles = newCount;

                if (nameMode) state.KnownNames = newNames; else state.KnownHashes = newHashes;
                state.IsPrimed = true;
            }

            if (r.FileCount == 0) { r.OldestFileTs = 0; r.NewestFileTs = 0; }
            else
            {
                r.OldestFileTs = oldest == double.MaxValue ? 0 : oldest;
                r.NewestFileTs = newest == double.MinValue ? 0 : newest;
            }

            if (t.DiskMetrics)
            {
                long free, total;
                string volRoot = VolumeRoot(t.Path);
                if (Win32.TryGetDiskSpace(volRoot, out free, out total))
                {
                    r.HasDisk = true;
                    r.Volume = volRoot;
                    r.DiskFreeBytes = free;
                    r.DiskTotalBytes = total;
                }
            }

            if (exts != null && exts.Count > 0)
            {
                var list = new List<ExtensionStat>(exts.Values);
                list.Sort(delegate(ExtensionStat a, ExtensionStat b) { return b.Bytes.CompareTo(a.Bytes); });
                if (t.TopExtensions > 0 && list.Count > t.TopExtensions)
                    list.RemoveRange(t.TopExtensions, list.Count - t.TopExtensions);
                r.Extensions = list;
            }

            state.ScansTotal++;
            state.ScanErrorsTotal += r.ScanErrors;
            r.Success = !r.TimedOut;
            r.ScanDurationSeconds = sw.Elapsed.TotalSeconds;
            Carry(state, r);

            _log.Debug("target " + r.TargetName + ": " + r.FileCount + " files, " +
                       r.SizeBytes + " bytes, +" + r.AddedLastScan + "/-" + r.RemovedLastScan +
                       " in " + r.ScanDurationSeconds.ToString("0.000") + "s");
            return r;
        }

        private static void Carry(TargetState s, ScanResult r)
        {
            r.FilesAddedTotal = s.FilesAddedTotal;
            r.FilesRemovedTotal = s.FilesRemovedTotal;
            r.ScansTotal = s.ScansTotal;
            r.ScanErrorsTotal = s.ScanErrorsTotal;
            r.LastAddedTs = s.LastAddedTs;
            r.LastRemovedTs = s.LastRemovedTs;
            r.LastAddedFile = s.LastAddedFile;
            r.LastRemovedFile = s.LastRemovedFile;
        }

        private struct DirFrame
        {
            public readonly string FullPath;
            public readonly string RelPath;
            public readonly int Depth;
            public DirFrame(string full, string rel, int depth) { FullPath = full; RelPath = rel; Depth = depth; }
        }

        private static string Combine(string dir, string name)
        {
            if (dir.EndsWith("\\")) return dir + name;
            return dir + "\\" + name;
        }

        private static bool Matches(System.Text.RegularExpressions.Regex[] set, string name)
        {
            if (set == null) return false;
            for (int i = 0; i < set.Length; i++)
                if (set[i].IsMatch(name)) return true;
            return false;
        }

        private static string ExtensionOf(string name)
        {
            int i = name.LastIndexOf('.');
            if (i <= 0 || i == name.Length - 1) return "(none)";
            return name.Substring(i + 1).ToLowerInvariant();
        }

        private static string VolumeRoot(string path)
        {
            if (path.Length >= 2 && path[1] == ':') return path.Substring(0, 2) + "\\";
            if (path.StartsWith(@"\\"))
            {
                // \\server\share -> keep both components
                int a = path.IndexOf('\\', 2);
                if (a > 0)
                {
                    int b = path.IndexOf('\\', a + 1);
                    return b > 0 ? path.Substring(0, b) : path;
                }
            }
            return path;
        }

        /// <summary>FNV-1a 64, case-insensitive (Windows paths are case-insensitive).</summary>
        internal static long Fnv1a64(string s)
        {
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= 'A' && c <= 'Z') c = (char)(c + 32);
                hash ^= (byte)(c & 0xFF);
                hash *= 1099511628211UL;
                hash ^= (byte)(c >> 8);
                hash *= 1099511628211UL;
            }
            return unchecked((long)hash);
        }

        public static double Now()
        {
            return (DateTime.UtcNow - Epoch).TotalSeconds;
        }

        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }
}
