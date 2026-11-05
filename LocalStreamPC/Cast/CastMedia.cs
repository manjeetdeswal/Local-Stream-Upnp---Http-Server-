using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using LocalStreamPC.Services;

namespace LocalStreamPC.Cast
{
    /// <summary>What a sender told us about the media (from its DIDL-Lite metadata), plus the URL.</summary>
    /// <param name="TitleFromSender">False when the title is just the file name (a title tag is better).</param>
    /// <param name="SubtitleUrl">Subtitles the sender offered (a text/srt res, or Samsung's CaptionInfoEx).</param>
    public sealed record CastMediaInfo(string Url, string Title, string? Artist, string? Album, string? AlbumArtUrl, string? UpnpClass, string? Mime,
                                       bool TitleFromSender = true, string? SubtitleUrl = null)
    {
        private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wav", ".wma", ".alac", ".aiff", ".ape", ".mpga" };
        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif" };

        public bool LooksLikeImage =>
            UpnpClass?.Contains("imageItem", StringComparison.OrdinalIgnoreCase) == true ||
            Mime?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true ||
            ImageExtensions.Contains(Extension);

        /// <summary>Key for "continue where you left off": the file name, so it survives IP/port changes.</summary>
        public string ResumeKey => (FileNameFromUrl(Url) ?? Url).ToLowerInvariant() + "|" + Title.ToLowerInvariant();

        /// <summary>Best guess before playback starts; the built-in player confirms once it sees the tracks.</summary>
        public bool LooksLikeAudio =>
            UpnpClass?.Contains("audioItem", StringComparison.OrdinalIgnoreCase) == true ||
            Mime?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true ||
            AudioExtensions.Contains(Extension);

        public string Extension => Path.GetExtension(FileNameFromUrl(Url) ?? "");

        public static CastMediaInfo Parse(string url, string? didl)
        {
            string? title = null, artist = null, album = null, art = null, cls = null, mime = null, subtitle = null;
            if (!string.IsNullOrWhiteSpace(didl))
            {
                try
                {
                    var doc = XDocument.Parse(didl);
                    string? First(string name) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name && e.Value.Trim().Length > 0)?.Value.Trim();
                    title = First("title");
                    artist = RealName(First("artist")) ?? RealName(First("creator"));
                    album = RealName(First("album"));
                    art = First("albumArtURI");
                    cls = First("class");
                    // protocolInfo = "http-get:*:audio/mpeg:DLNA.ORG_..."
                    string? protocol = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "res")?.Attribute("protocolInfo")?.Value;
                    var parts = protocol?.Split(':');
                    if (parts is { Length: >= 3 } && parts[2].Contains('/')) mime = parts[2];
                    // Subtitles: <res protocolInfo="http-get:*:text/srt:*">, <sec:CaptionInfoEx>, pv:subtitleFileUri.
                    subtitle = doc.Descendants().Where(e => e.Name.LocalName == "res")
                                   .FirstOrDefault(e => e.Attribute("protocolInfo")?.Value is string pi && (pi.Contains("text/srt") || pi.Contains("text/vtt") || pi.Contains("application/x-subrip") || pi.Contains("smi/caption")))?.Value.Trim()
                               ?? First("CaptionInfoEx") ?? First("CaptionInfo")
                               ?? doc.Descendants().Select(e => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "subtitleFileUri")?.Value).FirstOrDefault(v => v != null);
                    bool webUrl = Uri.TryCreate(subtitle, UriKind.Absolute, out var su) && su.Scheme is "http" or "https";
                    if (!webUrl) subtitle = null;
                }
                catch { /* bad metadata: fall back to the URL */ }
            }
            string? fileName = FileNameFromUrl(url);
            string fallbackTitle = fileName == null ? url : Path.GetFileNameWithoutExtension(fileName) is { Length: > 0 } n ? n : fileName;
            return new CastMediaInfo(url, title ?? fallbackTitle, artist, album, art, cls, mime, title != null, subtitle);
        }

        /// <summary>Null for the placeholders phones send when a song has no tags ("Unknown Album", "&lt;unknown&gt;"…).</summary>
        public static string? RealName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string v = value.Trim();
            string bare = v.Trim('<', '>', '[', ']', '(', ')').Trim();
            if (bare.Length == 0) return null;
            // "unknown", "Unknown Album", "Unknown Artist", "<unknown>"
            bool placeholder = bare.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
                               bare.StartsWith("unknown ", StringComparison.OrdinalIgnoreCase) && bare.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 2;
            return placeholder ? null : v;
        }

        /// <summary>
        /// The file name in a media URL. LocalStream servers use /file/{base64 of the path}, so that
        /// is decoded to get the real name (and its extension).
        /// </summary>
        public static string? FileNameFromUrl(string url)
        {
            try
            {
                string? local = PathFromLocalStreamUrl(url);
                if (local != null)
                {
                    int cut = Math.Max(local.LastIndexOf('/'), local.LastIndexOf('\\'));
                    return local[(cut + 1)..];
                }
                string name = Uri.UnescapeDataString(Path.GetFileName(new Uri(url).AbsolutePath));
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            catch { return null; }
        }

        /// <summary>The full path inside a LocalStream /file/ or /stream/ URL (base64 of the path), or null.</summary>
        public static string? PathFromLocalStreamUrl(string url)
        {
            try
            {
                string path = new Uri(url).AbsolutePath;
                foreach (var marker in new[] { "/file/", "/stream/" })
                {
                    int idx = path.IndexOf(marker, StringComparison.Ordinal);
                    if (idx >= 0 && LocalStreamPC.Server.DlnaService.TryDecodeId(path[(idx + marker.Length)..], out string decoded) &&
                        (decoded.Contains('/') || decoded.Contains('\\')) && !decoded.Any(char.IsControl))
                        return decoded;
                }
            }
            catch { }
            return null;
        }
    }

    /// <summary>Title/artist/album and cover shown for music in the player and on the dashboard.</summary>
    /// <param name="UpNext">Title of the queued next item, shown as "Up next".</param>
    public sealed record NowPlayingInfo(string Title, string? Artist, string? Album, byte[]? Artwork, bool IsAudio,
                                        string? UpNext = null, bool CanPrevious = false, bool CanNext = false, bool IsImage = false);

    /// <summary>
    /// Finds the cover for cast music, cheapest source first:
    /// 1. the albumArtURI the sender gave us;
    /// 2. a LocalStream server's /thumb/ for its own /file/ URLs;
    /// 3. the picture embedded in the file's tags, read with a few HTTP range requests
    ///    (usually well under 1 MB) instead of downloading the whole song.
    /// </summary>
    public sealed class ArtworkFetcher
    {
        private const long MaxImageBytes = 8 * 1024 * 1024;
        /// <summary>Covers smaller than this (px) look soft at full size, so a bigger one is looked for.</summary>
        private const int SharpCoverSize = 500;
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
        private readonly AppLog _log;

        public ArtworkFetcher(AppLog log) => _log = log;

        public async Task<(byte[]? Art, string? Artist, string? Album, string? Title)> FindAsync(CastMediaInfo media, CancellationToken ct)
        {
            byte[]? art = null;
            if (IsHttp(media.AlbumArtUrl)) art = await DownloadImageAsync(media.AlbumArtUrl!, ct);

            int fileIdx = media.Url.IndexOf("/file/", StringComparison.Ordinal);
            if (art == null && fileIdx > 0)
                art = await DownloadImageAsync(media.Url[..fileIdx] + "/thumb/" + media.Url[(fileIdx + 6)..], ct);

            // A sharp cover and the artist: done. Otherwise read the file's tags (a few hundred KB):
            // senders often pass a small thumbnail while the file itself holds the full-size picture.
            int artSize = Math.Min(CoverArt.Measure(art).Width, CoverArt.Measure(art).Height);
            if (art != null && artSize >= SharpCoverSize && (media.Artist != null || media.Album != null)) return (art, media.Artist, media.Album, null);
            var fromTags = await FromEmbeddedTagsAsync(media, ct);
            var tagSize = CoverArt.Measure(fromTags.Art);
            if (fromTags.Art != null && Math.Min(tagSize.Width, tagSize.Height) > artSize)
            {
                if (art != null) _log.Write($"Cast: using the {tagSize.Width}×{tagSize.Height} cover from the file instead of the sender's {artSize} px one.");
                art = fromTags.Art;
            }
            return (art, fromTags.Artist, fromTags.Album, fromTags.Title);
        }

        /// <summary>Reads the cover (and artist/album if missing) from the file's own tags over HTTP.</summary>
        public async Task<(byte[]? Art, string? Artist, string? Album, string? Title)> FromEmbeddedTagsAsync(CastMediaInfo media, CancellationToken ct)
        {
            if (!IsHttp(media.Url)) return (null, media.Artist, media.Album, null);
            try
            {
                return await Task.Run<(byte[]?, string?, string?, string?)>(() =>
                {
                    using var stream = HttpRangeStream.TryOpen(Http, new Uri(media.Url), ct);
                    if (stream == null) return (null, media.Artist, media.Album, null); // server can't do ranges

                    string ext = media.Extension.TrimStart('.').ToLowerInvariant();
                    if (ext.Length == 0) ext = MimeToExtension(media.Mime ?? stream.ContentType);
                    using var file = TagLib.File.Create(new StreamAbstraction(media.Title + "." + ext, stream), "taglib/" + ext, TagLib.ReadStyle.None);
                    var tag = file.Tag;
                    byte[]? art = tag.Pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover)?.Data.Data
                                  ?? tag.Pictures.FirstOrDefault()?.Data.Data;
                    string? artist = media.Artist ?? CastMediaInfo.RealName(tag.FirstPerformer) ?? CastMediaInfo.RealName(tag.FirstAlbumArtist);
                    string? album = media.Album ?? CastMediaInfo.RealName(tag.Album);
                    _log.Write($"Cast: read tags of \"{media.Title}\" ({stream.BytesDownloaded / 1024} KB of {stream.Length / 1024} KB downloaded).");
                    return (art, artist, album, string.IsNullOrWhiteSpace(tag.Title) ? null : tag.Title.Trim());
                }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Write($"Cast: no cover found in \"{media.Title}\" ({ex.GetType().Name}).");
                return (null, media.Artist, media.Album, null);
            }
        }

        private static async Task<byte[]?> DownloadImageAsync(string url, CancellationToken ct)
        {
            try
            {
                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode) return null;
                var type = response.Content.Headers.ContentType?.MediaType ?? "";
                if (type.Length > 0 && !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
                if (response.Content.Headers.ContentLength > MaxImageBytes) return null;
                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                return bytes.Length is > 0 and <= (int)MaxImageBytes ? bytes : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
        }

        private static bool IsHttp(string? url)
            => Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

        private static string MimeToExtension(string? mime) => mime?.ToLowerInvariant() switch
        {
            "audio/flac" or "audio/x-flac" => "flac",
            "audio/mp4" or "audio/x-m4a" or "audio/aac" => "m4a",
            "audio/ogg" => "ogg",
            "audio/opus" => "opus",
            "audio/wav" or "audio/x-wav" => "wav",
            "audio/x-ms-wma" => "wma",
            _ => "mp3",
        };

        /// <summary>Lets TagLib read from our HTTP stream.</summary>
        private sealed class StreamAbstraction : TagLib.File.IFileAbstraction
        {
            private readonly Stream _stream;
            public StreamAbstraction(string name, Stream stream) { Name = name; _stream = stream; }
            public string Name { get; }
            public Stream ReadStream => _stream;
            public Stream WriteStream => throw new NotSupportedException();
            public void CloseStream(Stream stream) { }
        }
    }

    /// <summary>
    /// A read-only, seekable stream over an HTTP resource using Range requests. Reads in 128 KB
    /// blocks that are kept, with a hard limit on how much is downloaded in total.
    /// </summary>
    internal sealed class HttpRangeStream : Stream
    {
        private const int BlockSize = 128 * 1024;
        private const long MaxDownload = 12 * 1024 * 1024;

        private readonly HttpClient _http;
        private readonly Uri _uri;
        private readonly CancellationToken _ct;
        private readonly Dictionary<long, byte[]> _blocks = new();
        private long _position, _downloaded;

        private HttpRangeStream(HttpClient http, Uri uri, long length, CancellationToken ct)
        {
            _http = http; _uri = uri; Length = length; _ct = ct;
        }

        /// <summary>Null if the server doesn't support byte ranges or doesn't report the size.</summary>
        public static HttpRangeStream? TryOpen(HttpClient http, Uri uri, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using var response = http.Send(request, HttpCompletionOption.ResponseHeadersRead, ct);
            long? total = response.Content.Headers.ContentRange?.Length;
            if (response.StatusCode != System.Net.HttpStatusCode.PartialContent || total is not > 0) return null;
            return new HttpRangeStream(http, uri, total.Value, ct) { ContentType = response.Content.Headers.ContentType?.MediaType };
        }

        /// <summary>The server's Content-Type, used when the URL has no file extension.</summary>
        public string? ContentType { get; private init; }

        public override long Length { get; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Position { get => _position; set => _position = Math.Clamp(value, 0, Length); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= Length) return 0;
            count = (int)Math.Min(count, Length - _position);
            int total = 0;
            while (total < count)
            {
                long blockIndex = _position / BlockSize;
                var block = GetBlock(blockIndex);
                int inBlock = (int)(_position - blockIndex * BlockSize);
                int n = Math.Min(count - total, block.Length - inBlock);
                if (n <= 0) break;
                Buffer.BlockCopy(block, inBlock, buffer, offset + total, n);
                total += n;
                _position += n;
            }
            return total;
        }

        private byte[] GetBlock(long index)
        {
            if (_blocks.TryGetValue(index, out var cached)) return cached;
            if (_downloaded > MaxDownload) throw new IOException("Read limit reached");

            long from = index * BlockSize;
            long to = Math.Min(Length - 1, from + BlockSize - 1);
            using var request = new HttpRequestMessage(HttpMethod.Get, _uri);
            request.Headers.Range = new RangeHeaderValue(from, to);
            using var response = _http.Send(request, _ct);
            if (response.StatusCode != System.Net.HttpStatusCode.PartialContent) throw new IOException("Range request failed");
            using var body = response.Content.ReadAsStream(_ct);
            var data = new byte[to - from + 1];
            int read = 0;
            while (read < data.Length)
            {
                int n = body.Read(data, read, data.Length - read);
                if (n == 0) break;
                read += n;
            }
            if (read < data.Length) Array.Resize(ref data, read);
            _downloaded += data.Length;
            _blocks[index] = data;
            return data;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => Length + offset,
            };
            return _position;
        }

        public long BytesDownloaded => _downloaded;
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
