using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LocalStreamPC.Services;
using Microsoft.AspNetCore.Http;

namespace LocalStreamPC.Server
{
    /// <summary>
    /// The library pages: Movies and TV shows (with TMDB posters), photos by date, each person's
    /// watched marks, favorites and playlists, song lyrics and "Find subtitles".
    /// </summary>
    public sealed class LibraryApi
    {
        private readonly ConfigService _config;
        private readonly AppLog _log;
        private readonly ServerServices _s;

        public LibraryApi(ConfigService config, AppLog log, ServerServices services)
        {
            _config = config;
            _log = log;
            _s = services;
        }

        private static object Item(string path, EntryKind kind, long size)
        {
            string id = DlnaService.EncodeId(path);
            return new
            {
                name = Path.GetFileName(path), path, type = "file", kind = kind.ToString().ToLowerInvariant(), size,
                file = $"/file/{id}", thumb = kind != EntryKind.Other ? $"/thumb/{id}" : null, folder = Path.GetDirectoryName(path),
            };
        }

        private static string? Poster(string? name) => name == null ? null : "/poster/" + name;

        private IEnumerable<LibraryEntry> Mine(IReadOnlyList<LibraryEntry> entries, Caller caller)
            => entries.Where(e => caller.Roots.Contains(e.Root));

        // ------------------------------------------------------------ changes

        /// <summary>GET /api/version : changes when files are added or removed (the page refreshes itself).</summary>
        public async Task VersionAsync(HttpContext ctx, Caller caller)
        {
            await _s.Library.GetAsync(ctx.RequestAborted);
            ctx.Response.Headers.CacheControl = "no-store";
            await ctx.Response.WriteAsJsonAsync(new { v = _s.Library.Version });
        }

        // ------------------------------------------------------------ watched, favorites, playlists

        /// <summary>GET /api/mylib : watched paths, favorites and playlists.</summary>
        public async Task MyLibraryAsync(HttpContext ctx, Caller caller)
        {
            var (watched, favorites, playlists) = _s.Marks.Get(caller.HistoryKey);
            bool Visible(string p) => PathGuard.TryResolve(p, caller.Roots, out _) && File.Exists(p);
            await ctx.Response.WriteAsJsonAsync(new
            {
                watched = watched.Where(p => PathGuard.TryResolve(p, caller.Roots, out _)),
                favorites = favorites.Where(Visible).Select(p => Item(p, LibraryIndex.KindOf(p), SafeSize(p))),
                playlists = playlists.OrderByDescending(p => p.Updated).Select(p =>
                {
                    var items = p.Items.Where(Visible).ToList();
                    return new { id = p.Id, name = p.Name, count = items.Count, cover = items.Count > 0 ? $"/thumb/{DlnaService.EncodeId(items[0])}" : null };
                }),
            });
        }

        private static long SafeSize(string p) { try { return new FileInfo(p).Length; } catch { return 0; } }

        /// <summary>POST /api/mark {path, watched?, favorite?}</summary>
        public async Task MarkAsync(HttpContext ctx, Caller caller)
        {
            var body = await ReadJsonAsync(ctx);
            if (body == null || !PathGuard.TryResolve(Str(body.Value, "path"), caller.Roots, out string full) || !File.Exists(full)) { ctx.Response.StatusCode = 400; return; }
            if (Bool(body.Value, "watched") is bool w)
            {
                _s.Marks.SetWatched(caller.HistoryKey, full, w);
                if (w) _s.History.Remove(caller.HistoryKey, full); // watched: out of "Continue watching"
            }
            if (Bool(body.Value, "favorite") is bool f) _s.Marks.SetFavorite(caller.HistoryKey, full, f);
            if (Bool(body.Value, "played") == true) _s.Marks.CountPlay(caller.HistoryKey, full);
            await ctx.Response.WriteAsJsonAsync(new { ok = true, watched = _s.Marks.IsWatched(caller.HistoryKey, full) });
        }

        /// <summary>GET /api/playlist?id= : the playlist's files (with song tags).</summary>
        public async Task PlaylistAsync(HttpContext ctx, Caller caller)
        {
            var p = _s.Marks.GetPlaylist(caller.HistoryKey, ctx.Request.Query["id"].ToString());
            if (p == null) { ctx.Response.StatusCode = 404; return; }
            var entries = (await _s.Library.GetAsync(ctx.RequestAborted)).ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
            var items = p.Items.Where(path => PathGuard.TryResolve(path, caller.Roots, out _) && File.Exists(path)).Select(path =>
            {
                var kind = LibraryIndex.KindOf(path);
                TrackTags? tags = kind == EntryKind.Audio && entries.TryGetValue(path, out var e) ? _s.Library.TagsFor(e) : null;
                return new { item = Item(path, kind, SafeSize(path)), title = tags?.Title ?? Path.GetFileNameWithoutExtension(path), artist = tags?.Artist, album = tags?.Album };
            });
            await ctx.Response.WriteAsJsonAsync(new { id = p.Id, name = p.Name, items });
        }

