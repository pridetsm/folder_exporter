// Watcher.cs - event-driven arrival/departure counting.
//
// WHY THIS EXISTS
//   The scanner compares one directory listing against the previous one, so it can only
//   see a file that was present when a scan ran. A message that arrives and is consumed
//   between two scans is invisible to it: never added to the index, so never counted as
//   added, and never counted as removed. On a fast interface that is most of the traffic —
//   with a 10s scan interval, a queue whose consumer takes files in under a second can show
//   thousands of messages a day as zero.
//
//   FileSystemWatcher wraps ReadDirectoryChangesW, so Windows reports every create, delete
//   and rename as it happens, whatever the file's lifetime. That makes throughput countable.
//
// WHAT IT DOES NOT REPLACE
//   The scan is still what measures STATE — how many files are waiting, how old the oldest
//   is, how large the folder is. A watcher cannot answer those; it only reports transitions.
//   The two are complementary and both are published.
//
// HONEST LIMITS
//   * The OS buffer between the kernel and this process is finite. Under a burst it can
//     overflow, and the events in it are lost, not queued. That is reported as
//     folder_events_lost_total rather than hidden — a throughput figure that quietly
//     under-reports is worse than one that admits a gap.
//   * Delivery is per-directory and best-effort. It is reliable on local NTFS, which is
//     what this exporter supports; on network paths it is not, which is one more reason
//     UNC paths are rejected.
using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace FolderExporter
{
    /// <summary>Watches one folder and counts files arriving and leaving, as they happen.</summary>
    public sealed class FolderWatcher : IDisposable
    {
        private readonly FolderConfig _cfg;
        private readonly Logger _log;
        private readonly object _gate = new object();
        private FileSystemWatcher _fsw;
        private bool _disposed;

        private long _arrived;
        private long _departed;
        private long _lost;

        public long Arrived { get { return Interlocked.Read(ref _arrived); } }
        public long Departed { get { return Interlocked.Read(ref _departed); } }
        public long Lost { get { return Interlocked.Read(ref _lost); } }
        public bool Active { get { lock (_gate) { return _fsw != null && _fsw.EnableRaisingEvents; } } }

        public FolderWatcher(FolderConfig cfg, Logger log)
        {
            _cfg = cfg;
            _log = log;
        }

        /// <summary>
        /// Starts watching, or does nothing if the folder is not there yet. Safe to call
        /// repeatedly: the scan loop calls it each cycle so a folder that appears later,
        /// or a watcher that died, is picked up without a restart.
        /// </summary>
        public void EnsureRunning()
        {
            lock (_gate)
            {
                if (_disposed || (_fsw != null && _fsw.EnableRaisingEvents)) return;
                if (!Directory.Exists(_cfg.Path)) return;

                Stop_NoLock();
                try
                {
                    var w = new FileSystemWatcher(_cfg.Path);
                    // FileName alone covers create, delete and rename. Watching LastWrite as
                    // well would fire on every block written to a file being copied in,
                    // which is noise this counts nothing from.
                    w.NotifyFilter = NotifyFilters.FileName;
                    w.IncludeSubdirectories = _cfg.Recursive;
                    // The kernel buffer is per-watcher and finite; the maximum is 64 KB.
                    // Anything smaller overflows sooner under a burst, and an overflow means
                    // lost events rather than delayed ones.
                    w.InternalBufferSize = 64 * 1024;
                    w.Created += OnCreated;
                    w.Deleted += OnDeleted;
                    w.Renamed += OnRenamed;
                    w.Error += OnError;
                    w.EnableRaisingEvents = true;
                    _fsw = w;
                    _log.Debug("watcher started for " + _cfg.Name);
                }
                catch (Exception ex)
                {
                    _log.Warn("could not watch " + _cfg.Name + ": " + ex.Message);
                    Stop_NoLock();
                }
            }
        }

        private void OnCreated(object sender, FileSystemEventArgs e)
        {
            if (Counts(e.Name, e.FullPath)) Interlocked.Increment(ref _arrived);
        }

        private void OnDeleted(object sender, FileSystemEventArgs e)
        {
            if (Counts(e.Name, e.FullPath)) Interlocked.Increment(ref _departed);
        }

        /// <summary>
        /// A rename can be an arrival, a departure, or neither.
        ///
        /// Interfaces very often deliver atomically by writing a temp file and renaming it
        /// into place. With `exclude: ["*.tmp"]` the temp file's creation is correctly
        /// ignored, so if the rename were ignored too the message would never be counted at
        /// all. A rename INTO the matched set is therefore an arrival, and a rename OUT of
        /// it is a departure. A rename within the set is neither — the same file, renamed.
        /// </summary>
        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            bool was = Counts(e.OldName, e.OldFullPath);
            bool now = Counts(e.Name, e.FullPath);
            if (now && !was) Interlocked.Increment(ref _arrived);
            else if (was && !now) Interlocked.Increment(ref _departed);
        }

        private void OnError(object sender, ErrorEventArgs e)
        {
            // Almost always a buffer overflow: events happened faster than this process
            // drained them and the kernel discarded some. They cannot be recovered, so the
            // only honest thing is to count the incident and start watching again.
            Interlocked.Increment(ref _lost);
            _log.Warn("watcher for " + _cfg.Name + " lost events (" +
                      e.GetException().Message + "); restarting it");
            lock (_gate) { Stop_NoLock(); }
            EnsureRunning();
        }

        /// <summary>
        /// Whether an event about this entry counts, applying the same include/exclude and
        /// excluded-directory rules the scanner applies — or the two figures would be
        /// counting different sets of files and could never be compared.
        /// </summary>
        private bool Counts(string relativeName, string fullPath)
        {
            if (string.IsNullOrEmpty(relativeName)) return false;

            // Directories are not files. A deleted directory no longer exists to test, so
            // the check is best-effort: a false negative here costs one uncounted event.
            try
            {
                if (Directory.Exists(fullPath)) return false;
            }
            catch { }

            string leaf = relativeName;
            int slash = leaf.LastIndexOf('\\');
            string dirPart = slash >= 0 ? leaf.Substring(0, slash) : "";
            if (slash >= 0) leaf = leaf.Substring(slash + 1);

            if (_cfg.ExcludeDirRx != null && dirPart.Length > 0)
            {
                foreach (string seg in dirPart.Split('\\'))
                    foreach (Regex rx in _cfg.ExcludeDirRx)
                        if (rx.IsMatch(seg)) return false;
            }
            if (_cfg.IncludeRx != null)
            {
                bool hit = false;
                foreach (Regex rx in _cfg.IncludeRx) { if (rx.IsMatch(leaf)) { hit = true; break; } }
                if (!hit) return false;
            }
            if (_cfg.ExcludeRx != null)
                foreach (Regex rx in _cfg.ExcludeRx)
                    if (rx.IsMatch(leaf)) return false;

            return true;
        }

        /// <summary>Carries counters across a configuration reload, so a saved YAML does
        /// not reset the day's throughput.</summary>
        public void AdoptCountsFrom(FolderWatcher other)
        {
            if (other == null) return;
            Interlocked.Exchange(ref _arrived, other.Arrived);
            Interlocked.Exchange(ref _departed, other.Departed);
            Interlocked.Exchange(ref _lost, other.Lost);
        }

        private void Stop_NoLock()
        {
            if (_fsw == null) return;
            try
            {
                _fsw.EnableRaisingEvents = false;
                _fsw.Created -= OnCreated;
                _fsw.Deleted -= OnDeleted;
                _fsw.Renamed -= OnRenamed;
                _fsw.Error -= OnError;
                _fsw.Dispose();
            }
            catch { }
            _fsw = null;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                Stop_NoLock();
            }
        }
    }
}
