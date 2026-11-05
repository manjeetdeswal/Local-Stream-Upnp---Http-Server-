using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LocalStreamPC.Services;

namespace LocalStreamPC.Server
{
    /// <summary>
    /// Album covers for songs that have none in their tags: first a picture in the song's folder
    /// (cover.jpg, folder.jpg…), then the free Cover Art Archive (found through MusicBrainz by artist
    /// and album). Downloaded covers are kept in the data folder, so each album is looked up once.
    /// </summary>
    public sealed class CoverFinder
    {
        private static readonly HttpClient Http = CreateClient();
        private static readonly string[] CoverNames = { "cover", "folder", "front", "album", "albumart", "albumartsmall", "thumb", "artwork" };
        private static readonly TimeSpan RetryMissesAfter = TimeSpan.FromDays(30);

        private readonly Func<bool> _online;
        private readonly AppLog _log;
        private readonly string _mb = (Environment.GetEnvironmentVariable("LOCALSTREAM_MB_URL") ?? "https://musicbrainz.org/ws/2").TrimEnd('/');
        private readonly string _caa = (Environment.GetEnvironmentVariable("LOCALSTREAM_CAA_URL") ?? "https://coverartarchive.org").TrimEnd('/');
        private readonly ConcurrentDictionary<string, (DateTime At, string? File)> _folderCovers = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<(string Key, string Artist, string Album)> _queue = new();
        private readonly ConcurrentDictionary<string, byte> _queued = new();
        private readonly SemaphoreSlim _worker = new(1, 1);
        private bool _warned;

        public static string CoverDir => Path.Combine(AppPaths.DataDir, "covers");

        public CoverFinder(Func<bool> online, AppLog log)
        {
            _online = online;
            _log = log;
        }

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            // MusicBrainz asks every program to say who it is.
            c.DefaultRequestHeaders.UserAgent.ParseAdd($"LocalStream/{UpdateService.CurrentVersionText} ( https://github.com/{UpdateService.Repository} )");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            return c;
        }

        /// <summary>A cover for this song, or null (an online lookup is started in the background).</summary>
        public string? FindFile(string audioPath, TrackTags tags)
        {
            if (FolderCover(Path.GetDirectoryName(audioPath)) is string local) return local;
            if (!_online() || !Worth(audioPath, tags)) return null;
            string key = Key(tags.Artist, tags.Album);
            string file = Path.Combine(CoverDir, key + ".jpg");
            if (File.Exists(file)) return file;
            string miss = Path.Combine(CoverDir, key + ".none");
            if (File.Exists(miss) && DateTime.UtcNow - File.GetLastWriteTimeUtc(miss) < RetryMissesAfter) return null;
            if (_queued.TryAdd(key, 0))
            {
                _queue.Enqueue((key, tags.Artist, tags.Album));
                _ = RunAsync();
            }
            return null;
        }

        /// <summary>Lookups still waiting.</summary>
        public int Pending => _queued.Count;

        /// <summary>Only real artist + album names are worth searching (not "Unknown artist" with a folder name as album).</summary>
        private static bool Worth(string path, TrackTags tags)
        {
            if (tags.Artist.Equals("Unknown artist", StringComparison.OrdinalIgnoreCase) || tags.Artist.Equals("Various Artists", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.IsNullOrWhiteSpace(tags.Album) || tags.Album.Equals("Unknown album", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private static string Key(string artist, string album)
            => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(artist.Trim().ToLowerInvariant() + "\n" + album.Trim().ToLowerInvariant())))[..24].ToLowerInvariant();

        /// <summary>cover.jpg / folder.jpg / front.png …, or the only picture in the folder.</summary>
        private string? FolderCover(string? dir)
        {
            if (dir == null) return null;
            if (_folderCovers.TryGetValue(dir, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(60)) return cached.File;
            string? found = null;
            try
            {
                var images = Directory.EnumerateFiles(dir).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp").Take(50).ToList();
                found = images.FirstOrDefault(f => CoverNames.Contains(Path.GetFileNameWithoutExtension(f).ToLowerInvariant()))
                     ?? images.FirstOrDefault(f => Path.GetFileName(f).StartsWith("AlbumArt", StringComparison.OrdinalIgnoreCase))
                     ?? (images.Count == 1 ? images[0] : null);
            }
            catch { }
            _folderCovers[dir] = (DateTime.UtcNow, found);
            return found;
        }

        private async Task RunAsync()
        {
            if (!await _worker.WaitAsync(0)) return;
            try
            {
                Directory.CreateDirectory(CoverDir);
                while (_queue.TryDequeue(out var job))
                {
                    try { await LookUpAsync(job.Key, job.Artist, job.Album); }
                    catch (Exception ex)
                    {
                        if (!_warned) { _warned = true; _log.Write($"Album covers: online lookup failed ({ex.Message}). Covers from tags and folders still work."); }
                    }
                    finally { _queued.TryRemove(job.Key, out _); }
                    await Task.Delay(1100); // MusicBrainz allows about one request a second
                }
            }
            finally { _worker.Release(); }
            if (!_queue.IsEmpty) _ = RunAsync();
        }

        private async Task LookUpAsync(string key, string artist, string album)
        {
            static string Q(string s) => "\"" + s.Replace("\\", "").Replace("\"", "") + "\"";
            string url = $"{_mb}/release/?query={Uri.EscapeDataString($"release:{Q(album)} AND artist:{Q(artist)}")}&fmt=json&limit=5";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var ids = new List<string>();
            if (doc.RootElement.TryGetProperty("releases", out var releases) && releases.ValueKind == JsonValueKind.Array)
                foreach (var r in releases.EnumerateArray())
                {
                    int score = r.TryGetProperty("score", out var sc) && sc.TryGetInt32(out int n) ? n : 100;
                    if (score >= 85 && r.TryGetProperty("id", out var id) && id.GetString() is string s) ids.Add(s);
                }
            foreach (var id in ids.Take(3))
            {
                using var res = await Http.GetAsync($"{_caa}/release/{id}/front-250");
                if (!res.IsSuccessStatusCode) continue;
                byte[] data = await res.Content.ReadAsByteArrayAsync();
                if (data.Length < 500 || data.Length > 5 * 1024 * 1024) continue;
                string file = Path.Combine(CoverDir, key + ".jpg");
                await File.WriteAllBytesAsync(file + ".tmp", data);
                File.Move(file + ".tmp", file, overwrite: true);
                return;
            }
            await File.WriteAllTextAsync(Path.Combine(CoverDir, key + ".none"), $"{artist} - {album}");
        }
    }
}
