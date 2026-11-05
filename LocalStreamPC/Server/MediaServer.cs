using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LocalStreamPC.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LocalStreamPC.Server
{
    /// <summary>
    /// Who may call an endpoint.
    /// <list type="bullet">
    /// <item><b>Dlna</b>: TVs and players. Login if one is sent; otherwise allowed without a
    /// password only when "Allow DLNA without login" is on and the client is on the local network.</item>
    /// <item><b>Viewer</b>: any logged-in user (read only).</item>
    /// <item><b>Admin</b>: changes files.</item>
    /// <item><b>Public</b>: the web-app manifest and icons (browsers fetch them without the login).</item>
    /// </list>
    /// Requests that change something (POST) from a logged-in browser must come from our own page (CSRF check).
    /// </summary>
    public enum Access { Public, Dlna, Viewer, Upload, Admin }

    /// <summary>The HTTP server (Kestrel) plus SSDP. One instance per "Start Server".</summary>
    public sealed class MediaServer : IAsyncDisposable
    {
        private readonly ConfigService _config;
        private readonly AppLog _log;
        private readonly AuthService _auth;
        private readonly WebApi _api;
        private readonly ServerServices _services;
        private readonly PlaybackApi _playback;
        private readonly LibraryApi _library;
        private readonly DlnaService _dlna;
        private readonly SsdpService _ssdp;
        private readonly SsdpDevice _ssdpDevice;
        private WebApplication? _app;
        private IAsyncDisposable? _ssdpRegistration;

        public MediaServer(ConfigService config, AppLog log, ThumbnailService thumbs, SsdpService ssdp, ServerServices services)
        {
            _config = config;
            _log = log;
            _auth = new AuthService(config, log, services.Sessions);
            _services = services;
            _api = new WebApi(config, log, thumbs, services);
            _playback = new PlaybackApi(config, log, _auth, services);
            _library = new LibraryApi(config, log, services);
            _dlna = new DlnaService(config, log);
            _ssdp = ssdp;
            _ssdpDevice = new SsdpDevice(
                "urn:schemas-upnp-org:device:MediaServer:1",
                () => config.Current.ServerUuid,
                () => config.Current.Port,
                "/description.xml",
                new[] { "urn:schemas-upnp-org:service:ContentDirectory:1", "urn:schemas-upnp-org:service:ConnectionManager:1" });
        }

        public bool IsRunning => _app != null;

        /// <returns>False (and logs why) if the server could not start, e.g. port in use.</returns>
        public async Task<bool> StartAsync(IReadOnlyList<(string Ip, string Name)> localIps, bool enableSsdp = true)
        {
            if (_app != null) return true;
            int port = _config.Current.Port;

            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ContentRootPath = AppContext.BaseDirectory,
                ApplicationName = "LocalStreamPC",
            });
            builder.Logging.ClearProviders();
            var cert = _config.Current.EnableHttps ? HttpsCertificate.Load(_config, _log) : null;
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.ListenAnyIP(port);
                if (cert != null) k.ListenAnyIP(_config.Current.HttpsPort, o => o.UseHttps(cert));
                k.AddServerHeader = false;
            });
            builder.Services.AddResponseCompression(o =>
            {
                o.EnableForHttps = false;
                o.Providers.Add<GzipCompressionProvider>();
                o.Providers.Add<BrotliCompressionProvider>();
                o.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[] { "text/vtt", "application/manifest+json" });
            });

            var app = builder.Build();
            app.UseResponseCompression();
            // Chromecast's player fetches media, playlists and subtitles from its own web page, so
            // signed TV links (?k=, one file each) allow being read from any origin.
            app.Use(async (ctx, next) =>
            {
                if (IsSignedMediaPath(ctx.Request.Path))
                {
                    var h = ctx.Response.Headers;
                    if (HttpMethods.IsOptions(ctx.Request.Method))
                    {
                        h.AccessControlAllowOrigin = "*";
                        h.AccessControlAllowMethods = "GET, HEAD, OPTIONS";
                        h.AccessControlAllowHeaders = "Range, Content-Type";
                        h.AccessControlMaxAge = "86400";
                        ctx.Response.StatusCode = 204;
                        return;
                    }
                    if (ctx.Request.Query.ContainsKey("k"))
                    {
                        h.AccessControlAllowOrigin = "*";
                        h.AccessControlExposeHeaders = "Content-Length, Content-Range, Accept-Ranges";
                    }
                }
                await next();
            });
            MapRoutes(app);

            try
            {
                await app.StartAsync();
            }
            catch (Exception ex)
            {
                string reason = ex is IOException && ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
                    ? $"Port {port} is already in use by another program. Pick another port in Settings."
                    : ex.Message;
                _log.Write($"Could not start the server: {reason}");
                await app.DisposeAsync();
                return false;
            }

            _app = app;
            if (enableSsdp) _ssdpRegistration = await _ssdp.RegisterAsync(_ssdpDevice, localIps);
            _log.Write($"HTTP server running on port {port} (all networks)." + (cert != null ? $" HTTPS on port {_config.Current.HttpsPort}." : ""));

            var cfg = _config.Current;
            if (!cfg.EnableAuth)
                _log.Write("Warning: web login is OFF. Anyone on your network can browse, upload, move and copy files. Turn it on in Settings.");
            else if (_config.UsesWeakDefaultPasswords)
                _log.Write("Warning: you are still using the old default password (admin123 / guest). Change it in Settings.");
            return true;
        }

        public async Task StopAsync()
        {
            if (_ssdpRegistration != null)
            {
                await _ssdpRegistration.DisposeAsync(); // sends ssdp:byebye
                _ssdpRegistration = null;
            }
            if (_app != null)
            {
                try { using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await _app.StopAsync(stopCts.Token); } catch { }
                await _app.DisposeAsync();
                _app = null;
                _log.Write("Server stopped.");
            }
        }

        public async ValueTask DisposeAsync() => await StopAsync();

        // ------------------------------------------------------------------ routing

        private void MapRoutes(WebApplication app)
        {
            // Browser UI (also installable as an app: manifest, icons, service worker)
            app.MapGet("/", Guard(Access.Viewer, (ctx, _) => ServeEmbeddedAsync(ctx, "Web/index.html", "text/html; charset=utf-8")));
            app.MapGet("/app.js", Guard(Access.Viewer, (ctx, _) => ServeEmbeddedAsync(ctx, "Web/app.js", "text/javascript; charset=utf-8")));
            app.MapGet("/library.js", Guard(Access.Viewer, (ctx, _) => ServeEmbeddedAsync(ctx, "Web/library.js", "text/javascript; charset=utf-8")));
            app.MapGet("/app.css", Guard(Access.Public, (ctx, _) => ServeEmbeddedAsync(ctx, "Web/app.css", "text/css; charset=utf-8")));

            // Sign-in page (instead of the browser's password pop-up), and share links.
            app.MapGet("/login", Guard(Access.Public, (ctx, _) => ServeEmbeddedAsync(ctx, "Web/login.html", "text/html; charset=utf-8")));
            app.MapGet("/login.js", Guard(Access.Public, (ctx, _) => ServeEmbeddedAsync(ctx, "Web/login.js", "text/javascript; charset=utf-8")));
            app.MapPost("/api/login", Guard(Access.Public, (ctx, _) => LoginAsync(ctx)));
            app.MapPost("/api/logout", Guard(Access.Public, (ctx, _) => LogoutAsync(ctx)));
            app.MapGet("/api/me", Guard(Access.Viewer, (ctx, caller) => ctx.Response.WriteAsJsonAsync(new
            {
                user = caller.UserName, role = caller.Role.ToString(), canUpload = caller.CanUpload, canShare = caller.CanShare && !caller.IsGuest,
                guest = caller.IsGuest, authEnabled = _config.Current.EnableAuth,
            })));
            app.MapGet("/s/{token}", Guard(Access.Public, (ctx, _) => OpenShareAsync(ctx, (string)ctx.Request.RouteValues["token"]!)));
            app.MapPost("/api/share", Guard(Access.Viewer, CreateShareAsync));
            app.MapGet("/api/shares", Guard(Access.Viewer, ListSharesAsync));
            app.MapPost("/api/share/revoke", Guard(Access.Viewer, RevokeShareAsync));
            app.MapGet("/manifest.webmanifest", Guard(Access.Public, (ctx, _) => ServeEmbeddedAsync(ctx, "Web/manifest.webmanifest", "application/manifest+json")));
            app.MapGet("/sw.js", Guard(Access.Public, (ctx, _) => ServeEmbeddedAsync(ctx, "Web/sw.js", "text/javascript; charset=utf-8")));
            foreach (var icon in new[] { "icon-192.png", "icon-512.png", "apple-touch-icon.png", "favicon.png" })
                app.MapGet("/" + icon, Guard(Access.Public, (ctx, _) => ServeEmbeddedAsync(ctx, "Web/" + icon, "image/png", cacheSeconds: 86400)));

            app.MapGet("/api/list", Guard(Access.Viewer, (ctx, caller) => _api.ListAsync(ctx, caller)));
            app.MapGet("/api/size", Guard(Access.Viewer, (ctx, caller) => _api.SizeAsync(ctx, caller)));
            app.MapGet("/zip", Guard(Access.Viewer, (ctx, caller) => _api.ZipAsync(ctx, caller)));
            app.MapGet("/api/media", Guard(Access.Viewer, _playback.MediaAsync));
            app.MapPost("/api/progress", Guard(Access.Viewer, _playback.ProgressAsync));
            app.MapGet("/api/home", Guard(Access.Viewer, _playback.HomeAsync));
            app.MapGet("/api/search", Guard(Access.Viewer, _playback.SearchAsync));
            app.MapGet("/api/music", Guard(Access.Viewer, _playback.MusicAsync));
            app.MapGet("/api/tv/devices", Guard(Access.Viewer, _playback.TvDevicesAsync));
            app.MapPost("/api/tv/play", Guard(Access.Viewer, _playback.TvPlayAsync));
            app.MapPost("/api/tv/control", Guard(Access.Viewer, _playback.TvControlAsync));
            app.MapGet("/api/tv/status", Guard(Access.Viewer, _playback.TvStatusAsync));
            app.MapGet("/api/version", Guard(Access.Viewer, _library.VersionAsync));
            app.MapGet("/api/mylib", Guard(Access.Viewer, _library.MyLibraryAsync));
            app.MapPost("/api/mark", Guard(Access.Viewer, _library.MarkAsync));
            app.MapGet("/api/playlist", Guard(Access.Viewer, _library.PlaylistAsync));
            app.MapPost("/api/playlist", Guard(Access.Viewer, _library.EditPlaylistAsync));
            app.MapGet("/api/videos", Guard(Access.Viewer, _library.VideosAsync));
            app.MapGet("/poster/{name}", Guard(Access.Viewer, (ctx, caller) => _library.PosterAsync(ctx, (string)ctx.Request.RouteValues["name"]!, caller)));
            app.MapGet("/api/photos", Guard(Access.Viewer, _library.PhotosAsync));
            app.MapGet("/api/lyrics", Guard(Access.Viewer, _library.LyricsAsync));
            app.MapPost("/api/subtitles/search", Guard(Access.Viewer, _library.SubtitleSearchAsync));
            app.MapPost("/api/subtitles/download", Guard(Access.Viewer, _library.SubtitleDownloadAsync));

            app.MapPost("/upload", Guard(Access.Upload, (ctx, caller) => _api.UploadAsync(ctx, caller)));
            app.MapPost("/delete", Guard(Access.Admin, (ctx, caller) => _api.DeleteAsync(ctx, caller)));
            app.MapPost("/mkdir", Guard(Access.Upload, (ctx, caller) => _api.CreateFolderAsync(ctx, caller)));
            app.MapPost("/move", Guard(Access.Admin, (ctx, caller) => _api.MoveOrCopyAsync(ctx, caller, isCopy: false)));
            app.MapPost("/copy", Guard(Access.Admin, (ctx, caller) => _api.MoveOrCopyAsync(ctx, caller, isCopy: true)));

            // Media (used by the browser UI, TVs and the LocalStream apps)
            app.MapMethods("/file/{**id}", new[] { "GET", "HEAD" }, Guard(Access.Dlna, (ctx, caller) => _api.ServeFileAsync(ctx, RouteId(ctx), caller)));
            app.MapMethods("/thumb/{**id}", new[] { "GET", "HEAD" }, Guard(Access.Dlna, (ctx, caller) => _api.ServeThumbnailAsync(ctx, RouteId(ctx), caller)));
            app.MapMethods("/stream/{**id}", new[] { "GET", "HEAD" }, Guard(Access.Dlna, (ctx, caller) => _playback.StreamAsync(ctx, RouteId(ctx), caller)));
            app.MapGet("/subs/{**id}", Guard(Access.Dlna, (ctx, caller) => _playback.SubtitlesAsync(ctx, RouteId(ctx), caller)));
            app.MapGet("/hls/{id}/{file}", Guard(Access.Dlna, (ctx, caller) => _playback.HlsAsync(ctx, RouteId(ctx), (string)ctx.Request.RouteValues["file"]!, caller)));

            // UPnP / DLNA
            app.MapGet("/description.xml", Guard(Access.Dlna, (ctx, _) => XmlAsync(ctx, _dlna.DeviceDescription())));
            app.MapGet("/scpd/ContentDirectory.xml", Guard(Access.Dlna, (ctx, _) => XmlAsync(ctx, DlnaService.ContentDirectoryScpd)));
            app.MapGet("/scpd/ConnectionManager.xml", Guard(Access.Dlna, (ctx, _) => XmlAsync(ctx, DlnaService.ConnectionManagerScpd)));
            app.MapPost("/control/ContentDirectory", Guard(Access.Dlna, (ctx, caller) => _dlna.HandleBrowseAsync(ctx, caller.Roots)));
        }

        private static string RouteId(HttpContext ctx) => (string)ctx.Request.RouteValues["id"]!;

        private static bool IsSignedMediaPath(PathString path)
            => path.StartsWithSegments("/hls") || path.StartsWithSegments("/subs") || path.StartsWithSegments("/file")
               || path.StartsWithSegments("/stream") || path.StartsWithSegments("/thumb");

        /// <summary>Wraps a handler with the access check and a catch-all, so one bad request can
        /// never take the app down (the old async-void handlers could).</summary>
        private RequestDelegate Guard(Access access, Func<HttpContext, Caller, Task> handler) => async ctx =>
        {
            try
            {
                Caller? caller = Authorize(ctx, access);
                if (caller == null) return;
                await handler(ctx, caller);
            }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
            {
                // Client disconnected.
            }
            catch (Exception ex)
            {
                _log.Write($"Error serving {ctx.Request.Path}: {ex.Message}");
                if (!ctx.Response.HasStarted) ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }
        };

        /// <returns>The caller, or null after writing a 401/403/429 response.</returns>
        private Caller? Authorize(HttpContext ctx, Access access)
        {
            var cfg = _config.Current;
            if (access == Access.Public) return new Caller(null, Role.Viewer, Array.Empty<string>());
            // A signed-in browser sends a cookie instead of a password: it counts as credentials, so
            // the account's own folder limits apply rather than the anonymous TV rules.
            bool hasCredentials = !string.IsNullOrEmpty(ctx.Request.Headers.Authorization)
                                  || ctx.Request.Cookies.ContainsKey(SessionStore.CookieName)
                                  || ctx.Request.Cookies.ContainsKey(AuthService.ShareCookie);

            if (access == Access.Dlna && !hasCredentials)
            {
                if (!cfg.EnableAuth) return new Caller(null, Role.Admin, _config.SharedRoots);
                // A link made by "Play on TV" opens that one file without a login (TVs can't type one).
                if (ctx.Request.RouteValues.TryGetValue("id", out var id) && id is string fileId &&
                    _auth.IsValidMediaKey(fileId, ctx.Request.Query["k"]))
                    return new Caller(null, Role.Viewer, _config.SharedRoots);
                if (cfg.AllowDlnaWithoutLogin && IsLocalNetworkClient(ctx))
                    return new Caller(null, Role.Viewer, _config.SharedRoots);
            }

            switch (_auth.TryAuthenticate(ctx, out Caller caller))
            {
                case AuthService.Outcome.TooManyAttempts:
                    ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    ctx.Response.Headers.RetryAfter = "300";
                    return null;
                case AuthService.Outcome.Unauthorized:
                    Challenge(ctx, access);
                    return null;
            }

            if ((access == Access.Admin && !caller.IsAdmin) || (access == Access.Upload && !caller.CanUpload))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return null;
            }
            if (access is Access.Admin or Access.Upload or Access.Viewer && !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)
                && AuthService.IsCrossSiteRequest(ctx))
            {
                _log.Write($"Blocked a cross-site request to {ctx.Request.Path} (possible CSRF).");
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return null;
            }
            return caller;
        }

        /// <summary>
        /// Not signed in. Browsers opening a page go to the sign-in page; the page's own requests get a
        /// plain 401 (no password pop-up). Apps, VLC and TVs get the usual HTTP login challenge.
        /// </summary>
        private static void Challenge(HttpContext ctx, Access access)
        {
            var headers = ctx.Request.Headers;
            bool browser = headers.ContainsKey("Sec-Fetch-Mode") || (headers.Accept.ToString().Contains("text/html") && headers.UserAgent.ToString().Contains("Mozilla"));
            if (browser && HttpMethods.IsGet(ctx.Request.Method) && (headers["Sec-Fetch-Mode"] == "navigate" || headers.Accept.ToString().Contains("text/html")) && access == Access.Viewer)
            {
                string next = ctx.Request.Path + ctx.Request.QueryString;
                ctx.Response.Redirect("/login?next=" + Uri.EscapeDataString(next));
                return;
            }
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            if (!browser) ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"LocalStream Secure Login\", charset=\"UTF-8\"";
        }

        // ------------------------------------------------------------------ sign-in, sign-out, share links

        private async Task LoginAsync(HttpContext ctx)
        {
            if (AuthService.IsCrossSiteRequest(ctx)) { ctx.Response.StatusCode = 403; return; }
            string user = "", password = "";
            bool remember = false;
            try
            {
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
                var r = doc.RootElement;
                user = r.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "";
                password = r.TryGetProperty("password", out var p) ? p.GetString() ?? "" : "";
                remember = r.TryGetProperty("remember", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.True;
            }
            catch { ctx.Response.StatusCode = 400; return; }

            switch (_auth.CheckPassword(ctx, user.Trim(), password, out var account))
            {
                case AuthService.Outcome.TooManyAttempts:
                    ctx.Response.StatusCode = 429;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Too many wrong passwords. Try again in 5 minutes." });
                    return;
                case AuthService.Outcome.Unauthorized:
                    ctx.Response.StatusCode = 401;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Wrong username or password." });
                    return;
            }
            string token = _services.Sessions.Create(account!.Username, account.PasswordHash, remember);
            ctx.Response.Cookies.Append(SessionStore.CookieName, token, new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = ctx.Request.IsHttps, Path = "/",
                Expires = remember ? DateTimeOffset.UtcNow + SessionStore.RememberFor : null,
            });
            ctx.Response.Cookies.Delete(AuthService.ShareCookie);
            _log.Write($"Signed in: {account.Username} ({StreamTracker.DescribeDevice(ctx.Request.Headers.UserAgent.ToString(), ctx.Connection.RemoteIpAddress?.ToString() ?? "?")}).");
            _services.Activity.Add(account.Username, "Signed in", "", ctx);
            await ctx.Response.WriteAsJsonAsync(new { ok = true });
        }

        private Task LogoutAsync(HttpContext ctx)
        {
            if (AuthService.IsCrossSiteRequest(ctx)) { ctx.Response.StatusCode = 403; return Task.CompletedTask; }
            _services.Sessions.Remove(ctx.Request.Cookies[SessionStore.CookieName]);
            ctx.Response.Cookies.Delete(SessionStore.CookieName);
            ctx.Response.Cookies.Delete(AuthService.ShareCookie);
            ctx.Response.StatusCode = 204;
            return Task.CompletedTask;
        }

        /// <summary>GET /s/{token}: remembers the share in a cookie and opens the shared folder or file.</summary>
        private async Task OpenShareAsync(HttpContext ctx, string token)
        {
            var guest = _auth.ShareCaller(token);
            var share = _config.FindShare(token);
            if (guest == null || share == null)
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync("<!DOCTYPE html><meta name=viewport content=\"width=device-width\"><title>LocalStream</title><body style=\"font-family:system-ui;padding:2rem\"><h2>This link has expired</h2><p>Ask the person who sent it for a new one.</p>");
                return;
            }
            _config.CountShareOpen(token);
            ctx.Response.Cookies.Append(AuthService.ShareCookie, token, new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = ctx.Request.IsHttps, Path = "/", Expires = share.Expires,
            });
            _services.Activity.Add("share link", "Opened share link", Path.GetFileName(share.Path), ctx);
            string target = share.IsFolder ? "/?path=" + Uri.EscapeDataString(guest.Roots[0])
                                           : "/?play=" + Uri.EscapeDataString(guest.Roots[0]);
            ctx.Response.Redirect(target);
        }

        /// <summary>POST /api/share {path, hours}</summary>
        private async Task CreateShareAsync(HttpContext ctx, Caller caller)
        {
            if (!caller.CanShare || caller.IsGuest || caller.UserName == null && _config.Current.EnableAuth) { ctx.Response.StatusCode = 403; return; }
            string? path = null;
            double hours = 24;
            try
            {
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
                path = doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() : null;
                if (doc.RootElement.TryGetProperty("hours", out var h) && h.ValueKind == System.Text.Json.JsonValueKind.Number) hours = h.GetDouble();
            }
            catch { }
            if (!PathGuard.TryResolve(path, caller.Roots, out string full) || !(File.Exists(full) || Directory.Exists(full))) { ctx.Response.StatusCode = 404; return; }
            hours = Math.Clamp(hours, 1, 24 * 30);
            var link = _config.AddShare(full, Directory.Exists(full), caller.UserName ?? "admin", TimeSpan.FromHours(hours));
            _log.Write($"Share link made for \"{Path.GetFileName(full)}\" by {caller.UserName ?? "admin"} (expires in {hours:0} h).");
            _services.Activity.Add(caller.UserName, "Created share link", Path.GetFileName(full), ctx);
            string url = ShareUrl(ctx, link.Token);
            await ctx.Response.WriteAsJsonAsync(new { url, expires = link.Expires, token = link.Token, localOnly = IsHomeNetworkUrl(url) });
        }

        private Task ListSharesAsync(HttpContext ctx, Caller caller)
        {
            if (!caller.CanShare || caller.IsGuest) { ctx.Response.StatusCode = 403; return Task.CompletedTask; }
            var list = _config.ActiveShares.Where(s => caller.IsAdmin || s.CreatedBy == caller.UserName)
                .Select(s => new { token = s.Token, name = Path.GetFileName(s.Path), path = s.Path, isFolder = s.IsFolder, createdBy = s.CreatedBy, expires = s.Expires, opens = s.Opens, url = ShareUrl(ctx, s.Token) });
            return ctx.Response.WriteAsJsonAsync(new { shares = list });
        }

        private async Task RevokeShareAsync(HttpContext ctx, Caller caller)
        {
            string? token = null;
            try
            {
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
                token = doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
            }
            catch { }
            var share = _config.FindShare(token);
            if (share == null || caller.IsGuest || !(caller.IsAdmin || (caller.CanShare && share.CreatedBy == caller.UserName))) { ctx.Response.StatusCode = 404; return; }
            _config.RemoveShare(share.Token);
            ctx.Response.StatusCode = 204;
        }

        /// <summary>The link as the person sees the app, but never "localhost" (that only works on this PC).</summary>
        private static string ShareUrl(HttpContext ctx, string token)
        {
            var host = ctx.Request.Host;
            string name = host.Host;
            if (name is "localhost" or "127.0.0.1" or "::1" or "[::1]")
                name = NetworkHelper.GetPrimaryIPv4();
            return $"{ctx.Request.Scheme}://{name}{(host.Port is int p ? ":" + p : "")}/s/{token}";
        }

        /// <summary>True when the link uses a home-network address (it won't open from outside).</summary>
        private static bool IsHomeNetworkUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
            if (u.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;
            if (!System.Net.IPAddress.TryParse(u.Host, out var ip)) return false;
            byte[] b = ip.GetAddressBytes();
            return b.Length == 4 && (b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 169 && b[1] == 254));
        }

        private static bool IsLocalNetworkClient(HttpContext ctx)
            => NetworkHelper.IsLocalClient(ctx.Connection.RemoteIpAddress, ctx.Connection.LocalIpAddress);

        private static async Task XmlAsync(HttpContext ctx, string xml)
        {
            ctx.Response.ContentType = "text/xml; charset=\"utf-8\"";
            await ctx.Response.WriteAsync(xml);
        }

        private static async Task ServeEmbeddedAsync(HttpContext ctx, string resource, string contentType, int cacheSeconds = 0)
        {
            await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
            if (stream == null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            ctx.Response.ContentType = contentType;
            ctx.Response.Headers.CacheControl = cacheSeconds > 0 ? $"public, max-age={cacheSeconds}" : "no-cache";
            // Only our own script and stylesheet may run - even if markup were ever injected,
            // inline scripts are refused by the browser.
            ctx.Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; img-src 'self' data: blob:; media-src 'self' blob:; style-src 'self'; script-src 'self'; worker-src 'self'; manifest-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        }
    }
}
