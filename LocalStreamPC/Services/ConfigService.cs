using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using LocalStreamPC.Models;

namespace LocalStreamPC.Services
{
    /// <summary>
    /// Loads and saves <see cref="AppConfig"/> from the per-user data folder, migrates old
    /// configs, and gives the server thread-safe snapshots of the settings it needs.
    /// </summary>
    public sealed class ConfigService
    {
        private static readonly string[] OldDefaultPasswords = { "admin123", "guest" };
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private static readonly StringComparison PathComparison =
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        private readonly AppLog _log;
        private readonly object _lock = new();
        private string[] _rootsSnapshot = Array.Empty<string>();
        private string[] _excludedSnapshot = Array.Empty<string>();
        private UserAccount[] _usersSnapshot = Array.Empty<UserAccount>();

        public AppConfig Current { get; private set; } = new();

        /// <summary>Raised after every save, so caches (e.g. auth) can be cleared.</summary>
        public event Action? Saved;

        public ConfigService(AppLog log) => _log = log;

        public void Load()
        {
            string? source = File.Exists(AppPaths.ConfigFile)
                ? AppPaths.ConfigFile
                : AppPaths.LegacyConfigFiles.FirstOrDefault(File.Exists);

            AppConfig? loaded = null;
            if (source != null)
            {
                try
                {
                    loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(source));
                    if (source != AppPaths.ConfigFile)
                        _log.Write($"Moved settings from {source} to {AppPaths.ConfigFile}.");
                }
                catch (Exception ex)
                {
                    _log.Write($"Could not read settings ({ex.Message}). Using defaults.");
                    TryBackupBrokenConfig(source);
                }
            }

            bool isNewInstall = loaded == null;
            var config = loaded ?? new AppConfig();
            config.SharedFolders ??= new List<string>();
            config.History ??= new List<PlaybackHistory>();
            config.Users ??= new List<UserAccount>();
            config.CastResumePoints ??= new List<ResumePoint>();
            foreach (var u in config.Users) u.Folders ??= new List<string>();
            config.Shares ??= new List<ShareLink>();
            config.Shares.RemoveAll(s => s.Expires < DateTime.UtcNow);
            if (config.HttpsPort is < 1 or > 65535) config.HttpsPort = 8443;
            if (string.IsNullOrWhiteSpace(config.SubtitleLanguages)) config.SubtitleLanguages = "en";
            if (config.CastSubtitleScale is < 50 or > 300) config.CastSubtitleScale = 100;
            if (config.Port is < 1 or > 65535) config.Port = 8082;
            if (config.SlideshowSeconds is < 2 or > 120) config.SlideshowSeconds = 6;
            config.TabOrder ??= new List<string>();
            if (config.SettingsRevision < 1)
            {
                // 1.2.1: the server starts with the app (the Library tab opens on it). Older configs saved "off" by default.
                config.AutoStartServer = true;
                config.SettingsRevision = 1;
            }
            if (string.IsNullOrEmpty(config.MediaLinkSecret))
                config.MediaLinkSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

            // Always write back after loading so new settings (e.g. ids added in this version) are
            // persisted - an id that changed on every launch would make devices look "new" each time.
            MigrateAccounts(config, isNewInstall);

            lock (_lock)
            {
                Current = config;
                RefreshSnapshots();
            }

            Save();
        }