        /// <summary>POST /api/playlist {action: create|rename|delete|add|remove|set, id, name, paths[]}</summary>
        public async Task EditPlaylistAsync(HttpContext ctx, Caller caller)
        {
            var body = await ReadJsonAsync(ctx);
            if (body == null) { ctx.Response.StatusCode = 400; return; }
            string user = caller.HistoryKey, action = Str(body.Value, "action") ?? "", id = Str(body.Value, "id") ?? "";
            var paths = new List<string>();
            if (body.Value.TryGetProperty("paths", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var p in arr.EnumerateArray().Take(UserLibrary.MaxPlaylistItems))
                    if (p.ValueKind == JsonValueKind.String && PathGuard.TryResolve(p.GetString(), caller.Roots, out string full) && File.Exists(full)) paths.Add(full);
            try
            {
                switch (action)
                {
                    case "create":
                        var created = _s.Marks.CreatePlaylist(user, Str(body.Value, "name") ?? "");
                        if (paths.Count > 0) _s.Marks.Edit(user, created.Id, p => p.Items.AddRange(paths));
                        await ctx.Response.WriteAsJsonAsync(new { ok = true, id = created.Id });
                        return;
                    case "rename": if (!_s.Marks.Edit(user, id, p => p.Name = Str(body.Value, "name") ?? p.Name)) { ctx.Response.StatusCode = 404; return; } break;
                    case "delete": if (!_s.Marks.DeletePlaylist(user, id)) { ctx.Response.StatusCode = 404; return; } break;
                    case "add": if (!_s.Marks.Edit(user, id, p => p.Items.AddRange(paths))) { ctx.Response.StatusCode = 404; return; } break;
                    case "remove": if (!_s.Marks.Edit(user, id, p => p.Items.RemoveAll(i => paths.Contains(i, StringComparer.OrdinalIgnoreCase)))) { ctx.Response.StatusCode = 404; return; } break;
                    case "set":
                        // New order. Files this person can't see (other folders) keep their place at the end.
                        if (!_s.Marks.Edit(user, id, p => p.Items = paths.Concat(p.Items.Where(i => !PathGuard.TryResolve(i, caller.Roots, out _))).ToList())) { ctx.Response.StatusCode = 404; return; }
                        break;
                    default: ctx.Response.StatusCode = 400; return;
                }
            }
            catch (InvalidOperationException ex) { await Error(ctx, 409, ex.Message); return; }
            await ctx.Response.WriteAsJsonAsync(new { ok = true });
        }

        // ------------------------------------------------------------ movies and TV shows

        private sealed record VideoRow(LibraryEntry Entry, VideoName Name);

        /// <summary>
        /// GET /api/videos?view=movies | view=shows | show=key : videos grouped from their file names,
        /// with posters and descriptions from TMDB when a key is set.
        /// </summary>
        public async Task VideosAsync(HttpContext ctx, Caller caller)
        {
            var entries = await _s.Library.GetAsync(ctx.RequestAborted);
            var videos = Mine(entries, caller).Where(e => e.Kind == EntryKind.Video && !IsExtra(e)).Select(e => new VideoRow(e, VideoNameParser.Parse(e.Path))).ToList();
            var (watchedList, favList, _) = _s.Marks.Get(caller.HistoryKey);
            var watched = new HashSet<string>(watchedList, StringComparer.OrdinalIgnoreCase);
            var favs = new HashSet<string>(favList, StringComparer.OrdinalIgnoreCase);
            var tmdb = _s.Tmdb;
            string? showKey = ctx.Request.Query["show"];

            object Progress(string path) => _s.History.Get(caller.HistoryKey, path) is { } p && p.Duration > 0 ? Math.Round(p.Position / p.Duration, 3) : 0;

            if (!string.IsNullOrEmpty(showKey))
            {
                var eps = videos.Where(v => v.Name.IsEpisode && VideoNameParser.Key(v.Name.Title) == showKey).ToList();
                if (eps.Count == 0) { ctx.Response.StatusCode = 404; return; }
                string title = eps.GroupBy(e => e.Name.Title).OrderByDescending(g => g.Count()).First().Key;
                var info = tmdb.Show(title);
                var seasons = eps.GroupBy(e => e.Name.Season).OrderBy(g => g.Key).Select(g =>
                {
                    var details = info != null ? tmdb.Season(info.Id, g.Key) : null;
                    return new
                    {
                        number = g.Key,
                        episodes = g.OrderBy(e => e.Name.Episode).ThenBy(e => e.Entry.Name, StringComparer.CurrentCultureIgnoreCase).Select(e =>
                        {
                            TmdbEpisode? d = details != null && details.TryGetValue(e.Name.Episode, out var x) ? x : null;
                            return new
                            {
                                item = Item(e.Entry.Path, e.Entry.Kind, e.Entry.Size), number = e.Name.Episode,
                                title = d?.Title ?? $"Episode {e.Name.Episode}", overview = d?.Overview ?? "", still = Poster(d?.Still),
                                watched = watched.Contains(e.Entry.Path), progress = Progress(e.Entry.Path),
                            };
                        }),
                    };
                }).ToList();
                // Next to watch: the first episode not marked watched.
                var next = eps.OrderBy(e => e.Name.Season).ThenBy(e => e.Name.Episode).FirstOrDefault(e => !watched.Contains(e.Entry.Path));
                await ctx.Response.WriteAsJsonAsync(new
                {
                    key = showKey, web = eps.Any(e => VideoNameParser.IsWebSeries(e.Entry.Path)),
                    title = info?.Title is { Length: > 0 } t ? t : title, year = info?.Year ?? 0, overview = info?.Overview ?? "",
                    poster = Poster(info?.Poster), backdrop = Poster(info?.Backdrop), rating = info?.Rating ?? 0,
                    next = next == null ? null : next.Entry.Path, seasons,
                    tmdb = tmdb.Enabled, pending = tmdb.Pending, error = tmdb.LastError,
                });
                return;
            }

            if (ctx.Request.Query["view"] == "shows")
            {
                // kind=web: web series only; kind=tv: everything else (a show is a web series when any episode says so).
                string kind = ctx.Request.Query["kind"].ToString();
                var shows = videos.Where(v => v.Name.IsEpisode && v.Name.Title.Length > 0).GroupBy(v => VideoNameParser.Key(v.Name.Title))
                    .Where(g => kind switch { "web" => g.Any(e => VideoNameParser.IsWebSeries(e.Entry.Path)), "tv" => !g.Any(e => VideoNameParser.IsWebSeries(e.Entry.Path)), _ => true })
                    .Select(g =>
                {
                    string title = g.GroupBy(e => e.Name.Title).OrderByDescending(x => x.Count()).First().Key;
                    var info = tmdb.Show(title);
                    var first = g.OrderBy(e => e.Name.Season).ThenBy(e => e.Name.Episode).First();
                    return new
                    {
                        key = g.Key, title = info?.Title is { Length: > 0 } t ? t : title, year = info?.Year ?? 0, overview = info?.Overview ?? "",
                        poster = Poster(info?.Poster), thumb = $"/thumb/{DlnaService.EncodeId(first.Entry.Path)}",
                        seasons = g.Select(e => e.Name.Season).Distinct().Count(), episodes = g.Count(),
                        unwatched = g.Count(e => !watched.Contains(e.Entry.Path)),
                        added = g.Max(e => e.Entry.Added),
                    };
                }).OrderBy(s => s.title, StringComparer.CurrentCultureIgnoreCase).ToList();
                await ctx.Response.WriteAsJsonAsync(new { shows, tmdb = tmdb.Enabled, pending = tmdb.Pending, error = tmdb.LastError });
                return;
            }

            var movies = videos.Where(v => !v.Name.IsEpisode).Select(v =>
            {
                var info = tmdb.Movie(v.Name.Title, v.Name.Year);
                return new
                {
                    item = Item(v.Entry.Path, v.Entry.Kind, v.Entry.Size),
                    title = info?.Title is { Length: > 0 } t ? t : v.Name.Title, year = info?.Year is > 0 and int y ? y : v.Name.Year,
                    overview = info?.Overview ?? "", poster = Poster(info?.Poster), rating = info?.Rating ?? 0,
                    watched = watched.Contains(v.Entry.Path), favorite = favs.Contains(v.Entry.Path), progress = Progress(v.Entry.Path),
                    added = v.Entry.Added,
                };
            }).OrderBy(m => m.title, StringComparer.CurrentCultureIgnoreCase).ThenBy(m => m.year).ToList();
            await ctx.Response.WriteAsJsonAsync(new { movies, tmdb = tmdb.Enabled, pending = tmdb.Pending, error = tmdb.LastError });
        }

        /// <summary>Trailers, samples and featurettes aren't movies of their own.</summary>
        private static bool IsExtra(LibraryEntry e)
        {
            string n = Path.GetFileNameWithoutExtension(e.Name).ToLowerInvariant();
            string? folder = Path.GetFileName(Path.GetDirectoryName(e.Path))?.ToLowerInvariant();
            return n == "sample" || n.EndsWith("-sample") || n.EndsWith(" sample") || n.EndsWith("-trailer") || n.EndsWith(".trailer")
                || folder is "extras" or "featurettes" or "trailers" or "samples" or "behind the scenes" or "deleted scenes";
        }

        /// <summary>GET /poster/{name} : a poster downloaded from TMDB.</summary>
        public async Task PosterAsync(HttpContext ctx, string name, Caller caller)
        {
            if (!Regex.IsMatch(name, "^[0-9a-f]{20}\\.jpg$")) { ctx.Response.StatusCode = 404; return; }
            string file = Path.Combine(TmdbClient.PosterDir, name);
            if (!File.Exists(file)) { ctx.Response.StatusCode = 404; return; }
            ctx.Response.ContentType = "image/jpeg";
            ctx.Response.Headers.CacheControl = "private, max-age=2592000, immutable";
            await ctx.Response.SendFileAsync(file, ctx.RequestAborted);
        }

        // ------------------------------------------------------------ photos by date

        /// <summary>GET /api/photos : every photo, newest first, grouped by month taken.</summary>
        public async Task PhotosAsync(HttpContext ctx, Caller caller)
        {
            var entries = await _s.Library.GetAsync(ctx.RequestAborted);
            var photos = Mine(entries, caller).Where(e => e.Kind == EntryKind.Image)
                .Select(e => (Entry: e, Taken: _s.Library.DateTakenFor(e))).OrderByDescending(p => p.Taken).Take(20000).ToList();
            var months = photos.GroupBy(p => new DateTime(p.Taken.Year, p.Taken.Month, 1)).Select(g => new
            {
                key = g.Key.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                label = g.Key.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
                items = g.Select(p => new
                {
                    name = p.Entry.Name, path = p.Entry.Path, file = $"/file/{DlnaService.EncodeId(p.Entry.Path)}", thumb = $"/thumb/{DlnaService.EncodeId(p.Entry.Path)}",
                    taken = p.Taken.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                }),
            });
            await ctx.Response.WriteAsJsonAsync(new { months, total = photos.Count, complete = _s.Library.PhotoDatesComplete });
        }

        // ------------------------------------------------------------ lyrics

        private static readonly Regex LrcTime = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
        private static readonly Regex LrcOffset = new(@"\[offset:\s*([+-]?\d+)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>GET /api/lyrics?path= : from a .lrc file next to the song, or the song's own tags.</summary>
        public async Task LyricsAsync(HttpContext ctx, Caller caller)
        {
            if (!PathGuard.TryResolve(ctx.Request.Query["path"], caller.Roots, out string full) || !File.Exists(full)) { ctx.Response.StatusCode = 404; return; }
            string? text = null;
            string lrc = Path.ChangeExtension(full, ".lrc");
            if (File.Exists(lrc) && PathGuard.TryResolve(lrc, caller.Roots, out lrc) && new FileInfo(lrc).Length < 512 * 1024)
                text = await ReadTextAsync(lrc);
            if (text == null)
            {
                try
                {
                    using var file = TagLib.File.Create(full, TagLib.ReadStyle.None);
                    text = file.Tag.Lyrics;
                }
                catch { }
            }
            if (string.IsNullOrWhiteSpace(text)) { await ctx.Response.WriteAsJsonAsync(new { found = false }); return; }
            var lines = ParseLrc(text);
            await ctx.Response.WriteAsJsonAsync(lines.Count > 0
                ? new { found = true, synced = true, lines = (object)lines.Select(l => new { t = Math.Round(l.Time, 2), text = l.Text }), text = (string?)null }
                : new { found = true, synced = false, lines = (object)Array.Empty<object>(), text = (string?)text.Replace("\r\n", "\n").Trim() });
        }

        internal static List<(double Time, string Text)> ParseLrc(string text)
        {
            var result = new List<(double, string)>();
            double offset = LrcOffset.Match(text) is { Success: true } o ? int.Parse(o.Groups[1].Value) / 1000.0 : 0;
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                var times = LrcTime.Matches(raw);
                if (times.Count == 0) continue;
                string lyric = LrcTime.Replace(raw, "").Trim();
                lyric = Regex.Replace(lyric, @"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>", ""); // word-by-word timings
                foreach (Match m in times)
                {
                    double t = int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value);
                    if (m.Groups[3].Success) t += int.Parse(m.Groups[3].Value) / Math.Pow(10, m.Groups[3].Value.Length);
                    result.Add((Math.Max(0, t - offset), lyric));
                }
            }
            return result.OrderBy(r => r.Item1).ToList();
        }

