using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LocalStreamPC.Services;
using Microsoft.AspNetCore.Http;

namespace LocalStreamPC.Server
{
    /// <summary>
    /// Live conversion as HLS: the file is cut into 4-second pieces, the way iPhones, iPads, Safari
    /// and Chromecast want converted video. The playlist lists every piece up front, so the player
    /// knows the full length and can jump anywhere instantly. ffmpeg makes the pieces just ahead of
    /// the viewer; a jump far away restarts it there, and it pauses when it is far enough ahead.
    /// </summary>
    public sealed class HlsService : IDisposable
    {
        public const double SegmentSeconds = 4;
        private const int AheadSegments = 45;   // ~3 minutes ready ahead of the viewer, then pause
        private const int MaxJobs = 3;
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(90);

        private readonly AppLog _log;
        private readonly Transcoder _transcoder;
        private readonly ConcurrentDictionary<string, Job> _jobs = new();
        private readonly Timer _cleanup;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalStream-hls");

        public HlsService(AppLog log, Transcoder transcoder)
        {
            _log = log;
            _transcoder = transcoder;
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
            _cleanup = new Timer(_ => Cleanup(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
        }

        /// <summary>Conversions running now (keeps the PC awake).</summary>
        public int Running => _jobs.Values.Count(j => j.IsRunning);

        private sealed class Job
        {
            public required string Key, Path, Dir;
            public required MediaInfo Info;
            public int Audio, Quality, SegmentCount;
            public Process? Process;
            public int StartSegment;           // first piece the running ffmpeg makes
            public int LastRequested;
            public DateTime LastAccess = DateTime.UtcNow;
            public readonly SemaphoreSlim Gate = new(1, 1);
            public bool IsRunning => Process is { HasExited: false };
        }

        public static int SegmentCount(double duration) => Math.Max(1, (int)Math.Ceiling(duration / SegmentSeconds));

        /// <summary>The playlist: every piece, each <see cref="SegmentSeconds"/> long (the last one shorter).</summary>
        public static string Playlist(double duration, string segmentQuery)
        {
            int count = SegmentCount(duration);
            var sb = new StringBuilder();
            sb.Append("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-INDEPENDENT-SEGMENTS\n");
            sb.Append("#EXT-X-TARGETDURATION:").Append((int)Math.Ceiling(SegmentSeconds)).Append("\n#EXT-X-MEDIA-SEQUENCE:0\n");
            for (int i = 0; i < count; i++)
            {
                double len = Math.Min(SegmentSeconds, duration - i * SegmentSeconds);
                if (len <= 0.05) len = 0.1;
                sb.Append("#EXTINF:").Append(len.ToString("0.000", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append(i).Append(".ts").Append(segmentQuery).Append('\n');
            }
            sb.Append("#EXT-X-ENDLIST\n");
            return sb.ToString();
        }

        /// <summary>Sends piece <paramref name="segment"/>, making it first if needed.</summary>
        public async Task ServeSegmentAsync(HttpContext ctx, string path, MediaInfo info, int segment, int audio, int quality)
        {
            if (FfmpegTools.FfmpegPath == null) { ctx.Response.StatusCode = 501; return; }
            string key = $"{path}|{audio}|{quality}";
            var job = _jobs.GetOrAdd(key, _ => new Job
            {
                Key = key, Path = path, Info = info, Audio = audio, Quality = quality,
                Dir = System.IO.Path.Combine(_root, Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16]),
                SegmentCount = SegmentCount(info.Duration),
                StartSegment = -1,
            });
            if (segment < 0 || segment >= job.SegmentCount) { ctx.Response.StatusCode = 404; return; }
            job.LastAccess = DateTime.UtcNow;
            job.LastRequested = segment;
            LimitJobs(job);

            string file = SegmentFile(job, segment);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (!IsComplete(job, segment))
            {
                await job.Gate.WaitAsync(ctx.RequestAborted);
                try
                {
                    // Start ffmpeg at this piece if it isn't heading here soon.
                    int produced = Produced(job);
                    bool coming = job.IsRunning && segment >= job.StartSegment && segment <= Math.Max(produced, job.StartSegment) + 6;
                    if (!coming) await StartAsync(job, segment);
                }
                finally { job.Gate.Release(); }
                if (DateTime.UtcNow > deadline) { ctx.Response.StatusCode = 504; return; }
                await Task.Delay(150, ctx.RequestAborted);
                if (!job.IsRunning && !IsComplete(job, segment)) { await Task.Delay(300, ctx.RequestAborted); if (!job.IsRunning && !IsComplete(job, segment)) { ctx.Response.StatusCode = 500; return; } }
            }

            // Keep ffmpeg going (or paused) depending on how far ahead it is.
            _ = KeepPaceAsync(job);

            ctx.Response.ContentType = "video/mp2t";
            ctx.Response.Headers.CacheControl = "private, max-age=3600";
            await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            ctx.Response.ContentLength = fs.Length;
            await fs.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        }

        private static string SegmentFile(Job job, int n) => System.IO.Path.Combine(job.Dir, n + ".ts");

        /// <summary>A piece is done once ffmpeg's own playlist lists it (it writes that after finishing a piece).</summary>
        private static bool IsComplete(Job job, int n)
        {
            if (!File.Exists(SegmentFile(job, n))) return false;
            if (File.Exists(SegmentFile(job, n + 1))) return true;
            return ListedSegments(job).Contains(n) || (!job.IsRunning && n == job.SegmentCount - 1);
        }

        private static HashSet<int> ListedSegments(Job job)
        {
            var set = new HashSet<int>();
            try
            {
                string list = System.IO.Path.Combine(job.Dir, "ffmpeg.m3u8");
                if (!File.Exists(list)) return set;
                using var fs = new FileStream(list, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                string? line;
                while ((line = reader.ReadLine()) != null)
                    if (line.EndsWith(".ts") && int.TryParse(System.IO.Path.GetFileNameWithoutExtension(line), out int n)) set.Add(n);
            }
            catch { }
            return set;
        }

        private static int Produced(Job job)
        {
            var listed = ListedSegments(job);
            return listed.Count == 0 ? job.StartSegment - 1 : listed.Max();
        }

        private async Task StartAsync(Job job, int segment)
        {
            Stop(job);
            Directory.CreateDirectory(job.Dir);
            try { File.Delete(System.IO.Path.Combine(job.Dir, "ffmpeg.m3u8")); } catch { }
            string? encoder = job.Info.HasVideo ? await _transcoder.EncoderAsync() : "aac-only";
            if (encoder == null) throw new InvalidOperationException("No H.264 encoder");

            double start = segment * SegmentSeconds;
            string ss = start.ToString("0.###", CultureInfo.InvariantCulture);
            var a = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
            if (start > 0) a.AddRange(new[] { "-ss", ss });
            a.AddRange(new[] { "-i", job.Path });
            if (job.Info.HasVideo)
            {
                a.AddRange(new[] { "-map", "0:v:0" });
                a.AddRange(FfmpegTools.VideoEncodeArgs(encoder, job.Quality, SegmentSeconds));
            }
            else a.Add("-vn");
            if (job.Info.Audio.Count > 0)
            {
                int track = Math.Clamp(job.Audio, 0, job.Info.Audio.Count - 1);
                a.AddRange(new[] { "-map", $"0:a:{track}", "-c:a", "aac", "-b:a", "192k", "-ac", "2" });
            }
            // Pieces keep the file's own timeline even when ffmpeg starts mid-file.
            if (start > 0) a.AddRange(new[] { "-output_ts_offset", ss });
            a.AddRange(new[] { "-sn", "-dn", "-map_metadata", "-1",
                "-f", "hls", "-hls_time", SegmentSeconds.ToString(CultureInfo.InvariantCulture), "-hls_list_size", "0",
                "-hls_segment_type", "mpegts", "-start_number", segment.ToString(),
                "-hls_segment_filename", System.IO.Path.Combine(job.Dir, "%d.ts"),
                System.IO.Path.Combine(job.Dir, "ffmpeg.m3u8") });

            var p = Process.Start(FfmpegTools.StartInfo(FfmpegTools.FfmpegPath!, a.ToArray()))!;
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            var errors = new StringBuilder();
            p.ErrorDataReceived += (_, e) => { if (e.Data != null && errors.Length < 2000) errors.AppendLine(e.Data); };
            p.BeginErrorReadLine();
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) => { if (p.ExitCode != 0 && p.ExitCode != -1 && p.ExitCode != 137 && errors.Length > 0) _log.Write($"HLS conversion stopped: {errors.ToString().Trim().Split('\n')[0]}"); };
            job.Process = p;
            job.StartSegment = segment;
            if (segment == 0 || start > 0)
                _log.Write($"Converting \"{System.IO.Path.GetFileName(job.Path)}\" for iPhone/Chromecast with {FfmpegTools.Describe(encoder == "aac-only" ? null : encoder).Replace("nothing (ffmpeg missing)", "audio only")}{(start > 0 ? $" from {TimeSpan.FromSeconds(start):h\\:mm\\:ss}" : "")}.");
        }

        /// <summary>Pauses ffmpeg when it is far ahead of the viewer; it starts again when needed.</summary>
        private async Task KeepPaceAsync(Job job)
        {
            if (!await job.Gate.WaitAsync(0)) return;
            try
            {
                if (!job.IsRunning) return;
                int produced = Produced(job);
                if (produced - job.LastRequested > AheadSegments) Stop(job);
                // Old pieces far behind the viewer take disk space: remove them.
                foreach (var f in Directory.EnumerateFiles(job.Dir, "*.ts"))
                    if (int.TryParse(System.IO.Path.GetFileNameWithoutExtension(f), out int n) && n < job.LastRequested - 30)
                        try { File.Delete(f); } catch { }
            }
            catch { }
            finally { job.Gate.Release(); }
        }

        private void LimitJobs(Job current)
        {
            foreach (var old in _jobs.Values.Where(j => j != current).OrderByDescending(j => j.LastAccess).Skip(MaxJobs - 1).ToList())
                Remove(old);
        }

        private static void Stop(Job job)
        {
            var p = job.Process;
            job.Process = null;
            if (p == null) return;
            try { if (!p.HasExited) p.Kill(true); } catch { }
            try { p.WaitForExit(2000); } catch { }
            p.Dispose();
        }

        private void Remove(Job job)
        {
            _jobs.TryRemove(job.Key, out _);
            Stop(job);
            try { Directory.Delete(job.Dir, true); } catch { }
        }

        private void Cleanup()
        {
            foreach (var job in _jobs.Values.Where(j => DateTime.UtcNow - j.LastAccess > IdleTimeout).ToList()) Remove(job);
        }

        public void Dispose()
        {
            _cleanup.Dispose();
            foreach (var job in _jobs.Values.ToList()) Remove(job);
        }
    }
}
