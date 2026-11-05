using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalStreamPC.Services;

namespace LocalStreamPC.Server
{
    /// <summary>
    /// Logins from the web page's sign-in form. The browser keeps a random token in a cookie; only a
    /// hash of it is stored here. "Remember me" sessions last 30 days and survive restarts; others
    /// last 12 hours. A session ends when its password changes or the account is removed.
    /// </summary>
    public sealed class SessionStore
    {
        public const string CookieName = "ls_session";
        public static readonly TimeSpan RememberFor = TimeSpan.FromDays(30);
        private static readonly TimeSpan ShortSession = TimeSpan.FromHours(12);

        private sealed record Session(string User, string PasswordStamp, DateTime Expires, bool Remember);

        private readonly ConcurrentDictionary<string, Session> _sessions = new();
        private readonly object _saveLock = new();

        public SessionStore()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var saved = JsonSerializer.Deserialize<Dictionary<string, Session>>(File.ReadAllText(FilePath));
                    if (saved != null)
                        foreach (var (k, v) in saved.Where(s => s.Value.Expires > DateTime.UtcNow)) _sessions[k] = v;
                }
            }
            catch { /* start with no saved logins */ }
        }

        private static string FilePath => Path.Combine(AppPaths.DataDir, "sessions.json");

        /// <returns>The token to put in the cookie.</returns>
        public string Create(string user, string? passwordHash, bool remember)
        {
            string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            _sessions[Key(token)] = new Session(user, Stamp(passwordHash), DateTime.UtcNow + (remember ? RememberFor : ShortSession), remember);
            if (remember) Save();
            return token;
        }

        /// <summary>The user a cookie belongs to, or null (unknown, expired, or the password changed since).</summary>
        public string? Validate(string? token, Func<string, string?> currentPasswordHash)
        {
            if (string.IsNullOrEmpty(token) || token.Length > 100) return null;
            if (!_sessions.TryGetValue(Key(token), out var s)) return null;
            if (s.Expires < DateTime.UtcNow || currentPasswordHash(s.User) is not { } hash || Stamp(hash) != s.PasswordStamp)
            {
                Remove(token);
                return null;
            }
            return s.User;
        }

        public void Remove(string? token)
        {
            if (string.IsNullOrEmpty(token)) return;
            if (_sessions.TryRemove(Key(token), out var s) && s.Remember) Save();
        }

        private void Save()
        {
            lock (_saveLock)
            {
                try
                {
                    var keep = _sessions.Where(s => s.Value.Remember && s.Value.Expires > DateTime.UtcNow).ToDictionary(s => s.Key, s => s.Value);
                    string tmp = FilePath + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(keep));
                    File.Move(tmp, FilePath, overwrite: true);
                }
                catch { }
            }
        }

        private static string Key(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        private static string Stamp(string? hash) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hash ?? "")))[..16];
    }
}