        private static async Task<string> ReadTextAsync(string path)
        {
            byte[] bytes = await File.ReadAllBytesAsync(path);
            try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
            catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
        }

        // ------------------------------------------------------------ find subtitles

        /// <summary>POST /api/subtitles/search {path}</summary>
        public async Task SubtitleSearchAsync(HttpContext ctx, Caller caller)
        {
            var body = await ReadJsonAsync(ctx);
            if (body == null || !PathGuard.TryResolve(Str(body.Value, "path"), caller.Roots, out string full) || !File.Exists(full)) { ctx.Response.StatusCode = 404; return; }
            if (!_s.Subtitles.Enabled) { await Error(ctx, 409, "Add a free OpenSubtitles.com API key in the LocalStream app (Settings) to search for subtitles."); return; }
            if (!caller.CanUpload) { await Error(ctx, 403, "Your account can't save files to this PC, so it can't add subtitles."); return; }
            try
            {
                var results = await _s.Subtitles.SearchAsync(full, Str(body.Value, "languages") ?? _config.Current.SubtitleLanguages, ctx.RequestAborted);
                await ctx.Response.WriteAsJsonAsync(new { results = results.Select(r => new { id = r.FileId, name = r.Name, lang = r.Language, downloads = r.Downloads, exact = r.HashMatch }) });
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException or TaskCanceledException)
            {
                await Error(ctx, 502, ex is InvalidOperationException ? ex.Message : "Couldn't reach OpenSubtitles.com.");
            }
        }

