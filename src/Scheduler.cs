// Scheduler.cs - runs configured jobs on a cron or interval schedule.
//
// Design mirrors App.cs's scan loop on purpose: a background thread that
// wakes up every job_poll_interval_seconds, checks what is due, and dispatches
// work to the thread pool. The one real addition is persisted state
// (JobStateStore), because unlike a scan a job's "due" moment matters even if
// the process was not running to see it - a reboot during a maintenance
// window must not silently swallow a job.
//
// Catch-up model: each job remembers the last local minute it evaluated
// (LastCheckedUtc). Every tick walks forward from there to now and asks the
// cron expression which of those minutes match (interval jobs use simple
// arithmetic instead of a calendar). Finding exactly one due occurrence is
// the normal, steady-state case - one boundary crossed since the last check
// - and it always runs. Finding more than one in a single scan only happens
// after a real gap (the scheduler was not running, or fell behind), and that
// whole backlog is handed to on_missed as a unit: skip it, run only the
// newest, or run all of it (capped, so a long outage cannot turn into an
// unbounded burst).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace FolderExporter
{
    public sealed class Scheduler
    {
        private readonly Logger _log;
        private Config _cfg;
        private JobStateStore _store;

        private readonly object _gate = new object();
        private List<JobEntry> _jobs = new List<JobEntry>();

        private Thread _loop;
        private volatile bool _running;

        private sealed class JobEntry
        {
            public JobConfig Config;
            public CronSchedule Cron;   // null for interval jobs
            public volatile bool Running;
            public volatile bool RerunPending;

            public double LastCheckedUtc;
            public double LastDueUtc;
            public double LastStartUtc;
            public double LastEndUtc;
            public int LastExitCode = -1;
            public bool LastSuccess;
            public long RunsOk, RunsFailed, RunsTimeout, RunsSkipped, MissedTotal;
        }

        public Scheduler(Logger log) { _log = log; }

        public void Start(Config cfg)
        {
            Initialize(cfg);
            _running = true;
            _loop = new Thread(Loop);
            _loop.IsBackground = true;
            _loop.Name = "job-scheduler";
            _loop.Start();
            if (_jobs.Count > 0) _log.Info(_jobs.Count + " job(s) configured");
        }

        public void Stop()
        {
            _running = false;
            if (_loop != null) { try { _loop.Join(3000); } catch { } }
            Persist();
        }

        public void Reload(Config cfg)
        {
            Initialize(cfg);
            _log.Info("job scheduler reloaded: " + _jobs.Count + " job(s) configured");
        }

        /// <summary>Loads config and persisted state without starting the background
        /// loop - used by --run-job and --check-config, which need the same
        /// parsing and state-carry-forward logic but drive execution themselves.</summary>
        public void LoadOnly(Config cfg) { Initialize(cfg); }

        private void Initialize(Config cfg)
        {
            Dictionary<string, JobEntry> previous;
            lock (_gate) { previous = ToMap(_jobs); }

            if (previous.Count == 0)
            {
                // First load: seed from whatever was persisted on disk, so a
                // restart resumes the catch-up window instead of starting blind.
                _store = new JobStateStore(cfg.JobStateFile, _log);
                foreach (KeyValuePair<string, JobStateStore.Record> kv in _store.Load())
                {
                    JobStateStore.Record r = kv.Value;
                    var e = new JobEntry();
                    e.LastCheckedUtc = r.LastChecked;
                    e.LastDueUtc = r.LastDue;
                    e.LastStartUtc = r.LastStart;
                    e.LastEndUtc = r.LastEnd;
                    e.LastExitCode = r.LastExitCode;
                    e.LastSuccess = r.LastSuccess;
                    e.RunsOk = r.RunsOk; e.RunsFailed = r.RunsFailed;
                    e.RunsTimeout = r.RunsTimeout; e.RunsSkipped = r.RunsSkipped;
                    e.MissedTotal = r.MissedTotal;
                    previous[kv.Key] = e;
                }
            }
            else
            {
                _store = new JobStateStore(cfg.JobStateFile, _log);
            }

            _cfg = cfg;
            BuildJobs(cfg, previous);
        }

        private void BuildJobs(Config cfg, Dictionary<string, JobEntry> previous)
        {
            var list = new List<JobEntry>();
            double now = Scanner.Now();

            foreach (JobConfig jc in cfg.Jobs)
            {
                if (!jc.Enabled) continue;

                var e = new JobEntry();
                e.Config = jc;
                if (!string.IsNullOrEmpty(jc.Cron))
                {
                    try { e.Cron = CronSchedule.Parse(jc.Cron); }
                    catch (Exception ex)
                    {
                        _log.Error("job \"" + jc.Name + "\": " + ex.Message + " (job disabled)");
                        continue;
                    }
                }

                JobEntry old;
                if (previous != null && previous.TryGetValue(jc.Name, out old))
                {
                    e.LastCheckedUtc = old.LastCheckedUtc;
                    e.LastDueUtc = old.LastDueUtc;
                    e.LastStartUtc = old.LastStartUtc;
                    e.LastEndUtc = old.LastEndUtc;
                    e.LastExitCode = old.LastExitCode;
                    e.LastSuccess = old.LastSuccess;
                    e.RunsOk = old.RunsOk; e.RunsFailed = old.RunsFailed;
                    e.RunsTimeout = old.RunsTimeout; e.RunsSkipped = old.RunsSkipped;
                    e.MissedTotal = old.MissedTotal;
                }
                else
                {
                    // A job seen for the first time never backfills - it starts
                    // counting from the moment it was added, not from whenever its
                    // schedule would first have matched in the past.
                    e.LastCheckedUtc = now;
                }

                // An interval job with no due-time baseline yet (brand new, or only
                // ever run manually via --run-job) needs one fixed anchor here.
                // Without it, FindIntervalOccurrences would re-derive "N seconds from
                // whenever we happen to poll" on every tick - a moving target that
                // can never actually elapse.
                if (e.Cron == null && e.LastDueUtc <= 0) e.LastDueUtc = now;

                list.Add(e);
            }

            lock (_gate) { _jobs = list; }
        }

        private static Dictionary<string, JobEntry> ToMap(List<JobEntry> jobs)
        {
            var m = new Dictionary<string, JobEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (JobEntry e in jobs) m[e.Config.Name] = e;
            return m;
        }

        // ------------------------------------------------------------------ loop

        private void Loop()
        {
            while (_running)
            {
                try
                {
                    List<JobEntry> snapshot;
                    lock (_gate) { snapshot = _jobs; }

                    DateTime nowLocal = DateTime.Now;
                    double nowUtc = Scanner.Now();
                    foreach (JobEntry e in snapshot)
                    {
                        if (!_running) break;
                        Evaluate(e, nowLocal, nowUtc);
                    }
                    Persist();
                }
                catch (Exception ex)
                {
                    _log.Error("job scheduler loop error: " + ex.Message);
                }

                int pollMs = Math.Max(5, _cfg.JobPollIntervalSeconds) * 1000;
                for (int slept = 0; slept < pollMs && _running; slept += 250) Thread.Sleep(250);
            }
        }

        private void Evaluate(JobEntry e, DateTime nowLocal, double nowUtc)
        {
            List<DateTime> due;
            DateTime nowFloor = Floor(nowLocal);

            if (e.Cron != null)
            {
                DateTime lastMinute = e.LastCheckedUtc > 0 ? Floor(FromUnix(e.LastCheckedUtc)) : nowFloor;
                double graceSeconds = Math.Max(e.Config.MissedGraceSeconds, 60);
                DateTime clamp = Floor(nowLocal.AddSeconds(-(graceSeconds + 120)));
                DateTime fromLocal = lastMinute > clamp ? lastMinute : clamp;

                due = FindCronOccurrences(e.Cron, fromLocal, nowFloor, 20000);

                if (lastMinute < clamp)
                {
                    _log.Warn("job \"" + e.Config.Name + "\" was not evaluated for " +
                              FormatDuration((nowFloor - lastMinute).TotalSeconds) +
                              "; occurrences before " + clamp.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) +
                              " were not evaluated (missed_grace_seconds)");
                }

                e.LastCheckedUtc = ToUnix(nowFloor);
            }
            else
            {
                due = FindIntervalOccurrences(e, nowUtc);
                e.LastCheckedUtc = nowUtc;
            }

            if (due.Count > 0) ApplyOnMissedAndDispatch(e, due);
        }

        private static List<DateTime> FindCronOccurrences(CronSchedule cron, DateTime fromLocal, DateTime toLocalInclusive, int cap)
        {
            var hits = new List<DateTime>();
            DateTime t = fromLocal.AddMinutes(1);
            int steps = 0;
            while (t <= toLocalInclusive && steps < cap)
            {
                if (cron.Matches(t)) hits.Add(t);
                t = t.AddMinutes(1);
                steps++;
            }
            return hits;
        }

        private List<DateTime> FindIntervalOccurrences(JobEntry e, double nowUtc)
        {
            var hits = new List<DateTime>();
            int interval = e.Config.EverySeconds;
            if (interval <= 0) return hits;

            // No prior due time: start counting from now, so a brand-new interval
            // job's first run is one interval away rather than immediate.
            double baseline = e.LastDueUtc > 0 ? e.LastDueUtc : nowUtc;
            double next = baseline + interval;
            int guard = 0;
            while (next <= nowUtc && guard < 20000)
            {
                hits.Add(FromUnix(next));
                next += interval;
                guard++;
            }
            if (guard >= 20000)
                _log.Warn("job \"" + e.Config.Name + "\" has an extremely large backlog of missed intervals; older ones were dropped without counting them");
            return hits;
        }

        private void ApplyOnMissedAndDispatch(JobEntry e, List<DateTime> dueLocal)
        {
            if (dueLocal.Count == 1)
            {
                // The single normal case: exactly one boundary was crossed since the
                // last check. Always runs - on_missed only governs a genuine backlog,
                // and a lone occurrence is not one, however late this particular poll
                // tick happened to land.
                Dispatch(e, dueLocal);
                return;
            }

            switch (e.Config.OnMissed)
            {
                case "skip":
                {
                    Interlocked.Add(ref e.MissedTotal, dueLocal.Count);
                    break;
                }
                case "run_all":
                {
                    // One worker runs the whole burst sequentially - dispatching each
                    // occurrence as its own Dispatch() call would have every one past
                    // the first find the job already "running" and be discarded by
                    // if_running before it ever got to execute.
                    const int maxBurst = 20;
                    int start = Math.Max(0, dueLocal.Count - maxBurst);
                    if (start > 0) Interlocked.Add(ref e.MissedTotal, start);
                    Dispatch(e, dueLocal.GetRange(start, dueLocal.Count - start));
                    break;
                }
                default: // run_once
                {
                    Interlocked.Add(ref e.MissedTotal, dueLocal.Count - 1);
                    Dispatch(e, new List<DateTime> { dueLocal[dueLocal.Count - 1] });
                    break;
                }
            }
        }

        private void Dispatch(JobEntry e, List<DateTime> dueTimes)
        {
            if (dueTimes.Count == 0) return;
            e.LastDueUtc = ToUnix(dueTimes[dueTimes.Count - 1]);

            if (e.Running)
            {
                if (e.Config.IfRunning == "queue")
                {
                    e.RerunPending = true;
                    _log.Debug("job \"" + e.Config.Name + "\" due while already running; queued to run again on completion");
                }
                else
                {
                    Interlocked.Add(ref e.RunsSkipped, dueTimes.Count);
                    _log.Warn("job \"" + e.Config.Name + "\" due while a previous run is still in progress; skipping " +
                              dueTimes.Count + " occurrence(s) (if_running: skip)");
                }
                return;
            }

            e.Running = true;
            var burst = new DispatchBurst { Entry = e, Due = dueTimes };
            ThreadPool.QueueUserWorkItem(RunWorker, burst);
        }

        private sealed class DispatchBurst
        {
            public JobEntry Entry;
            public List<DateTime> Due;
        }

        private void RunWorker(object state)
        {
            var burst = (DispatchBurst)state;
            JobEntry e = burst.Entry;
            try
            {
                foreach (DateTime unused in burst.Due)
                {
                    if (!_running) break;
                    RunOnce(e);
                }
                while (e.RerunPending && _running)
                {
                    e.RerunPending = false;
                    RunOnce(e);
                }
            }
            finally
            {
                e.Running = false;
            }
        }

        private void RunOnce(JobEntry e)
        {
            double startUtc = Scanner.Now();
            e.LastStartUtc = startUtc;
            _log.Info("job \"" + e.Config.Name + "\" starting: " + e.Config.Command);

            JobRunResult r = JobRunner.Run(e.Config, _cfg.JobLogDirectory, _log);

            e.LastEndUtc = Scanner.Now();
            e.LastExitCode = r.ExitCode;
            e.LastSuccess = r.Started && !r.TimedOut && r.ExitCode == 0;

            if (!r.Started)
            {
                Interlocked.Increment(ref e.RunsFailed);
                _log.Error("job \"" + e.Config.Name + "\" failed to start: " + r.Error);
            }
            else if (r.TimedOut)
            {
                Interlocked.Increment(ref e.RunsTimeout);
                _log.Error("job \"" + e.Config.Name + "\" timed out after " + e.Config.TimeoutSeconds + "s and was killed");
            }
            else if (r.ExitCode != 0)
            {
                Interlocked.Increment(ref e.RunsFailed);
                _log.Warn("job \"" + e.Config.Name + "\" exited with code " + r.ExitCode +
                          " (" + r.DurationSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s)");
            }
            else
            {
                Interlocked.Increment(ref e.RunsOk);
                _log.Info("job \"" + e.Config.Name + "\" completed ok (" +
                          r.DurationSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s)");
            }

            Persist();
        }

        /// <summary>Runs one job immediately, outside its schedule - used by --run-job.
        /// Respects if_running so it never clobbers a run already in progress.</summary>
        public bool RunJobOnceByName(string name, out string message)
        {
            JobEntry e = null;
            List<JobEntry> snapshot;
            lock (_gate) { snapshot = _jobs; }
            foreach (JobEntry j in snapshot)
                if (string.Equals(j.Config.Name, name, StringComparison.OrdinalIgnoreCase)) { e = j; break; }

            if (e == null) { message = "no such job: \"" + name + "\""; return false; }
            if (e.Running) { message = "job \"" + name + "\" is already running"; return false; }

            e.Running = true;
            // Anchors an interval job's next scheduled run to this manual run,
            // rather than leaving a stale or zero baseline behind.
            e.LastDueUtc = Scanner.Now();
            try { RunOnce(e); }
            finally { e.Running = false; }

            message = e.LastSuccess
                ? "ok, exit code " + e.LastExitCode
                : "FAILED, exit code " + e.LastExitCode;
            return true;
        }

        // ------------------------------------------------------------------ time helpers

        private static DateTime Floor(DateTime t)
        {
            return new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, t.Kind);
        }

        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static double ToUnix(DateTime local)
        {
            return (local.ToUniversalTime() - UnixEpoch).TotalSeconds;
        }

        private static DateTime FromUnix(double unix)
        {
            return UnixEpoch.AddSeconds(unix).ToLocalTime();
        }

        private static string FormatDuration(double seconds)
        {
            if (seconds < 0) seconds = 0;
            if (seconds < 60) return ((int)seconds) + "s";
            if (seconds < 3600) return ((int)(seconds / 60)) + "m";
            if (seconds < 86400) return (seconds / 3600).ToString("0.#", CultureInfo.InvariantCulture) + "h";
            return (seconds / 86400).ToString("0.#", CultureInfo.InvariantCulture) + "d";
        }

        private double NextDueEstimate(JobEntry e, double now)
        {
            if (e.Cron != null)
            {
                DateTime t = Floor(FromUnix(now)).AddMinutes(1);
                for (int i = 0; i < 200000; i++)
                {
                    if (e.Cron.Matches(t)) return ToUnix(t);
                    t = t.AddMinutes(1);
                }
                return 0;
            }
            if (e.Config.EverySeconds > 0)
            {
                double baseline = e.LastDueUtc > 0 ? e.LastDueUtc : now;
                return baseline + e.Config.EverySeconds;
            }
            return 0;
        }

        // ------------------------------------------------------------------ persistence

        private void Persist()
        {
            List<JobEntry> snapshot;
            lock (_gate) { snapshot = _jobs; }
            if (_store == null) return;

            var records = new Dictionary<string, JobStateStore.Record>(StringComparer.OrdinalIgnoreCase);
            foreach (JobEntry e in snapshot)
            {
                var r = new JobStateStore.Record();
                r.LastChecked = e.LastCheckedUtc;
                r.LastDue = e.LastDueUtc;
                r.LastStart = e.LastStartUtc;
                r.LastEnd = e.LastEndUtc;
                r.LastExitCode = e.LastExitCode;
                r.LastSuccess = e.LastSuccess;
                r.RunsOk = e.RunsOk; r.RunsFailed = e.RunsFailed;
                r.RunsTimeout = e.RunsTimeout; r.RunsSkipped = e.RunsSkipped;
                r.MissedTotal = e.MissedTotal;
                records[e.Config.Name] = r;
            }

            try { _store.Save(records); }
            catch (Exception ex) { _log.Warn("could not save job state: " + ex.Message); }
        }

        // ------------------------------------------------------------------ rendering

        public string RenderMetrics(double now)
        {
            List<JobEntry> snapshot;
            lock (_gate) { snapshot = _jobs; }
            var sb = new StringBuilder();

            Family(sb, "scheduler_jobs_configured", "Number of jobs configured and enabled.", "gauge");
            sb.Append("scheduler_jobs_configured ").Append(snapshot.Count).Append('\n');

            Family(sb, "scheduler_job_running", "1 if the job is currently executing.", "gauge");
            foreach (JobEntry e in snapshot) Line(sb, "scheduler_job_running", e.Config.Name, e.Running ? 1 : 0);

            Family(sb, "scheduler_job_last_start_timestamp_seconds", "When this job last started (unix seconds).", "gauge");
            foreach (JobEntry e in snapshot) if (e.LastStartUtc > 0) Line(sb, "scheduler_job_last_start_timestamp_seconds", e.Config.Name, e.LastStartUtc);

            Family(sb, "scheduler_job_last_end_timestamp_seconds", "When this job last finished (unix seconds).", "gauge");
            foreach (JobEntry e in snapshot) if (e.LastEndUtc > 0) Line(sb, "scheduler_job_last_end_timestamp_seconds", e.Config.Name, e.LastEndUtc);

            Family(sb, "scheduler_job_last_success_timestamp_seconds", "When this job last exited 0 (unix seconds).", "gauge");
            foreach (JobEntry e in snapshot) if (e.LastSuccess) Line(sb, "scheduler_job_last_success_timestamp_seconds", e.Config.Name, e.LastEndUtc);

            Family(sb, "scheduler_job_last_exit_code", "Exit code of the last completed run; -1 if it never ran or failed to start.", "gauge");
            foreach (JobEntry e in snapshot) Line(sb, "scheduler_job_last_exit_code", e.Config.Name, e.LastExitCode);

            Family(sb, "scheduler_job_last_duration_seconds", "Wall-clock duration of the last completed run.", "gauge");
            foreach (JobEntry e in snapshot) if (e.LastEndUtc > 0 && e.LastStartUtc > 0) Line(sb, "scheduler_job_last_duration_seconds", e.Config.Name, Math.Max(0, e.LastEndUtc - e.LastStartUtc));

            Family(sb, "scheduler_job_last_due_timestamp_seconds", "The scheduled time of the last run this job attempted (unix seconds).", "gauge");
            foreach (JobEntry e in snapshot) if (e.LastDueUtc > 0) Line(sb, "scheduler_job_last_due_timestamp_seconds", e.Config.Name, e.LastDueUtc);

            Family(sb, "scheduler_job_next_due_timestamp_seconds", "Best-effort estimate of when this job is next scheduled to run (unix seconds).", "gauge");
            foreach (JobEntry e in snapshot)
            {
                double next = NextDueEstimate(e, now);
                if (next > 0) Line(sb, "scheduler_job_next_due_timestamp_seconds", e.Config.Name, next);
            }

            Family(sb, "scheduler_job_runs_total", "Completed runs of this job, by result.", "counter");
            foreach (JobEntry e in snapshot)
            {
                LineResult(sb, e.Config.Name, "ok", e.RunsOk);
                LineResult(sb, e.Config.Name, "failed", e.RunsFailed);
                LineResult(sb, e.Config.Name, "timeout", e.RunsTimeout);
                LineResult(sb, e.Config.Name, "overlap_skipped", e.RunsSkipped);
            }

            Family(sb, "scheduler_job_missed_total", "Scheduled occurrences dropped by on_missed policy or the catch-up grace window.", "counter");
            foreach (JobEntry e in snapshot) Line(sb, "scheduler_job_missed_total", e.Config.Name, e.MissedTotal);

            Family(sb, "scheduler_up", "1 if the job scheduler loop is running.", "gauge");
            sb.Append("scheduler_up ").Append(_running ? 1 : 0).Append('\n');

            return sb.ToString();
        }

        private static void Family(StringBuilder sb, string name, string help, string type)
        {
            sb.Append("# HELP ").Append(name).Append(' ').Append(help.Replace("\\", "\\\\").Replace("\n", "\\n")).Append('\n');
            sb.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
        }

        private static void Line(StringBuilder sb, string name, string job, double value)
        {
            sb.Append(name).Append("{job=\"").Append(Metrics.Esc(job)).Append("\"} ").Append(Metrics.Fmt(value)).Append('\n');
        }

        private static void LineResult(StringBuilder sb, string job, string result, double value)
        {
            sb.Append("scheduler_job_runs_total{job=\"").Append(Metrics.Esc(job)).Append("\",result=\"").Append(result).Append("\"} ")
              .Append(Metrics.Fmt(value)).Append('\n');
        }

        public void RenderStatusRows(StringBuilder sb, double now)
        {
            List<JobEntry> snapshot;
            lock (_gate) { snapshot = _jobs; }
            if (snapshot.Count == 0) return;

            sb.Append("<h1>Scheduled jobs</h1>");
            sb.Append("<table><tr><th>Job</th><th>Schedule</th><th>Status</th><th>Last run</th>")
              .Append("<th>Result</th><th>Next due</th><th>Runs ok/failed/timeout</th></tr>");

            foreach (JobEntry e in snapshot)
            {
                sb.Append("<tr><td>").Append(H(e.Config.Name)).Append("</td><td><code>")
                  .Append(H(e.Cron != null ? e.Cron.Source : "every " + e.Config.EverySeconds + "s"))
                  .Append("</code></td>");
                sb.Append("<td>").Append(e.Running ? "<span class=\"ok\">running</span>" : "idle").Append("</td>");
                sb.Append("<td>").Append(e.LastStartUtc > 0 ? HumanAge(now - e.LastStartUtc) + " ago" : "-").Append("</td>");

                string result = e.LastEndUtc <= 0 ? "-" :
                    (e.LastSuccess ? "<span class=\"ok\">ok</span>" : "<span class=\"bad\">exit " + e.LastExitCode + "</span>");
                sb.Append("<td>").Append(result).Append("</td>");

                double next = NextDueEstimate(e, now);
                sb.Append("<td>").Append(next > 0 ? "in " + HumanAge(next - now) : "-").Append("</td>");
                sb.Append("<td>").Append(e.RunsOk).Append(" / ").Append(e.RunsFailed).Append(" / ").Append(e.RunsTimeout).Append("</td></tr>");
            }
            sb.Append("</table>");
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