        public void Save()
        {
            try
            {
                string json;
                lock (_lock)
                {
                    RefreshSnapshots();
                    json = JsonSerializer.Serialize(Current, JsonOptions);
                }

                // Write to a temp file and swap it in so a crash mid-write can't corrupt settings.
                string tmp = AppPaths.ConfigFile + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, AppPaths.ConfigFile, overwrite: true);
            }
            catch (Exception ex)
            {
                _log.Write("Error saving settings: " + ex.Message);
            }
            Saved?.Invoke();
        }

        /// <summary>Shared folders as a stable array that's safe to read from server threads.</summary>
        public IReadOnlyList<string> SharedRoots => _rootsSnapshot;

        /// <summary>Folders the library leaves out (full paths, no trailing separator).</summary>
        public IReadOnlyList<string> ExcludedFolders => _excludedSnapshot;

        /// <summary>Folder names that are programming project output, never media libraries.</summary>
        public static readonly HashSet<string> BuildFolderNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", "build", "node_modules", ".git", ".svn", ".hg", ".gradle", ".idea", ".vs", ".vscode",
            "__pycache__", ".venv", "venv", "intermediates", "Pods", "DerivedData", ".dart_tool", ".next",
            ".nuget", ".cache", "$RECYCLE.BIN", "System Volume Information",
        };

        /// <summary>True when the library should leave this folder (and everything in it) out.</summary>
        public bool IsExcludedFolder(string fullPath, string name)
        {
            if (Current.SkipBuildFolders && BuildFolderNames.Contains(name)) return true;
            var ex = _excludedSnapshot;
            if (ex.Length == 0) return false;
            string p = Path.TrimEndingDirectorySeparator(fullPath);
            return ex.Any(e => p.Equals(e, PathComparison));
        }

        /// <summary>Web accounts as of the last save (safe to read from server threads).</summary>
        public IReadOnlyList<UserAccount> Users => _usersSnapshot;

        public UserAccount? FindUser(string? name)
            => string.IsNullOrEmpty(name) ? null : _usersSnapshot.FirstOrDefault(u => u.Username.Equals(name, StringComparison.OrdinalIgnoreCase));

        /// <summary>The shared folders an account may use (all of them for accounts without a list).</summary>
        public IReadOnlyList<string> RootsFor(UserAccount? user)
        {
            var roots = _rootsSnapshot;
            if (user == null || user.AllFolders) return roots;
            return roots.Where(r => user.Folders.Any(f => string.Equals(Path.TrimEndingDirectorySeparator(f), Path.TrimEndingDirectorySeparator(r), PathComparison))).ToArray();
        }

        public void AddSharedFolder(string path)
        {
            lock (_lock)
            {
                if (!Current.SharedFolders.Contains(path, StringComparer.OrdinalIgnoreCase))
                    Current.SharedFolders.Add(path);
                RefreshSnapshots();
            }
        }

        public void RemoveSharedFolder(string path)
        {
            lock (_lock)
            {
                Current.SharedFolders.Remove(path);
                foreach (var user in Current.Users) user.Folders.RemoveAll(f => string.Equals(f, path, PathComparison));
                RefreshSnapshots();
            }
        }

        public static string HashPassword(string password) => PasswordHasher.Hash(password);

        // ------------------------------------------------------------ share links (used by server threads too)

        public ShareLink? FindShare(string? token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            lock (_lock)
                return Current.Shares.FirstOrDefault(s => s.Token == token && s.Expires > DateTime.UtcNow);
        }

        public ShareLink AddShare(string path, bool isFolder, string createdBy, TimeSpan lifetime)
        {
            var link = new ShareLink
            {
                Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                Path = path, IsFolder = isFolder, CreatedBy = createdBy,
                Created = DateTime.UtcNow, Expires = DateTime.UtcNow + lifetime,
            };
            lock (_lock)
            {
                Current.Shares.RemoveAll(s => s.Expires < DateTime.UtcNow);
                Current.Shares.Add(link);
            }
            Save();
            return link;
        }

        public bool RemoveShare(string token)
        {
            bool removed;
            lock (_lock) removed = Current.Shares.RemoveAll(s => s.Token == token) > 0;
            if (removed) Save();
            return removed;
        }

        public IReadOnlyList<ShareLink> ActiveShares
        {
            get { lock (_lock) return Current.Shares.Where(s => s.Expires > DateTime.UtcNow).OrderByDescending(s => s.Created).ToList(); }
        }

        public void CountShareOpen(string token)
        {
            lock (_lock) { var s = Current.Shares.FirstOrDefault(x => x.Token == token); if (s != null) s.Opens++; }
        }

        /// <summary>True while any account still uses one of the old built-in default passwords.</summary>
        public bool UsesWeakDefaultPasswords =>
            _usersSnapshot.Any(u => OldDefaultPasswords.Any(p => PasswordHasher.Verify(p, u.PasswordHash)));

        private void RefreshSnapshots()
        {
            _rootsSnapshot = Current.SharedFolders.Where(f => !string.IsNullOrWhiteSpace(f)).ToArray();
            _excludedSnapshot = (Current.ExcludedFolders ??= new()).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => Path.TrimEndingDirectorySeparator(f.Trim())).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            // Copies, so the UI editing an account can't change what a request thread is reading.
            _usersSnapshot = Current.Users.Select(u => new UserAccount
            {
                Username = u.Username,
                PasswordHash = u.PasswordHash,
                IsAdmin = u.IsAdmin,
                AllFolders = u.AllFolders,
                Folders = u.Folders.ToList(),
                CanUpload = u.CanUpload,
                CanShare = u.CanShare,
            }).ToArray();
        }

        private void MigrateAccounts(AppConfig config, bool isNewInstall)
        {
            // v1.0-1.2 stored passwords in plain text. Hash them and drop the plain text.
            if (!string.IsNullOrEmpty(config.AdminPassword) && string.IsNullOrEmpty(config.AdminPasswordHash))
                config.AdminPasswordHash = PasswordHasher.Hash(config.AdminPassword);
            if (!string.IsNullOrEmpty(config.ViewerPassword) && string.IsNullOrEmpty(config.ViewerPasswordHash))
                config.ViewerPasswordHash = PasswordHasher.Hash(config.ViewerPassword);
            config.AdminPassword = config.ViewerPassword = null;

            // v1.2 had exactly one admin and one viewer login: they become the first two accounts,
            // with the same names and passwords.
            if (config.Users.Count == 0)
            {
                string adminName = string.IsNullOrWhiteSpace(config.AdminUsername) ? "admin" : config.AdminUsername.Trim();
                string viewerName = string.IsNullOrWhiteSpace(config.ViewerUsername) ? "viewer" : config.ViewerUsername.Trim();
                if (viewerName.Equals(adminName, StringComparison.OrdinalIgnoreCase)) viewerName = "viewer";

                config.Users.Add(new UserAccount { Username = adminName, PasswordHash = config.AdminPasswordHash, IsAdmin = true });
                config.Users.Add(new UserAccount { Username = viewerName, PasswordHash = config.ViewerPasswordHash, IsAdmin = false });
                if (!isNewInstall && config.AdminPasswordHash != null) _log.Write("Logins moved to the new account list (same names and passwords).");
            }
            config.AdminUsername = config.ViewerUsername = config.AdminPasswordHash = config.ViewerPasswordHash = null;

            // Accounts without a password (new install, or an old config with empty fields):
            // create random ones and show them once in the console.
            foreach (var user in config.Users.Where(u => string.IsNullOrEmpty(u.PasswordHash)))
            {
                string pwd = PasswordHasher.GeneratePassword();
                user.PasswordHash = PasswordHasher.Hash(pwd);
                _log.Write($"Web login for '{user.Username}' ({(user.IsAdmin ? "admin" : "viewer")}): password {pwd}  (change it in Settings)");
            }

            // Never lock everyone out of managing files.
            if (!config.Users.Any(u => u.IsAdmin) && config.Users.Count > 0) config.Users[0].IsAdmin = true;

            if (isNewInstall) config.EnableAuth = true;
        }

        private static void TryBackupBrokenConfig(string path)
        {
            try { File.Copy(path, path + ".broken", overwrite: true); } catch { }
        }
    }
}
