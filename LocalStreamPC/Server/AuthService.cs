using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using LocalStreamPC.Services;
using Microsoft.AspNetCore.Http;

namespace LocalStreamPC.Server
{
    public enum Role { Viewer = 1, Admin = 2 }

    /// <summary>Who made a request: an account (or an anonymous TV), what it may do and which shared folders it sees.</summary>
    /// <param name="ShareToken">Set for a visitor who came through a share link (no account).</param>
    public sealed record Caller(string? UserName, Role Role, IReadOnlyList<string> Roots,
                                bool CanUploadFiles = false, bool CanCreateShares = false, string? ShareToken = null)
    {
        public bool IsAdmin => Role == Role.Admin;
        public bool IsGuest => ShareToken != null;
        public bool CanUpload => IsAdmin || CanUploadFiles;
        public bool CanShare => IsAdmin || CanCreateShares;
        /// <summary>Key for per-person data such as "continue watching".</summary>
        public string HistoryKey => ShareToken != null ? "share:" + ShareToken : UserName?.ToLowerInvariant() ?? "";
        /// <summary>Who to show in "Streaming now" and the history.</summary>
        public string? DisplayName => UserName ?? (ShareToken != null ? "share link" : null);
    }

    /// <summary>
    /// HTTP Basic authentication against the hashed passwords in the config.
    /// Successful logins are cached for a few minutes (keyed by a hash of the header) because the
    /// browser re-sends credentials on every request, including each thumbnail, and PBKDF2 is slow
    /// on purpose. Repeated failures from one address are throttled.
    /// </summary>
    public sealed class AuthService
    {
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(5);
        private const int MaxFailures = 10;

        public const string ShareCookie = "ls_share";

        private readonly ConfigService _config;
        private readonly AppLog _log;
        private readonly SessionStore _sessions;
        private readonly ConcurrentDictionary<string, (string User, DateTime Expires)> _cache = new();
        private readonly ConcurrentDictionary<string, (int Count, DateTime WindowStart)> _failures = new();

        public AuthService(ConfigService config, AppLog log, SessionStore sessions)
        {
            _config = config;
            _log = log;
            _sessions = sessions;
            _config.Saved += () => _cache.Clear(); // password, name or folders changed
        }

        public enum Outcome { Ok, Unauthorized, TooManyAttempts }

        public Outcome TryAuthenticate(HttpContext ctx, out Caller caller)
        {
            caller = new Caller(null, Role.Viewer, Array.Empty<string>());
            var cfg = _config.Current;
            if (!cfg.EnableAuth) { caller = new Caller(null, Role.Admin, _config.SharedRoots); return Outcome.Ok; }

            string ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
            if (IsLockedOut(ip)) return Outcome.TooManyAttempts;

            string? header = ctx.Request.Headers.Authorization;
            if (string.IsNullOrEmpty(header) || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                // The web page's sign-in cookie, or a share link's cookie.
                if (_sessions.Validate(ctx.Request.Cookies[SessionStore.CookieName], u => _config.FindUser(u)?.PasswordHash) is { } sessionUser &&
                    _config.FindUser(sessionUser) is { } byCookie)
                {
                    caller = ForUser(byCookie);
                    return Outcome.Ok;
                }
                if (ShareCaller(ctx.Request.Cookies[ShareCookie]) is { } guest)
                {
                    caller = guest;
                    return Outcome.Ok;
                }
                return Outcome.Unauthorized;
            }

            string cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(header)));
            if (_cache.TryGetValue(cacheKey, out var cached) && cached.Expires > DateTime.UtcNow && _config.FindUser(cached.User) is { } known)
            {
                caller = ForUser(known);
                return Outcome.Ok;
            }

            if (!TryDecode(header["Basic ".Length..].Trim(), out string user, out string password))
            {
                RecordFailure(ip);
                return Outcome.Unauthorized;
            }

            var account = _config.FindUser(user);
            if (account == null || !PasswordHasher.Verify(password, account.PasswordHash))
            {
                RecordFailure(ip);
                return Outcome.Unauthorized;
            }

