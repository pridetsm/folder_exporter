// Metrics.cs - renders the Prometheus text exposition format (version 0.0.4).
//
// Rendering happens at scrape time, but only from the cached ScanResult objects:
// no disk is touched. That keeps a scrape at well under a millisecond even for
// dozens of targets, and means a slow/hung filesystem can never stall Prometheus.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace FolderExporter
{
    public static class Metrics
    {
        /* Bump this whenever the binary changes behaviour. It rides on
           folder_exporter_build_info, which is the only way to tell from Prometheus which
           build a server is actually running — without it a deployment that silently did
           not happen looks identical to one that did.

           1.1.0  fixed-cadence scan scheduling (was drifting: each cycle absorbed the
                  dispatch latency, so folders slid tens of seconds apart over a day);
                  --once no longer writes log lines to stdout; log directory is created. */
        public const string Version = "1.1.0";

        public static string Render(IList<ScanResult> results, ExporterStats stats)
        {
            var sb = new StringBuilder(16 * 1024);
            double now = Scanner.Now();

            // ---------------- folder state ----------------
            Family(sb, "folder_up", "1 if the last scan of this target completed successfully.", "gauge");
            foreach (var r in results) Sample(sb, "folder_up", r, r.Success ? 1 : 0);

            Family(sb, "folder_exists", "1 if the configured path exists and is a directory.", "gauge");
            foreach (var r in results) Sample(sb, "folder_exists", r, r.Exists ? 1 : 0);

            Family(sb, "folder_size_bytes", "Total size of all matching files in the folder.", "gauge");
            foreach (var r in results) if (r.Exists) Sample(sb, "folder_size_bytes", r, r.SizeBytes);

            Family(sb, "folder_files", "Number of matching files in the folder.", "gauge");
            foreach (var r in results) if (r.Exists) Sample(sb, "folder_files", r, r.FileCount);

            Family(sb, "folder_directories", "Number of subdirectories in the folder.", "gauge");
            foreach (var r in results) if (r.Exists) Sample(sb, "folder_directories", r, r.DirectoryCount);

            Family(sb, "folder_created_timestamp_seconds", "Creation time of the folder itself (unix seconds).", "gauge");
            foreach (var r in results) if (r.Exists && r.FolderCreatedTs > 0) Sample(sb, "folder_created_timestamp_seconds", r, r.FolderCreatedTs);

            Family(sb, "folder_age_seconds", "Age of the folder itself, i.e. now minus its creation time.", "gauge");
            foreach (var r in results) if (r.Exists && r.FolderCreatedTs > 0) Sample(sb, "folder_age_seconds", r, Math.Max(0, now - r.FolderCreatedTs));

            Family(sb, "folder_modified_timestamp_seconds", "Last write time of the folder itself (unix seconds).", "gauge");
            foreach (var r in results) if (r.Exists && r.FolderModifiedTs > 0) Sample(sb, "folder_modified_timestamp_seconds", r, r.FolderModifiedTs);

            // ---------------- file timestamps / ages ----------------
            Family(sb, "folder_oldest_file_timestamp_seconds", "Timestamp of the oldest file in the folder (unix seconds).", "gauge");
            foreach (var r in results) if (r.OldestFileTs > 0) Sample(sb, "folder_oldest_file_timestamp_seconds", r, r.OldestFileTs);

            Family(sb, "folder_oldest_file_age_seconds", "Age of the oldest file in the folder.", "gauge");
            foreach (var r in results) if (r.OldestFileTs > 0) Sample(sb, "folder_oldest_file_age_seconds", r, Math.Max(0, now - r.OldestFileTs));

            Family(sb, "folder_newest_file_timestamp_seconds", "Timestamp of the newest file in the folder (unix seconds).", "gauge");
            foreach (var r in results) if (r.NewestFileTs > 0) Sample(sb, "folder_newest_file_timestamp_seconds", r, r.NewestFileTs);

            Family(sb, "folder_newest_file_age_seconds", "Age of the newest file in the folder. Rises when nothing new arrives.", "gauge");
            foreach (var r in results) if (r.NewestFileTs > 0) Sample(sb, "folder_newest_file_age_seconds", r, Math.Max(0, now - r.NewestFileTs));

            Family(sb, "folder_largest_file_bytes", "Size of the largest file in the folder.", "gauge");
            foreach (var r in results) if (r.Exists) Sample(sb, "folder_largest_file_bytes", r, r.LargestFileBytes);

            // ---------------- add / remove tracking ----------------
            Family(sb, "folder_files_added_total", "Files observed appearing in the folder since the exporter started.", "counter");
            foreach (var r in results) if (Tracked(r)) Sample(sb, "folder_files_added_total", r, r.FilesAddedTotal);

            Family(sb, "folder_files_removed_total", "Files observed disappearing from the folder since the exporter started.", "counter");
            foreach (var r in results) if (Tracked(r)) Sample(sb, "folder_files_removed_total", r, r.FilesRemovedTotal);

            Family(sb, "folder_files_added_last_scan", "Files that appeared between the two most recent scans.", "gauge");
            foreach (var r in results) if (Tracked(r)) Sample(sb, "folder_files_added_last_scan", r, r.AddedLastScan);

            Family(sb, "folder_files_removed_last_scan", "Files that disappeared between the two most recent scans.", "gauge");
            foreach (var r in results) if (Tracked(r)) Sample(sb, "folder_files_removed_last_scan", r, r.RemovedLastScan);

            Family(sb, "folder_last_file_added_timestamp_seconds", "When a file was last observed being added (unix seconds). 0 if never.", "gauge");
            foreach (var r in results) if (Tracked(r)) Sample(sb, "folder_last_file_added_timestamp_seconds", r, r.LastAddedTs);

            Family(sb, "folder_seconds_since_last_file_added", "Seconds since a file was last observed being added.", "gauge");
            foreach (var r in results) if (Tracked(r) && r.LastAddedTs > 0) Sample(sb, "folder_seconds_since_last_file_added", r, Math.Max(0, now - r.LastAddedTs));

            Family(sb, "folder_last_file_removed_timestamp_seconds", "When a file was last observed being removed (unix seconds). 0 if never.", "gauge");
            foreach (var r in results) if (Tracked(r)) Sample(sb, "folder_last_file_removed_timestamp_seconds", r, r.LastRemovedTs);

            Family(sb, "folder_seconds_since_last_file_removed", "Seconds since a file was last observed being removed.", "gauge");
            foreach (var r in results) if (Tracked(r) && r.LastRemovedTs > 0) Sample(sb, "folder_seconds_since_last_file_removed", r, Math.Max(0, now - r.LastRemovedTs));

            Family(sb, "folder_tracked_files", "Files currently held in the change-tracking index for this target.", "gauge");
            foreach (var r in results) if (Tracked(r)) Sample(sb, "folder_tracked_files", r, r.TrackedFiles);

            // ---------------- optional filename info metrics ----------------
            bool anyNames = false;
            foreach (var r in results) if (HasNameInfo(r)) { anyNames = true; break; }
            if (anyNames)
            {
                Family(sb, "folder_last_added_file_info", "Name of the most recently added file, as a label. Always 1.", "gauge");
                foreach (var r in results)
                    if (HasNameInfo(r) && r.LastAddedFile.Length > 0)
                        SampleWith(sb, "folder_last_added_file_info", r, new string[] { "file", r.LastAddedFile }, 1);

                Family(sb, "folder_last_removed_file_info", "Name of the most recently removed file, as a label. Always 1. Requires change_tracking_mode=name.", "gauge");
                foreach (var r in results)
                    if (HasNameInfo(r) && r.LastRemovedFile.Length > 0)
                        SampleWith(sb, "folder_last_removed_file_info", r, new string[] { "file", r.LastRemovedFile }, 1);

                Family(sb, "folder_newest_file_info", "Name of the newest file, as a label. Always 1.", "gauge");
                foreach (var r in results)
                    if (HasNameInfo(r) && r.NewestFileName.Length > 0)
                        SampleWith(sb, "folder_newest_file_info", r, new string[] { "file", r.NewestFileName }, 1);

                Family(sb, "folder_largest_file_info", "Name of the largest file, as a label. Always 1.", "gauge");
                foreach (var r in results)
                    if (HasNameInfo(r) && r.LargestFileName.Length > 0)
                        SampleWith(sb, "folder_largest_file_info", r, new string[] { "file", r.LargestFileName }, 1);
            }

            // ---------------- file age histogram ----------------
            bool anyHist = false;
            foreach (var r in results) if (r.Exists && r.AgeBucketBounds != null && r.AgeBucketBounds.Length > 0) { anyHist = true; break; }
            if (anyHist)
            {
                // Samples of one metric name are kept contiguous, so the output is
                // valid for both the Prometheus text parser and OpenMetrics tooling.
                Family(sb, "folder_file_age_seconds", "Distribution of file ages in the folder at last scan.", "histogram");
                foreach (var r in results)
                {
                    if (!r.Exists || r.AgeBucketBounds == null) continue;
                    long cum = 0;
                    for (int i = 0; i < r.AgeBucketBounds.Length; i++)
                    {
                        cum += r.AgeBucketCounts[i];
                        SampleWith(sb, "folder_file_age_seconds_bucket", r,
                            new string[] { "le", Fmt(r.AgeBucketBounds[i]) }, cum);
                    }
                    SampleWith(sb, "folder_file_age_seconds_bucket", r, new string[] { "le", "+Inf" }, r.AgeObservations);
                }
                foreach (var r in results)
                    if (r.Exists && r.AgeBucketBounds != null) Sample(sb, "folder_file_age_seconds_sum", r, r.AgeSumSeconds);
                foreach (var r in results)
                    if (r.Exists && r.AgeBucketBounds != null) Sample(sb, "folder_file_age_seconds_count", r, r.AgeObservations);
            }

            // ---------------- per-extension breakdown ----------------
            bool anyExt = false;
            foreach (var r in results) if (r.Extensions != null && r.Extensions.Count > 0) { anyExt = true; break; }
            if (anyExt)
            {
                Family(sb, "folder_extension_files", "Number of files per extension (top N by size).", "gauge");
                foreach (var r in results)
                {
                    if (r.Extensions == null) continue;
                    foreach (var e in r.Extensions)
                        SampleWith(sb, "folder_extension_files", r, new string[] { "extension", e.Extension }, e.Files);
                }
                Family(sb, "folder_extension_size_bytes", "Total bytes per extension (top N by size).", "gauge");
                foreach (var r in results)
                {
                    if (r.Extensions == null) continue;
                    foreach (var e in r.Extensions)
                        SampleWith(sb, "folder_extension_size_bytes", r, new string[] { "extension", e.Extension }, e.Bytes);
                }
            }

            // ---------------- volume ----------------
            bool anyDisk = false;
            foreach (var r in results) if (r.HasDisk) { anyDisk = true; break; }
            if (anyDisk)
            {
                Family(sb, "folder_volume_free_bytes", "Free bytes on the volume holding this folder.", "gauge");
                foreach (var r in results) if (r.HasDisk) SampleWith(sb, "folder_volume_free_bytes", r, new string[] { "volume", r.Volume }, r.DiskFreeBytes);

                Family(sb, "folder_volume_total_bytes", "Total bytes on the volume holding this folder.", "gauge");
                foreach (var r in results) if (r.HasDisk) SampleWith(sb, "folder_volume_total_bytes", r, new string[] { "volume", r.Volume }, r.DiskTotalBytes);
            }

            // ---------------- scan health ----------------
            Family(sb, "folder_last_scan_timestamp_seconds", "When this target was last scanned (unix seconds).", "gauge");
            foreach (var r in results) Sample(sb, "folder_last_scan_timestamp_seconds", r, r.ScanTimestamp);

            Family(sb, "folder_last_scan_duration_seconds", "Wall-clock duration of the last scan of this target.", "gauge");
            foreach (var r in results) Sample(sb, "folder_last_scan_duration_seconds", r, r.ScanDurationSeconds);

            Family(sb, "folder_scans_total", "Scans performed for this target since the exporter started.", "counter");
            foreach (var r in results) Sample(sb, "folder_scans_total", r, r.ScansTotal);

            Family(sb, "folder_scan_errors_total", "Errors (access denied, timeouts, vanished dirs) hit while scanning.", "counter");
            foreach (var r in results) Sample(sb, "folder_scan_errors_total", r, r.ScanErrorsTotal);

            Family(sb, "folder_scan_timed_out", "1 if the last scan hit scan_timeout_seconds and returned partial data.", "gauge");
            foreach (var r in results) Sample(sb, "folder_scan_timed_out", r, r.TimedOut ? 1 : 0);

            Family(sb, "folder_tracking_truncated", "1 if max_tracked_files was reached, making add/remove counts unreliable.", "gauge");
            foreach (var r in results) if (Tracked(r)) Sample(sb, "folder_tracking_truncated", r, r.TrackingTruncated ? 1 : 0);

            // ---------------- exporter self-monitoring ----------------
            Family(sb, "folder_exporter_build_info", "Exporter build information. Always 1.", "gauge");
            sb.Append("folder_exporter_build_info{version=\"").Append(Esc(Version))
              .Append("\",runtime=\"").Append(Esc(Environment.Version.ToString()))
              .Append("\",os=\"").Append(Esc(Environment.OSVersion.VersionString))
              .Append("\"} 1\n");

            Family(sb, "folder_exporter_start_time_seconds", "Unix time at which the exporter started.", "gauge");
            sb.Append("folder_exporter_start_time_seconds ").Append(Fmt(stats.StartTime)).Append('\n');

            Family(sb, "folder_exporter_uptime_seconds", "Seconds since the exporter started.", "gauge");
            sb.Append("folder_exporter_uptime_seconds ").Append(Fmt(Math.Max(0, now - stats.StartTime))).Append('\n');

            Family(sb, "folder_exporter_targets", "Number of configured targets.", "gauge");
            sb.Append("folder_exporter_targets ").Append(results.Count).Append('\n');

            Family(sb, "folder_exporter_scrapes_total", "Number of /metrics scrapes served.", "counter");
            sb.Append("folder_exporter_scrapes_total ").Append(stats.Scrapes).Append('\n');

            Family(sb, "folder_exporter_scan_cycles_total", "Number of completed scan cycles across all targets.", "counter");
            sb.Append("folder_exporter_scan_cycles_total ").Append(stats.ScanCycles).Append('\n');

            Family(sb, "folder_exporter_config_reloads_total", "Successful configuration reloads.", "counter");
            sb.Append("folder_exporter_config_reloads_total ").Append(stats.ConfigReloads).Append('\n');

            Family(sb, "folder_exporter_config_reload_failures_total", "Failed configuration reloads.", "counter");
            sb.Append("folder_exporter_config_reload_failures_total ").Append(stats.ConfigReloadFailures).Append('\n');

            try
            {
                var p = Process.GetCurrentProcess();
                Family(sb, "folder_exporter_resident_memory_bytes", "Working set of the exporter process.", "gauge");
                sb.Append("folder_exporter_resident_memory_bytes ").Append(p.WorkingSet64).Append('\n');

                Family(sb, "folder_exporter_private_memory_bytes", "Private bytes of the exporter process.", "gauge");
                sb.Append("folder_exporter_private_memory_bytes ").Append(p.PrivateMemorySize64).Append('\n');

                Family(sb, "folder_exporter_cpu_seconds_total", "Total CPU time consumed by the exporter.", "counter");
                sb.Append("folder_exporter_cpu_seconds_total ").Append(Fmt(p.TotalProcessorTime.TotalSeconds)).Append('\n');

                Family(sb, "folder_exporter_open_handles", "OS handles held by the exporter process.", "gauge");
                sb.Append("folder_exporter_open_handles ").Append(p.HandleCount).Append('\n');
            }
            catch { /* process counters are best-effort */ }

            Family(sb, "folder_exporter_managed_heap_bytes", "Bytes currently allocated on the managed heap.", "gauge");
            sb.Append("folder_exporter_managed_heap_bytes ").Append(GC.GetTotalMemory(false)).Append('\n');

            return sb.ToString();
        }

        private static bool Tracked(ScanResult r)
        {
            return r.Exists && r.TrackingEnabled;
        }

        private static bool HasNameInfo(ScanResult r)
        {
            return r.Exists && r.ExposeNames;
        }

        private static void Family(StringBuilder sb, string name, string help, string type)
        {
            sb.Append("# HELP ").Append(name).Append(' ').Append(EscHelp(help)).Append('\n');
            sb.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
        }

        private static void Sample(StringBuilder sb, string name, ScanResult r, double value)
        {
            SampleWith(sb, name, r, null, value);
        }

        private static void SampleWith(StringBuilder sb, string name, ScanResult r, string[] extra, double value)
        {
            sb.Append(name).Append("{target=\"").Append(Esc(r.TargetName))
              .Append("\",path=\"").Append(Esc(r.Path)).Append('"');
            if (r.ExtraLabels != null)
            {
                foreach (var kv in r.ExtraLabels)
                    sb.Append(',').Append(kv.Key).Append("=\"").Append(Esc(kv.Value)).Append('"');
            }
            if (extra != null)
            {
                for (int i = 0; i + 1 < extra.Length; i += 2)
                    sb.Append(',').Append(extra[i]).Append("=\"").Append(Esc(extra[i + 1])).Append('"');
            }
            sb.Append("} ").Append(Fmt(value)).Append('\n');
        }

        /// <summary>Formats a value without scientific notation for integral magnitudes.</summary>
        internal static string Fmt(double v)
        {
            if (double.IsNaN(v)) return "NaN";
            if (double.IsPositiveInfinity(v)) return "+Inf";
            if (double.IsNegativeInfinity(v)) return "-Inf";
            if (v == Math.Floor(v) && Math.Abs(v) < 1e15)
                return ((long)v).ToString(CultureInfo.InvariantCulture);
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>Escapes a label value per the exposition format: \\ \" \n.</summary>
        internal static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOf('\\') < 0 && s.IndexOf('"') < 0 && s.IndexOf('\n') < 0) return s;
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                if (c == '\\') sb.Append("\\\\");
                else if (c == '"') sb.Append("\\\"");
                else if (c == '\n') sb.Append("\\n");
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static string EscHelp(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\n", "\\n");
        }
    }

    public sealed class ExporterStats
    {
        public double StartTime;
        public long Scrapes;
        public long ScanCycles;
        public long ConfigReloads;
        public long ConfigReloadFailures;
    }
}
