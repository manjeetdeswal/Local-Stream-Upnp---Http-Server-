using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LocalStreamPC.Services;
using Microsoft.AspNetCore.Http;

namespace LocalStreamPC.Server
{
    /// <summary>Everything the server shares between restarts of the HTTP server.</summary>
    public sealed class ServerServices
    {
        public required StreamTracker Streams { get; init; }
        public required Transcoder Transcoder { get; init; }
        public required HlsService Hls { get; init; }
        public required MediaProbe Probe { get; init; }
        public required LibraryIndex Library { get; init; }
        public required WatchHistory History { get; init; }
        public required TvCastService Tv { get; init; }
        public required SessionStore Sessions { get; init; }
        public required ActivityLog Activity { get; init; }
        public required UserLibrary Marks { get; init; }
        public required TmdbClient Tmdb { get; init; }
        public required OpenSubtitlesClient Subtitles { get; init; }
        public required CoverFinder Covers { get; init; }

        /// <summary>Connects the pieces that report to the history.</summary>
        public void WireHistory()
        {
            Streams.Finished += s =>
            {
                string verb = LibraryIndex.KindOf(s.FileName) switch
                {
                    EntryKind.Video => "Watched", EntryKind.Audio => "Listened to", EntryKind.Image => "Viewed", _ => "Opened",
                };
                var minutes = DateTime.UtcNow - s.Started - TimeSpan.FromSeconds(25);
                string detail = $"{(minutes.TotalMinutes >= 1 ? $"{(int)minutes.TotalMinutes} min · " : "")}{MediaTypes.FormatSize(s.Bytes)}{(s.Converting ? " · converted" : "")}";
                Activity.Add(new ActivityEntry(s.Started, s.Who, s.Device, verb, s.FileName, detail));
            };
        }
    }

    /// <summary>
    /// The web player's endpoints: media info, live conversion, subtitles, "continue watching",
    /// search across all folders, the music section and "Play on TV".
    /// </summary>
    public sealed class PlaybackApi
    {
        private static readonly TimeSpan TvLinkLifetime = TimeSpan.FromHours(12);
        private readonly ConfigService _config;
        private readonly AppLog _log;
        private readonly AuthService _auth;
        private readonly ServerServices _s;

        public PlaybackApi(ConfigService config, AppLog log, AuthService auth, ServerServices services)
        {
            _config = config;
            _log = log;
            _auth = auth;
            _s = services;
        }

        // ------------------------------------------------------------ shapes sent to the browser

        private static object Item(string path, EntryKind kind, long size, string? extra = null)
        {
            string id = DlnaService.EncodeId(path);
            bool isFolder = kind == EntryKind.Folder;
            return new
            {
                name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
                path,
                type = isFolder ? "folder" : "file",
                kind = kind.ToString().ToLowerInvariant(),
                size = isFolder ? (long?)null : size,
                file = isFolder ? null : $"/file/{id}",
                thumb = !isFolder && kind != EntryKind.Other ? $"/thumb/{id}" : null,
                folder = Path.GetDirectoryName(path),
                extra,
            };
        }

        private static bool TryFile(Caller caller, string? path, out string full)
            => PathGuard.TryResolve(path, caller.Roots, out full) && File.Exists(full);

        // ------------------------------------------------------------ media info + resume

