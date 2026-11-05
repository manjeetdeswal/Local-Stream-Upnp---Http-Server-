using System;
using System.IO;

namespace LocalStreamPC.Services
{
    /// <summary>
    /// Where the app keeps its files. Everything lives in the per-user data folder
    /// (%AppData%\LocalStreamPC on Windows, ~/.config/LocalStreamPC on Linux/macOS) so it works
    /// no matter what the working directory is (e.g. when Windows launches the app at login
    /// from System32) and even when the app is installed in a read-only folder like Program Files.
    /// </summary>
    public static class AppPaths
    {
        public static string DataDir { get; } = CreateDataDir();
        public static string ConfigFile => Path.Combine(DataDir, "config.json");
        public static string ThumbnailCacheDir => EnsureDir(Path.Combine(DataDir, "Cache"));
        public static string BaseDir => AppContext.BaseDirectory;

        /// <summary>Old location (v1.0–1.2): config.json next to the exe / in the working directory.</summary>
        public static string[] LegacyConfigFiles => new[]
        {
            Path.Combine(AppContext.BaseDirectory, "config.json"),
            Path.Combine(Environment.CurrentDirectory, "config.json"),
        };

        public static string FfmpegPath => Path.Combine(BaseDir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        public static string FfprobePath => Path.Combine(BaseDir, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");

        private static string CreateDataDir()
        {
            // Option.Create: without it .NET returns "" when the folder doesn't exist yet (fresh Linux user).
            string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);
            // Last resort: next to the exe ("-data" so it can't clash with the Linux/macOS executable "LocalStreamPC").
            if (string.IsNullOrEmpty(root)) return EnsureDir(Path.Combine(AppContext.BaseDirectory, "LocalStreamPC-data"));
            return EnsureDir(Path.Combine(root, "LocalStreamPC"));
        }

        private static string EnsureDir(string dir)
        {
            try { Directory.CreateDirectory(dir); } catch { /* reported later when used */ }
            return dir;
        }
    }
}
