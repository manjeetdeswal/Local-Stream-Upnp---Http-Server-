using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace LocalStreamPC.Services
{
    /// <summary>
    /// Finds ffmpeg/ffprobe (the copies shipped next to the app first, then any on the PATH) and
    /// works out which H.264 encoder this ffmpeg build has, for live conversion.
    /// </summary>
    public static class FfmpegTools
    {
        private static readonly Lazy<string?> Ffmpeg = new(() => Find("ffmpeg"));
        private static readonly Lazy<string?> Ffprobe = new(() => Find("ffprobe"));
        private static readonly Lazy<Task<string?>> H264 = new(DetectH264EncoderAsync);
        private static readonly Lazy<Task<string?>> HardwareH264 = new(DetectHardwareEncoderAsync);

        public static string? FfmpegPath => Ffmpeg.Value;
        public static string? FfprobePath => Ffprobe.Value;
        public static bool CanConvert => FfmpegPath != null;

        /// <summary>libx264 when available (best quality per bit), else a system encoder, else null.</summary>
        public static Task<string?> H264EncoderAsync() => H264.Value;

        /// <summary>
        /// The encoder to convert with: a graphics-card encoder that really works on this PC when
        /// <paramref name="preferHardware"/> (NVIDIA NVENC, Intel Quick Sync, AMD AMF, Apple VideoToolbox),
        /// otherwise the software one.
        /// </summary>
        public static async Task<string?> PickEncoderAsync(bool preferHardware)
            => preferHardware && await HardwareH264.Value is { } hw ? hw : await H264.Value;

        public static string Describe(string? encoder) => encoder switch
        {
            "h264_nvenc" => "NVIDIA graphics (NVENC)",
            "h264_qsv" => "Intel graphics (Quick Sync)",
            "h264_amf" => "AMD graphics (AMF)",
            "h264_videotoolbox" => "Apple VideoToolbox",
            "libx264" => "the processor (x264)",
            "h264_mf" => "Windows Media Foundation",
            null => "nothing (ffmpeg missing)",
            _ => encoder,
        };

        public static bool IsHardware(string? encoder) => encoder is "h264_nvenc" or "h264_qsv" or "h264_amf" or "h264_videotoolbox";

        /// <summary>Every build lists the hardware encoders; only a test encode shows whether the hardware is there.</summary>
        private static async Task<string?> DetectHardwareEncoderAsync()
        {
            if (FfmpegPath == null) return null;
            string[] candidates = OperatingSystem.IsMacOS() ? new[] { "h264_videotoolbox" } : new[] { "h264_nvenc", "h264_qsv", "h264_amf" };
            foreach (var encoder in candidates)
            {
                try
                {
                    string pix = encoder == "h264_qsv" ? "nv12" : "yuv420p";
                    var psi = StartInfo(FfmpegPath, "-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i", "color=c=black:s=320x240:d=0.3",
                                        "-vf", "format=" + pix, "-c:v", encoder, "-frames:v", "5", "-f", "null", "-");
                    using var p = Process.Start(psi)!;
                    var err = p.StandardError.ReadToEndAsync();
                    var output = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(8000)) { try { p.Kill(true); } catch { } continue; }
                    await Task.WhenAll(err, output);
                    if (p.ExitCode == 0) return encoder;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Video encoding arguments for <paramref name="encoder"/> at a target height (0 = keep, at most 1080p).
        /// <paramref name="keyframeSeconds"/> forces a keyframe every few seconds (HLS segments must start on one).
        /// </summary>
        public static List<string> VideoEncodeArgs(string encoder, int height, double keyframeSeconds = 0)
        {
            int h = height is 480 or 720 or 1080 ? height : 1080;
            string bitrate = h switch { 480 => "1500k", 720 => "4000k", _ => "8000k" };
            string bufsize = h switch { 480 => "3000k", 720 => "8000k", _ => "16000k" };
            string pixels = encoder is "h264_mf" or "h264_videotoolbox" or "h264_qsv" ? "nv12" : "yuv420p";
            // Never upscale; keep the width even.
            var a = new List<string> { "-vf", $"scale=-2:'min({h},ih)':flags=bicubic,format={pixels}", "-c:v", encoder };
            switch (encoder)
            {
                case "libx264":
                    a.AddRange(new[] { "-preset", "veryfast", "-crf", "22", "-maxrate", bitrate, "-bufsize", bufsize, "-profile:v", "high", "-threads", "0" });
                    break;
                case "h264_nvenc":
                    a.AddRange(new[] { "-preset", "p4", "-rc", "vbr", "-cq", "23", "-b:v", bitrate, "-maxrate", bitrate, "-bufsize", bufsize, "-profile:v", "high" });
                    if (keyframeSeconds > 0) a.AddRange(new[] { "-forced-idr", "1" });
                    break;
                case "h264_qsv":
                    a.AddRange(new[] { "-preset", "veryfast", "-global_quality", "23", "-maxrate", bitrate, "-bufsize", bufsize, "-look_ahead", "0" });
                    break;
                case "h264_amf":
                    a.AddRange(new[] { "-quality", "speed", "-rc", "vbr_peak", "-b:v", bitrate, "-maxrate", bitrate, "-bufsize", bufsize });
                    break;
                default:
                    a.AddRange(new[] { "-b:v", bitrate, "-maxrate", bitrate, "-bufsize", bufsize });
                    break;
            }
            if (keyframeSeconds > 0)
                a.AddRange(new[] { "-force_key_frames", $"expr:gte(t,n_forced*{keyframeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)})", "-sc_threshold", "0" });
            else
                a.AddRange(new[] { "-g", "48" });
            return a;
        }

        private static string? Find(string name)
        {
            string exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
            string bundled = Path.Combine(AppPaths.BaseDir, exe);
            if (File.Exists(bundled)) return bundled;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim('"'), exe);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            return null;
        }

        private static async Task<string?> DetectH264EncoderAsync()
        {
            if (FfmpegPath == null) return null;
            try
            {
                var psi = new ProcessStartInfo(FfmpegPath, "-hide_banner -encoders")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                };
                using var p = Process.Start(psi)!;
                var errTask = p.StandardError.ReadToEndAsync();
                string list = await p.StandardOutput.ReadToEndAsync();
                await errTask;
                await p.WaitForExitAsync();
                // Hardware encoders are listed by every build but fail without the hardware, so
                // only the software one and the OS media framework encoders are used.
                string[] preferred = { "libx264", "h264_mf", "h264_videotoolbox", "libopenh264" };
                return preferred.FirstOrDefault(e => list.Split('\n').Any(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) == e));
            }
            catch { return null; }
        }

        public static ProcessStartInfo StartInfo(string exe, params string[] args)
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = false,
                UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            return psi;
        }
    }
}
