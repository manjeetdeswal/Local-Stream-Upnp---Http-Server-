using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LocalStreamPC.Services;
using SkiaSharp;

namespace LocalStreamPC.Server
{
    public sealed record Thumbnail(byte[] Data, string ContentType);

    /// <summary>
    /// Makes small preview images for images, music (embedded cover art) and video (an ffmpeg frame),
    /// with an on-disk cache. Callers must already have checked the path with <see cref="PathGuard"/>.
    /// </summary>
    public sealed class ThumbnailService
    {
        private const int ImageWidth = 300;
        private const long MaxOriginalFallbackBytes = 20 * 1024 * 1024;
        private static readonly TimeSpan FfmpegTimeout = TimeSpan.FromSeconds(20);

        // Allow up to half the CPU threads to run ffmpeg at once.
        private readonly SemaphoreSlim _ffmpegSlots = new(Math.Max(1, Environment.ProcessorCount / 2));
        private readonly AppLog _log;

        public ThumbnailService(AppLog log) => _log = log;

        /// <summary>For songs without a cover in their tags: a picture file to use instead (folder or online cover).</summary>
        public Func<string, string?>? AudioCoverFallback { get; set; }

        public async Task<Thumbnail?> GetAsync(string fullPath, CancellationToken ct)
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists) return null;

            string cacheFile = Path.Combine(AppPaths.ThumbnailCacheDir, CacheKey(info) + ".img");
            if (File.Exists(cacheFile))
            {
                try
                {
                    byte[] cached = await File.ReadAllBytesAsync(cacheFile, ct);
                    File.SetLastAccessTimeUtc(cacheFile, DateTime.UtcNow);
                    return new Thumbnail(cached, SniffContentType(cached));
                }
                catch (IOException) { /* regenerate */ }
            }

            string ext = info.Extension;
            byte[]? bytes = null;
            if (MediaTypes.Image.Contains(ext)) bytes = await Task.Run(() => FromImage(info), ct);
            else if (MediaTypes.Audio.Contains(ext))
            {
                bytes = FromAudio(fullPath);
                if (bytes == null && AudioCoverFallback?.Invoke(fullPath) is string cover && File.Exists(cover))
                {
                    bytes = await Task.Run(() => FromImage(new FileInfo(cover)), ct);
                    // Not cached under the song's key: a better cover (in the tags, or found later) must show up.
                    return bytes == null ? null : new Thumbnail(bytes, SniffContentType(bytes));
                }
            }
            else if (MediaTypes.Video.Contains(ext)) bytes = await FromVideoAsync(fullPath, ct);

            if (bytes == null || bytes.Length == 0) return null;

