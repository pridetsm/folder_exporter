// JobRunner.cs - executes one job's command as a native process.
//
// Arguments are always passed as a properly escaped argv, never concatenated
// into a shell string: "exe" mode runs the command directly under
// CreateProcess with no shell involved, so characters like ; & | or a
// backtick inside an argument are inert data, not shell metacharacters.
// "powershell"/"cmd" modes still start a shell (that is what running a
// .ps1/.bat requires), but the script path and each argument are still
// passed as one escaped token apiece.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace FolderExporter
{
    internal sealed class JobRunResult
    {
        public bool Started;
        public bool TimedOut;
        public int ExitCode = -1;
        public string Error = "";
        public double DurationSeconds;
    }

    internal static class JobRunner
    {
        public static JobRunResult Run(JobConfig cfg, string logDirectory, Logger log)
        {
            var result = new JobRunResult();
            StreamWriter logWriter = null;

            try
            {
                string exe, argLine;
                ResolveCommand(cfg, out exe, out argLine);

                if (!string.IsNullOrEmpty(logDirectory))
                {
                    try
                    {
                        string jobDir = Path.Combine(logDirectory, SanitizeForPath(cfg.Name));
                        Directory.CreateDirectory(jobDir);
                        string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                        string logPath = Path.Combine(jobDir, stamp + ".log");
                        logWriter = new StreamWriter(logPath, false, new UTF8Encoding(false));
                        RotateOldLogs(jobDir, 20);
                    }
                    catch (Exception ex)
                    {
                        if (log != null) log.Warn("job \"" + cfg.Name + "\": could not open a log file: " + ex.Message);
                        logWriter = null;
                    }
                }

                var psi = new ProcessStartInfo();
                psi.FileName = exe;
                psi.Arguments = argLine;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = ResolveWorkingDirectory(cfg);
                foreach (KeyValuePair<string, string> kv in cfg.Env)
                    psi.EnvironmentVariables[kv.Key] = kv.Value;

                var writerGate = new object();
                Stopwatch sw = Stopwatch.StartNew();
                IntPtr job = Win32.CreateKillOnCloseJob();
                Process p = null;
                try
                {
                    p = new Process();
                    p.StartInfo = psi;
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                    { if (e.Data != null) WriteLog(logWriter, writerGate, e.Data); };
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                    { if (e.Data != null) WriteLog(logWriter, writerGate, "STDERR " + e.Data); };

                    p.Start();
                    result.Started = true;

                    if (job != IntPtr.Zero)
                    {
                        // A process that exits in the window between Start() and here
                        // leaves nothing to assign; the exit code captured below is
                        // still correct either way, so this race is harmless.
                        try { Win32.AssignProcessToJobObject(job, p.Handle); } catch { }
                    }

                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    bool exited = p.WaitForExit(Math.Max(1, cfg.TimeoutSeconds) * 1000);
                    if (!exited)
                    {
                        result.TimedOut = true;
                        if (job != IntPtr.Zero)
                        {
                            // Closing the last handle to a KILL_ON_JOB_CLOSE job
                            // terminates every process still assigned to it - the
                            // whole tree, not just this one PID.
                            Win32.CloseHandle(job);
                            job = IntPtr.Zero;
                        }
                        else
                        {
                            try { p.Kill(); } catch { }
                        }
                        p.WaitForExit(5000);
                    }

                    try { result.ExitCode = p.ExitCode; } catch { result.ExitCode = -1; }
                }
                finally
                {
                    if (job != IntPtr.Zero) Win32.CloseHandle(job);
                    if (p != null) { try { p.Dispose(); } catch { } }
                }
                result.DurationSeconds = sw.Elapsed.TotalSeconds;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                if (log != null) log.Error("job \"" + cfg.Name + "\" failed to start: " + ex.Message);
            }
            finally
            {
                if (logWriter != null) { try { logWriter.Flush(); logWriter.Dispose(); } catch { } }
            }

            return result;
        }

        // ------------------------------------------------------------------ command resolution

        private static void ResolveCommand(JobConfig cfg, out string exe, out string argLine)
        {
            string shell = cfg.Shell;
            if (shell == "auto")
            {
                string ext = Path.GetExtension(cfg.Command).ToLowerInvariant();
                if (ext == ".ps1") shell = "powershell";
                else if (ext == ".bat" || ext == ".cmd") shell = "cmd";
                else shell = "exe";
            }

            switch (shell)
            {
                case "powershell":
                    exe = "powershell.exe";
                    argLine = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Quote(cfg.Command);
                    if (cfg.Args.Count > 0) argLine += " " + BuildArguments(cfg.Args);
                    break;
                case "cmd":
                    exe = "cmd.exe";
                    argLine = "/c " + Quote(cfg.Command);
                    if (cfg.Args.Count > 0) argLine += " " + BuildArguments(cfg.Args);
                    break;
                default:
                    exe = cfg.Command;
                    argLine = BuildArguments(cfg.Args);
                    break;
            }
        }

        private static string ResolveWorkingDirectory(JobConfig cfg)
        {
            string wd = cfg.WorkingDirectory;
            if (string.IsNullOrEmpty(wd) && Path.IsPathRooted(cfg.Command))
            {
                try { wd = Path.GetDirectoryName(cfg.Command); } catch { wd = null; }
            }
            if (string.IsNullOrEmpty(wd) || !Directory.Exists(wd))
                wd = AppDomain.CurrentDomain.BaseDirectory;
            return wd;
        }

        private static string Quote(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            return "\"" + s.Replace("\"", "\\\"") + "\"";
        }

        /// <summary>
        /// Builds a Win32 command line from discrete arguments using the escaping
        /// rules CommandLineToArgvW expects, so each list element survives as
        /// exactly one argument no matter what it contains.
        /// </summary>
        private static string BuildArguments(List<string> args)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                AppendArgument(sb, args[i]);
            }
            return sb.ToString();
        }

        private static void AppendArgument(StringBuilder sb, string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            {
                sb.Append(arg);
                return;
            }
            sb.Append('"');
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                    backslashes = 0;
                    continue;
                }
                if (backslashes > 0) { sb.Append('\\', backslashes); backslashes = 0; }
                sb.Append(c);
            }
            if (backslashes > 0) sb.Append('\\', backslashes * 2);
            sb.Append('"');
        }

        // ------------------------------------------------------------------ logging

        private static void WriteLog(StreamWriter w, object gate, string line)
        {
            if (w == null) return;
            try
            {
                lock (gate)
                    w.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + line);
            }
            catch { /* never let a logging failure take the job down */ }
        }

        private static string SanitizeForPath(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.ToString();
        }

        private static void RotateOldLogs(string dir, int keep)
        {
            try
            {
                var files = new List<string>(Directory.GetFiles(dir, "*.log"));
                if (files.Count <= keep) return;
                files.Sort(StringComparer.Ordinal);   // yyyyMMdd_HHmmss names sort chronologically
                for (int i = 0; i < files.Count - keep; i++)
                    try { File.Delete(files[i]); } catch { }
            }
            catch { /* best-effort housekeeping */ }
        }
    }
}