            _failures.TryRemove(ip, out _);
            _cache[cacheKey] = (account.Username, DateTime.UtcNow + CacheLifetime);
            caller = ForUser(account);
            return Outcome.Ok;
        }

        private Caller ForUser(Models.UserAccount account)
            => new(account.Username, account.IsAdmin ? Role.Admin : Role.Viewer, _config.RootsFor(account), account.CanUpload, account.CanShare);

        /// <summary>A visitor with a valid share link sees only the shared file or folder.</summary>
        public Caller? ShareCaller(string? token)
        {
            var share = _config.FindShare(token);
            if (share == null) return null;
            // The shared item must still be inside a shared folder.
            if (!PathGuard.TryResolve(share.Path, _config.SharedRoots, out string path)) return null;
            return new Caller(null, Role.Viewer, new[] { path }, ShareToken: share.Token);
        }

        /// <summary>The sign-in form: checks a username and password (with the same lock-out as HTTP logins).</summary>
        public Outcome CheckPassword(HttpContext ctx, string user, string password, out Models.UserAccount? account)
        {
            account = null;
            string ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
            if (IsLockedOut(ip)) return Outcome.TooManyAttempts;
            var found = _config.FindUser(user);
            if (found == null || !PasswordHasher.Verify(password, found.PasswordHash))
            {
                RecordFailure(ip);
                return Outcome.Unauthorized;
            }
            _failures.TryRemove(ip, out _);
            account = found;
            return Outcome.Ok;
        }

        // ------------------------------------------------------------ signed media links

        /// <summary>
        /// A link a TV can open without logging in (TVs can't type passwords): /file/{id}?k=...
        /// The key is an HMAC of the file id and an expiry time, so it only opens that one file.
        /// </summary>
        public string SignMediaId(string id, TimeSpan lifetime)
        {
            long expires = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds();
            return $"{expires:x}.{Mac(id, expires)}";
        }

        public bool IsValidMediaKey(string id, string? key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            int dot = key.IndexOf('.');
            if (dot <= 0 || !long.TryParse(key[..dot], System.Globalization.NumberStyles.HexNumber, null, out long expires)) return false;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expires) return false;
            byte[] expected = Encoding.ASCII.GetBytes(Mac(id, expires));
            byte[] given = Encoding.ASCII.GetBytes(key[(dot + 1)..]);
            return CryptographicOperations.FixedTimeEquals(expected, given);
        }

        private string Mac(string id, long expires)
        {
            byte[] secret = Convert.FromBase64String(_config.Current.MediaLinkSecret ?? "");
            byte[] mac = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes($"{NormalizeId(id)}|{expires}"));
            return Convert.ToHexString(mac, 0, 16).ToLowerInvariant();
        }

        /// <summary>Ids may arrive URL-escaped or in URL-safe base64; compare the decoded path.</summary>
        private static string NormalizeId(string id) => DlnaService.TryDecodeId(id, out var path) ? path : id;

        // ------------------------------------------------------------ CSRF

        /// <summary>
        /// True when a browser tells us the request came from another site (CSRF). Non-browser
        /// clients (apps, curl) don't send these headers and are not affected.
        /// </summary>
        public static bool IsCrossSiteRequest(HttpContext ctx)
        {
            string? fetchSite = ctx.Request.Headers["Sec-Fetch-Site"];
            if (!string.IsNullOrEmpty(fetchSite) &&
                !fetchSite.Equals("same-origin", StringComparison.OrdinalIgnoreCase) &&
                !fetchSite.Equals("none", StringComparison.OrdinalIgnoreCase))
                return true;

            string? origin = ctx.Request.Headers.Origin;
            if (!string.IsNullOrEmpty(origin))
            {
                if (origin == "null") return true;
                if (!Uri.TryCreate(origin, UriKind.Absolute, out var o)) return true;
                string originHost = o.IsDefaultPort ? o.Host : $"{o.Host}:{o.Port}";
                string requestHost = ctx.Request.Host.Value ?? "";
                if (!string.Equals(originHost.Trim('[', ']'), requestHost.Replace("[", "").Replace("]", ""), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool TryDecode(string encoded, out string user, out string password)
        {
            user = password = "";
            Span<byte> buffer = stackalloc byte[Math.Min(encoded.Length, 1024)];
            if (encoded.Length > 1024 || !Convert.TryFromBase64String(encoded, buffer, out int written)) return false;

            // Browsers send UTF-8; very old ones Latin-1. UTF-8 decoding of plain ASCII is identical.
            string decoded = Encoding.UTF8.GetString(buffer[..written]);
            int sep = decoded.IndexOf(':');
            if (sep <= 0) return false;
            user = decoded[..sep];
            password = decoded[(sep + 1)..];
            return true;
        }

        private bool IsLockedOut(string ip)
            => _failures.TryGetValue(ip, out var f) && f.Count >= MaxFailures && DateTime.UtcNow - f.WindowStart < FailureWindow;

        private void RecordFailure(string ip)
        {
            var now = DateTime.UtcNow;
            var entry = _failures.AddOrUpdate(ip, _ => (1, now),
                (_, f) => now - f.WindowStart > FailureWindow ? (1, now) : (f.Count + 1, f.WindowStart));
            if (entry.Count == MaxFailures)
                _log.Write($"Too many failed logins from {ip}. Blocking it for {FailureWindow.TotalMinutes:0} minutes.");
        }
    }
}
