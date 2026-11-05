using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LocalStreamPC.Services;

namespace LocalStreamPC.Server
{
    public sealed record AudioTrackInfo(int Index, string Codec, string? Language, string? Title, int Channels, bool IsDefault);
    public sealed record SubtitleTrackInfo(int Index, string Codec, string? Language, string? Title, bool IsText);

    /// <summary>What ffprobe found in a media file.</summary>
    public sealed record MediaInfo(
        double Duration, string Container,
        string? VideoCodec, int Width, int Height, string? PixelFormat,
        IReadOnlyList<AudioTrackInfo> Audio, IReadOnlyList<SubtitleTrackInfo> Subtitles)
    {
        /// <summary>Chapters (start, end, title), e.g. "Intro", "Opening", "Credits".</summary>
        public IReadOnlyList<(double Start, double End, string Title)> Chapters { get; init; } = Array.Empty<(double, double, string)>();

        public bool HasVideo => VideoCodec != null;

        /// <summary>Video that can be copied into an MP4 as is (no re-encoding needed).</summary>
        public bool VideoIsBrowserSafe => VideoCodec is "h264" && PixelFormat is null or "yuv420p" or "yuvj420p";

        public bool AudioIsBrowserSafe(int track = 0)
        {
            var a = Audio.ElementAtOrDefault(track);
            return a == null || a.Codec is "aac" or "mp3" or "opus" or "vorbis" or "flac";
        }
    }

    /// <summary>
    /// Runs ffprobe (cached per file version) and decides whether a browser can play a file
    /// directly or needs live conversion (<see cref="Transcoder"/>).
    /// </summary>
    public sealed class MediaProbe
    {
        private readonly ConcurrentDictionary<string, (DateTime Stamp, long Size, MediaInfo Info)> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _slots = new(3);

        public async Task<MediaInfo?> ProbeAsync(string path, CancellationToken ct)
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            if (_cache.TryGetValue(path, out var c) && c.Stamp == file.LastWriteTimeUtc && c.Size == file.Length) return c.Info;
            if (FfmpegTools.FfprobePath == null) return null;

            await _slots.WaitAsync(ct);
            try
            {
                var psi = FfmpegTools.StartInfo(FfmpegTools.FfprobePath, "-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-show_chapters", path);
                using var p = System.Diagnostics.Process.Start(psi)!;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var errTask = p.StandardError.ReadToEndAsync(timeout.Token);
                string json;
                try
                {
                    json = await p.StandardOutput.ReadToEndAsync(timeout.Token);
                    await p.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    try { p.Kill(true); } catch { }
                    throw;
                }
                try { await errTask; } catch { }

                var info = Parse(json, file.Extension);
                if (info == null) return null;
                if (_cache.Count > 2000) _cache.Clear();
                _cache[path] = (file.LastWriteTimeUtc, file.Length, info);
                return info;
            }
            finally { _slots.Release(); }
        }

        /// <summary>
        /// Whether a typical browser plays the file without help. Browsers handle MP4/WebM with
        /// H.264/VP9/AV1 video and AAC/MP3/Opus audio; MKV, AVI, HEVC, AC3/DTS etc. need conversion.
        /// </summary>
        public static (bool Direct, string? Reason) BrowserCanPlay(MediaInfo info, string extension)
        {
            string ext = extension.ToLowerInvariant();
            if (!info.HasVideo)
            {
                bool okAudio = ext is ".mp3" or ".m4a" or ".m4b" or ".aac" or ".ogg" or ".oga" or ".opus" or ".flac" or ".wav" or ".webm" or ".weba"
                               && info.AudioIsBrowserSafe() || (ext == ".wav" && info.Audio.FirstOrDefault()?.Codec.StartsWith("pcm") == true);
                return okAudio ? (true, null) : (false, $"{info.Audio.FirstOrDefault()?.Codec?.ToUpperInvariant() ?? "this"} audio");
            }

            bool container = ext is ".mp4" or ".m4v" or ".mov" or ".webm";
            bool video = info.VideoCodec is "h264" or "vp8" or "vp9" or "av1";
            bool tenBitH264 = info.VideoCodec == "h264" && info.PixelFormat is not (null or "yuv420p" or "yuvj420p");
            bool audio = info.AudioIsBrowserSafe();
            if (container && video && audio && !tenBitH264) return (true, null);

            var why = new List<string>();
            if (!container) why.Add(ext.TrimStart('.').ToUpperInvariant() + " file");
            if (!video || tenBitH264) why.Add((info.VideoCodec ?? "?").ToUpperInvariant() + (tenBitH264 ? " 10-bit" : "") + " video");
            if (!audio) why.Add(info.Audio[0].Codec.ToUpperInvariant() + " audio");
            return (false, string.Join(", ", why));
        }

        private static MediaInfo? Parse(string json, string extension)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                double duration = 0;
                string container = extension.TrimStart('.').ToLowerInvariant();
                if (root.TryGetProperty("format", out var format))
                {
                    if (format.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dd)) duration = dd;
                    if (format.TryGetProperty("format_name", out var fn)) container = fn.GetString() ?? container;
                }

                string? vcodec = null, pix = null;
                int w = 0, h = 0;
                var audio = new List<AudioTrackInfo>();
                var subs = new List<SubtitleTrackInfo>();
                if (root.TryGetProperty("streams", out var streams))
                {
                    foreach (var s in streams.EnumerateArray())
                    {
                        string type = Str(s, "codec_type") ?? "";
                        string codec = Str(s, "codec_name") ?? "unknown";
                        string? lang = null, title = null;
                        bool isDefault = false, attachedPic = false;
                        if (s.TryGetProperty("tags", out var tags))
                        {
                            lang = Str(tags, "language");
                            title = Str(tags, "title");
                            if (lang == "und") lang = null;
                        }
                        if (s.TryGetProperty("disposition", out var disp))
                        {
                            isDefault = disp.TryGetProperty("default", out var def) && def.GetInt32() == 1;
                            attachedPic = disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                        }

                        switch (type)
                        {
                            case "video" when vcodec == null && !attachedPic && codec is not ("mjpeg" or "png" or "bmp"):
                                vcodec = codec;
                                pix = Str(s, "pix_fmt");
                                w = s.TryGetProperty("width", out var ww) ? ww.GetInt32() : 0;
                                h = s.TryGetProperty("height", out var hh) ? hh.GetInt32() : 0;
                                break;
                            case "audio":
                                int ch = s.TryGetProperty("channels", out var c) ? c.GetInt32() : 2;
                                audio.Add(new AudioTrackInfo(audio.Count, codec, lang, title, ch, isDefault));
                                break;
                            case "subtitle":
                                bool text = codec is "subrip" or "srt" or "ass" or "ssa" or "webvtt" or "mov_text" or "text";
                                subs.Add(new SubtitleTrackInfo(subs.Count, codec, lang, title, text));
                                break;
                        }
                    }
                }
                var chapters = new List<(double, double, string)>();
                if (root.TryGetProperty("chapters", out var chs) && chs.ValueKind == JsonValueKind.Array)
                    foreach (var ch in chs.EnumerateArray())
                    {
                        double Num(string n) => ch.TryGetProperty(n, out var v) && double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : -1;
                        string title = ch.TryGetProperty("tags", out var tg) && tg.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "";
                        double st = Num("start_time"), en = Num("end_time");
                        if (st >= 0 && en > st) chapters.Add((st, en, title));
                    }
                return new MediaInfo(duration, container, vcodec, w, h, pix, audio, subs) { Chapters = chapters };
            }
            catch { return null; }
        }

        private static string? Str(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
