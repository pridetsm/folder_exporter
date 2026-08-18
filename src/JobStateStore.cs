// JobStateStore.cs - persists per-job scheduler state to a small tab-separated
// file, so a restart or reboot can tell which jobs it owes a catch-up run to
// instead of silently resuming as if nothing had happened.
//
// No JSON: the exporter ships with zero third-party assemblies and this is
// a dozen fixed fields, not a document format. If it ever needs to grow past
// that, that is the point to reach for real serialization - not before.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FolderExporter
{
    internal sealed class JobStateStore
    {
        public sealed class Record
        {
            public double LastChecked;
            public double LastDue;
            public double LastStart;
            public double LastEnd;
            public int LastExitCode = -1;
            public bool LastSuccess;
            public long RunsOk, RunsFailed, RunsTimeout, RunsSkipped, MissedTotal;
        }

        private const string Header = "# folder_exporter job scheduler state - do not edit while the service is running";
        private readonly string _path;
        private readonly Logger _log;

        public JobStateStore(string path, Logger log)
        {
            _path = path;
            _log = log;
        }

        public Dictionary<string, Record> Load()
        {
            var map = new Dictionary<string, Record>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(_path) || !File.Exists(_path)) return map;

            try
            {
                foreach (string line in File.ReadAllLines(_path, Encoding.UTF8))
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] f = line.Split('\t');
                    if (f.Length < 9) continue;

                    var r = new Record();
                    r.LastChecked = ParseD(f[1]);
                    r.LastDue = ParseD(f[2]);
                    r.LastStart = ParseD(f[3]);
                    r.LastEnd = ParseD(f[4]);
                    r.LastExitCode = ParseI(f[5]);
                    r.LastSuccess = f[6] == "1";
                    r.RunsOk = ParseL(f[7]);
                    r.RunsFailed = ParseL(f[8]);
                    r.RunsTimeout = f.Length > 9 ? ParseL(f[9]) : 0;
                    r.RunsSkipped = f.Length > 10 ? ParseL(f[10]) : 0;
                    r.MissedTotal = f.Length > 11 ? ParseL(f[11]) : 0;
                    map[f[0]] = r;
                }
            }
            catch (Exception ex)
            {
                if (_log != null) _log.Warn("could not read job state file \"" + _path + "\": " + ex.Message);
            }
            return map;
        }

        public void Save(Dictionary<string, Record> records)
        {
            if (string.IsNullOrEmpty(_path)) return;

            string dir = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.Append(Header).Append('\n');
            foreach (KeyValuePair<string, Record> kv in records)
            {
                Record r = kv.Value;
                sb.Append(kv.Key).Append('\t')
                  .Append(FmtD(r.LastChecked)).Append('\t')
                  .Append(FmtD(r.LastDue)).Append('\t')
                  .Append(FmtD(r.LastStart)).Append('\t')
                  .Append(FmtD(r.LastEnd)).Append('\t')
                  .Append(r.LastExitCode).Append('\t')
                  .Append(r.LastSuccess ? '1' : '0').Append('\t')
                  .Append(r.RunsOk).Append('\t')
                  .Append(r.RunsFailed).Append('\t')
                  .Append(r.RunsTimeout).Append('\t')
                  .Append(r.RunsSkipped).Append('\t')
                  .Append(r.MissedTotal).Append('\n');
            }

            // A service killed mid-write must never leave a truncated file that
            // Load() then trips over on the next start, so write to a temp file
            // and swap it in.
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(_path)) File.Replace(tmp, _path, null);
            else File.Move(tmp, _path);
        }

        private static string FmtD(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
        private static double ParseD(string s) { double v; return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0; }
        private static int ParseI(string s) { int v; return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : -1; }
        private static long ParseL(string s) { long v; return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0; }
    }
}
