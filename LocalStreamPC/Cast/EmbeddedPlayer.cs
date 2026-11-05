using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using LocalStreamPC.Services;

namespace LocalStreamPC.Cast
{
    /// <summary>
    /// Plays cast media inside LocalStream using LibVLC (VLC's engine as a library, via LibVLCSharp).
    /// Video is shown in <see cref="PlayerWindow"/>, which can go full screen and has its own controls.
    ///
    /// Windows/macOS: libvlc ships with the app (VideoLAN.LibVLC.* packages).
    /// Linux: uses the system libvlc (install the "vlc" package).
    /// </summary>
    public sealed class EmbeddedPlayer : IPlaybackEngine
    {
        private static readonly object InitLock = new();
        private static bool? _available;

        private readonly AppLog _log;
        private LibVLC? _libVlc;
        private MediaPlayer? _player;
        private PlayerWindow? _window;
        private volatile bool _windowOpen;
        private NowPlayingInfo? _nowPlaying;
        private CoverArt? _art;              // cover + blurred backdrop for _nowPlaying
        private byte[]? _artSource;          // the picture _art was made from
        private readonly SemaphoreSlim _nowPlayingGate = new(1, 1);
        private volatile bool _showingPhoto;
        private static readonly System.Net.Http.HttpClient PhotoClient = new() { Timeout = TimeSpan.FromSeconds(30) };
        private const long MaxPhotoBytes = 80L * 1024 * 1024;

        private readonly Func<int> _subtitleScale;

        public EmbeddedPlayer(AppLog log, Func<int>? subtitleScale = null)
        {
            _log = log;
            _subtitleScale = subtitleScale ?? (() => 100);
        }

        public event Action<PlayerCommand>? CommandRequested;

        /// <summary>URL of the current cover for Windows' media overlay (served by the cast receiver on localhost).</summary>
        public Func<string?>? CoverUrl { get; set; }

        internal void RaiseCommand(PlayerCommand command) => CommandRequested?.Invoke(command);
        internal void Log(string message) => _log.Write(message);

        public bool IsRunning => _windowOpen;
        public bool ReportsVolume => true;

        /// <summary>Loads libvlc once. False if it isn't available (then the external VLC player is used).</summary>
        public static bool TryInitialize(AppLog log)
        {
            lock (InitLock)
            {
                if (_available.HasValue) return _available.Value;
                try
                {
                    // Self-contained / single-file builds: point LibVLCSharp at the folder next to the exe.
                    string bundled = Path.Combine(AppContext.BaseDirectory, "libvlc", OperatingSystem.IsWindows()
                        ? (Environment.Is64BitProcess ? "win-x64" : "win-x86") : "osx-x64");
                    if (OperatingSystem.IsLinux()) MapLinuxLibraryNames();
                    if (!OperatingSystem.IsLinux() && Directory.Exists(bundled)) Core.Initialize(bundled);
                    else Core.Initialize();
                    _available = true;
                }
                catch (Exception ex)
                {
                    log.Write($"Built-in player unavailable ({ex.Message}). " +
                              (OperatingSystem.IsLinux() ? "Install VLC (e.g. 'sudo apt install vlc'). " : "") +
                              "Falling back to the external VLC player.");
                    _available = false;
                }
                return _available.Value;
            }
        }

        /// <summary>
        /// LibVLCSharp asks for "libvlc", which on Linux only resolves when the -dev package is
        /// installed. A normal VLC install has libvlc.so.5, so map the names to the real files.
        /// </summary>
        private static void MapLinuxLibraryNames()
        {
            NativeLibrary.SetDllImportResolver(typeof(Core).Assembly, (name, assembly, path) =>
            {
                string[] candidates = name switch
                {
                    "libvlc" => new[] { "libvlc.so.5", "libvlc.so" },
                    "libvlccore" => new[] { "libvlccore.so.9", "libvlccore.so" },
                    "libX11" => new[] { "libX11.so.6", "libX11.so" },
                    _ => Array.Empty<string>(),
                };
                foreach (var candidate in candidates)
                    if (NativeLibrary.TryLoad(candidate, assembly, path, out var handle)) return handle;
                return IntPtr.Zero; // default probing
            });
        }

