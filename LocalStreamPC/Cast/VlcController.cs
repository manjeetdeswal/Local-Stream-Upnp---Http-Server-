using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LocalStreamPC.Services;

namespace LocalStreamPC.Cast
{
    /// <summary>
    /// Plays cast media in VLC and controls it through VLC's built-in HTTP interface.
    /// VLC runs as our own child process with no VLC interface (just the video window), listening
    /// only on 127.0.0.1 with a random password, so nothing else can control it.
    /// </summary>
    public sealed class VlcController : IPlaybackEngine
    {
        private readonly Func<string?> _vlcPath;
        private readonly AppLog _log;
        private readonly SemaphoreSlim _startLock = new(1, 1);
        private HttpClient? _http;
        private Process? _process;
        private int _port;

        public VlcController(Func<string?> configuredVlcPath, AppLog log)
        {
            _vlcPath = configuredVlcPath;
            _log = log;
        }

        public bool IsRunning => _process is { HasExited: false };
        public bool ReportsVolume => false; // VLC reports 0 when it has no audio device

        /// <summary>Starts VLC if it isn't running. Returns false (and logs why) if it can't.</summary>
        /// <param name="fullscreen">Whether new videos open full screen.</param>
        public async Task<bool> EnsureStartedAsync(bool fullscreen, CancellationToken ct = default)
        {
            if (IsRunning && _http != null) return true;
            await _startLock.WaitAsync(ct);
            try
            {
                if (IsRunning && _http != null) return true;
                KillProcess();

                _port = FreeTcpPort();
                string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                string exe = VlcLauncher.FindVlc(_vlcPath());

                var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in BuildArguments(_port, password, fullscreen)) psi.ArgumentList.Add(arg);

                try
                {
                    _process = Process.Start(psi);
                }
                catch (Exception ex)
                {
                    _log.Write($"Cast: could not start VLC ({ex.Message}). Install VLC from videolan.org, or set its path in Settings.");
                    return false;
                }
                if (_process == null) return false;

                _http?.Dispose();
                _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}/"), Timeout = TimeSpan.FromSeconds(3) };
                _http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + password)));

                // Wait for VLC's HTTP interface to come up.
                for (int i = 0; i < 40; i++)
                {
                    if (_process.HasExited)
                    {
                        _log.Write($"Cast: VLC exited straight away (code {_process.ExitCode}). Is VLC 3.x installed?");
                        return false;
                    }
                    if (await TryGetStatusAsync(ct) != null) return true;
                    await Task.Delay(250, ct);
                }
                _log.Write("Cast: VLC started but its control interface didn't answer.");
                KillProcess();
                return false;
            }
            finally
            {
                _startLock.Release();
            }
        }

        private static string[] BuildArguments(int port, string password, bool fullscreen)
        {
            var args = new System.Collections.Generic.List<string>
            {
                "--intf", "dummy",                     // no VLC window chrome; our app is the remote
                "--extraintf", "http",
                "--http-host", "127.0.0.1",            // never reachable from the network
                "--http-port", port.ToString(CultureInfo.InvariantCulture),
                "--http-password", password,
                "--no-video-title-show",
                "--video-title", "LocalStream Cast",
                "--image-duration", "-1",              // photos stay on screen until the next cast
                "--no-loop", "--no-repeat", "--no-random",
                "--network-caching", "1500",
                fullscreen ? "--fullscreen" : "--no-fullscreen",
            };
            if (OperatingSystem.IsWindows()) args.Add("--dummy-quiet"); // no console window

            // For testing on machines without audio: LOCALSTREAM_VLC_ARGS="--aout dummy"
            string? extra = Environment.GetEnvironmentVariable("LOCALSTREAM_VLC_ARGS");
            if (!string.IsNullOrWhiteSpace(extra)) args.AddRange(extra.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            return args.ToArray();
        }

        // The external VLC window has no next/previous buttons of ours, resume notice or photo fades.
        public event Action<PlayerCommand>? CommandRequested { add { } remove { } }
        public Task ShowResumeNoticeAsync(TimeSpan position) => Task.CompletedTask;
        public Task SetSlideshowAsync(bool running) => Task.CompletedTask;

        public async Task PlayAsync(string url, string title, PlayOptions options, CancellationToken ct = default)
        {
            await CommandAsync("command=pl_empty", ct);
            string option = options.SubtitlePath != null ? "&option=" + Uri.EscapeDataString("sub-file=" + options.SubtitlePath) : "";
            await CommandAsync("command=in_play&input=" + Uri.EscapeDataString(url) + option, ct);
        }

        public Task PauseAsync(CancellationToken ct = default) => CommandAsync("command=pl_forcepause", ct);
        public Task ResumeAsync(CancellationToken ct = default) => CommandAsync("command=pl_forceresume", ct);
        public Task StopAsync(CancellationToken ct = default) => CommandAsync("command=pl_stop", ct);

        public Task SeekAsync(TimeSpan position, CancellationToken ct = default)
            => CommandAsync($"command=seek&val={(int)Math.Max(0, position.TotalSeconds)}", ct);

        /// <param name="volume">0-100 (VLC's 256 = 100%).</param>
        public Task SetVolumeAsync(int volume, CancellationToken ct = default)
            => CommandAsync($"command=volume&val={(int)Math.Round(Math.Clamp(volume, 0, 100) * 2.56)}", ct);

        public async Task SetFullscreenAsync(bool fullscreen, CancellationToken ct = default)
        {
            var status = await TryGetStatusAsync(ct);
            if (status != null && status.Fullscreen != fullscreen) await CommandAsync("command=fullscreen", ct);
        }

        /// <summary>Current playback status, or null if VLC isn't running / not answering.</summary>
        public async Task<PlayerStatus?> TryGetStatusAsync(CancellationToken ct = default)
        {
            if (_http == null || !IsRunning) return null;
            try
            {
                using var response = await _http.GetAsync("requests/status.json", ct);
                if (!response.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;

                var state = (root.TryGetProperty("state", out var s) ? s.GetString() : "") switch
                {
                    "playing" => PlayerState.Playing,
                    "paused" => PlayerState.Paused,
                    _ => PlayerState.Stopped,
                };
                double length = Number(root, "length");
                double time = Number(root, "time");
                double fraction = Number(root, "position");
                if (length > 0 && fraction > 0) time = Math.Max(time, fraction * length); // finer than whole seconds
                int volume = (int)Math.Round(Number(root, "volume") / 2.56);
                bool fullscreen = root.TryGetProperty("fullscreen", out var f) &&
                                  (f.ValueKind == JsonValueKind.True || (f.ValueKind == JsonValueKind.Number && f.GetDouble() != 0));

                return new PlayerStatus(state, TimeSpan.FromSeconds(time), TimeSpan.FromSeconds(length), Math.Clamp(volume, 0, 100), fullscreen);
            }
            catch
            {
                return null;
            }
        }

        private static double Number(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var v)) return 0;
            return v.ValueKind switch
            {
                JsonValueKind.Number => v.GetDouble(),
                JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
                _ => 0,
            };
        }

        private async Task CommandAsync(string query, CancellationToken ct)
        {
            if (_http == null) return;
            try
            {
                using var _ = await _http.GetAsync("requests/status.json?" + query, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // VLC closed by the user; the status poll will notice.
            }
        }

        private static int FreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private void KillProcess()
        {
            try
            {
                if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
            }
            catch { }
            _process?.Dispose();
            _process = null;
        }

        /// <summary>Closes the VLC window we opened.</summary>
        public void Shutdown()
        {
            KillProcess();
            _http?.Dispose();
            _http = null;
        }

        public Task ShutdownAsync() { Shutdown(); return Task.CompletedTask; }

        public Task SetNowPlayingAsync(NowPlayingInfo info) => Task.CompletedTask; // VLC shows its own

        public void Dispose() => Shutdown();
    }
}
