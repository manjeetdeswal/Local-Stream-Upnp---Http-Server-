using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LocalStreamPC.Services
{
    /// <summary>What a video's file name says it is: a movie (title + year) or a TV episode.</summary>
    public sealed record VideoName(bool IsEpisode, string Title, int Year, int Season, int Episode);

    public static class VideoNameParser
    {
        private static readonly Regex SxxEyy = new(@"\bS(\d{1,2})\s?(?:-\s?)?EP?\s?(\d{1,3})(?:\s?-?\s?EP?\d{1,3})?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SeasonEpisodeWords = new(@"\bSeason\s?(\d{1,2})\s?[-,]?\s?(?:Episode|Ep)\s?(\d{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex EpisodeOnly = new(@"(?:^|\s)(?:Episode|Ep|E)\s?(\d{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex NxNN = new(@"\b(\d{1,2})x(\d{2,3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex YearRx = new(@"(?:^|[\s(\[])((?:19|20)\d{2})(?=$|[\s)\]])", RegexOptions.Compiled);
        private static readonly Regex SeasonFolder = new(@"^(?:season|series|staffel|saison|temporada)\s*(\d{1,2})$|^s(\d{1,2})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex LeadingEpisode = new(@"^(?:e|ep|episode)?\s*(\d{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Junk = new(@"\b(2160p|1080p|1080i|720p|576p|480p|4k|uhd|hdr|hdr10|dv|bluray|blu ray|bdrip|brrip|dvdrip|webrip|web dl|webdl|web|hdtv|x264|x265|h264|h265|hevc|avc|xvid|aac|ac3|dts|ddp5|dd5|atmos|remux|proper|repack|extended|unrated|imax|10bit|multi|dual audio|amzn|nf|dsnp|hmax|hs|hotstar|zee5|sonyliv|jiocinema|mxplayer|altbalaji|voot|yts|rarbg|esub|esubs)\b.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static VideoName Parse(string path)
        {
            string file = Path.GetFileNameWithoutExtension(path);
            string clean = Tidy(file);
            string? folder = Path.GetFileName(Path.GetDirectoryName(path));
            string? grandFolder = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path)));

            var m = SxxEyy.Match(clean);
            if (!m.Success) m = SeasonEpisodeWords.Match(clean);
            if (!m.Success) m = NxNN.Match(clean);
            if (m.Success)
            {
                string show = CutJunk(clean[..m.Index]);
                if (show.Length == 0) show = ShowFromFolders(folder, grandFolder);
                return new VideoName(true, show, 0, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
            }

            // "Show/Season 2/03 - Name.mkv"
            var sf = folder == null ? Match.Empty : SeasonFolder.Match(Tidy(folder));
            if (sf.Success && LeadingEpisode.Match(clean) is { Success: true } ep && grandFolder != null)
            {
                int season = int.Parse(sf.Groups[1].Success ? sf.Groups[1].Value : sf.Groups[2].Value);
                return new VideoName(true, CutJunk(Tidy(grandFolder)), 0, season, int.Parse(ep.Groups[1].Value));
            }

            // "Panchayat/Episode 3.mp4", "Mirzapur Ep 05.mkv": an episode of the folder's show (season from
            // a "Season 2" folder, else 1). Names with a year are left to the movie rules ("Star Wars Episode 4 (1977)").
            if (folder != null && !YearRx.IsMatch(clean) && EpisodeOnly.Match(clean) is { Success: true } eo)
            {
                string show = CutJunk(clean[..eo.Index]);
                if (show.Length == 0) show = ShowFromFolders(folder, grandFolder);
                int season = sf.Success ? int.Parse(sf.Groups[1].Success ? sf.Groups[1].Value : sf.Groups[2].Value) : 1;
                if (show.Length > 0) return new VideoName(true, show, 0, season, int.Parse(eo.Groups[1].Value));
            }

            // Movie: the title is what comes before the year (the last plausible one: "2001 A Space Odyssey 1968").
            var years = YearRx.Matches(clean).Where(y => y.Groups[1].Index > 0).ToList();
            if (years.Count > 0)
            {
                var y = years[^1];
                string title = CutJunk(clean[..y.Groups[1].Index]).Trim(' ', '(', '[', '-');
                if (title.Length > 0) return new VideoName(false, title, int.Parse(y.Groups[1].Value), 0, 0);
            }
            string t = CutJunk(clean);
            return new VideoName(false, t.Length > 0 ? t : file, 0, 0, 0);
        }

        private static string ShowFromFolders(string? folder, string? grandFolder)
        {
            if (folder == null) return "";
            if (SeasonFolder.IsMatch(Tidy(folder))) return grandFolder != null ? CutJunk(Tidy(grandFolder)) : "";
            return CutJunk(Tidy(folder));
        }

        private static string Tidy(string s)
        {
            s = Regex.Replace(s, @"^(?:\[[^\]]*\]\s*)+", ""); // "[YTS.MX] Name" release-group tags in front
            s = Regex.Replace(s, @"[._]+", " ");
            s = Regex.Replace(s, @"\[[^\]]*(?:rip|web|1080|720|x26|hevc)[^\]]*\]", " ", RegexOptions.IgnoreCase); // [1080p WEB] style tags
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        private static string CutJunk(string s) => Regex.Replace(Junk.Replace(s, ""), @"\s+", " ").Trim(' ', '-', '(', '[');

        private static readonly Regex WebFolder = new(@"(^|[\\/])[^\\/]*web[\s._-]*series[^\\/]*[\\/]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Release tags are upper case ("Mirzapur.S01E01.AMZN.WEB-DL"); service names may be written any way.
        private static readonly Regex StreamingTag = new(@"(?:^|[\s._\[\(-])(NF|AMZN|DSNP|HS|JC|ATVP|HMAX|PCOK|SNXT|VOOT)(?=$|[\s._\]\)-])", RegexOptions.Compiled);
        private static readonly Regex StreamingName = new(@"\b(netflix|hotstar|sony ?liv|zee5|jio ?cinema|mx ?player|alt ?balaji|voot|amazon prime|prime video|sun ?nxt|aha video)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// A web series rather than a TV show: kept in a folder named like "Web Series", or released
        /// by a streaming service (NF, AMZN, ZEE5, SonyLIV, Hotstar… in the file name).
        /// </summary>
        public static bool IsWebSeries(string path)
            => WebFolder.IsMatch(path.Replace('\\', '/')) || StreamingTag.IsMatch(Path.GetFileNameWithoutExtension(path)) || StreamingName.IsMatch(Path.GetFileNameWithoutExtension(path).Replace('.', ' ').Replace('_', ' '));

        /// <summary>For grouping: "The Office (US)" and "the office us" are the same show.</summary>
        public static string Key(string title) => Regex.Replace(title.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
    }

    /// <summary>A movie or show as TMDB describes it.</summary>
    public sealed record TmdbInfo(int Id, string Title, int Year, string Overview, string? Poster, string? Backdrop, double Rating);

    public sealed record TmdbEpisode(int Number, string Title, string Overview, string? Still);

    /// <summary>
    /// Looks movies and shows up on The Movie Database (themoviedb.org) with the user's own free API key,
    /// and keeps every answer and poster on disk so each title is looked up only once.
    /// </summary>
    public sealed class TmdbClient
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
        private readonly Func<string> _apiKey;
        private readonly AppLog _log;
        private readonly string _api = (Environment.GetEnvironmentVariable("LOCALSTREAM_TMDB_URL") ?? "https://api.themoviedb.org/3").TrimEnd('/');
        private readonly string _images = (Environment.GetEnvironmentVariable("LOCALSTREAM_TMDB_IMAGES") ?? "https://image.tmdb.org/t/p").TrimEnd('/');
        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
        private readonly ConcurrentQueue<Func<Task>> _work = new();
        private readonly ConcurrentDictionary<string, byte> _queued = new();
        private readonly SemaphoreSlim _workerGate = new(1, 1);
        private int _dirty;
        private string? _lastError;

        private sealed class CacheEntry
        {
            public TmdbInfo? Info { get; set; }
            public Dictionary<int, TmdbEpisode>? Episodes { get; set; }
            public DateTime Fetched { get; set; }
        }

        public static string PosterDir => Path.Combine(AppPaths.DataDir, "posters");
        private static string CacheFile => Path.Combine(AppPaths.DataDir, "tmdb-cache.json");

        public TmdbClient(Func<string> apiKey, AppLog log)
        {
            _apiKey = apiKey;
            _log = log;
            try
            {
                if (File.Exists(CacheFile))
                    foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(CacheFile)) ?? new())
                        _cache[k] = v;
            }
            catch { }
        }

        public bool Enabled => !string.IsNullOrWhiteSpace(_apiKey());

        /// <summary>Lookups still waiting (the web page reloads while there are some).</summary>
        public int Pending => _queued.Count;

        public string? LastError => _lastError;

        public static string MovieKey(string title, int year) => $"movie:{VideoNameParser.Key(title)}|{year}";
        public static string ShowKey(string title) => $"tv:{VideoNameParser.Key(title)}";
        public static string SeasonKey(int showId, int season) => $"season:{showId}:{season}";

        /// <summary>What is known now; unknown titles are queued for lookup.</summary>
        public TmdbInfo? Movie(string title, int year)
        {
            string key = MovieKey(title, year);
            if (Known(key, out var e)) return e?.Info;
            Enqueue(key, () => FetchMovieAsync(key, title, year));
            return null;
        }

        public TmdbInfo? Show(string title)
        {
            string key = ShowKey(title);
            if (Known(key, out var e)) return e?.Info;
            Enqueue(key, () => FetchShowAsync(key, title));
            return null;
        }

        public IReadOnlyDictionary<int, TmdbEpisode>? Season(int showId, int season)
        {
            string key = SeasonKey(showId, season);
            if (Known(key, out var e)) return e?.Episodes;
            Enqueue(key, () => FetchSeasonAsync(key, showId, season));
            return null;
        }

        private bool Known(string key, out CacheEntry? entry)
        {
            if (_cache.TryGetValue(key, out entry))
            {
                // "Not found" answers are tried again after a week (TMDB keeps growing).
                if (entry.Info != null || entry.Episodes != null || DateTime.UtcNow - entry.Fetched < TimeSpan.FromDays(7)) return true;
            }
            return !Enabled; // no key: nothing to look up
        }

        private void Enqueue(string key, Func<Task> fetch)
        {
            if (!Enabled || !_queued.TryAdd(key, 0)) return;
            _work.Enqueue(async () => { try { await fetch(); } finally { _queued.TryRemove(key, out _); } });
            _ = RunQueueAsync();
        }

        private async Task RunQueueAsync()
        {
            if (!await _workerGate.WaitAsync(0)) return;
            try
            {
                while (_work.TryDequeue(out var job))
                {
                    try { await job(); }
                    catch (Exception ex) { _lastError = ex.Message; }
                    await Task.Delay(60); // well under TMDB's rate limit
                }
                Save();
            }
            finally { _workerGate.Release(); }
            if (!_work.IsEmpty) _ = RunQueueAsync();
        }

        private async Task FetchMovieAsync(string key, string title, int year)
        {
            var doc = await GetAsync($"/search/movie?query={Uri.EscapeDataString(title)}{(year > 0 ? "&year=" + year : "")}&include_adult=false");
            var r = Best(doc, year, "release_date");
            if (r == null && year > 0) r = Best(await GetAsync($"/search/movie?query={Uri.EscapeDataString(title)}&include_adult=false"), year, "release_date");
            Store(key, r == null ? null : await ToInfoAsync(r.Value, "title", "release_date"));
        }

        private async Task FetchShowAsync(string key, string title)
        {
            var r = Best(await GetAsync($"/search/tv?query={Uri.EscapeDataString(title)}"), 0, "first_air_date");
            Store(key, r == null ? null : await ToInfoAsync(r.Value, "name", "first_air_date"));
        }

        private async Task FetchSeasonAsync(string key, int showId, int season)
        {
            var doc = await GetAsync($"/tv/{showId}/season/{season}");
            var episodes = new Dictionary<int, TmdbEpisode>();
            if (doc.TryGetProperty("episodes", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var e in list.EnumerateArray())
                {
                    int n = e.TryGetProperty("episode_number", out var en) && en.TryGetInt32(out int x) ? x : 0;
                    if (n <= 0) continue;
                    string? still = Str(e, "still_path");
                    episodes[n] = new TmdbEpisode(n, Str(e, "name") ?? $"Episode {n}", Str(e, "overview") ?? "", still != null ? await PosterAsync(still, "w300") : null);
                }
            _cache[key] = new CacheEntry { Episodes = episodes, Fetched = DateTime.UtcNow };
            Interlocked.Exchange(ref _dirty, 1);
        }

        private void Store(string key, TmdbInfo? info)
        {
            _cache[key] = new CacheEntry { Info = info, Fetched = DateTime.UtcNow };
            Interlocked.Exchange(ref _dirty, 1);
        }

        private static JsonElement? Best(JsonElement doc, int year, string dateField)
        {
            if (!doc.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return null;
            var list = results.EnumerateArray().ToList();
            if (list.Count == 0) return null;
            if (year > 0)
            {
                var sameYear = list.FirstOrDefault(r => (Str(r, dateField) ?? "").StartsWith(year.ToString()));
                if (sameYear.ValueKind == JsonValueKind.Object) return sameYear;
            }
            return list[0];
        }

        private async Task<TmdbInfo> ToInfoAsync(JsonElement r, string titleField, string dateField)
        {
            int id = r.TryGetProperty("id", out var i) && i.TryGetInt32(out int n) ? n : 0;
            string date = Str(r, dateField) ?? "";
            int year = date.Length >= 4 && int.TryParse(date[..4], out int y) ? y : 0;
            double rating = r.TryGetProperty("vote_average", out var v) && v.TryGetDouble(out double d) ? Math.Round(d, 1) : 0;
            string? poster = Str(r, "poster_path"), backdrop = Str(r, "backdrop_path");
            return new TmdbInfo(id, Str(r, titleField) ?? "", year, Str(r, "overview") ?? "",
                poster != null ? await PosterAsync(poster, "w342") : null,
                backdrop != null ? await PosterAsync(backdrop, "w780") : null, rating);
        }

        /// <summary>Downloads an image once; returns the local file name (served as /poster/{name}).</summary>
        private async Task<string?> PosterAsync(string tmdbPath, string size)
        {
            string name = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(size + tmdbPath)))[..20].ToLowerInvariant() + ".jpg";
            string file = Path.Combine(PosterDir, name);
            if (File.Exists(file)) return name;
            try
            {
                Directory.CreateDirectory(PosterDir);
                byte[] data = await Http.GetByteArrayAsync($"{_images}/{size}/{tmdbPath.TrimStart('/')}");
                if (data.Length < 100 || data.Length > 8 * 1024 * 1024) return null;
                await File.WriteAllBytesAsync(file + ".tmp", data);
                File.Move(file + ".tmp", file, overwrite: true);
                return name;
            }
            catch { return null; }
        }

        private async Task<JsonElement> GetAsync(string pathAndQuery)
        {
            string key = _apiKey().Trim();
            bool bearer = key.StartsWith("eyJ"); // the long "read access token" instead of the short API key
            string url = _api + pathAndQuery + (bearer ? "" : (pathAndQuery.Contains('?') ? "&" : "?") + "api_key=" + Uri.EscapeDataString(key));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (bearer) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var res = await Http.SendAsync(req);
            if ((int)res.StatusCode == 401)
            {
                if (_lastError == null) _log.Write("TMDB: the API key was refused. Check it in Settings.");
                _lastError = "The TMDB key was refused.";
                throw new InvalidOperationException(_lastError);
            }
            if ((int)res.StatusCode == 429) { await Task.Delay(2000); throw new InvalidOperationException("TMDB is busy."); }
            res.EnsureSuccessStatusCode();
            _lastError = null;
            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync());
            return doc.RootElement.Clone();
        }

        private static string? Str(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

        /// <summary>Forgets every "not found" answer (after the key changes).</summary>
        public void RetryMisses()
        {
            foreach (var (k, v) in _cache) if (v.Info == null && v.Episodes == null) _cache.TryRemove(k, out _);
        }

        public void Save()
        {
            if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
            try
            {
                string tmp = CacheFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_cache.ToDictionary(k => k.Key, v => v.Value)));
                File.Move(tmp, CacheFile, overwrite: true);
            }
            catch { Interlocked.Exchange(ref _dirty, 1); }
        }
    }
}