            try { await File.WriteAllBytesAsync(cacheFile, bytes, CancellationToken.None); } catch { }
            return new Thumbnail(bytes, SniffContentType(bytes));
        }

        /// <summary>Deletes cache entries older than 7 days, then the least recently used ones above 500 MB.</summary>
        public void CleanCacheInBackground()
        {
            Task.Run(() =>
            {
                try
                {
                    var dir = new DirectoryInfo(AppPaths.ThumbnailCacheDir);
                    foreach (var f in dir.GetFiles().Where(f => DateTime.UtcNow - f.LastWriteTimeUtc > TimeSpan.FromDays(7)))
                        try { f.Delete(); } catch { }

                    var files = dir.GetFiles();
                    long total = files.Sum(f => f.Length);
                    const long limit = 500L * 1024 * 1024;
                    foreach (var f in files.OrderBy(f => f.LastAccessTimeUtc))
                    {
                        if (total < limit) break;
                        try { long len = f.Length; f.Delete(); total -= len; } catch { }
                    }
                }
                catch (Exception ex)
                {
                    _log.Write($"Cache cleanup error: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Short, fixed-length name that changes when the file changes. The old cache used the
        /// base64 of the full path, which broke the 255-character file-name limit for deep folders
        /// and never noticed when a file was replaced.
        /// </summary>
        private static string CacheKey(FileInfo info)
        {
            string key = $"{info.FullName}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..40];
        }

        private byte[]? FromImage(FileInfo info)
        {
            try
            {
                using var codec = SKCodec.Create(info.FullName) ?? throw new InvalidDataException("Unsupported image");

                // Let the decoder shrink while decoding (JPEG can decode at 1/2, 1/4, 1/8 size directly).
                // A 24 MP photo then needs ~1.5 MB instead of ~96 MB and decodes several times faster.
                var full = codec.Info;
                bool sideways = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
                int displayWidth = sideways ? full.Height : full.Width;
                float scale = Math.Min(1f, ImageWidth * 2f / displayWidth); // decode at >= 2x the target, then resample
                var decodeSize = codec.GetScaledDimensions(scale);
                using var decoded = SKBitmap.Decode(codec, new SKImageInfo(decodeSize.Width, decodeSize.Height, SKColorType.Rgba8888, SKAlphaType.Premul))
                                    ?? throw new InvalidDataException("Could not decode image");

                using var upright = ApplyOrientation(decoded, codec.EncodedOrigin); // phone photos store rotation in EXIF
                var source = upright ?? decoded;
                int width = Math.Min(ImageWidth, source.Width);
                int height = Math.Max(1, (int)Math.Round(source.Height * (width / (double)source.Width)));
                using var resized = source.Resize(new SKImageInfo(width, height), SKFilterQuality.Medium);
                using var image = SKImage.FromBitmap(resized ?? source);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);
                return data.ToArray();
            }
            catch (Exception ex)
            {
                // Unusual formats: send the original if it's not huge.
                if (info.Length <= MaxOriginalFallbackBytes)
                {
                    try { return File.ReadAllBytes(info.FullName); } catch { }
                }
                _log.Write($"Image thumbnail failed for {info.Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Rotates/flips to match the EXIF orientation. Null when no change is needed.</summary>
        internal static SKBitmap? ApplyOrientation(SKBitmap bitmap, SKEncodedOrigin origin)
        {
            float w = bitmap.Width, h = bitmap.Height;
            // x' = a*x + b*y + c ; y' = d*x + e*y + f
            SKMatrix? m = origin switch
            {
                SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),    // mirror
                SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1), // 180°
                SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),   // flip
                SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),       // transpose
                SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),     // 90° clockwise
                SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1), // transverse
                SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),   // 90° counter-clockwise
                _ => null,
            };
            if (m == null) return null;

            bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
            var result = new SKBitmap(swap ? bitmap.Height : bitmap.Width, swap ? bitmap.Width : bitmap.Height);
            using var canvas = new SKCanvas(result);
            canvas.SetMatrix(m.Value);
            canvas.DrawBitmap(bitmap, 0, 0);
            return result;
        }

        private static byte[]? FromAudio(string path)
        {
            try
            {
                // ReadStyle.None: we only need the tags, not the audio properties (skips scanning frames).
                using var tagFile = TagLib.File.Create(path, TagLib.ReadStyle.None);
                return tagFile.Tag.Pictures.Length > 0 ? tagFile.Tag.Pictures[0].Data.Data : null;
            }
            catch { return null; }
        }

        private async Task<byte[]?> FromVideoAsync(string path, CancellationToken ct)
        {
            if (FfmpegTools.FfmpegPath == null) return null;

            await _ffmpegSlots.WaitAsync(ct);
            try
            {
                // 60s avoids black intros; clips shorter than that get a frame from the first second.
                return await GrabFrameAsync(path, "00:01:00", ct) ?? await GrabFrameAsync(path, "00:00:01", ct);
            }
            finally
            {
                _ffmpegSlots.Release();
            }
        }

        private async Task<byte[]?> GrabFrameAsync(string videoPath, string seek, CancellationToken ct)
        {
            string tempThumb = Path.Combine(Path.GetTempPath(), $"ls_thumb_{Guid.NewGuid():N}.jpg");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = FfmpegTools.FfmpegPath!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                };
                // ArgumentList quotes each argument correctly, whatever characters the file name has.
                // -skip_frame nokey: decode only keyframes (about 2x faster). -threads 2: several
                // thumbnails run in parallel already, so don't let each ffmpeg grab every core.
                foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-threads", "2", "-skip_frame", "nokey", "-ss", seek, "-i", videoPath,
                                            "-vframes", "1", "-q:v", "2", "-pix_fmt", "yuvj420p",
                                            "-vf", $"scale={ImageWidth}:-1", "-y", tempThumb })
                    psi.ArgumentList.Add(arg);

                using var process = Process.Start(psi);
                if (process == null) return null;
                _ = process.StandardError.ReadToEndAsync(); // drain so ffmpeg never blocks on a full pipe

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(FfmpegTimeout);
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return null;
                }

                if (!File.Exists(tempThumb)) return null;
                byte[] bytes = await File.ReadAllBytesAsync(tempThumb, CancellationToken.None);
                return bytes.Length > 0 ? bytes : null;
            }
            catch (Exception ex)
            {
                _log.Write($"FFmpeg error: {ex.Message}");
                return null;
            }
            finally
            {
                try { File.Delete(tempThumb); } catch { }
            }
        }

        private static string SniffContentType(byte[] b)
        {
            if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8) return "image/jpeg";
            if (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
            if (b.Length > 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F') return "image/gif";
            if (b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E') return "image/webp";
            if (b.Length > 2 && b[0] == 'B' && b[1] == 'M') return "image/bmp";
            return "application/octet-stream";
        }
    }
}
