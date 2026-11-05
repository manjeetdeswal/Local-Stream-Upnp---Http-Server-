using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LocalStreamPC.Services;
using Microsoft.AspNetCore.Http;

namespace LocalStreamPC.Server
{
    /// <summary>
    /// Live conversion with ffmpeg for files a browser or TV can't play (MKV, HEVC, AC3/DTS...).
    /// Output is a fragmented MP4 streamed while it's made, so playback starts within seconds.
    /// H.264 video is copied as is (only the audio and container change), which costs almost no CPU.
    /// Seeking restarts the conversion at the new position (?start=seconds).
    /// </summary>
    public sealed class Transcoder
    {
        private const int MaxConversions = 3;
        private readonly SemaphoreSlim _slots = new(MaxConversions);
        private readonly AppLog _log;
        private readonly Func<bool> _preferHardware;
        private int _running;

        /// <param name="preferHardware">Use the graphics card when one works (read for each conversion).</param>
        public Transcoder(AppLog log, Func<bool>? preferHardware = null)
        {
            _log = log;
            _preferHardware = preferHardware ?? (() => true);
        }

        public Task<string?> EncoderAsync() => FfmpegTools.PickEncoderAsync(_preferHardware());

        /// <summary>Conversions running right now (used to keep the PC awake).</summary>
        public int Running => Volatile.Read(ref _running);

        /// <summary>Streams the converted file. <paramref name="audioTrack"/> is the n-th audio track.</summary>
        /// <param name="quality">Target height (480/720/1080) or 0 for the original (H.264 is then copied as is).</param>
        public async Task StreamAsync(HttpContext ctx, string path, MediaInfo info, double start, int audioTrack, int quality = 0)
        {
            string? ffmpeg = FfmpegTools.FfmpegPath;
            if (ffmpeg == null)
            {
                ctx.Response.StatusCode = StatusCodes.Status501NotImplemented;
                await ctx.Response.WriteAsync("ffmpeg is missing, so this file can't be converted.");
                return;
            }
            if (!await _slots.WaitAsync(TimeSpan.FromSeconds(2), ctx.RequestAborted))
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                ctx.Response.Headers.RetryAfter = "5";
                await ctx.Response.WriteAsync($"Already converting {MaxConversions} files. Try again in a moment.");
                return;
            }