        /// <summary>GET /api/media?path= : how to play a file in the browser (direct or converted), tracks, resume point.</summary>
        public async Task MediaAsync(HttpContext ctx, Caller caller)
        {
            if (!TryFile(caller, ctx.Request.Query["path"], out string full)) { ctx.Response.StatusCode = 404; return; }
            var kind = LibraryIndex.KindOf(full);
            string id = DlnaService.EncodeId(full);
            var info = kind is EntryKind.Video or EntryKind.Audio ? await _s.Probe.ProbeAsync(full, ctx.RequestAborted) : null;

            bool direct = true;
            string? reason = null;
            if (info != null) (direct, reason) = MediaProbe.BrowserCanPlay(info, Path.GetExtension(full));

            var subtitles = new List<object>();
            if (kind == EntryKind.Video)
            {
                var externals = SubtitleMatcher.FindAll(full);
                for (int i = 0; i < externals.Count; i++)
                {
                    string ext = externals[i];
                    string? lang = SubtitleMatcher.LanguageOf(full, ext);
                    string label = lang != null ? TrackLabel(null, lang, lang) + " (file)" : Path.GetFileNameWithoutExtension(ext);
                    subtitles.Add(new { label, lang, src = i == 0 ? $"/subs/{id}?src=ext" : $"/subs/{id}?src=file&name={Uri.EscapeDataString(Path.GetFileName(ext))}" });
                }
                if (info != null)
                    foreach (var t in info.Subtitles.Where(t => t.IsText))
                        subtitles.Add(new { label = TrackLabel(t.Title, t.Language, $"Subtitles {t.Index + 1}"), lang = t.Language, src = $"/subs/{id}?src={t.Index}" });
            }

            var resume = _s.History.Get(caller.HistoryKey, full);

            // Chapters named like an intro or the end credits ("Skip intro", next episode a little early).
            var intro = info?.Chapters.FirstOrDefault(c => c.Start < 900 && System.Text.RegularExpressions.Regex.IsMatch(c.Title, @"\b(intro|opening|op|recap|title sequence)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            var credits = info?.Chapters.LastOrDefault(c => c.Start > (info.Duration * 0.6) && System.Text.RegularExpressions.Regex.IsMatch(c.Title, @"\b(credits?|ending|ed|outro|end titles)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            object? next = kind == EntryKind.Video ? await NextEpisodeAsync(full, caller, ctx.RequestAborted) : null;
            await ctx.Response.WriteAsJsonAsync(new
            {
                name = Path.GetFileName(full),
                path = full,
                kind = kind.ToString().ToLowerInvariant(),
                file = $"/file/{id}",
                stream = $"/stream/{id}",
                hls = $"/hls/{UrlId(full)}/index.m3u8",
                thumb = $"/thumb/{id}",
                duration = info?.Duration ?? 0,
                direct,
                reason,
                canConvert = FfmpegTools.CanConvert,
                width = info?.Width ?? 0,
                height = info?.Height ?? 0,
                audio = info?.Audio.Select(a => new { index = a.Index, label = TrackLabel(a.Title, a.Language, $"Track {a.Index + 1}") + (a.Channels > 2 ? $" ({a.Channels}ch)" : ""), codec = a.Codec }) ?? Enumerable.Empty<object>(),
                subtitles,
                resume = resume?.Position,
                watched = _s.Marks.IsWatched(caller.HistoryKey, full),
                favorite = _s.Marks.Get(caller.HistoryKey).Favorites.Contains(full, StringComparer.OrdinalIgnoreCase),
                canFindSubtitles = kind == EntryKind.Video && caller.CanUpload && _s.Subtitles.Enabled,
                hasLyrics = kind == EntryKind.Audio && (File.Exists(Path.ChangeExtension(full, ".lrc")) || HasEmbeddedLyrics(full)),
                intro = intro is { Title.Length: > 0 } introCh ? new { start = introCh.Start, end = introCh.End } : null,
                credits = credits is { Title.Length: > 0 } creditsCh ? creditsCh.Start : (double?)null,
                next,
            });
        }

        /// <summary>The episode after this one in the same show (next number, or the first of the next season).</summary>
        private async Task<object?> NextEpisodeAsync(string full, Caller caller, System.Threading.CancellationToken ct)
        {
            var me = VideoNameParser.Parse(full);
            if (!me.IsEpisode) return null;
            string show = VideoNameParser.Key(me.Title);
            var entries = await _s.Library.GetAsync(ct);
            var next = entries.Where(e => e.Kind == EntryKind.Video && caller.Roots.Contains(e.Root) && !e.Path.Equals(full, StringComparison.OrdinalIgnoreCase))
                .Select(e => (e.Path, N: VideoNameParser.Parse(e.Path)))
                .Where(x => x.N.IsEpisode && VideoNameParser.Key(x.N.Title) == show && (x.N.Season > me.Season || (x.N.Season == me.Season && x.N.Episode > me.Episode)))
                .OrderBy(x => x.N.Season).ThenBy(x => x.N.Episode).FirstOrDefault();
            return next.Path == null ? null : new { path = next.Path, title = $"S{next.N.Season} E{next.N.Episode}", show = me.Title };
        }

        private static bool HasEmbeddedLyrics(string path)
        {
            try { using var f = TagLib.File.Create(path, TagLib.ReadStyle.None); return !string.IsNullOrWhiteSpace(f.Tag.Lyrics); }
            catch { return false; }
        }

        /// <summary>A file id without '/' (URL-safe base64), for routes with more path after the id.</summary>
        public static string UrlId(string path) => DlnaService.EncodeId(path).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static string TrackLabel(string? title, string? lang, string fallback)
        {
            string? language = null;
            if (!string.IsNullOrEmpty(lang))
            {
                try { language = CultureInfo.GetCultureInfo(lang).EnglishName; } catch { language = lang.ToUpperInvariant(); }
                if (language.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase)) language = lang.ToUpperInvariant();
            }
            if (!string.IsNullOrWhiteSpace(title) && language != null && !title.Contains(language, StringComparison.OrdinalIgnoreCase)) return $"{language} - {title}";
            return title ?? language ?? fallback;
        }

        /// <summary>POST /api/progress {path, position, duration} : remembered for "Continue watching".</summary>
        public async Task ProgressAsync(HttpContext ctx, Caller caller)
        {
            var body = await ReadJsonAsync(ctx);
            if (body == null || !TryFile(caller, Str(body.Value, "path"), out string full)) { ctx.Response.StatusCode = 400; return; }
            double position = Num(body.Value, "position"), duration = Num(body.Value, "duration");
            if (Str(body.Value, "action") == "remove") _s.History.Remove(caller.HistoryKey, full);
            else if (position >= 0 && duration >= 0)
            {
                _s.History.Report(caller.HistoryKey, full, position, duration);
                if (WatchHistory.IsFinished(position, duration) && duration > 60) _s.Marks.SetWatched(caller.HistoryKey, full, true);
            }
            ctx.Response.StatusCode = 204;
        }

        // ------------------------------------------------------------ conversion + subtitles

        /// <summary>GET /stream/{id}?start=&amp;audio= : live-converted MP4.</summary>
        public async Task StreamAsync(HttpContext ctx, string id, Caller caller)
        {
            if (!DlnaService.TryDecodeId(id, out string raw) || !TryFile(caller, raw, out string full)) { ctx.Response.StatusCode = 404; return; }
            var kind = LibraryIndex.KindOf(full);
            var info = kind is EntryKind.Video or EntryKind.Audio ? await _s.Probe.ProbeAsync(full, ctx.RequestAborted) : null;
            if (info == null || (!info.HasVideo && info.Audio.Count == 0))
            {
                ctx.Response.StatusCode = 415;
                await ctx.Response.WriteAsync("Not a video or audio file ffmpeg can read.");
                return;
            }
            double.TryParse(ctx.Request.Query["start"], NumberStyles.Float, CultureInfo.InvariantCulture, out double start);
            int.TryParse(ctx.Request.Query["audio"], out int audio);
            int.TryParse(ctx.Request.Query["q"], out int quality);
            if (HttpMethods.IsHead(ctx.Request.Method))
            {
                ctx.Response.ContentType = info.HasVideo ? "video/mp4" : "audio/mp4";
                return;
            }
            if (_s.Streams.IsBlocked(ctx, Path.GetFileName(full))) { ctx.Response.StatusCode = 403; return; }
            using var tracking = _s.Streams.Begin(ctx, Path.GetFileName(full), converting: true, caller.DisplayName);
            await _s.Transcoder.StreamAsync(ctx, full, info, Math.Clamp(start, 0, Math.Max(0, info.Duration - 1)), audio, quality);
        }

        /// <summary>
        /// GET /hls/{id}/index.m3u8 and /hls/{id}/{n}.ts (?audio=&amp;q=&amp;k=): converted video in
        /// 4-second pieces, for iPhone/iPad/Safari and Chromecast.
        /// </summary>
        public async Task HlsAsync(HttpContext ctx, string id, string file, Caller caller)
        {
            if (!DlnaService.TryDecodeId(id, out string raw) || !TryFile(caller, raw, out string full)) { ctx.Response.StatusCode = 404; return; }
            var kind = LibraryIndex.KindOf(full);
            var info = kind is EntryKind.Video or EntryKind.Audio ? await _s.Probe.ProbeAsync(full, ctx.RequestAborted) : null;
            if (info == null || info.Duration <= 0 || (!info.HasVideo && info.Audio.Count == 0)) { ctx.Response.StatusCode = 415; return; }
            int.TryParse(ctx.Request.Query["audio"], out int audio);
            int.TryParse(ctx.Request.Query["q"], out int quality);
            if (file == "index.m3u8")
            {
                // Pieces inherit the same options (and the TV link key).
                var q = new List<string>();
                if (audio > 0) q.Add("audio=" + audio);
                if (quality > 0) q.Add("q=" + quality);
                if (ctx.Request.Query["k"].ToString() is { Length: > 0 } k) q.Add("k=" + Uri.EscapeDataString(k));
                ctx.Response.ContentType = "application/vnd.apple.mpegurl";
                ctx.Response.Headers.CacheControl = "no-cache";
                await ctx.Response.WriteAsync(HlsService.Playlist(info.Duration, q.Count > 0 ? "?" + string.Join("&", q) : ""));
                return;
            }
            if (!file.EndsWith(".ts") || !int.TryParse(file[..^3], out int segment)) { ctx.Response.StatusCode = 404; return; }
            if (_s.Streams.IsBlocked(ctx, Path.GetFileName(full))) { ctx.Response.StatusCode = 403; return; }
            using var tracking = _s.Streams.Begin(ctx, Path.GetFileName(full), converting: true, caller.DisplayName);
            await _s.Hls.ServeSegmentAsync(ctx, full, info, segment, audio, quality);
        }

        /// <summary>GET /subs/{id}?src=ext|N&amp;shift=s : subtitles as WebVTT for the browser.</summary>
        public async Task SubtitlesAsync(HttpContext ctx, string id, Caller caller)
        {
            if (!DlnaService.TryDecodeId(id, out string raw) || !TryFile(caller, raw, out string full)) { ctx.Response.StatusCode = 404; return; }
            double.TryParse(ctx.Request.Query["shift"], NumberStyles.Float, CultureInfo.InvariantCulture, out double shift);
            string src = ctx.Request.Query["src"].ToString();
            string? vtt = null;
            if (src == "ext")
            {
                string? external = SubtitleMatcher.FindBestMatch(full);
                if (external != null && PathGuard.TryResolve(external, caller.Roots, out external)) vtt = await Transcoder.ExternalToVttAsync(external, shift);
            }
            else if (src == "file")
            {
                // Another subtitle file next to the video (by name only: no folders).
                string name = ctx.Request.Query["name"].ToString();
                string candidate = Path.Combine(Path.GetDirectoryName(full)!, Path.GetFileName(name));
                if (name.Length > 0 && name == Path.GetFileName(name) && MediaTypes.Subtitle.Contains(Path.GetExtension(name))
                    && PathGuard.TryResolve(candidate, caller.Roots, out string sub) && File.Exists(sub))
                    vtt = await Transcoder.ExternalToVttAsync(sub, shift);
            }
            else if (int.TryParse(src, out int track) && track >= 0)
            {
                vtt = await _s.Transcoder.EmbeddedToVttAsync(full, track, shift, ctx.RequestAborted);
            }
            if (vtt == null) { ctx.Response.StatusCode = 404; return; }
            ctx.Response.ContentType = "text/vtt; charset=utf-8";
            ctx.Response.Headers.CacheControl = "private, max-age=600";
            await ctx.Response.WriteAsync(vtt);
        }

        // ------------------------------------------------------------ home, search, music

        /// <summary>GET /api/home : "Continue watching" and "Recently added" for the start page.</summary>
        public async Task HomeAsync(HttpContext ctx, Caller caller)
        {
            var entries = await _s.Library.GetAsync(ctx.RequestAborted);
            var mine = entries.Where(e => caller.Roots.Contains(e.Root));
            var continueItems = _s.History.Recent(caller.HistoryKey)
                .Where(h => PathGuard.TryResolve(h.Path, caller.Roots, out _) && File.Exists(h.Path))
                .Take(20)
                .Select(h => new { item = Item(h.Path, LibraryIndex.KindOf(h.Path), 0), position = h.Point.Position, duration = h.Point.Duration })
                .ToList();
            var recent = LibraryIndex.RecentlyAdded(mine, 24).Select(e => Item(e.Path, e.Kind, e.Size)).ToList();
            var videoNames = mine.Where(e => e.Kind == EntryKind.Video).Select(e => (e.Path, Name: VideoNameParser.Parse(e.Path))).ToList();
            var episodes = videoNames.Where(v => v.Name.IsEpisode).Select(v => v.Path).ToList();
            await ctx.Response.WriteAsJsonAsync(new
            {
                @continue = continueItems,
                recent,
                hasMusic = mine.Any(e => e.Kind == EntryKind.Audio),
                hasPhotos = mine.Any(e => e.Kind == EntryKind.Image),
                hasMovies = videoNames.Any(v => !v.Name.IsEpisode),
                hasShows = episodes.Any(p => !VideoNameParser.IsWebSeries(p)),
                hasWebSeries = episodes.Any(VideoNameParser.IsWebSeries),
            });
        }

        /// <summary>GET /api/search?q= : files and folders in every shared folder this person can see.</summary>
        public async Task SearchAsync(HttpContext ctx, Caller caller)
        {
            string q = ctx.Request.Query["q"].ToString().Trim();
            if (q.Length < 2) { await ctx.Response.WriteAsJsonAsync(new { items = Array.Empty<object>(), truncated = false }); return; }
            var entries = await _s.Library.GetAsync(ctx.RequestAborted);
            var results = LibraryIndex.Search(entries.Where(e => caller.Roots.Contains(e.Root)), q, 150, out bool truncated);
            await ctx.Response.WriteAsJsonAsync(new { items = results.Select(e => Item(e.Path, e.Kind, e.Size)), truncated });
        }

        /// <summary>
        /// GET /api/music : artists. ?artist= : that artist's albums. ?artist=&amp;album= : the tracks.
        /// ?view=albums : every album.
        /// </summary>
        public async Task MusicAsync(HttpContext ctx, Caller caller)
        {
            var entries = await _s.Library.GetAsync(ctx.RequestAborted);
            // The same song copied into several folders (backups, app build folders) is listed once:
            // same title, artist and exact file size. The copy with the shortest path wins.
            var tracks = entries.Where(e => e.Kind == EntryKind.Audio && caller.Roots.Contains(e.Root))
                                .Select(e => (Entry: e, Tags: _s.Library.TagsFor(e)))
                                .GroupBy(t => (t.Tags.Title.ToLowerInvariant(), t.Tags.Artist.ToLowerInvariant(), t.Entry.Size))
                                .Select(g => g.OrderBy(t => t.Entry.Path.Length).First())
                                .ToList();
            string? artist = ctx.Request.Query["artist"], album = ctx.Request.Query["album"], view = ctx.Request.Query["view"];
            static object Cover(LibraryEntry e) => $"/thumb/{DlnaService.EncodeId(e.Path)}";

            if (!string.IsNullOrEmpty(album))
            {
                var list = tracks.Where(t => t.Tags.Album.Equals(album, StringComparison.CurrentCultureIgnoreCase)
                                             && (string.IsNullOrEmpty(artist) || t.Tags.Artist.Equals(artist, StringComparison.CurrentCultureIgnoreCase)))
                                 .OrderBy(t => t.Tags.Disc).ThenBy(t => t.Tags.Track == 0 ? uint.MaxValue : t.Tags.Track).ThenBy(t => t.Entry.Name, StringComparer.CurrentCultureIgnoreCase)
                                 .Select(t => new { title = t.Tags.Title, artist = t.Tags.Artist, album = t.Tags.Album, track = t.Tags.Track, item = Item(t.Entry.Path, t.Entry.Kind, t.Entry.Size) });
                await ctx.Response.WriteAsJsonAsync(new { level = "tracks", artist, album, tracks = list, complete = _s.Library.MusicTagsComplete });
                return;
            }

            if (view == "songs")
            {
                // Every song, tagged or not, A-Z (sorted by the cleaned title).
                var plays = _s.Marks.PlayCounts(caller.HistoryKey);
                var all = tracks.OrderBy(t => t.Tags.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.Tags.Artist, StringComparer.CurrentCultureIgnoreCase)
                    .Select(t => new
                    {
                        title = t.Tags.Title, artist = t.Tags.Artist, album = t.Tags.Album, track = t.Tags.Track, item = Item(t.Entry.Path, t.Entry.Kind, t.Entry.Size),
                        added = t.Entry.Added, plays = plays.TryGetValue(t.Entry.Path, out int n) ? n : 0,
                    });
                await ctx.Response.WriteAsJsonAsync(new { level = "songs", tracks = all, complete = _s.Library.MusicTagsComplete });
                return;
            }

            if (!string.IsNullOrEmpty(artist) || view == "albums")
            {
                var albums = tracks.Where(t => string.IsNullOrEmpty(artist) || t.Tags.Artist.Equals(artist, StringComparison.CurrentCultureIgnoreCase))
                    .GroupBy(t => (t.Tags.Artist, t.Tags.Album))
                    .Select(g => new { name = g.Key.Album, artist = g.Key.Artist, year = g.Max(t => t.Tags.Year), tracks = g.Count(), cover = Cover(g.OrderBy(t => t.Tags.Track).First().Entry) })
                    .OrderBy(a => a.artist, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(a => a.year).ThenBy(a => a.name, StringComparer.CurrentCultureIgnoreCase);
                await ctx.Response.WriteAsJsonAsync(new { level = "albums", artist, albums, complete = _s.Library.MusicTagsComplete });
                return;
            }

            var artists = tracks.GroupBy(t => t.Tags.Artist, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => new { name = g.First().Tags.Artist, albums = g.Select(t => t.Tags.Album).Distinct(StringComparer.CurrentCultureIgnoreCase).Count(), tracks = g.Count(), cover = Cover(g.First().Entry) })
                .OrderBy(a => a.name == "Unknown artist").ThenBy(a => a.name, StringComparer.CurrentCultureIgnoreCase);
            await ctx.Response.WriteAsJsonAsync(new { level = "artists", artists, complete = _s.Library.MusicTagsComplete });
        }

        // ------------------------------------------------------------ Play on TV

        /// <summary>GET /api/tv/devices?refresh=1</summary>
        public async Task TvDevicesAsync(HttpContext ctx, Caller caller)
        {
            if (caller.IsGuest) { ctx.Response.StatusCode = 403; return; } // share links don't control the TVs
            bool refresh = ctx.Request.Query["refresh"] == "1";
            // Always look again (at most every 20 s): TVs come and go.
            var devices = await _s.Tv.Discovery.ScanAsync(force: refresh, ctx.RequestAborted);
            await ctx.Response.WriteAsJsonAsync(new
            {
                devices = devices.Select(d => new { id = d.Id, name = d.Name, model = d.Description, thisPc = d.IsThisPc, cast = d.IsCast }),
                canConvert = FfmpegTools.CanConvert,
            });
        }

        /// <summary>POST /api/tv/play {device, path, convert, start}</summary>
        public async Task TvPlayAsync(HttpContext ctx, Caller caller)
        {
            if (caller.IsGuest) { ctx.Response.StatusCode = 403; return; } // share links don't control the TVs
            var body = await ReadJsonAsync(ctx);
            if (body == null) { ctx.Response.StatusCode = 400; return; }
            string deviceId = Str(body.Value, "device") ?? "";
            if (!TryFile(caller, Str(body.Value, "path"), out string full)) { ctx.Response.StatusCode = 404; return; }
            bool convert = body.Value.TryGetProperty("convert", out var c) && c.ValueKind == JsonValueKind.True;
            double start = Num(body.Value, "start");

            var device = _s.Tv.Discovery.Find(deviceId);
            if (device == null) { await _s.Tv.Discovery.ScanAsync(false, ctx.RequestAborted); device = _s.Tv.Discovery.Find(deviceId); }
            if (device == null) { await Error(ctx, 404, "That TV isn't on the network any more. Refresh the list."); return; }

            var kind = LibraryIndex.KindOf(full);
            var info = kind is EntryKind.Video or EntryKind.Audio ? await _s.Probe.ProbeAsync(full, ctx.RequestAborted) : null;
            if (convert && (!FfmpegTools.CanConvert || info == null)) convert = false;

            string host = TvCastService.LocalAddressFor(device.Ip);
            string baseUrl = $"http://{host}:{_config.Current.Port}";
            string id = DlnaService.EncodeId(full);
            string key = _auth.SignMediaId(id, TvLinkLifetime);
            string? subtitle = kind == EntryKind.Video ? SubtitleMatcher.FindBestMatch(full) : null;
            string? subtitleUrl = subtitle != null && PathGuard.TryResolve(subtitle, caller.Roots, out _)
                ? $"{baseUrl}/file/{DlnaService.EncodeId(subtitle)}?k={_auth.SignMediaId(DlnaService.EncodeId(subtitle), TvLinkLifetime)}" : null;
            string title = Path.GetFileNameWithoutExtension(full);
            string upnpClass = kind switch
            {
                EntryKind.Video => "object.item.videoItem",
                EntryKind.Audio => "object.item.audioItem.musicTrack",
                EntryKind.Image => "object.item.imageItem.photo",
                _ => "object.item",
            };
            long size = new FileInfo(full).Length;
            string thumb = $"{baseUrl}/thumb/{id}?k={key}";

            int quality = (int)Num(body.Value, "quality");
            string q = quality is 480 or 720 or 1080 ? "&q=" + quality : "";
            if (quality is 480 or 720 or 1080 && info != null && info.HasVideo && FfmpegTools.CanConvert) convert = true; // a smaller size needs conversion

            TvMedia Converted(double at) => new($"{baseUrl}/stream/{id}?k={key}{q}{(at > 0.5 ? "&start=" + at.ToString("0.#", CultureInfo.InvariantCulture) : "")}",
                title, info!.HasVideo ? "video/mp4" : "audio/mp4", upnpClass, 0, Math.Max(0, (info?.Duration ?? 0) - at), thumb, subtitleUrl, true);

            TvMedia media;
            Func<double, TvMedia>? reseek = convert ? Converted : null;
            if (device.IsCast)
            {
                // Chromecast: converted video goes as HLS (it seeks by itself), subtitles as WebVTT.
                string? vtt = subtitle != null && subtitleUrl != null ? $"{baseUrl}/subs/{id}?src=ext&k={key}" : null;
                if (convert)
                {
                    string urlId = UrlId(full);
                    string hlsKey = _auth.SignMediaId(urlId, TvLinkLifetime);
                    media = new TvMedia($"{baseUrl}/hls/{urlId}/index.m3u8?k={hlsKey}{q}", title, "application/x-mpegurl", upnpClass, 0, info?.Duration ?? 0, thumb, vtt, true);
                }
                else
                    media = new TvMedia($"{baseUrl}/file/{id}?k={key}", title, MediaTypes.GetMimeType(Path.GetExtension(full)), upnpClass, size, info?.Duration ?? 0, thumb, vtt, false);
                reseek = null;
            }
            else
                media = convert ? Converted(0)
                    : new TvMedia($"{baseUrl}/file/{id}?k={key}", title, MediaTypes.GetMimeType(Path.GetExtension(full)), upnpClass, size, info?.Duration ?? 0, thumb, subtitleUrl, false);
            try
            {
                await _s.Tv.PlayAsync(device, media, caller.UserName, start, reseek);
                _s.Activity.Add(caller.DisplayName, $"Played on {device.Name}", Path.GetFileName(full), ctx, convert ? "converted" : null);
            }
            catch (Exception ex)
            {
                _log.Write($"Play on {device.Name} failed: {ex.Message}");
                await Error(ctx, 502, $"{device.Name} didn't accept the file: {ex.Message}" + (convert ? "" : " Try \"Convert while playing\"."));
                return;
            }
            await ctx.Response.WriteAsJsonAsync(new { ok = true, status = _s.Tv.StatusOf(device.Id) });
        }

        /// <summary>POST /api/tv/control {device, action, value}</summary>
        public async Task TvControlAsync(HttpContext ctx, Caller caller)
        {
            if (caller.IsGuest) { ctx.Response.StatusCode = 403; return; } // share links don't control the TVs
            var body = await ReadJsonAsync(ctx);
            if (body == null) { ctx.Response.StatusCode = 400; return; }
            try
            {
                await _s.Tv.ControlAsync(Str(body.Value, "device") ?? "", Str(body.Value, "action") ?? "", Num(body.Value, "value"));
            }
            catch (Exception ex)
            {
                await Error(ctx, 409, ex.Message);
                return;
            }
            await ctx.Response.WriteAsJsonAsync(new { ok = true, status = _s.Tv.StatusOf(Str(body.Value, "device") ?? "") });
        }

        /// <summary>GET /api/tv/status?device=</summary>
        public Task TvStatusAsync(HttpContext ctx, Caller caller)
        {
            string device = ctx.Request.Query["device"].ToString();
            return ctx.Response.WriteAsJsonAsync(new
            {
                status = device.Length > 0 ? _s.Tv.StatusOf(device) : null,
                all = _s.Tv.Statuses,
            });
        }

        // ------------------------------------------------------------ helpers

        private static async Task<JsonElement?> ReadJsonAsync(HttpContext ctx)
        {
            try
            {
                if (ctx.Request.ContentLength > 64 * 1024) return null;
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
                return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
            }
            catch { return null; }
        }

        private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static double Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

        private static async Task Error(HttpContext ctx, int status, string message)
        {
            ctx.Response.StatusCode = status;
            await ctx.Response.WriteAsJsonAsync(new { error = message });
        }
    }
}