        public async Task<bool> EnsureStartedAsync(bool fullscreen, CancellationToken ct = default)
        {
            if (!TryInitialize(_log)) return false;
            try
            {
                if (_libVlc == null)
                {
                    var options = new List<string> { "--no-video-title-show", "--image-duration=-1", "--network-caching=1500" };
                    // For testing on machines without audio: LOCALSTREAM_VLC_ARGS="--aout=dummy"
                    string? extra = Environment.GetEnvironmentVariable("LOCALSTREAM_VLC_ARGS");
                    if (!string.IsNullOrWhiteSpace(extra)) options.AddRange(extra.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                    _libVlc = new LibVLC(options.ToArray());
                    _player = new MediaPlayer(_libVlc)
                    {
                        EnableHardwareDecoding = true,
                        // Let our window handle the mouse and keyboard (full screen, shortcuts).
                        EnableMouseInput = false,
                        EnableKeyInput = false,
                    };
                }
            }
            catch (Exception ex)
            {
                _log.Write($"Built-in player failed to start: {ex.Message}");
                return false;
            }

            if (_windowOpen) return true;

            var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _window = new PlayerWindow(this, _player!);
                if (_nowPlaying != null) _window.SetNowPlaying(_nowPlaying, _art);
                _window.Closed += (_, _) => { _windowOpen = false; _window = null; };
                _window.VideoReady += () => opened.TrySetResult(true);
                if (fullscreen) _window.WindowState = WindowState.FullScreen;
                _window.Show();
                _window.Activate();
                _windowOpen = true;
            });

            // libvlc must have the video surface before playback starts, or it opens its own window.
            await Task.WhenAny(opened.Task, Task.Delay(3000, ct));
            return true;
        }

        public async Task PlayAsync(string url, string title, PlayOptions options, CancellationToken ct = default)
        {
            var player = _player;
            var libVlc = _libVlc;
            if (player == null || libVlc == null) return;
            await Dispatcher.UIThread.InvokeAsync(() => _window?.SetTitle(title));

            if (options.IsImage)
            {
                // Photos are shown by the window itself (with a fade), not by libvlc.
                _showingPhoto = true;
                var photo = await LoadPhotoAsync(url, ct);
                if (player.State is VLCState.Playing or VLCState.Paused or VLCState.Buffering) await Run(p => p.Stop());
                await Dispatcher.UIThread.InvokeAsync(() => _window?.ShowPhoto(photo));
                return;
            }

            _showingPhoto = false;
            // libvlc calls can block briefly; keep them off the UI thread.
            await Task.Run(() =>
            {
                using var media = new Media(libVlc, new Uri(url));
                // Subtitle size from Settings (VLC's own scale, 100 = normal).
                int scale = Math.Clamp(_subtitleScale(), 10, 500);
                if (scale != 100) media.AddOption($":sub-text-scale={scale}");
                string? subtitle = options.SubtitlePath != null ? new Uri(options.SubtitlePath).AbsoluteUri : options.SubtitleUrl;
                if (subtitle != null)
                {
                    try { media.AddSlave(MediaSlaveType.Subtitle, 4, subtitle); }
                    catch (Exception ex) { _log.Write($"Cast: subtitles not loaded ({ex.Message})."); }
                }
                player.Play(media);
            }, ct);
        }