            Process? process = null;
            Interlocked.Increment(ref _running);
            try
            {
                var args = await BuildArgumentsAsync(path, info, start, audioTrack, quality);
                if (args == null)
                {
                    ctx.Response.StatusCode = StatusCodes.Status501NotImplemented;
                    await ctx.Response.WriteAsync("This ffmpeg can't encode H.264 video.");
                    return;
                }

                var psi = FfmpegTools.StartInfo(ffmpeg, args.ToArray());
                process = Process.Start(psi)!;
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { } // keep the PC responsive
                var stderr = new StringBuilder();
                process.ErrorDataReceived += (_, e) => { if (e.Data != null && stderr.Length < 4000) stderr.AppendLine(e.Data); };
                process.BeginErrorReadLine();

                ctx.Response.ContentType = info.HasVideo ? "video/mp4" : "audio/mp4";
                ctx.Response.Headers.CacheControl = "no-store";
                ctx.Response.Headers["Accept-Ranges"] = "none";
                ctx.Response.Headers["X-Content-Duration"] = info.Duration.ToString("0.###", CultureInfo.InvariantCulture);
                ctx.Response.Headers["transferMode.dlna.org"] = "Streaming";
                ctx.Response.Headers["contentFeatures.dlna.org"] = "DLNA.ORG_OP=00;DLNA.ORG_CI=1;DLNA.ORG_FLAGS=01700000000000000000000000000000";

                _log.Write($"Converting \"{Path.GetFileName(path)}\" for playback{(start > 1 ? $" from {TimeSpan.FromSeconds(start):h\\:mm\\:ss}" : "")}.");
                try
                {
                    await process.StandardOutput.BaseStream.CopyToAsync(ctx.Response.Body, 64 * 1024, ctx.RequestAborted);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException)
                {
                    // Player seeked, paused for long or closed. Normal.
                }

                if (!ctx.RequestAborted.IsCancellationRequested)
                {
                    await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });
                    if (process.HasExited && process.ExitCode != 0)
                        _log.Write($"Conversion of \"{Path.GetFileName(path)}\" failed: {FirstLine(stderr.ToString())}");
                }
            }
            finally
            {
                if (process != null)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                    process.Dispose();
                }
                Interlocked.Decrement(ref _running);
                _slots.Release();
            }
        }

        private async Task<List<string>?> BuildArgumentsAsync(string path, MediaInfo info, double start, int audioTrack, int quality)
        {
            var a = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
            if (start > 0.5) a.AddRange(new[] { "-ss", start.ToString("0.###", CultureInfo.InvariantCulture) });
            a.AddRange(new[] { "-i", path });

            audioTrack = Math.Clamp(audioTrack, 0, Math.Max(0, info.Audio.Count - 1));
            if (info.HasVideo)
            {
                a.AddRange(new[] { "-map", "0:v:0" });
                bool smaller = quality > 0 && info.Height > quality + 8;
                if (info.VideoIsBrowserSafe && !smaller)
                {
                    a.AddRange(new[] { "-c:v", "copy" });
                }
                else
                {
                    string? encoder = await EncoderAsync();
                    if (encoder == null) return null;
                    // Never above 1080p: converting 4K live is too slow on most PCs.
                    a.AddRange(FfmpegTools.VideoEncodeArgs(encoder, quality));
                }
            }
            else
            {
                a.Add("-vn");
            }

            if (info.Audio.Count > 0)
            {
                a.AddRange(new[] { "-map", $"0:a:{audioTrack}" });
                var track = info.Audio[audioTrack];
                if (track.Codec == "aac" && track.Channels <= 2) a.AddRange(new[] { "-c:a", "copy" });
                else a.AddRange(new[] { "-c:a", "aac", "-b:a", track.Channels > 2 ? "256k" : "192k", "-ac", "2" });
            }

            a.AddRange(new[] { "-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1",
                               "-f", "mp4", "-movflags", "frag_keyframe+empty_moov+default_base_moof", "-frag_duration", "2000000", "pipe:1" });
            return a;
        }

        // ------------------------------------------------------------ subtitles (WebVTT for the browser)

        /// <summary>A .srt/.vtt file next to the video, as WebVTT shifted back by <paramref name="shift"/> seconds.</summary>
        public static async Task<string> ExternalToVttAsync(string subtitlePath, double shift)
        {
            string text = await ReadTextAsync(subtitlePath);
            return Shift(Path.GetExtension(subtitlePath).Equals(".vtt", StringComparison.OrdinalIgnoreCase) ? text : SrtToVtt(text), shift);
        }

        /// <summary>An embedded text subtitle track, extracted with ffmpeg.</summary>
        public async Task<string?> EmbeddedToVttAsync(string path, int track, double shift, CancellationToken ct)
        {
            if (FfmpegTools.FfmpegPath == null) return null;
            var psi = FfmpegTools.StartInfo(FfmpegTools.FfmpegPath, "-hide_banner", "-loglevel", "error", "-nostdin",
                                            "-i", path, "-map", $"0:s:{track}", "-f", "webvtt", "pipe:1");
            using var p = Process.Start(psi)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60)); // the whole file is read to find every cue
            try
            {
                var err = p.StandardError.ReadToEndAsync(timeout.Token);
                string vtt = await p.StandardOutput.ReadToEndAsync(timeout.Token);
                await p.WaitForExitAsync(timeout.Token);
                try { await err; } catch { }
                return p.ExitCode == 0 ? Shift(vtt, shift) : null;
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                return null;
            }
        }

        private static async Task<string> ReadTextAsync(string path)
        {
            byte[] bytes = await File.ReadAllBytesAsync(path);
            // Subtitles are often in a legacy code page; UTF-8 decoding that fails falls back to Latin-1.
            try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
            catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
        }

        private static readonly Regex SrtTime = new(@"(\d{1,2}):(\d{2}):(\d{2})[,.](\d{1,3})", RegexOptions.Compiled);
        private static readonly Regex VttTime = new(@"(?:(\d{1,2}):)?(\d{2}):(\d{2})\.(\d{3})", RegexOptions.Compiled);

        public static string SrtToVtt(string srt)
        {
            var sb = new StringBuilder("WEBVTT\n\n");
            foreach (var rawLine in srt.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = rawLine;
                if (line.Contains("-->")) line = SrtTime.Replace(line, m => $"{int.Parse(m.Groups[1].Value):00}:{m.Groups[2].Value}:{m.Groups[3].Value}.{m.Groups[4].Value.PadRight(3, '0')}");
                sb.Append(line).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>Moves every cue <paramref name="shift"/> seconds earlier (for a conversion that started mid-file).</summary>
        public static string Shift(string vtt, double shift)
        {
            if (shift <= 0) return vtt;
            var sb = new StringBuilder();
            foreach (var line in vtt.Replace("\r\n", "\n").Split('\n'))
            {
                if (!line.Contains("-->")) { sb.Append(line).Append('\n'); continue; }
                sb.Append(VttTime.Replace(line, m =>
                {
                    int h = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0;
                    var t = new TimeSpan(0, h, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value)) - TimeSpan.FromSeconds(shift);
                    if (t < TimeSpan.Zero) t = TimeSpan.Zero;
                    return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
                })).Append('\n');
            }
            return sb.ToString();
        }

        private static string FirstLine(string s)
        {
            s = s.Trim();
            int nl = s.IndexOf('\n');
            return nl > 0 ? s[..nl] : s;
        }
    }
}
