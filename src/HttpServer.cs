// HttpServer.cs - HttpListener front end. HttpListener sits on http.sys, the
// kernel-mode HTTP stack, so idle cost is essentially zero and we never spin a
// thread waiting on a socket.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading;

namespace FolderExporter
{
    public sealed class HttpServer
    {
        private readonly HttpListener _listener = new HttpListener();
        private readonly Logger _log;
        private readonly Func<string> _renderMetrics;
        private readonly Func<string> _renderStatus;
        private readonly Action _reload;
        private string _metricsPath;
        private string _authUser = "";
        private string _authPass = "";
        private Thread _thread;
        private volatile bool _running;

        public HttpServer(Logger log, Func<string> renderMetrics, Func<string> renderStatus, Action reload)
        {
            _log = log;
            _renderMetrics = renderMetrics;
            _renderStatus = renderStatus;
            _reload = reload;
        }

        public void Configure(Config cfg)
        {
            _metricsPath = cfg.MetricsPath;
            _authUser = cfg.BasicAuthUser ?? "";
            _authPass = cfg.BasicAuthPassword ?? "";
        }

        public void Start(Config cfg)
        {
            Configure(cfg);
            string host = cfg.ListenHost;
            // "+" binds every interface; "0.0.0.0"/"*"/"" mean the same thing here.
            string bind = (host == "0.0.0.0" || host == "*" || host.Length == 0) ? "+" : host;
            string prefix = "http://" + bind + ":" + cfg.ListenPort + "/";
            _listener.Prefixes.Add(prefix);

            try
            {
                _listener.Start();
            }
            catch (HttpListenerException ex)
            {
                if (ex.ErrorCode == 5) // ERROR_ACCESS_DENIED
                {
                    throw new Exception(
                        "cannot bind " + prefix + " - access denied.\r\n" +
                        "Either run as Administrator / as a service, or grant the URL once with:\r\n" +
                        "  netsh http add urlacl url=" + prefix + " user=\"" +
                        Environment.UserDomainName + "\\" + Environment.UserName + "\"\r\n" +
                        "Alternatively set \"listen_address\" to \"127.0.0.1:" + cfg.ListenPort + "\".", ex);
                }
                if (ex.ErrorCode == 183) // ERROR_ALREADY_EXISTS
                    throw new Exception("port " + cfg.ListenPort + " is already in use by another process.", ex);
                throw;
            }

            _running = true;
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "http-accept";
            _thread.Start();
            _log.Info("listening on " + prefix + " (metrics at " + _metricsPath + ")");
        }

        public void Stop()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }

        private void Loop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = _listener.GetContext();
                }
                catch (Exception)
                {
                    if (!_running) return;
                    Thread.Sleep(100);
                    continue;
                }
                ThreadPool.QueueUserWorkItem(HandleSafe, ctx);
            }
        }

        private void HandleSafe(object state)
        {
            var ctx = (HttpListenerContext)state;
            try { Handle(ctx); }
            catch (Exception ex) { _log.Debug("request failed: " + ex.Message); }
            finally { try { ctx.Response.Close(); } catch { } }
        }

        private void Handle(HttpListenerContext ctx)
        {
            HttpListenerRequest req = ctx.Request;
            HttpListenerResponse res = ctx.Response;
            res.Headers["Server"] = "folder_exporter/" + Metrics.Version;

            if (!Authorized(req))
            {
                res.StatusCode = 401;
                res.AddHeader("WWW-Authenticate", "Basic realm=\"folder_exporter\"");
                Send(ctx, "401 unauthorized\n", "text/plain; charset=utf-8", false);
                return;
            }

            string path = req.Url.AbsolutePath;
            if (path.Length > 1 && path.EndsWith("/")) path = path.TrimEnd('/');

            if (string.Equals(path, _metricsPath, StringComparison.OrdinalIgnoreCase))
            {
                string body = _renderMetrics();
                Send(ctx, body, "text/plain; version=0.0.4; charset=utf-8", AcceptsGzip(req));
                return;
            }

            if (path == "/healthz" || path == "/health" || path == "/-/healthy")
            {
                Send(ctx, "ok\n", "text/plain; charset=utf-8", false);
                return;
            }

            if (path == "/-/reload")
            {
                if (req.HttpMethod != "POST" && req.HttpMethod != "PUT")
                {
                    res.StatusCode = 405;
                    Send(ctx, "use POST /-/reload\n", "text/plain; charset=utf-8", false);
                    return;
                }
                try
                {
                    _reload();
                    Send(ctx, "configuration reloaded\n", "text/plain; charset=utf-8", false);
                }
                catch (Exception ex)
                {
                    res.StatusCode = 500;
                    Send(ctx, "reload failed: " + ex.Message + "\n", "text/plain; charset=utf-8", false);
                }
                return;
            }

            if (path == "" || path == "/")
            {
                Send(ctx, _renderStatus(), "text/html; charset=utf-8", AcceptsGzip(req));
                return;
            }

            res.StatusCode = 404;
            Send(ctx, "404 not found\n", "text/plain; charset=utf-8", false);
        }

        private bool Authorized(HttpListenerRequest req)
        {
            if (_authUser.Length == 0) return true;
            string h = req.Headers["Authorization"];
            if (string.IsNullOrEmpty(h) || !h.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                return false;
            string decoded;
            try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(h.Substring(6).Trim())); }
            catch { return false; }
            int i = decoded.IndexOf(':');
            if (i < 0) return false;
            string u = decoded.Substring(0, i);
            string p = decoded.Substring(i + 1);
            return FixedTimeEquals(u, _authUser) & FixedTimeEquals(p, _authPass);
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            int diff = a.Length ^ b.Length;
            for (int i = 0; i < a.Length && i < b.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static bool AcceptsGzip(HttpListenerRequest req)
        {
            string ae = req.Headers["Accept-Encoding"];
            return !string.IsNullOrEmpty(ae) && ae.IndexOf("gzip", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void Send(HttpListenerContext ctx, string body, string contentType, bool gzip)
        {
            HttpListenerResponse res = ctx.Response;
            byte[] raw = Encoding.UTF8.GetBytes(body);
            res.ContentType = contentType;

            if (gzip && raw.Length > 1024)
            {
                using (var ms = new MemoryStream())
                {
                    using (var gz = new GZipStream(ms, CompressionMode.Compress, true))
                        gz.Write(raw, 0, raw.Length);
                    raw = ms.ToArray();
                }
                res.AddHeader("Content-Encoding", "gzip");
            }

            res.ContentLength64 = raw.Length;
            if (ctx.Request.HttpMethod == "HEAD") return;
            res.OutputStream.Write(raw, 0, raw.Length);
        }
    }
}
