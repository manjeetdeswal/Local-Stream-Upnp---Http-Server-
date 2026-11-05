using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LocalStreamPC.Services
{
    /// <param name="InstallerUrl">The Windows setup .exe attached to the release, if there is one.</param>
    public sealed record UpdateInfo(bool IsUpdateAvailable, string LatestVersion, string CurrentVersion, string ReleaseUrl,
                                    string? InstallerUrl = null, long InstallerSize = 0, string? Notes = null);

    /// <summary>
    /// Checks GitHub for a newer release and, on Windows, downloads its installer and runs it
    /// (the installer closes this app, updates it in place and starts it again).
    /// </summary>
    public sealed class UpdateService
    {
        public const string Repository = "manjeetdeswal/Local-Stream-Upnp---Http-Server-";
        public const string DefaultReleaseUrl = $"https://github.com/{Repository}/releases/latest";
        private const string ApiUrl = $"https://api.github.com/repos/{Repository}/releases/latest";
        private static readonly HttpClient Http = CreateClient();

        /// <summary>The running app's version, from &lt;Version&gt; in the .csproj (e.g. "1.3.0").</summary>
        public static Version CurrentVersion { get; } = ReadCurrentVersion();

        public static string CurrentVersionText => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{Math.Max(0, CurrentVersion.Build)}";

        /// <summary>True when an update can be installed with one click (Windows, and the release has a setup file).</summary>
        public static bool CanInstall(UpdateInfo info) => OperatingSystem.IsWindows() && info.InstallerUrl != null;

        public async Task<UpdateInfo> CheckAsync()
        {
            return ParseRelease(await Http.GetStringAsync(ApiUrl));
        }

        /// <summary>Reads GitHub's "latest release" JSON.</summary>
        public static UpdateInfo ParseRelease(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            string url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? DefaultReleaseUrl : DefaultReleaseUrl;
            string? notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;

            // The installer: "LocalStream-Setup-1.3.0.exe" (any .exe with "setup" in its name), 64-bit preferred.
            string? installer = null;
            long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                var exes = assets.EnumerateArray()
                    .Select(a => (Name: a.GetProperty("name").GetString() ?? "", Url: a.GetProperty("browser_download_url").GetString(), Size: a.TryGetProperty("size", out var s) ? s.GetInt64() : 0))
                    .Where(a => a.Url != null && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && a.Name.Contains("setup", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(a => a.Name.Contains("x64", StringComparison.OrdinalIgnoreCase) || a.Name.Contains("win64", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (exes.Count > 0) { installer = exes[0].Url; size = exes[0].Size; }
            }

            bool newer = TryParseVersion(tag, out var latest) && latest > CurrentVersion;
            return new UpdateInfo(newer, tag, CurrentVersionText, url, installer, size, notes);
        }

        /// <summary>Downloads the installer to the temp folder. <paramref name="progress"/> gets 0..1.</summary>
        public async Task<string> DownloadInstallerAsync(UpdateInfo info, IProgress<double> progress, CancellationToken ct)
        {
            if (info.InstallerUrl == null) throw new InvalidOperationException("This release has no installer.");
            string file = Path.Combine(Path.GetTempPath(), $"LocalStream-Setup-{SafeName(info.LatestVersion)}.exe");
            string partial = file + ".download";
            using (var response = await Http.GetAsync(info.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? info.InstallerSize;
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(partial);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0) progress.Report(Math.Min(1, done / (double)total));
                }
                if (total > 0 && done != total) throw new IOException("The download was incomplete.");
            }
            File.Move(partial, file, overwrite: true);
            return file;
        }

        /// <summary>
        /// Starts the installer without its wizard. It waits for this app to close, replaces it and
        /// starts the new version. The caller should exit right after this.
        /// </summary>
        public static void RunInstaller(string installer)
        {
            Process.Start(new ProcessStartInfo(installer)
            {
                Arguments = "/SILENT /SP- /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /NORESTART",
                UseShellExecute = true, // lets Windows ask for permission if the app was installed for all users
            });
        }

        /// <summary>Parses tags like "v1.3", "1.3.0", "LocalStream-1.3.1-beta" into a Version.</summary>
        public static bool TryParseVersion(string? tag, out Version version)
        {
            version = new Version(0, 0);
            if (string.IsNullOrWhiteSpace(tag)) return false;
            var match = Regex.Match(tag, @"(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?");
            if (!match.Success) return false;

            int Part(int i) => match.Groups[i].Success ? int.Parse(match.Groups[i].Value) : 0;
            version = new Version(Part(1), Part(2), Part(3), Part(4));
            return true;
        }

        private static string SafeName(string s) => new(s.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-').ToArray());

        private static Version ReadCurrentVersion()
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0);
            // Normalise to 4 parts so 1.2 == 1.2.0.0 when comparing.
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.Add("User-Agent", "LocalStream-App");
            return client;
        }
    }
}
