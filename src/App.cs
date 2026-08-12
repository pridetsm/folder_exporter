// App.cs - orchestration: scan scheduling, config hot-reload, status page.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace FolderExporter
{
    public sealed class App
    {
        private readonly Logger _log;
        private readonly string _configPath;
        private readonly bool _console;

        private Config _cfg;
        private Scanner _scanner;
        private HttpServer _http;
        private readonly ExporterStats _stats = new ExporterStats();

        private readonly object _gate = new object();
        private List<Entry> _entries = new List<Entry>();
        private int _activeScans;

        private Thread _loop;
        private volatile bool _running;
        private CancellationTokenSource _cancel;
        private DateTime _configStamp;

        // Monotonic millisecond clock. Environment.TickCount would wrap to
        // negative after ~24.9 days and stall scheduling on a long-lived service.
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static long NowMs() { return Clock.ElapsedMilliseconds; }

        private sealed class Entry
        {
            public TargetState State;
            public long NextDueMs;
            public volatile bool Scanning;
            public int IntervalSeconds;
        }

        public App(string configPath, bool console, Logger log)
        {
            _configPath = configPath;
            _console = console;
            _log = log;
        }

        public void Start()
        {
            _stats.StartTime = Scanner.Now();
            _cancel = new CancellationTokenSource();

            _cfg = Config.Load(_configPath);
            _configStamp = _cfg.SourceStamp;
            _log.Configure(_cfg.LogLevel, _cfg.LogFile, _cfg.LogMaxBytes, _console);
            _log.Info("folder_exporter " + Metrics.Version + " starting; config=" + _cfg.SourcePath);

            if (_cfg.LowPriority)
            {
                if (Win32.EnterBackgroundMode())
                    _log.Info("process running in background priority mode (low CPU and low disk I/O priority)");
                else
                    _log.Warn("could not lower process priority");
            }

            _scanner = new Scanner(_cfg, _log);
            BuildEntries(_cfg, null);

            _http = new HttpServer(_log, RenderMetrics, RenderStatus, Reload);
            _http.Start(_cfg);

            _running = true;
            _loop = new Thread(ScanLoop);
            _loop.IsBackground = true;
            _loop.Name = "scan-loop";
            _loop.Start();

            _log.Info(_entries.Count + " folder(s) configured; scan interval " + _cfg.ScanIntervalSeconds + "s");
        }

        public void Stop()
        {
            _running = false;
            try { _cancel.Cancel(); } catch { }
            if (_http != null) _http.Stop();
            if (_loop != null) { try { _loop.Join(3000); } catch { } }
            _log.Info("folder_exporter stopped");
        }

        // ------------------------------------------------------------------ scanning

        private void BuildEntries(Config cfg, List<Entry> previous)
        {
            var list = new List<Entry>();
            long nowMs = NowMs();
            int stagger = 0;

            foreach (FolderConfig tc in cfg.Folders)
            {
                var st = new TargetState(tc);

                if (previous != null)
                {
                    foreach (Entry old in previous)
                    {
                        if (string.Equals(old.State.Config.Name, tc.Name, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(old.State.Config.Path, tc.Path, StringComparison.OrdinalIgnoreCase))
                        {
                            AdoptState(st, old.State, tc);
                            break;
                        }
                    }
                }

                var e = new Entry();
                e.State = st;
                e.IntervalSeconds = tc.ScanIntervalSeconds > 0 ? tc.ScanIntervalSeconds : cfg.ScanIntervalSeconds;
                // Stagger start times so many targets do not all hit the disk at once.
                e.NextDueMs = cfg.ScanOnStartup ? nowMs + stagger : nowMs + (long)e.IntervalSeconds * 1000;
                stagger += 250;
                list.Add(e);
            }

            lock (_gate) { _entries = list; }
        }

        private static void AdoptState(TargetState fresh, TargetState old, FolderConfig cfg)
        {
            fresh.FilesAddedTotal = old.FilesAddedTotal;
            fresh.FilesRemovedTotal = old.FilesRemovedTotal;
            fresh.ScansTotal = old.ScansTotal;
            fresh.ScanErrorsTotal = old.ScanErrorsTotal;
            fresh.LastAddedTs = old.LastAddedTs;
            fresh.LastRemovedTs = old.LastRemovedTs;
            fresh.LastAddedFile = old.LastAddedFile;
            fresh.LastRemovedFile = old.LastRemovedFile;
            if (old.Last != null) fresh.Publish(old.Last);

            // The file index can only be reused when the tracking mode is unchanged.
            bool sameMode = old.Config.TrackChanges == cfg.TrackChanges &&
                            old.Config.ChangeTrackingMode == cfg.ChangeTrackingMode;
            if (sameMode && cfg.TrackChanges)
            {
                fresh.KnownHashes = old.KnownHashes;
                fresh.KnownNames = old.KnownNames;
                fresh.IsPrimed = old.IsPrimed;
            }
        }

        private void ScanLoop()
        {
            while (_running)
            {
                try
                {
                    long nowMs = NowMs();
                    List<Entry> snapshot;
                    lock (_gate) { snapshot = _entries; }

                    foreach (Entry e in snapshot)
                    {
                        if (!_running) break;
                        if (e.Scanning) continue;
                        if (nowMs < e.NextDueMs) continue;
                        if (Interlocked.CompareExchange(ref _activeScans, 0, 0) >= _cfg.MaxConcurrentScans) break;

                        e.Scanning = true;
                        Interlocked.Increment(ref _activeScans);
                        ThreadPool.QueueUserWorkItem(RunScan, e);
                    }

                    CheckConfigFile();
                }
                catch (Exception ex)
                {
                    _log.Error("scan loop error: " + ex.Message);
                }

                for (int i = 0; i < 10 && _running; i++) Thread.Sleep(50);
            }
        }

        private void RunScan(object state)
        {
            var e = (Entry)state;
            try
            {
                ScanResult r = _scanner.Scan(e.State, _cancel.Token);
                e.State.Publish(r);
                Interlocked.Increment(ref _stats.ScanCycles);
            }
            catch (Exception ex)
            {
                _log.Error("target " + e.State.Config.Name + " scan failed: " + ex.Message);
            }
            finally
            {
                // Anchor the next scan to when this one was DUE, not to when it finished.
                // Scheduling from completion makes every cycle absorb the dispatch latency
                // (up to one tick, plus any wait behind another folder when
                // max_concurrent_scans is 1), and that error accumulates: over a day the
                // folders drift tens of seconds apart and each one's effective interval is
                // longer than configured. Anchoring to the due time keeps a fixed cadence.
                long interval = (long)e.IntervalSeconds * 1000;
                long next = e.NextDueMs + interval;
                long now = NowMs();
                // If we fell more than a whole interval behind - a scan that outran its own
                // period, or the machine was suspended - resync instead of trying to catch
                // up with a burst of back-to-back scans.
                if (next <= now) next = now + interval;
                e.NextDueMs = next;
                e.Scanning = false;
                if (Interlocked.Decrement(ref _activeScans) == 0 && _cfg.TrimWorkingSet)
                {
                    // All scans idle: release the transient index/marshalling garbage
                    // back to the OS so the resident set stays flat between cycles.
                    GC.Collect(2, GCCollectionMode.Optimized);
                    Win32.TrimWorkingSet();
                }
            }
        }

        /// <summary>Runs one scan of every target synchronously (used by --once).</summary>
        public string ScanOnceAndRender()
        {
            List<Entry> snapshot;
            lock (_gate) { snapshot = _entries; }
            foreach (Entry e in snapshot)
            {
                ScanResult r = _scanner.Scan(e.State, _cancel.Token);
                e.State.Publish(r);
            }
            return RenderMetrics();
        }

        // ------------------------------------------------------------------ reload

        private void CheckConfigFile()
        {
            try
            {
                DateTime stamp = File.GetLastWriteTimeUtc(_configPath);
                if (stamp == _configStamp) return;
                _configStamp = stamp;
                _log.Info("config file changed on disk, reloading");
                Reload();
            }
            catch { /* file briefly locked by an editor: retry next tick */ }
        }

        public void Reload()
        {
            try
            {
                Config fresh = Config.Load(_configPath);
                List<Entry> previous;
                lock (_gate) { previous = _entries; }

                _cfg = fresh;
                _configStamp = fresh.SourceStamp;
                _scanner = new Scanner(fresh, _log);
                _log.Configure(fresh.LogLevel, fresh.LogFile, fresh.LogMaxBytes, _console);
                BuildEntries(fresh, previous);
                _http.Configure(fresh);
                Interlocked.Increment(ref _stats.ConfigReloads);
                _log.Info("configuration reloaded: " + fresh.Folders.Count + " folder(s)");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _stats.ConfigReloadFailures);
                _log.Error("config reload failed, keeping previous configuration: " + ex.Message);
                throw;
            }
        }

        // ------------------------------------------------------------------ rendering

        public string RenderMetrics()
        {
            Interlocked.Increment(ref _stats.Scrapes);
            return Metrics.Render(CurrentResults(), _stats);
        }

        private List<ScanResult> CurrentResults()
        {
            List<Entry> snapshot;
            lock (_gate) { snapshot = _entries; }
            var results = new List<ScanResult>(snapshot.Count);
            foreach (Entry e in snapshot)
            {
                ScanResult r = e.State.Last;
                if (r != null) { results.Add(r); continue; }
                // Not scanned yet: emit a placeholder so the target is visible immediately.
                var pending = new ScanResult();
                pending.TargetName = e.State.Config.Name;
                pending.Path = e.State.Config.Path;
                pending.ExtraLabels = e.State.Config.Labels;
                pending.TrackingEnabled = e.State.Config.TrackChanges;
                pending.ExposeNames = e.State.Config.ExposeFilenameLabels;
                pending.AgeBucketBounds = _cfg.FileAgeBucketsSeconds;
                pending.AgeBucketCounts = new long[_cfg.FileAgeBucketsSeconds.Length];
                results.Add(pending);
            }
            return results;
        }

        public string RenderStatus()
        {
            var sb = new StringBuilder();
            List<ScanResult> rs = CurrentResults();
            sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>folder_exporter</title>");
            sb.Append("<style>body{font:14px/1.5 Segoe UI,system-ui,sans-serif;margin:2rem;color:#222}")
              .Append("h1{font-size:1.2rem}table{border-collapse:collapse;margin-top:1rem}")
              .Append("th,td{border:1px solid #ddd;padding:.35rem .6rem;text-align:left}")
              .Append("th{background:#f4f4f4}code{background:#f4f4f4;padding:.1rem .3rem}")
              .Append(".bad{color:#b00}.ok{color:#070}</style></head><body>");
            sb.Append("<h1>folder_exporter ").Append(Metrics.Version).Append("</h1>");
            sb.Append("<p><a href=\"").Append(H(_cfg.MetricsPath)).Append("\">Metrics</a> &middot; ")
              .Append("<a href=\"/healthz\">Health</a> &middot; config: <code>")
              .Append(H(_cfg.SourcePath)).Append("</code></p>");
            sb.Append("<table><tr><th>Target</th><th>Path</th><th>Status</th><th>Files</th><th>Dirs</th>")
              .Append("<th>Size</th><th>Newest file age</th><th>Added / Removed</th><th>Last scan</th><th>Scan time</th></tr>");

            double now = Scanner.Now();
            foreach (ScanResult r in rs)
            {
                sb.Append("<tr><td>").Append(H(r.TargetName)).Append("</td><td><code>").Append(H(r.Path)).Append("</code></td>");
                if (r.ScanTimestamp == 0) sb.Append("<td>pending</td>");
                else if (!r.Exists) sb.Append("<td class=\"bad\">missing</td>");
                else if (!r.Success) sb.Append("<td class=\"bad\">partial</td>");
                else sb.Append("<td class=\"ok\">ok</td>");
                sb.Append("<td>").Append(r.FileCount.ToString("N0", CultureInfo.InvariantCulture)).Append("</td>");
                sb.Append("<td>").Append(r.DirectoryCount.ToString("N0", CultureInfo.InvariantCulture)).Append("</td>");
                sb.Append("<td>").Append(HumanBytes(r.SizeBytes)).Append("</td>");
                sb.Append("<td>").Append(r.NewestFileTs > 0 ? HumanAge(now - r.NewestFileTs) : "-").Append("</td>");
                sb.Append("<td>").Append(r.FilesAddedTotal).Append(" / ").Append(r.FilesRemovedTotal).Append("</td>");
                sb.Append("<td>").Append(r.ScanTimestamp > 0 ? HumanAge(now - r.ScanTimestamp) + " ago" : "-").Append("</td>");
                sb.Append("<td>").Append(r.ScanDurationSeconds.ToString("0.000", CultureInfo.InvariantCulture)).Append("s</td></tr>");
            }
            sb.Append("</table></body></html>");
            return sb.ToString();
        }

        private static string H(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&#39;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        private static string HumanBytes(long b)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
            double v = b; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return v.ToString(i == 0 ? "0" : "0.##", CultureInfo.InvariantCulture) + " " + u[i];
        }

        private static string HumanAge(double s)
        {
            if (s < 0) s = 0;
            if (s < 60) return ((int)s) + "s";
            if (s < 3600) return ((int)(s / 60)) + "m";
            if (s < 86400) return (s / 3600).ToString("0.#", CultureInfo.InvariantCulture) + "h";
            return (s / 86400).ToString("0.#", CultureInfo.InvariantCulture) + "d";
        }
    }
}
