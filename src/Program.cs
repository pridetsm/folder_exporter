// Program.cs - entry point. The same binary runs as a console app or as a real
// Windows service (ServiceBase), so no wrapper like NSSM is needed.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.ServiceProcess;
using System.Threading;

[assembly: AssemblyTitle("folder_exporter")]
[assembly: AssemblyDescription("Prometheus exporter for Windows file and folder metrics")]
[assembly: AssemblyProduct("folder_exporter")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]

namespace FolderExporter
{
    public static class Program
    {
        public const string DefaultServiceName = "folder_exporter";
        private static readonly Logger Log = new Logger();

        public static int Main(string[] args)
        {
            string configPath = null;
            string serviceName = DefaultServiceName;
            bool forceConsole = false, install = false, uninstall = false;
            bool once = false, check = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].TrimStart('-', '/').ToLowerInvariant();
                switch (a)
                {
                    case "config": case "c":
                        if (i + 1 < args.Length) configPath = args[++i];
                        break;
                    case "service-name": case "name":
                        if (i + 1 < args.Length) serviceName = args[++i];
                        break;
                    case "console": forceConsole = true; break;
                    case "install": install = true; break;
                    case "uninstall": case "remove": uninstall = true; break;
                    case "once": once = true; break;
                    case "check-config": case "check": check = true; break;
                    case "version": case "v":
                        Console.WriteLine("folder_exporter " + Metrics.Version);
                        return 0;
                    case "help": case "h": case "?":
                        Usage();
                        return 0;
                    default:
                        Console.Error.WriteLine("unknown argument: " + args[i]);
                        Usage();
                        return 2;
                }
            }

            if (configPath == null) configPath = DefaultConfigPath();
            configPath = Path.GetFullPath(configPath);

            if (install) return Install(serviceName, configPath);
            if (uninstall) return Uninstall(serviceName);

            if (!File.Exists(configPath))
            {
                Console.Error.WriteLine("config file not found: " + configPath);
                Console.Error.WriteLine("Pass --config <path>, or place folder_exporter.yml next to the executable.");
                return 2;
            }

            if (check)
            {
                try
                {
                    Config c = Config.Load(configPath);
                    Console.WriteLine("config OK: " + c.Folders.Count + " folder(s), listening on " + c.ListenAddress);
                    Console.WriteLine("host: " + Environment.MachineName);
                    foreach (FolderConfig t in c.Folders)
                    {
                        bool exists = Directory.Exists(t.Path);
                        Console.WriteLine("  - " + t.Name + " -> " + t.Path + (exists ? "" : "   [WARNING: path not found]"));
                    }
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("config INVALID: " + ex.Message);
                    return 1;
                }
            }

            if (once)
            {
                try
                {
                    // --once is a machine-readable mode: stdout must be nothing but the
                    // exposition, or piping it into promtool/a file breaks. Console logging
                    // is therefore off; scan health is still reported by the metrics
                    // themselves (folder_up, folder_scan_errors_total, folder_scan_timed_out).
                    Log.Configure("warn", "", 0, false);
                    var app = new App(configPath, false, Log);
                    app.Start();
                    Console.Out.Write(app.ScanOnceAndRender());
                    app.Stop();
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("error: " + ex.Message);
                    return 1;
                }
            }

            bool interactive = Environment.UserInteractive || forceConsole;
            if (!interactive)
            {
                ServiceBase.Run(new ExporterService(configPath, Log, serviceName));
                return 0;
            }

