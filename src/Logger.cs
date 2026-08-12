// Logger.cs - minimal level-filtered logger with size-capped file rotation.
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace FolderExporter
{
    public sealed class Logger
    {
        private readonly object _gate = new object();
        private int _level = 2;              // 0=error 1=warn 2=info 3=debug
        private string _file = "";
        private long _maxBytes = 8 * 1024 * 1024;
        private bool _console = true;

        public void Configure(string level, string file, long maxBytes, bool console)
        {
            lock (_gate)
            {
                switch ((level ?? "info").ToLowerInvariant())
                {
                    case "error": _level = 0; break;
                    case "warn": case "warning": _level = 1; break;
                    case "debug": _level = 3; break;
                    default: _level = 2; break;
                }
                _file = file ?? "";
                _maxBytes = maxBytes > 0 ? maxBytes : 8 * 1024 * 1024;
                _console = console;

                // Create the log directory up front. Writes are wrapped in a
                // catch-all so logging can never take the exporter down, which
                // means a missing directory would otherwise fail silently and
                // leave a service with no diagnostics at all.
                if (_file.Length > 0)
                {
                    try
                    {
                        string dir = Path.GetDirectoryName(Path.GetFullPath(_file));
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);
                    }
                    catch (Exception ex)
                    {
                        _file = "";
                        try
                        {
                            Console.Error.WriteLine("WARNING: cannot use log_file \"" + file +
                                                    "\" (" + ex.Message + "); logging to console only.");
                        }
                        catch { }
                    }
                }
            }
        }

        public void Error(string msg) { Write(0, "ERROR", msg); }
        public void Warn(string msg) { Write(1, "WARN ", msg); }
        public void Info(string msg) { Write(2, "INFO ", msg); }
        public void Debug(string msg) { Write(3, "DEBUG", msg); }

        private void Write(int level, string tag, string msg)
        {
            if (level > _level) return;
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                          + " " + tag + " " + msg;
            lock (_gate)
            {
                if (_console)
                {
                    try { Console.WriteLine(line); } catch { }
                }
                if (_file.Length > 0)
                {
                    try
                    {
                        RotateIfNeeded();
                        File.AppendAllText(_file, line + Environment.NewLine, Encoding.UTF8);
                    }
                    catch { /* never let logging kill the exporter */ }
                }
            }
        }

        private void RotateIfNeeded()
        {
            var fi = new FileInfo(_file);
            if (!fi.Exists || fi.Length < _maxBytes) return;
            string old = _file + ".1";
            try { if (File.Exists(old)) File.Delete(old); } catch { }
            try { File.Move(_file, old); } catch { }
        }
    }
}
