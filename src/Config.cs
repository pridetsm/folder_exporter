// Config.cs - loads folder_exporter.yml.
//
// Scope rule: this exporter reports on the server it runs on. Folder paths must
// resolve to local volumes; UNC paths and mapped network drives are rejected at
// load time rather than silently producing metrics about someone else's disk.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace FolderExporter
{
    public sealed class FolderConfig
    {
        public string Name;
        public string Path;
        public bool Recursive = true;
        public int MaxDepth;                     // 0 = unlimited
        public List<string> Include = new List<string>();
        public List<string> Exclude = new List<string>();
        public List<string> ExcludeDirectories = new List<string>();
        public bool FollowReparsePoints;
        public bool SkipHidden;
        public bool SkipSystem;
        public bool WatchEvents = true;
        public bool TrackChanges = true;
        public string ChangeTrackingMode = "hash";   // "hash" | "name"
        public bool ExposeFilenameLabels;
        public int MaxTrackedFiles = 5000000;
        public bool DiskMetrics = true;
        public bool ExtensionMetrics;
        public int TopExtensions = 10;
        public string AgeBasis = "write";            // "write" | "create"
        public int ScanIntervalSeconds;              // 0 = inherit global
        public bool CountInitialFilesAsAdded;
        public Dictionary<string, string> Labels = new Dictionary<string, string>();

        internal Regex[] IncludeRx;
        internal Regex[] ExcludeRx;
        internal Regex[] ExcludeDirRx;

        public void Prepare()
        {
            IncludeRx = CompileAll(Include);
            ExcludeRx = CompileAll(Exclude);
            ExcludeDirRx = CompileAll(ExcludeDirectories);
        }

        private static Regex[] CompileAll(List<string> patterns)
        {
            if (patterns == null || patterns.Count == 0) return null;
            var list = new List<Regex>(patterns.Count);
            foreach (string p in patterns)
            {
                if (string.IsNullOrEmpty(p)) continue;
                string rx = "^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
                list.Add(new Regex(rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));
            }
            return list.Count == 0 ? null : list.ToArray();
        }

        public FolderConfig CloneFrom()
        {
            var t = new FolderConfig();
            t.Recursive = Recursive;
            t.MaxDepth = MaxDepth;
            t.Include = new List<string>(Include);
            t.Exclude = new List<string>(Exclude);
            t.ExcludeDirectories = new List<string>(ExcludeDirectories);
            t.FollowReparsePoints = FollowReparsePoints;
            t.SkipHidden = SkipHidden;
            t.SkipSystem = SkipSystem;
            t.WatchEvents = WatchEvents;
            t.TrackChanges = TrackChanges;
            t.ChangeTrackingMode = ChangeTrackingMode;
            t.ExposeFilenameLabels = ExposeFilenameLabels;
            t.MaxTrackedFiles = MaxTrackedFiles;
            t.DiskMetrics = DiskMetrics;
            t.ExtensionMetrics = ExtensionMetrics;
            t.TopExtensions = TopExtensions;
            t.AgeBasis = AgeBasis;
            t.ScanIntervalSeconds = ScanIntervalSeconds;
            t.CountInitialFilesAsAdded = CountInitialFilesAsAdded;
            return t;
        }
    }

    public sealed class JobConfig
    {
        public string Name;
        public bool Enabled = true;
        public string Cron = "";           // 5-field cron; mutually exclusive with EverySeconds
        public int EverySeconds;
        public string Command = "";
        public List<string> Args = new List<string>();
        public string Shell = "auto";      // auto | powershell | cmd | exe
        public string WorkingDirectory = "";
        public int TimeoutSeconds = 3600;
        public string IfRunning = "skip";      // skip | queue
        public string OnMissed = "run_once";   // skip | run_once | run_all
        public int MissedGraceSeconds = 7200;
        public Dictionary<string, string> Env = new Dictionary<string, string>();

        public JobConfig CloneFrom()
        {
            var j = new JobConfig();
            j.Enabled = Enabled;
            j.Cron = Cron;
            j.EverySeconds = EverySeconds;
            j.Command = Command;
            j.Args = new List<string>(Args);
            j.Shell = Shell;
            j.WorkingDirectory = WorkingDirectory;
            j.TimeoutSeconds = TimeoutSeconds;
            j.IfRunning = IfRunning;
            j.OnMissed = OnMissed;
            j.MissedGraceSeconds = MissedGraceSeconds;
            j.Env = new Dictionary<string, string>(Env);
            return j;
        }
    }

    public sealed class Config
    {
        public string ListenAddress = "0.0.0.0:9847";
        public string MetricsPath = "/metrics";
        public int ScanIntervalSeconds = 60;
        public int ScanTimeoutSeconds = 900;
        public int MaxConcurrentScans = 1;
        public bool LowPriority = true;
        public bool TrimWorkingSet = true;
        public int ThrottleEveryFiles;
        public int ThrottleSleepMs = 5;
        public bool ScanOnStartup = true;
        public string LogLevel = "info";
        public string LogFile = "";
        public long LogMaxBytes = 8 * 1024 * 1024;
        public string BasicAuthUser = "";
        public string BasicAuthPassword = "";
        public double[] FileAgeBucketsSeconds = new double[] { 300, 3600, 21600, 86400, 604800, 2592000 };
        public List<FolderConfig> Folders = new List<FolderConfig>();

        public int JobPollIntervalSeconds = 15;
        public string JobStateFile = @"C:\ProgramData\folder_exporter\jobs\state.tsv";
        public string JobLogDirectory = @"C:\ProgramData\folder_exporter\jobs\logs";
        public List<JobConfig> Jobs = new List<JobConfig>();

        public string SourcePath = "";
        public DateTime SourceStamp;

        public string ListenHost
        {
            get
            {
                int i = ListenAddress.LastIndexOf(':');
                return i < 0 ? ListenAddress : ListenAddress.Substring(0, i);
            }
        }

        public int ListenPort
        {
            get
            {
                int i = ListenAddress.LastIndexOf(':');
                int p;
                if (i >= 0 && int.TryParse(ListenAddress.Substring(i + 1), out p)) return p;
                return 9847;
            }
        }

        public static Config Load(string path)
        {
            Dictionary<string, object> root;
            try
            {
                root = Yaml.ParseFile(path);
            }
            catch (YamlException ex)
            {
                throw new Exception("invalid YAML in " + Path.GetFileName(path) + ", " + ex.Message);
            }

            var c = new Config();
            c.SourcePath = Path.GetFullPath(path);
            c.SourceStamp = File.GetLastWriteTimeUtc(path);

            c.ListenAddress = Str(root, "listen_address", c.ListenAddress);
            c.MetricsPath = Str(root, "metrics_path", c.MetricsPath);
            if (!c.MetricsPath.StartsWith("/")) c.MetricsPath = "/" + c.MetricsPath;
            c.ScanIntervalSeconds = Math.Max(1, Int(root, "scan_interval_seconds", c.ScanIntervalSeconds));
            c.ScanTimeoutSeconds = Math.Max(0, Int(root, "scan_timeout_seconds", c.ScanTimeoutSeconds));
            c.MaxConcurrentScans = Math.Max(1, Int(root, "max_concurrent_scans", c.MaxConcurrentScans));
            c.LowPriority = Bool(root, "low_priority", c.LowPriority);
            c.TrimWorkingSet = Bool(root, "trim_working_set", c.TrimWorkingSet);
            c.ThrottleEveryFiles = Math.Max(0, Int(root, "throttle_every_files", c.ThrottleEveryFiles));
            c.ThrottleSleepMs = Math.Max(0, Int(root, "throttle_sleep_ms", c.ThrottleSleepMs));
            c.ScanOnStartup = Bool(root, "scan_on_startup", c.ScanOnStartup);
            c.LogLevel = Str(root, "log_level", c.LogLevel).ToLowerInvariant();
            c.LogFile = Str(root, "log_file", c.LogFile);
            c.LogMaxBytes = (long)Dbl(root, "log_max_bytes", c.LogMaxBytes);

            Dictionary<string, object> auth = Map(root, "basic_auth");
            if (auth != null)
            {
                c.BasicAuthUser = Str(auth, "username", "");
                c.BasicAuthPassword = Str(auth, "password", "");
            }

            List<object> buckets = List(root, "file_age_buckets_seconds");
            if (buckets != null && buckets.Count > 0)
            {
                var vals = new List<double>();
                foreach (object o in buckets)
                {
                    double d;
                    if (Yaml.TryDouble(o, out d) && d > 0) vals.Add(d);
                }
                vals.Sort();
                if (vals.Count > 0) c.FileAgeBucketsSeconds = vals.ToArray();
            }

            var defaults = new FolderConfig();
            Dictionary<string, object> defMap = Map(root, "defaults");
            if (defMap != null) ApplyFolder(defaults, defMap);

            List<object> folders = List(root, "folders");
            if (folders == null || folders.Count == 0)
                throw new Exception("no folders configured - add entries under \"folders:\" in " + Path.GetFileName(path));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object o in folders)
            {
                Dictionary<string, object> fo = Yaml.AsMap(o);
                if (fo == null)
                    throw new Exception("each entry under \"folders:\" must be a mapping with at least a \"path:\"");

                FolderConfig f = defaults.CloneFrom();
                ApplyFolder(f, fo);

                if (string.IsNullOrEmpty(f.Path))
                    throw new Exception("a folder entry is missing its \"path:\"");

                f.Path = NormalizePath(f.Path);
                RequireLocalPath(f.Path);

                if (string.IsNullOrEmpty(f.Name)) f.Name = DeriveName(f.Path);
                if (!seen.Add(f.Name))
                    throw new Exception("duplicate folder name \"" + f.Name + "\" - names must be unique");
                if (f.ChangeTrackingMode != "name") f.ChangeTrackingMode = "hash";
                if (f.AgeBasis != "create") f.AgeBasis = "write";
                f.Prepare();
                c.Folders.Add(f);
            }

            c.JobPollIntervalSeconds = Math.Max(5, Int(root, "job_poll_interval_seconds", c.JobPollIntervalSeconds));
            c.JobStateFile = Str(root, "job_state_file", c.JobStateFile);
            c.JobLogDirectory = Str(root, "job_log_directory", c.JobLogDirectory);
            if (c.JobStateFile.Length > 0) c.JobStateFile = Environment.ExpandEnvironmentVariables(c.JobStateFile);
            if (c.JobLogDirectory.Length > 0) c.JobLogDirectory = Environment.ExpandEnvironmentVariables(c.JobLogDirectory);

            var jobDefaults = new JobConfig();
            Dictionary<string, object> jobDefMap = Map(root, "job_defaults");
            if (jobDefMap != null) ApplyJob(jobDefaults, jobDefMap);

            List<object> jobs = List(root, "jobs");
            if (jobs != null)
            {
                var seenJobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (object o in jobs)
                {
                    Dictionary<string, object> jo = Yaml.AsMap(o);
                    if (jo == null)
                        throw new Exception("each entry under \"jobs:\" must be a mapping with at least a \"name:\" and \"command:\"");

                    JobConfig j = jobDefaults.CloneFrom();
                    ApplyJob(j, jo);

                    if (string.IsNullOrEmpty(j.Name))
                        throw new Exception("a job entry is missing its \"name:\"");
                    if (!seenJobs.Add(j.Name))
                        throw new Exception("duplicate job name \"" + j.Name + "\" - names must be unique");
                    if (string.IsNullOrEmpty(j.Command))
                        throw new Exception("job \"" + j.Name + "\" is missing its \"command:\"");

                    bool hasCron = !string.IsNullOrEmpty(j.Cron);
                    bool hasEvery = j.EverySeconds > 0;
                    if (hasCron == hasEvery)
                        throw new Exception("job \"" + j.Name + "\" must set exactly one of \"cron:\" or \"every_seconds:\"");
                    if (hasCron)
                    {
                        try { CronSchedule.Validate(j.Cron); }
                        catch (Exception ex) { throw new Exception("job \"" + j.Name + "\" has an invalid cron expression \"" + j.Cron + "\": " + ex.Message); }
                    }

                    j.Shell = NormalizeJobEnum(j.Shell, "shell", j.Name, "auto", "powershell", "cmd", "exe");
                    j.IfRunning = NormalizeJobEnum(j.IfRunning, "if_running", j.Name, "skip", "queue");
                    j.OnMissed = NormalizeJobEnum(j.OnMissed, "on_missed", j.Name, "skip", "run_once", "run_all");
                    if (j.TimeoutSeconds <= 0) j.TimeoutSeconds = 3600;
                    if (j.MissedGraceSeconds < 0) j.MissedGraceSeconds = 0;

                    j.Command = Environment.ExpandEnvironmentVariables(j.Command);
                    if (!string.IsNullOrEmpty(j.WorkingDirectory))
                        j.WorkingDirectory = Environment.ExpandEnvironmentVariables(j.WorkingDirectory);

                    c.Jobs.Add(j);
                }
            }

            return c;
        }

        private static void ApplyJob(JobConfig j, Dictionary<string, object> o)
        {
            j.Name = Str(o, "name", j.Name);
            j.Enabled = Bool(o, "enabled", j.Enabled);
            j.Cron = Str(o, "cron", j.Cron);
            j.EverySeconds = Int(o, "every_seconds", j.EverySeconds);
            j.Command = Str(o, "command", j.Command);
            j.Args = StrList(o, "args", j.Args);
            j.Shell = Str(o, "shell", j.Shell).ToLowerInvariant();
            j.WorkingDirectory = Str(o, "working_directory", j.WorkingDirectory);
            j.TimeoutSeconds = Int(o, "timeout_seconds", j.TimeoutSeconds);
            j.IfRunning = Str(o, "if_running", j.IfRunning).ToLowerInvariant();
            j.OnMissed = Str(o, "on_missed", j.OnMissed).ToLowerInvariant();
            j.MissedGraceSeconds = Int(o, "missed_grace_seconds", j.MissedGraceSeconds);

            Dictionary<string, object> env = Map(o, "env");
            if (env != null)
            {
                foreach (KeyValuePair<string, object> kv in env)
                {
                    string v = kv.Value as string;
                    if (v != null) j.Env[kv.Key] = v;
                }
            }
        }

        private static string NormalizeJobEnum(string value, string field, string jobName, params string[] allowed)
        {
            string v = (value ?? "").Trim().ToLowerInvariant();
            foreach (string a in allowed) if (v == a) return v;
            throw new Exception("job \"" + jobName + "\": \"" + field + "\" must be one of: " + string.Join(", ", allowed));
        }

        /// <summary>
        /// Expands and validates a configured path. Rejects UNC paths and anything
        /// not absolute: a relative path would resolve against the working
        /// directory, which for a Windows service is System32 - never what was meant.
        /// </summary>
        private static string NormalizePath(string raw)
        {
            string p = Environment.ExpandEnvironmentVariables(raw.Trim());

            if (p.StartsWith(@"\\", StringComparison.Ordinal))
                throw new Exception(
                    "\"" + raw + "\" is a network (UNC) path. folder_exporter is scoped to the server it runs on; " +
                    "run an instance on the file server that owns this share instead.");

            bool rooted = p.Length >= 3 && char.IsLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/');
            if (!rooted)
                throw new Exception(
                    "\"" + raw + "\" is not an absolute local path. Use a full path such as D:\\data\\inbound.");

            p = Path.GetFullPath(p).TrimEnd('\\');
            if (p.Length == 2 && p[1] == ':') p += "\\";   // "C:" -> "C:\"
            return p;
        }

        /// <summary>
        /// Enforces that a folder lives on this server. Metrics from a remote share
        /// would be attributed to this host's `instance` label in Prometheus, which
        /// makes them actively misleading.
        /// </summary>
        private static void RequireLocalPath(string p)
        {
            try
            {
                var drive = new DriveInfo(p.Substring(0, 1));
                if (drive.DriveType == DriveType.Network)
                    throw new Exception(
                        "drive " + p.Substring(0, 2) + " is a mapped network drive. folder_exporter is scoped to the " +
                        "server it runs on; run an instance on the server that owns the storage instead.");
            }
            catch (ArgumentException)
            {
                // Drive letter is not currently mounted. That is a runtime condition,
                // not a config error - folder_exists will report it as 0.
            }
        }

        private static void ApplyFolder(FolderConfig f, Dictionary<string, object> o)
        {
            f.Name = Str(o, "name", f.Name);
            f.Path = Str(o, "path", f.Path);
            f.Recursive = Bool(o, "recursive", f.Recursive);
            f.MaxDepth = Int(o, "max_depth", f.MaxDepth);
            f.Include = StrList(o, "include", f.Include);
            f.Exclude = StrList(o, "exclude", f.Exclude);
            f.ExcludeDirectories = StrList(o, "exclude_directories", f.ExcludeDirectories);
            f.FollowReparsePoints = Bool(o, "follow_reparse_points", f.FollowReparsePoints);
            f.SkipHidden = Bool(o, "skip_hidden", f.SkipHidden);
            f.SkipSystem = Bool(o, "skip_system", f.SkipSystem);
            f.WatchEvents = Bool(o, "watch_events", f.WatchEvents);
            f.TrackChanges = Bool(o, "track_changes", f.TrackChanges);
            f.ChangeTrackingMode = Str(o, "change_tracking_mode", f.ChangeTrackingMode).ToLowerInvariant();
            f.ExposeFilenameLabels = Bool(o, "expose_filename_labels", f.ExposeFilenameLabels);
            f.MaxTrackedFiles = Int(o, "max_tracked_files", f.MaxTrackedFiles);
            f.DiskMetrics = Bool(o, "disk_metrics", f.DiskMetrics);
            f.ExtensionMetrics = Bool(o, "extension_metrics", f.ExtensionMetrics);
            f.TopExtensions = Int(o, "top_extensions", f.TopExtensions);
            f.AgeBasis = Str(o, "age_basis", f.AgeBasis).ToLowerInvariant();
            f.ScanIntervalSeconds = Int(o, "scan_interval_seconds", f.ScanIntervalSeconds);
            f.CountInitialFilesAsAdded = Bool(o, "count_initial_files_as_added", f.CountInitialFilesAsAdded);

            Dictionary<string, object> lbl = Map(o, "labels");
            if (lbl != null)
            {
                foreach (KeyValuePair<string, object> kv in lbl)
                {
                    string v = kv.Value as string;
                    if (v == null) continue;
                    if (!IsValidLabelName(kv.Key))
                        throw new Exception("\"" + kv.Key + "\" is not a valid Prometheus label name");
                    f.Labels[kv.Key] = v;
                }
            }
        }

        private static bool IsValidLabelName(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (!(char.IsLetter(s[0]) || s[0] == '_')) return false;
            foreach (char ch in s)
                if (!(char.IsLetterOrDigit(ch) || ch == '_')) return false;
            // Reserved by the exporter itself.
            return !s.Equals("target", StringComparison.OrdinalIgnoreCase) &&
                   !s.Equals("path", StringComparison.OrdinalIgnoreCase);
        }

        private static string DeriveName(string path)
        {
            string n = path.Replace('\\', '_').Replace(':', '_').Replace(' ', '_');
            return n.Trim('_').ToLowerInvariant();
        }

        // ---- typed accessors over the parsed YAML tree ---------------------------

        private static string Str(Dictionary<string, object> o, string k, string def)
        {
            object v;
            if (!o.TryGetValue(k, out v) || v == null) return def;
            string s = v as string;
            return s ?? def;
        }

        private static bool Bool(Dictionary<string, object> o, string k, bool def)
        {
            object v;
            if (!o.TryGetValue(k, out v)) return def;
            bool b;
            if (Yaml.TryBool(v, out b)) return b;
            throw new Exception("\"" + k + "\" must be true or false");
        }

        private static int Int(Dictionary<string, object> o, string k, int def)
        {
            return (int)Dbl(o, k, def);
        }

        private static double Dbl(Dictionary<string, object> o, string k, double def)
        {
            object v;
            if (!o.TryGetValue(k, out v)) return def;
            double d;
            if (Yaml.TryDouble(v, out d)) return d;
            throw new Exception("\"" + k + "\" must be a number");
        }

        private static Dictionary<string, object> Map(Dictionary<string, object> o, string k)
        {
            object v;
            if (o.TryGetValue(k, out v)) return Yaml.AsMap(v);
            return null;
        }

        private static List<object> List(Dictionary<string, object> o, string k)
        {
            object v;
            if (o.TryGetValue(k, out v)) return Yaml.AsList(v);
            return null;
        }

        private static List<string> StrList(Dictionary<string, object> o, string k, List<string> def)
        {
            object v;
            if (!o.TryGetValue(k, out v) || v == null) return def;
            List<object> raw = Yaml.AsList(v);
            var list = new List<string>();
            if (raw != null)
            {
                foreach (object e in raw)
                {
                    string s = e as string;
                    if (!string.IsNullOrEmpty(s)) list.Add(s);
                }
            }
            else
            {
                string s = v as string;
                if (!string.IsNullOrEmpty(s)) list.Add(s);
            }
            return list;
        }
    }
}