            return RunConsole(configPath);
        }

        private static int RunConsole(string configPath)
        {
            var app = new App(configPath, true, Log);
            var stopped = new ManualResetEvent(false);

            Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                Console.WriteLine();
                Log.Info("shutdown requested");
                stopped.Set();
            };

            try
            {
                app.Start();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("failed to start: " + ex.Message);
                return 1;
            }

            Console.WriteLine("Press Ctrl+C to stop.");
            stopped.WaitOne();
            app.Stop();
            return 0;
        }

        private static string DefaultConfigPath()
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return Path.Combine(dir, "folder_exporter.yml");
        }

        private static string ExePath()
        {
            return Assembly.GetExecutingAssembly().Location;
        }

        // ---------------------------------------------------------------- service install

        private static int Install(string serviceName, string configPath)
        {
            if (!IsAdmin())
            {
                Console.Error.WriteLine("--install must be run from an elevated (Administrator) prompt.");
                return 5;
            }
            // sc.exe wants binPath as a single argument; inner quotes are escaped with \".
            // The service name is baked in because ServiceBase.ServiceName must match
            // the name the SCM registered, or the service refuses to start.
            string bin = "\\\"" + ExePath() + "\\\" --config \\\"" + configPath +
                         "\\\" --service-name \\\"" + serviceName + "\\\"";
            int rc = Sc("create " + serviceName + " binPath= \"" + bin + "\" start= auto DisplayName= \"Prometheus folder exporter\"");
            if (rc != 0) return rc;
            Sc("description " + serviceName + " \"Exposes Windows file and folder metrics to Prometheus.\"");
            // Restart automatically on failure: 5s, 10s, then every 60s; reset counter daily.
            Sc("failure " + serviceName + " reset= 86400 actions= restart/5000/restart/10000/restart/60000");
            Console.WriteLine("Service '" + serviceName + "' installed.");
            Console.WriteLine("Start it with:  sc.exe start " + serviceName);
            return 0;
        }

        private static int Uninstall(string serviceName)
        {
            if (!IsAdmin())
            {
                Console.Error.WriteLine("--uninstall must be run from an elevated (Administrator) prompt.");
                return 5;
            }
            Sc("stop " + serviceName);
            int rc = Sc("delete " + serviceName);
            if (rc == 0) Console.WriteLine("Service '" + serviceName + "' removed.");
            return rc;
        }

        private static int Sc(string arguments)
        {
            var psi = new ProcessStartInfo("sc.exe", arguments);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd();
                string e = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                {
                    Console.Error.WriteLine("sc.exe " + arguments);
                    Console.Error.WriteLine(o.Trim());
                    Console.Error.WriteLine(e.Trim());
                }
                return p.ExitCode;
            }
        }

        private static bool IsAdmin()
        {
            try
            {
                var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                var pr = new System.Security.Principal.WindowsPrincipal(id);
                return pr.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static void Usage()
        {
            Console.WriteLine(
@"folder_exporter " + Metrics.Version + @" - Prometheus exporter for Windows file/folder metrics

Usage:
  folder_exporter.exe [--config <path>]      Run in the foreground (Ctrl+C to stop)
  folder_exporter.exe --once                 Scan once, print metrics to stdout, exit
  folder_exporter.exe --check-config         Validate the config file and exit
  folder_exporter.exe --install              Install as a Windows service (elevated)
  folder_exporter.exe --uninstall            Remove the Windows service (elevated)
  folder_exporter.exe --version              Print the version

Options:
  --config <path>        Config file. Default: folder_exporter.yml next to the .exe
  --service-name <name>  Service name for --install/--uninstall. Default: folder_exporter
  --console              Force console mode even when not interactive");
        }
    }

    internal sealed class ExporterService : ServiceBase
    {
        private readonly string _configPath;
        private readonly Logger _log;
        private App _app;

        public ExporterService(string configPath, Logger log, string serviceName)
        {
            _configPath = configPath;
            _log = log;
            // Must equal the name the SCM registered this service under.
            ServiceName = string.IsNullOrEmpty(serviceName) ? Program.DefaultServiceName : serviceName;
            CanShutdown = true;
            CanStop = true;
            AutoLog = true;
        }

        protected override void OnStart(string[] args)
        {
            try
            {
                _app = new App(_configPath, false, _log);
                _app.Start();
            }
            catch (Exception ex)
            {
                _log.Error("service failed to start: " + ex.Message);
                try
                {
                    EventLog.WriteEntry("folder_exporter failed to start: " + ex.Message, EventLogEntryType.Error);
                }
                catch { }
                ExitCode = 1;
                Stop();
            }
        }

        protected override void OnStop()
        {
            if (_app != null) _app.Stop();
        }

        protected override void OnShutdown()
        {
            OnStop();
        }
    }
}
