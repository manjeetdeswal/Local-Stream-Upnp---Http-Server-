using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using LocalStreamPC.Services;

namespace LocalStreamPC.Server
{
    /// <summary>A playlist: a name and an ordered list of files.</summary>
    public sealed class Playlist
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<string> Items { get; set; } = new();
        public DateTime Updated { get; set; } = DateTime.UtcNow;
    }

    /// <summary>Each person's own marks: watched, favorites and playlists (saved in user-library.json).</summary>
    public sealed class UserLibrary
    {
        private sealed class Data
        {
            public HashSet<string> Watched { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Favorites { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public List<Playlist> Playlists { get; set; } = new();
            public Dictionary<string, int> Plays { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        public const int MaxPlaylists = 200, MaxPlaylistItems = 5000;

        private readonly Dictionary<string, Data> _users = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();
        private readonly Timer _saveTimer;
        private bool _dirty;

        private static string FilePath => Path.Combine(AppPaths.DataDir, "user-library.json");

        public UserLibrary()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var data = JsonSerializer.Deserialize<Dictionary<string, Data>>(File.ReadAllText(FilePath));
                    if (data != null)
                        foreach (var (user, d) in data)
                        {
                            // JSON loses the comparer: put it back.
                            _users[user] = new Data
                            {
                                Watched = new HashSet<string>(d.Watched ?? new(), StringComparer.OrdinalIgnoreCase),
                                Favorites = new HashSet<string>(d.Favorites ?? new(), StringComparer.OrdinalIgnoreCase),
                                Playlists = d.Playlists ?? new(),
                                Plays = new Dictionary<string, int>(d.Plays ?? new(), StringComparer.OrdinalIgnoreCase),
                            };
                        }
                }
            }
            catch { }
            _saveTimer = new Timer(_ => Save(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        private Data For(string user)
        {
            if (!_users.TryGetValue(user, out var d)) _users[user] = d = new Data();
            return d;
        }

        public (IReadOnlyCollection<string> Watched, IReadOnlyCollection<string> Favorites, IReadOnlyList<Playlist> Playlists) Get(string user)
        {
            lock (_lock)
            {
                if (!_users.TryGetValue(user, out var d)) return (Array.Empty<string>(), Array.Empty<string>(), Array.Empty<Playlist>());
                return (d.Watched.ToList(), d.Favorites.ToList(),
                        d.Playlists.Select(p => new Playlist { Id = p.Id, Name = p.Name, Items = p.Items.ToList(), Updated = p.Updated }).ToList());
            }
        }

        public bool IsWatched(string user, string path) { lock (_lock) return _users.TryGetValue(user, out var d) && d.Watched.Contains(path); }

        public void SetWatched(string user, string path, bool watched)
        {
            lock (_lock)
            {
                var d = For(user);
                if (watched ? d.Watched.Add(path) : d.Watched.Remove(path)) _dirty = true;
                if (d.Watched.Count > 20000) { d.Watched.Remove(d.Watched.First()); }
            }
        }

        public void SetFavorite(string user, string path, bool favorite)
        {
            lock (_lock)
            {
                var d = For(user);
                if (favorite ? d.Favorites.Count < 5000 && d.Favorites.Add(path) : d.Favorites.Remove(path)) _dirty = true;
            }
        }

        /// <summary>A song (or video) was played through: counts for "Most played".</summary>
        public void CountPlay(string user, string path)
        {
            lock (_lock)
            {
                var d = For(user);
                d.Plays[path] = d.Plays.TryGetValue(path, out int n) ? n + 1 : 1;
                if (d.Plays.Count > 50000) d.Plays.Remove(d.Plays.OrderBy(p => p.Value).First().Key);
                _dirty = true;
            }
        }

        public IReadOnlyDictionary<string, int> PlayCounts(string user)
        {
            lock (_lock) return _users.TryGetValue(user, out var d) ? new Dictionary<string, int>(d.Plays, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, int>();
        }

        public Playlist? GetPlaylist(string user, string id)
        {
            lock (_lock)
            {
                var p = _users.TryGetValue(user, out var d) ? d.Playlists.FirstOrDefault(x => x.Id == id) : null;
                return p == null ? null : new Playlist { Id = p.Id, Name = p.Name, Items = p.Items.ToList(), Updated = p.Updated };
            }
        }

        public Playlist CreatePlaylist(string user, string name)
        {
            lock (_lock)
            {
                var d = For(user);
                if (d.Playlists.Count >= MaxPlaylists) throw new InvalidOperationException($"You can have up to {MaxPlaylists} playlists.");
                var p = new Playlist { Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(), Name = Clean(name) };
                d.Playlists.Add(p);
                _dirty = true;
                return p;
            }
        }

        /// <summary>Changes a playlist. Returns false when it doesn't exist.</summary>
        public bool Edit(string user, string id, Action<Playlist> change)
        {
            lock (_lock)
            {
                var p = _users.TryGetValue(user, out var d) ? d.Playlists.FirstOrDefault(x => x.Id == id) : null;
                if (p == null) return false;
                change(p);
                p.Name = Clean(p.Name);
                p.Items = p.Items.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxPlaylistItems).ToList();
                p.Updated = DateTime.UtcNow;
                _dirty = true;
                return true;
            }
        }

        public bool DeletePlaylist(string user, string id)
        {
            lock (_lock)
            {
                bool removed = _users.TryGetValue(user, out var d) && d.Playlists.RemoveAll(x => x.Id == id) > 0;
                _dirty |= removed;
                return removed;
            }
        }

        private static string Clean(string? name)
        {
            name = (name ?? "").Trim();
            if (name.Length > 80) name = name[..80];
            return name.Length == 0 ? "New playlist" : name;
        }

        public void Save()
        {
            string json;
            lock (_lock)
            {
                if (!_dirty) return;
                _dirty = false;
                json = JsonSerializer.Serialize(_users);
            }
            try
            {
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch { lock (_lock) _dirty = true; }
        }
    }
}