        private async Task<Avalonia.Media.Imaging.Bitmap?> LoadPhotoAsync(string url, CancellationToken ct)
        {
            try
            {
                byte[] data;
                if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    // Next/previous photo in a folder on this PC (the receiver checked it's a shared folder).
                    string path = new Uri(url).LocalPath;
                    if (new FileInfo(path).Length > MaxPhotoBytes) return null;
                    data = await File.ReadAllBytesAsync(path, ct);
                }
                else
                {
                    using var response = await PhotoClient.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxPhotoBytes) return null;
                    data = await response.Content.ReadAsByteArrayAsync(ct);
                }
                // 4K is plenty for a screen and keeps memory reasonable for 50 MP phone photos.
                return await Task.Run(() => CoverArt.DecodePhoto(data, 3840), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Write($"Cast: photo couldn't be loaded ({ex.Message}).");
                return null;
            }
        }

        public async Task ShowResumeNoticeAsync(TimeSpan position)
            => await Dispatcher.UIThread.InvokeAsync(() => _window?.ShowResumeNotice(position));

        public async Task SetSlideshowAsync(bool running)
            => await Dispatcher.UIThread.InvokeAsync(() => _window?.SetSlideshow(running));

        public async Task SetNowPlayingAsync(NowPlayingInfo info)
        {
            await _nowPlayingGate.WaitAsync();
            try
            {
                // Decoding and blurring the cover happens here, off the UI thread, once per picture.
                bool newPicture = !ReferenceEquals(info.Artwork, _artSource);
                var art = newPicture ? await Task.Run(() => CoverArt.TryCreate(info.Artwork)) : _art;
                var old = newPicture ? _art : null;
                _nowPlaying = info;
                _art = art;
                _artSource = info.Artwork;
                await Dispatcher.UIThread.InvokeAsync(() => _window?.SetNowPlaying(info, art));
                old?.Dispose(); // no longer shown anywhere
            }
            finally
            {
                _nowPlayingGate.Release();
            }
        }

        public Task PauseAsync(CancellationToken ct = default) => Run(p => p.SetPause(true));
        public Task ResumeAsync(CancellationToken ct = default) => Run(p => p.SetPause(false));

        public async Task StopAsync(CancellationToken ct = default)
        {
            _showingPhoto = false;
            await Run(p => p.Stop());
            await Dispatcher.UIThread.InvokeAsync(() => _window?.CloseAfterStop());
        }

        public Task SeekAsync(TimeSpan position, CancellationToken ct = default)
            => Run(p =>
            {
                // Right after playback starts the stream may not be seekable yet; a seek then is ignored.
                for (int i = 0; i < 30 && !p.IsSeekable; i++) Thread.Sleep(100);
                if (p.IsSeekable) p.Time = (long)Math.Max(0, position.TotalMilliseconds);
            });

        public Task SetVolumeAsync(int volume, CancellationToken ct = default)
            => Run(p => p.Volume = Math.Clamp(volume, 0, 100));

        public async Task SetFullscreenAsync(bool fullscreen, CancellationToken ct = default)
            => await Dispatcher.UIThread.InvokeAsync(() => _window?.SetFullscreen(fullscreen));

        public async Task<PlayerStatus?> TryGetStatusAsync(CancellationToken ct = default)
        {
            var player = _player;
            if (player == null || !_windowOpen) return null;
            if (_showingPhoto)
            {
                bool fs = await Dispatcher.UIThread.InvokeAsync(() => _window?.IsFullscreen == true);
                return new PlayerStatus(PlayerState.Playing, TimeSpan.Zero, TimeSpan.Zero, Math.Clamp(player.Volume, 0, 100), fs, HasVideo: null);
            }

            var state = player.State switch
            {
                VLCState.Playing or VLCState.Buffering => PlayerState.Playing,
                VLCState.Paused => PlayerState.Paused,
                _ => PlayerState.Stopped, // NothingSpecial, Opening, Stopped, Ended, Error
            };
            bool fullscreen = await Dispatcher.UIThread.InvokeAsync(() => _window?.IsFullscreen == true);
            long time = Math.Max(0, player.Time);
            // Track info is reliable once playback has really started.
            bool? hasVideo = state == PlayerState.Playing && time > 700 ? player.VideoTrackCount > 0 : null;
            return new PlayerStatus(state,
                TimeSpan.FromMilliseconds(time),
                TimeSpan.FromMilliseconds(Math.Max(0, player.Length)),
                Math.Clamp(player.Volume, 0, 100),
                fullscreen,
                hasVideo);
        }

        // Called by PlayerWindow's own buttons.
        internal Task TogglePauseAsync() => Run(p =>
        {
            if (p.State == VLCState.Paused) p.SetPause(false);
            else if (p.State is VLCState.Playing or VLCState.Buffering) p.SetPause(true);
            else p.Play();
        });

        internal Task SeekByAsync(TimeSpan delta) => Run(p =>
        {
            if (!p.IsSeekable) return;
            long target = Math.Clamp(p.Time + (long)delta.TotalMilliseconds, 0, Math.Max(0, p.Length - 500));
            p.Time = target;
        });

        internal Task StopFromWindowAsync() { _showingPhoto = false; return Run(p => p.Stop()); }

        private Task Run(Action<MediaPlayer> action)
        {
            var player = _player;
            return player == null ? Task.CompletedTask : Task.Run(() =>
            {
                try { action(player); }
                catch (Exception ex) { _log.Write($"Player error: {ex.Message}"); }
            });
        }

        public async Task ShutdownAsync()
        {
            // Never call libvlc's Stop on the UI thread: it can wait for the video output, which
            // may need the UI thread - a deadlock. Run it in the background, with a time limit.
            var player = _player;
            if (player != null)
            {
                var stop = Task.Run(() => { try { player.Stop(); } catch { } });
                await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(3)));
            }
            if (Dispatcher.UIThread.CheckAccess()) _window?.CloseAfterStop();
            else await Dispatcher.UIThread.InvokeAsync(() => _window?.CloseAfterStop());
        }

        public void Dispose()
        {
            _player?.Dispose();
            _libVlc?.Dispose();
            _player = null;
            _libVlc = null;
        }
    }
}
