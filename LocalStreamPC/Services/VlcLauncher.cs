using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using LocalStreamPC.Models;

namespace LocalStreamPC.Services
{
    /// <summary>Writes an .m3u playlist and opens it in VLC.</summary>
    public sealed class VlcLauncher
    {
        private readonly AppLog _log;
        public VlcLauncher(AppLog log) => _log = log;

        public void Play(IReadOnlyList<RemoteItem> items)
        {
            if (items.Count == 0) return;

            var sb = new StringBuilder();
            sb.AppendLine("#EXTM3U");
            foreach (var item in items)
            {
                // Titles come from the remote server: strip line breaks so a title can't inject
                // extra playlist lines (e.g. #EXTVLCOPT options).
                sb.AppendLine($"#EXTINF:-1,{OneLine(item.Title)}");

                string? sub = TryFindLocalSubtitle(item.Url);
                if (sub != null) sb.AppendLine($"#EXTVLCOPT:sub-file={OneLine(sub)}");

                sb.AppendLine(OneLine(item.Url));
            }

            string playlist = Path.Combine(Path.GetTempPath(), $"LocalStream_{DateTime.Now.Ticks}.m3u");
            File.WriteAllText(playlist, sb.ToString());

            try
            {
                var psi = new ProcessStartInfo { FileName = FindVlc(null), UseShellExecute = false };
                psi.ArgumentList.Add(playlist);
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                _log.Write($"VLC error: {ex.Message}. Make sure VLC is installed.");
            }
        }

        /// <summary>
        /// When the URL points at a LocalStream server on this PC, the /file/ id is the local path,
        /// so we can hand VLC the matching subtitle file directly.
        /// </summary>
        private static string? TryFindLocalSubtitle(string url)
        {
            try
            {
                int idx = url.IndexOf("/file/", StringComparison.Ordinal);
                if (idx < 0) return null;
                string path = Encoding.UTF8.GetString(Convert.FromBase64String(Uri.UnescapeDataString(url[(idx + 6)..])));
                if (!File.Exists(path)) return null;
                return SubtitleMatcher.FindBestMatch(path);
            }
            catch { return null; }
        }

        private static string OneLine(string? s) => (s ?? "").Replace("\r", " ").Replace("\n", " ");

        /// <summary>Path of the VLC executable (or just "vlc" to rely on PATH).</summary>
        public static string FindVlc(string? configuredPath)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath)) return configuredPath;
            if (OperatingSystem.IsWindows())
            {
                string[] candidates = { @"C:\Program Files\VideoLAN\VLC\vlc.exe", @"C:\Program Files (x86)\VideoLAN\VLC\vlc.exe" };
                foreach (var p in candidates) if (File.Exists(p)) return p;
                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\VideoLAN\VLC");
                    if (key?.GetValue("InstallDir") is string dir && File.Exists(Path.Combine(dir, "vlc.exe")))
                        return Path.Combine(dir, "vlc.exe");
                }
                catch { }
            }
            else if (OperatingSystem.IsMacOS())
            {
                return "/Applications/VLC.app/Contents/MacOS/VLC";
            }
            return "vlc";
        }
    }
}