        /// <summary>POST /api/subtitles/download {path, id, lang} : saves "Video.lang.srt" next to the video.</summary>
        public async Task SubtitleDownloadAsync(HttpContext ctx, Caller caller)
        {
            var body = await ReadJsonAsync(ctx);
            if (body == null || !PathGuard.TryResolve(Str(body.Value, "path"), caller.Roots, out string full) || !File.Exists(full)) { ctx.Response.StatusCode = 404; return; }
            if (!caller.CanUpload) { ctx.Response.StatusCode = 403; return; }
            int fileId = body.Value.TryGetProperty("id", out var i) && i.TryGetInt32(out int n) ? n : 0;
            if (fileId <= 0 || !_s.Subtitles.Enabled) { ctx.Response.StatusCode = 400; return; }
            try
            {
                string saved = await _s.Subtitles.DownloadAsync(full, fileId, Str(body.Value, "lang") ?? "", ctx.RequestAborted);
                SubtitleMatcher.Forget(Path.GetDirectoryName(full)!);
                _s.Activity.Add(caller.DisplayName, "Added subtitles", Path.GetFileName(full), ctx, Path.GetFileName(saved));
                _log.Write($"Saved subtitles \"{Path.GetFileName(saved)}\".");
                string id = DlnaService.EncodeId(full);
                await ctx.Response.WriteAsJsonAsync(new { ok = true, name = Path.GetFileName(saved), src = $"/subs/{id}?src=file&name={Uri.EscapeDataString(Path.GetFileName(saved))}" });
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
            {
                await Error(ctx, 502, ex is InvalidOperationException ? ex.Message : ex is IOException or UnauthorizedAccessException ? "Couldn't save the subtitle file next to the video." : "Couldn't reach OpenSubtitles.com.");
            }
        }

        // ------------------------------------------------------------ helpers

        private static async Task<JsonElement?> ReadJsonAsync(HttpContext ctx)
        {
            try
            {
                if (ctx.Request.ContentLength > 2 * 1024 * 1024) return null;
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
                return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
            }
            catch { return null; }
        }

        private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static bool? Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

        private static async Task Error(HttpContext ctx, int status, string message)
        {
            ctx.Response.StatusCode = status;
            await ctx.Response.WriteAsJsonAsync(new { error = message });
        }
    }
}
