using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using LocalStreamPC.Server;
using LocalStreamPC.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LocalStreamPC.Cast
{
    public enum TransportState { NoMediaPresent, Stopped, Transitioning, Playing, Paused }

    public sealed record CastSnapshot(
        bool IsReceiving, TransportState State, string Title, string? Sender,
        TimeSpan Position, TimeSpan Duration, int Volume, bool Muted, bool Fullscreen, bool HasMedia,
        string? Artist = null, string? Album = null, byte[]? Artwork = null, bool IsAudio = false,
        string? UpNext = null, bool CanPrevious = false, bool CanNext = false, bool IsImage = false, bool Slideshow = false);

    /// <summary>
    /// Makes this PC a cast target ("LocalStream Player") using the open DLNA / UPnP MediaRenderer
    /// standard. Phones, tablets, PCs and apps that can "cast to TV" over DLNA (e.g. BubbleUPnP,
    /// Web Video Caster, LocalCast, Windows "Cast to Device", Kodi) find it automatically and can
    /// play, pause, seek, change volume and stop. Media plays in LocalStream's built-in player
    /// (LibVLC) or, if that's turned off or unavailable, in an external VLC window.
    ///
    /// Only devices on the local network can control it, and it only plays http(s) URLs.
    /// </summary>
    public sealed class CastReceiver : IAsyncDisposable
    {
        private static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(25);
        private static readonly HttpClient EventClient = new() { Timeout = TimeSpan.FromSeconds(3) };

        private readonly ConfigService _config;
        private readonly AppLog _log;
        private readonly VlcController _externalVlc;
        private readonly EmbeddedPlayer _builtIn;
        private IPlaybackEngine _player;
        private readonly SemaphoreSlim _op = new(1, 1);
        private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new();

        private readonly SsdpService _ssdp;
        private WebApplication? _app;
        private IAsyncDisposable? _ssdpRegistration;
        private CancellationTokenSource? _cts;
        private Task? _pollTask;

        // Transport state (guarded by _op)
        private TransportState _state = TransportState.NoMediaPresent;
        private string _uri = "", _uriMetadata = "", _nextUri = "", _nextMetadata = "";
        private string _title = "";
        private CastMediaInfo? _media;
        private string? _artist, _album;
        private byte[]? _artwork;
        private bool _isAudio;
        private CancellationTokenSource? _artCts;
        private readonly ArtworkFetcher _artFetcher;
        private string? _sender;
        private TimeSpan _position, _duration;
        private int _volume = 100;
        private bool _muted, _fullscreen;
        private DateTime _playRequestedAt = DateTime.MinValue;
        private bool _vlcStartedWithFullscreen;

        private string _lastAvtEvent = "", _lastRcsEvent = "";

        // Queue: what played before (for Previous), photos in the same folder on this PC.
        private readonly List<(string Uri, string Metadata)> _played = new();
        private const int MaxPlayed = 30;
        private bool _isImage;
        private List<string>? _photoSiblings;   // full paths, when the photo is a file on this PC
        private int _photoIndex = -1;
        private bool _slideshow;
        private DateTime _slideShownAt;
        private int _artVersion;

        // Resume
        private double _pendingResume;
        private DateTime _lastResumeSave = DateTime.MinValue, _lastConfigSave = DateTime.MinValue;
        private static readonly HttpClient SubtitleProbe = new() { Timeout = TimeSpan.FromSeconds(2) };

        public CastReceiver(ConfigService config, AppLog log, SsdpService ssdp)
        {
            _config = config;
            _log = log;
            _ssdp = ssdp;
            _externalVlc = new VlcController(() => config.Current.VlcPath, log);
            _builtIn = new EmbeddedPlayer(log, () => config.Current.CastSubtitleScale);
            _artFetcher = new ArtworkFetcher(log);
            _player = _builtIn;
            _builtIn.CommandRequested += command => _ = HandleCommandAsync(command);
            _builtIn.CoverUrl = () => _artwork != null && _app != null ? $"http://127.0.0.1:{_config.Current.CastPort}/nowplaying/cover?v={_artVersion}" : null;
        }

        public bool IsReceiving => _app != null;

        /// <summary>Raised (on any thread) when anything shown in the UI may have changed.</summary>
        public event Action? Changed;

        public CastSnapshot Snapshot => new(IsReceiving, _state, _title, _sender, _position, _duration, _volume, _muted, _fullscreen,
                                            _state is not TransportState.NoMediaPresent && _uri.Length > 0, _artist, _album, _artwork, _isAudio,
                                            UpNextTitle, CanPrevious, CanNext, _isImage, _slideshow);

        // ================================================================== lifecycle

        public async Task<bool> StartAsync(IReadOnlyList<(string Ip, string Name)> localIps)
        {
            if (_app != null) return true;
            int port = _config.Current.CastPort;

            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory, ApplicationName = "LocalStreamPC" });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => { k.ListenAnyIP(port); k.AddServerHeader = false; });
            var app = builder.Build();
            MapRoutes(app);

            try
            {
                await app.StartAsync();
            }
            catch (Exception ex)
            {
                _log.Write($"Cast receiver could not start on port {port}: {ex.Message}");
                await app.DisposeAsync();
                return false;
            }

            _app = app;
            _cts = new CancellationTokenSource();
            _ssdpRegistration = await _ssdp.RegisterAsync(new SsdpDevice(
                RendererDescriptions.DeviceType,
                () => _config.Current.RendererUuid,
                () => _config.Current.CastPort,
                "/description.xml",
                new[] { RendererDescriptions.AvTransport, RendererDescriptions.RenderingControl, RendererDescriptions.ConnectionManager }), localIps);
            _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));

            _log.Write($"Cast receiver on: other devices can now cast to \"{RendererDescriptions.FriendlyName}\".");
            Changed?.Invoke();
            return true;
        }

        public async Task StopAsync()
        {
            if (_app == null) return;
            _cts?.Cancel();
            if (_pollTask != null) { try { await _pollTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { } }
            if (_ssdpRegistration != null) { await _ssdpRegistration.DisposeAsync(); _ssdpRegistration = null; }
            try { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await _app.StopAsync(stop.Token); } catch { }
            await _app.DisposeAsync();
            _app = null;
            _cts?.Dispose();
            _cts = null;
            _subscriptions.Clear();

            SaveResumePoint(force: true);
            await _builtIn.ShutdownAsync();
            await _externalVlc.ShutdownAsync();
            _state = TransportState.NoMediaPresent;
            _played.Clear();
            _photoSiblings = null;
            _slideshow = false;
            _uri = _nextUri = _title = "";
            _sender = null;
            _artCts?.Cancel();
            _artCts = null;
            _media = null;
            _artwork = null;
            _artist = _album = null;
            _isAudio = false;
            _position = _duration = TimeSpan.Zero;
            _log.Write("Cast receiver off.");
            Changed?.Invoke();
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _builtIn.Dispose();
            _externalVlc.Dispose();
        }

        /// <summary>Call after settings change: player choice and full screen apply to the next cast.</summary>
        public async Task ApplySettingsAsync()
        {
            bool wantFullscreen = _config.Current.CastFullscreen;
            await _op.WaitAsync();
            try
            {
                bool busy = _state is TransportState.Playing or TransportState.Paused or TransportState.Transitioning;
                if (!_player.IsRunning) return;
                if (busy) { if (wantFullscreen != _vlcStartedWithFullscreen) await _player.SetFullscreenAsync(wantFullscreen); }
                else await _player.ShutdownAsync(); // the next cast starts with the new settings
            }
            finally { _op.Release(); }
        }

        /// <summary>The built-in player when enabled and libvlc is available, otherwise external VLC.</summary>
        private IPlaybackEngine ChoosePlayer()
            => _config.Current.UseBuiltInPlayer && EmbeddedPlayer.TryInitialize(_log) ? _builtIn : _externalVlc;

        // ================================================================== controls used by the app UI

        public Task PlayPauseAsync() => WithLockAsync(async () =>
        {
            if (_state == TransportState.Playing) await PauseCoreAsync();
            else if (_state is TransportState.Paused or TransportState.Stopped) await PlayCoreAsync();
        });

        public Task StopPlaybackAsync() => WithLockAsync(StopCoreAsync);
        public Task NextAsync() => HandleCommandAsync(PlayerCommand.Next);
        public Task PreviousAsync() => HandleCommandAsync(PlayerCommand.Previous);
        public Task SeekAsync(TimeSpan position) => WithLockAsync(() => SeekCoreAsync(position));
        public Task SetVolumeAsync(int volume) => WithLockAsync(() => SetVolumeCoreAsync(volume));

        public Task SetFullscreenAsync(bool fullscreen) => WithLockAsync(async () =>
        {
            await _player.SetFullscreenAsync(fullscreen);
            _fullscreen = fullscreen;
        });

        private async Task WithLockAsync(Func<Task> action)
        {
            await _op.WaitAsync();
            try { await action(); }
            finally { _op.Release(); }
            AfterChange();
        }

        // ================================================================== transport core (call with _op held)

        private async Task SetUriCoreAsync(string uri, string metadata, string? sender)
        {
            if (string.IsNullOrWhiteSpace(uri))
            {
                await _player.StopAsync();
                _uri = _uriMetadata = _title = "";
                _state = TransportState.NoMediaPresent;
                return;
            }
            if (!Uri.TryCreate(uri.Trim(), UriKind.Absolute, out var parsed) || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
                throw new UpnpException(716, "Only http(s) media URLs can be played");

            bool wasPlaying = _state is TransportState.Playing or TransportState.Transitioning;
            if (_uri.Length > 0 && _uri != parsed.AbsoluteUri) Remember(_uri, _uriMetadata);
            SaveResumePoint(force: true);
            _uri = parsed.AbsoluteUri;
            _uriMetadata = metadata ?? "";
            SetMedia(CastMediaInfo.Parse(_uri, _uriMetadata));
            _sender = sender;
            _position = _duration = TimeSpan.Zero;
            _log.Write($"Cast: \"{_title}\" sent from {sender ?? "a device"}.");

            // UPnP: a new URI while playing starts playing it; otherwise wait for Play.
            if (wasPlaying) await PlayCoreAsync();
            else
            {
                if (_state == TransportState.Paused) await _player.StopAsync();
                _state = TransportState.Stopped;
            }
        }

        private async Task PlayCoreAsync()
        {
            if (_uri.Length == 0) throw new UpnpException(701, "No media to play");

            if (_state == TransportState.Paused)
            {
                await _player.ResumeAsync();
                _state = TransportState.Playing;
                return;
            }

            if (!await StartVlcAsync())
            {
                _state = TransportState.Stopped;
                throw new UpnpException(704, "Playing failed: no media player is available on this PC");
            }
            await StartCurrentAsync();
        }

        /// <summary>Starts the current URI from the beginning (or its resume point), with subtitles.</summary>
        private async Task StartCurrentAsync()
        {
            var media = _media ?? CastMediaInfo.Parse(_uri, _uriMetadata);
            await _player.SetNowPlayingAsync(NowPlaying);
            var options = new PlayOptions(IsImage: _isImage);
            if (!_isImage && !_isAudio) options = await FindSubtitlesAsync(media);
            _pendingResume = 0;
            if (!_isImage && !_isAudio && _config.Current.CastResume)
            {
                var point = _config.Current.CastResumePoints.FirstOrDefault(p => p.Key == media.ResumeKey);
                if (point != null && point.Seconds > 30) _pendingResume = point.Seconds;
            }
            await _player.PlayAsync(_uri, _title, options);
            _state = _isImage ? TransportState.Playing : TransportState.Transitioning;
            _playRequestedAt = DateTime.UtcNow;
            _slideShownAt = DateTime.UtcNow;
            _position = TimeSpan.Zero;
        }

        private async Task<bool> StartVlcAsync()
        {
            bool wasRunning = _player.IsRunning;
            if (!wasRunning)
            {
                _player = ChoosePlayer();
                _vlcStartedWithFullscreen = _config.Current.CastFullscreen;
            }
            if (!await _player.EnsureStartedAsync(_vlcStartedWithFullscreen))
            {
                if (_player != _builtIn) return false;
                _log.Write("Cast: using the external VLC player instead.");
                _player = _externalVlc;
                if (!await _player.EnsureStartedAsync(_vlcStartedWithFullscreen)) return false;
            }
            if (!wasRunning) await _player.SetVolumeAsync(_muted ? 0 : _volume);
            return true;
        }

        private async Task PauseCoreAsync()
        {
            if (_state is not (TransportState.Playing or TransportState.Transitioning)) throw new UpnpException(701, "Transition not available");
            await _player.PauseAsync();
            _state = TransportState.Paused;
        }

        private async Task StopCoreAsync()
        {
            if (_state == TransportState.NoMediaPresent) return;
            SaveResumePoint(force: true);
            _slideshow = false;
            await _player.StopAsync();
            _state = TransportState.Stopped;
            _position = TimeSpan.Zero;
        }

        private async Task SeekCoreAsync(TimeSpan target)
        {
            if (_state is not (TransportState.Playing or TransportState.Paused or TransportState.Transitioning))
                throw new UpnpException(701, "Transition not available");
            if (_duration > TimeSpan.Zero && target > _duration) target = _duration;
            await _player.SeekAsync(target);
            _position = target;
        }

        private async Task SetVolumeCoreAsync(int volume)
        {
            _volume = Math.Clamp(volume, 0, 100);
            if (_muted && volume > 0) _muted = false;
            await _player.SetVolumeAsync(_muted ? 0 : _volume);
        }

        // ================================================================== now playing (music covers)

        private NowPlayingInfo NowPlaying => new(_title, _artist, _album, _artwork, _isAudio, UpNextTitle, CanPrevious, CanNext, _isImage);

        private string? UpNextTitle
        {
            get
            {
                if (_isImage && _photoSiblings != null) return null;
                if (_nextUri.Length == 0) return null;
                return CastMediaInfo.Parse(_nextUri, _nextMetadata).Title;
            }
        }

        private bool CanNext => _nextUri.Length > 0 || (_isImage && _photoSiblings != null && _photoIndex < _photoSiblings.Count - 1)
                                || (_isImage && _slideshow && _photoSiblings is { Count: > 1 });
        // Video and music can always go "previous" (back to the start, then the item before).
        private bool CanPrevious => _played.Count > 0 || (_isImage ? _photoSiblings != null && _photoIndex > 0 : _uri.Length > 0);

        /// <summary>New media: reset the cover and, for music, start looking for one.</summary>
        private void SetMedia(CastMediaInfo media)
        {
            _media = media;
            _title = media.Title;
            _artist = media.Artist;
            _album = media.Album;
            _artwork = null;
            _artVersion++;
            _isImage = media.LooksLikeImage;
            _isAudio = !_isImage && media.LooksLikeAudio;
            if (_isImage) FindPhotoSiblings(media);
            else { _photoSiblings = null; _slideshow = false; }
            _artCts?.Cancel();
            _artCts = null;
            if (_isAudio) StartArtworkLookup();
        }

        private void StartArtworkLookup()
        {
            var media = _media;
            if (media == null || _artCts != null) return;
            var cts = _artCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            _ = Task.Run(async () =>
            {
                var (art, artist, album, tagTitle) = await _artFetcher.FindAsync(media, cts.Token);
                await _op.WaitAsync();
                try
                {
                    if (!ReferenceEquals(media, _media)) return; // something else is playing now
                    _artwork = art;
                    _artVersion++;
                    _artist ??= artist;
                    _album ??= album;
                    if (!media.TitleFromSender && tagTitle != null) _title = tagTitle;
                    await _player.SetNowPlayingAsync(NowPlaying);
                }
                finally { _op.Release(); }
                Changed?.Invoke();
            });
        }

        // ================================================================== watching VLC

        private async Task PollLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await _op.WaitAsync(token);
                    try { await PollOnceAsync(token); }
                    finally { _op.Release(); }
                    AfterChange();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.Write($"Cast status error: {ex.Message}"); }

                try { await Task.Delay(1000, token); } catch (OperationCanceledException) { break; }
            }
        }

        private async Task PollOnceAsync(CancellationToken token)
        {
            if (_state is TransportState.NoMediaPresent or TransportState.Stopped) return;

            var status = await _player.TryGetStatusAsync(token);
            if (status == null)
            {
                if (!_player.IsRunning) // the user closed VLC
                {
                    _state = TransportState.Stopped;
                    _position = TimeSpan.Zero;
                }
                return;
            }

            _fullscreen = status.Fullscreen;

            // The built-in player can tell whether there's any picture. Music sent without good
            // metadata is found here; a "song" that turns out to be a video switches back.
            if (!_isImage && status.HasVideo is bool hasVideo && hasVideo == _isAudio)
            {
                _isAudio = !hasVideo;
                if (_isAudio) StartArtworkLookup();
                await _player.SetNowPlayingAsync(NowPlaying);
            }

            if (status.State == PlayerState.Playing && _state == TransportState.Transitioning)
                await _player.SetVolumeAsync(_muted ? 0 : _volume); // libvlc ignores volume set before playback starts
            else if (_player.ReportsVolume && !_muted && _state is TransportState.Playing or TransportState.Paused)
                _volume = status.Volume; // may have been changed in the player window
            if (status.Duration > TimeSpan.Zero) _duration = status.Duration;
            _position = status.Position;

            // Continue where this video was left last time (once it's really playing and its length is known).
            if (_pendingResume > 0 && status.State == PlayerState.Playing && _duration > TimeSpan.Zero)
            {
                double at = _pendingResume;
                _pendingResume = 0;
                if (_duration.TotalSeconds > 120 && at < _duration.TotalSeconds - 30)
                {
                    await _player.SeekAsync(TimeSpan.FromSeconds(at));
                    _position = TimeSpan.FromSeconds(at);
                    await _player.ShowResumeNoticeAsync(_position);
                    _log.Write($"Cast: continuing \"{_title}\" at {UpnpXml.FormatTime(_position)}.");
                }
            }
            if (status.State == PlayerState.Playing) SaveResumePoint(force: false);

            // Photo slideshow
            if (_isImage && _slideshow && DateTime.UtcNow - _slideShownAt >= TimeSpan.FromSeconds(_config.Current.SlideshowSeconds))
            {
                if (!await StepAsync(+1, wrap: true)) { _slideshow = false; await _player.SetSlideshowAsync(false); }
            }

            switch (status.State)
            {
                case PlayerState.Playing:
                    _state = TransportState.Playing;
                    break;
                case PlayerState.Paused:
                    _state = TransportState.Paused;
                    break;
                case PlayerState.Stopped:
                    var sincePlay = DateTime.UtcNow - _playRequestedAt;
                    if (_state == TransportState.Transitioning)
                    {
                        if (sincePlay > GiveUpAfter)
                        {
                            _log.Write($"Cast: VLC couldn't play \"{_title}\".");
                            _state = TransportState.Stopped;
                        }
                    }
                    else if (_state is TransportState.Playing or TransportState.Paused && sincePlay > StartGrace)
                    {
                        // Finished (or closed). Continue with the queued next item, if any.
                        if (_nextUri.Length > 0)
                        {
                            SaveResumePoint(force: true);
                            Remember(_uri, _uriMetadata);
                            _uri = _nextUri; _uriMetadata = _nextMetadata;
                            _nextUri = _nextMetadata = "";
                            SetMedia(CastMediaInfo.Parse(_uri, _uriMetadata));
                            _duration = TimeSpan.Zero;
                            await StartCurrentAsync();
                        }
                        else
                        {
                            _state = TransportState.Stopped;
                            _position = TimeSpan.Zero;
                            await _player.StopAsync(); // the built-in player closes its window
                        }
                    }
                    break;
            }
        }

        // ================================================================== queue, photos, resume, subtitles

        /// <summary>Player window buttons / Windows media keys.</summary>
        private async Task HandleCommandAsync(PlayerCommand command)
        {
            await _op.WaitAsync();
            try
            {
                switch (command)
                {
                    case PlayerCommand.Next: await StepAsync(+1, wrap: _slideshow); break;
                    case PlayerCommand.Previous: await StepAsync(-1, wrap: false); break;
                    case PlayerCommand.ToggleSlideshow when _isImage:
                        _slideshow = !_slideshow && (_photoSiblings is { Count: > 1 } || _nextUri.Length > 0);
                        _slideShownAt = DateTime.UtcNow;
                        await _player.SetSlideshowAsync(_slideshow);
                        await _player.SetNowPlayingAsync(NowPlaying);
                        break;
                    case PlayerCommand.StartOver:
                        _config.Current.CastResumePoints.RemoveAll(p => p.Key == _media?.ResumeKey);
                        if (_state is TransportState.Playing or TransportState.Paused) await SeekCoreAsync(TimeSpan.Zero);
                        break;
                }
            }
            catch (Exception ex) { _log.Write($"Cast: {ex.Message}"); }
            finally { _op.Release(); }
            AfterChange();
        }

        /// <summary>Next (+1) or previous (-1): photos in the same folder, the queued next item, or what played before.</summary>
        private async Task<bool> StepAsync(int direction, bool wrap)
        {
            if (_uri.Length == 0) return false;

            if (_isImage && _photoSiblings is { Count: > 0 } siblings)
            {
                int i = _photoIndex + direction;
                if (wrap && i >= siblings.Count) i = 0;
                if (i >= 0 && i < siblings.Count)
                {
                    _photoIndex = i;
                    string path = siblings[i];
                    _uri = new Uri(path).AbsoluteUri; // shown straight from the disk
                    _uriMetadata = "";
                    _title = Path.GetFileNameWithoutExtension(path);
                    _media = _media! with { Url = _uri, Title = _title };
                    await StartCurrentAsync();
                    return true;
                }
                if (direction > 0 && _nextUri.Length == 0) return false;
            }

            if (direction > 0)
            {
                if (_nextUri.Length == 0) return false;
                SaveResumePoint(force: true);
                Remember(_uri, _uriMetadata);
                _uri = _nextUri; _uriMetadata = _nextMetadata;
                _nextUri = _nextMetadata = "";
                SetMedia(CastMediaInfo.Parse(_uri, _uriMetadata));
                _duration = TimeSpan.Zero;
                if (!await StartVlcAsync()) return false;
                await StartCurrentAsync();
                return true;
            }

            // Previous: back to the start first, like any music player; then the item before.
            if (!_isImage && _position > TimeSpan.FromSeconds(5))
            {
                await SeekCoreAsync(TimeSpan.Zero);
                return true;
            }
            if (_played.Count == 0) return false;
            var (uri, metadata) = _played[^1];
            _played.RemoveAt(_played.Count - 1);
            SaveResumePoint(force: true);
            // The current item becomes "next" again, so Next returns to it.
            _nextUri = _uri; _nextMetadata = _uriMetadata;
            _uri = uri; _uriMetadata = metadata;
            SetMedia(CastMediaInfo.Parse(_uri, _uriMetadata));
            _duration = TimeSpan.Zero;
            if (!await StartVlcAsync()) return false;
            await StartCurrentAsync();
            return true;
        }

        private void Remember(string uri, string metadata)
        {
            if (uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return; // photo from the folder
            if (_played.Count > 0 && _played[^1].Uri == uri) return;
            _played.Add((uri, metadata));
            if (_played.Count > MaxPlayed) _played.RemoveAt(0);
        }

        /// <summary>A photo that is a file on this PC: the other photos in its folder, for next/previous and the slideshow.</summary>
        private void FindPhotoSiblings(CastMediaInfo media)
        {
            _photoSiblings = null;
            _photoIndex = -1;
            string? path = media.Url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new Uri(media.Url).LocalPath : CastMediaInfo.PathFromLocalStreamUrl(media.Url);
            if (path == null || !File.Exists(path)) return;
            // Only folders this PC shares (the URL came from our own server).
            if (!PathGuard.TryResolve(path, _config.SharedRoots, out path)) return;
            try
            {
                var list = Directory.EnumerateFiles(Path.GetDirectoryName(path)!)
                    .Where(f => MediaTypes.Image.Contains(Path.GetExtension(f)))
                    .OrderBy(f => Path.GetFileName(f), StringComparer.CurrentCultureIgnoreCase).ToList();
                int index = list.FindIndex(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
                if (index < 0) return;
                _photoSiblings = list;
                _photoIndex = index;
            }
            catch { }
        }

        /// <summary>Subtitles the sender gave, or a matching .srt next to the video (on this PC, or at the same web address).</summary>
        private async Task<PlayOptions> FindSubtitlesAsync(CastMediaInfo media)
        {
            if (media.SubtitleUrl != null) return new PlayOptions(SubtitleUrl: media.SubtitleUrl);
            string? local = CastMediaInfo.PathFromLocalStreamUrl(media.Url);
            bool convertedFromMiddle = media.Url.Contains("/stream/") && media.Url.Contains("start=");
            if (local != null && File.Exists(local) && !convertedFromMiddle && PathGuard.TryResolve(local, _config.SharedRoots, out local))
            {
                string? match = SubtitleMatcher.FindBestMatch(local);
                if (match != null) { _log.Write($"Cast: subtitles from {Path.GetFileName(match)}."); return new PlayOptions(SubtitlePath: match); }
                return new PlayOptions();
            }
            // "http://nas/movies/film.mkv" -> try "film.srt" beside it.
            if (local == null && Uri.TryCreate(media.Url, UriKind.Absolute, out var u) && MediaTypes.Video.Contains(Path.GetExtension(u.AbsolutePath)))
            {
                var guess = new UriBuilder(u) { Path = Path.ChangeExtension(u.AbsolutePath, ".srt") }.Uri;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Head, guess);
                    using var response = await SubtitleProbe.SendAsync(request);
                    if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength is null or < 4_000_000)
                        return new PlayOptions(SubtitleUrl: guess.AbsoluteUri);
                }
                catch { }
            }
            return new PlayOptions();
        }

        /// <summary>Remembers where a video is (every 10 s while playing, and when it stops or changes).</summary>
        private void SaveResumePoint(bool force)
        {
            if (_media == null || _isAudio || _isImage || !_config.Current.CastResume) return;
            if (!force && DateTime.UtcNow - _lastResumeSave < TimeSpan.FromSeconds(10)) return;
            double pos = _position.TotalSeconds, dur = _duration.TotalSeconds;
            if (dur < 180 || _pendingResume > 0) return; // short clips aren't worth resuming
            _lastResumeSave = DateTime.UtcNow;
            var points = _config.Current.CastResumePoints;
            string key = _media.ResumeKey;
            points.RemoveAll(p => p.Key == key || DateTime.UtcNow - p.Updated > TimeSpan.FromDays(180));
            bool nearEnd = pos > dur * 0.95 || dur - pos < 60;
            if (pos > 30 && !nearEnd) points.Add(new Models.ResumePoint { Key = key, Seconds = pos, Duration = dur, Updated = DateTime.UtcNow });
            if (points.Count > 200) points.RemoveRange(0, points.Count - 200);
            if (force || DateTime.UtcNow - _lastConfigSave > TimeSpan.FromMinutes(1))
            {
                _lastConfigSave = DateTime.UtcNow;
                _config.Save();
            }
        }

        private void AfterChange()
        {
            Changed?.Invoke();
            _ = SendEventsIfChangedAsync();
        }

        // ================================================================== HTTP / SOAP

        private void MapRoutes(WebApplication app)
        {
            app.MapGet("/description.xml", Local(ctx => Xml(ctx, RendererDescriptions.DeviceDescription(_config.Current.RendererUuid))));
            app.MapGet("/scpd/AVTransport.xml", Local(ctx => Xml(ctx, RendererDescriptions.AvTransportScpd)));
            app.MapGet("/scpd/RenderingControl.xml", Local(ctx => Xml(ctx, RendererDescriptions.RenderingControlScpd)));
            app.MapGet("/scpd/ConnectionManager.xml", Local(ctx => Xml(ctx, RendererDescriptions.ConnectionManagerScpd)));
            app.MapPost("/control/{service}", Local(HandleControlAsync));
            app.MapMethods("/event/{service}", new[] { "SUBSCRIBE" }, Local(HandleSubscribeAsync));
            app.MapMethods("/event/{service}", new[] { "UNSUBSCRIBE" }, Local(HandleUnsubscribe));
            // The cover for Windows' media overlay (it downloads the picture itself). Only this PC may ask.
            app.MapGet("/nowplaying/cover", async ctx =>
            {
                var art = _artwork;
                bool loopback = ctx.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip);
                if (!loopback || art == null) { ctx.Response.StatusCode = 404; return; }
                ctx.Response.ContentType = art.Length > 3 && art[0] == 0x89 ? "image/png" : "image/jpeg";
                await ctx.Response.Body.WriteAsync(art);
            });
        }

        /// <summary>Only devices on the local network (or this PC) may use the receiver.</summary>
        private RequestDelegate Local(Func<HttpContext, Task> handler) => async ctx =>
        {
            if (!NetworkHelper.IsLocalClient(ctx.Connection.RemoteIpAddress, ctx.Connection.LocalIpAddress))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            try { await handler(ctx); }
            catch (Exception ex) when (!ctx.RequestAborted.IsCancellationRequested)
            {
                _log.Write($"Cast request error ({ctx.Request.Path}): {ex.Message}");
                if (!ctx.Response.HasStarted) ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }
            catch (OperationCanceledException) { }
        };

        private RequestDelegate Local(Action<HttpContext> handler) => Local(ctx => { handler(ctx); return Task.CompletedTask; });

        private static Task Xml(HttpContext ctx, string xml)
        {
            ctx.Response.ContentType = "text/xml; charset=\"utf-8\"";
            return ctx.Response.WriteAsync(xml, Encoding.UTF8);
        }

        private async Task HandleControlAsync(HttpContext ctx)
        {
            string service = (string)ctx.Request.RouteValues["service"]!;
            string body;
            using (var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8))
                body = await reader.ReadToEndAsync(ctx.RequestAborted);

            string serviceType = service switch
            {
                "AVTransport" => RendererDescriptions.AvTransport,
                "RenderingControl" => RendererDescriptions.RenderingControl,
                "ConnectionManager" => RendererDescriptions.ConnectionManager,
                _ => "",
            };
            if (serviceType.Length == 0) { ctx.Response.StatusCode = 404; return; }

            string sender = ctx.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "?";
            string response;
            try
            {
                var req = UpnpXml.ParseSoap(body);
                if (req.Args.TryGetValue("InstanceID", out var instance) && instance.Trim() != "0")
                    throw new UpnpException(718, "Invalid InstanceID");

                IEnumerable<(string, string)> outArgs;
                await _op.WaitAsync(ctx.RequestAborted);
                try
                {
                    outArgs = service switch
                    {
                        "AVTransport" => await AvTransportAsync(req, sender),
                        "RenderingControl" => await RenderingControlAsync(req),
                        _ => ConnectionManagerAction(req),
                    };
                }
                finally { _op.Release(); }
                AfterChange();
                response = UpnpXml.Response(serviceType, req.Action, outArgs);
            }
            catch (UpnpException ex)
            {
                ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                response = UpnpXml.Fault(ex.Code, ex.Message);
            }

            ctx.Response.ContentType = "text/xml; charset=\"utf-8\"";
            ctx.Response.Headers["EXT"] = "";
            await ctx.Response.WriteAsync(response, Encoding.UTF8);
        }

        private async Task<IEnumerable<(string, string)>> AvTransportAsync(SoapRequest req, string sender)
        {
            switch (req.Action)
            {
                case "SetAVTransportURI":
                    await SetUriCoreAsync(req.Arg("CurrentURI"), req.Arg("CurrentURIMetaData"), sender);
                    return None;

                case "SetNextAVTransportURI":
                    string next = req.Arg("NextURI").Trim();
                    if (next.Length > 0 && (!Uri.TryCreate(next, UriKind.Absolute, out var n) || (n.Scheme != Uri.UriSchemeHttp && n.Scheme != Uri.UriSchemeHttps)))
                        throw new UpnpException(716, "Only http(s) media URLs can be played");
                    _nextUri = next;
                    _nextMetadata = req.Arg("NextURIMetaData");
                    if (_player.IsRunning) await _player.SetNowPlayingAsync(NowPlaying); // show "Up next"
                    return None;

                case "Play":
                    string speed = req.Arg("Speed").Trim();
                    if (speed.Length > 0 && speed != "1") throw new UpnpException(717, "Play speed not supported");
                    await PlayCoreAsync();
                    return None;

                case "Pause":
                    await PauseCoreAsync();
                    return None;

                case "Stop":
                    await StopCoreAsync();
                    return None;

                case "Seek":
                    string unit = req.Arg("Unit");
                    if (unit is not ("REL_TIME" or "ABS_TIME")) throw new UpnpException(710, "Seek mode not supported");
                    if (!UpnpXml.TryParseTime(req.Arg("Target"), out var target)) throw new UpnpException(711, "Illegal seek target");
                    await SeekCoreAsync(target);
                    return None;

                case "Next":
                    if (!await StepAsync(+1, wrap: false)) throw new UpnpException(701, "Transition not available");
                    return None;

                case "Previous":
                    if (!await StepAsync(-1, wrap: false)) throw new UpnpException(701, "Transition not available");
                    return None;

                case "GetTransportInfo":
                    return new[] { ("CurrentTransportState", StateName(_state)), ("CurrentTransportStatus", "OK"), ("CurrentSpeed", "1") };

                case "GetPositionInfo":
                    bool hasMedia = _uri.Length > 0;
                    string rel = UpnpXml.FormatTime(_position);
                    return new[]
                    {
                        ("Track", hasMedia ? "1" : "0"),
                        ("TrackDuration", UpnpXml.FormatTime(_duration)),
                        ("TrackMetaData", _uriMetadata),
                        ("TrackURI", _uri),
                        ("RelTime", rel),
                        ("AbsTime", rel),
                        ("RelCount", "2147483647"),
                        ("AbsCount", "2147483647"),
                    };

                case "GetMediaInfo":
                    return new[]
                    {
                        ("NrTracks", _uri.Length > 0 ? "1" : "0"),
                        ("MediaDuration", UpnpXml.FormatTime(_duration)),
                        ("CurrentURI", _uri),
                        ("CurrentURIMetaData", _uriMetadata),
                        ("NextURI", _nextUri),
                        ("NextURIMetaData", _nextMetadata),
                        ("PlayMedium", _uri.Length > 0 ? "NETWORK" : "NONE"),
                        ("RecordMedium", "NOT_IMPLEMENTED"),
                        ("WriteStatus", "NOT_IMPLEMENTED"),
                    };

                case "GetDeviceCapabilities":
                    return new[] { ("PlayMedia", "NETWORK"), ("RecMedia", "NOT_IMPLEMENTED"), ("RecQualityModes", "NOT_IMPLEMENTED") };

                case "GetTransportSettings":
                    return new[] { ("PlayMode", "NORMAL"), ("RecQualityMode", "NOT_IMPLEMENTED") };

                case "GetCurrentTransportActions":
                    return new[] { ("Actions", CurrentActions()) };

                default:
                    throw new UpnpException(401, "Invalid Action");
            }
        }

        private async Task<IEnumerable<(string, string)>> RenderingControlAsync(SoapRequest req)
        {
            switch (req.Action)
            {
                case "GetVolume":
                    return new[] { ("CurrentVolume", _volume.ToString()) };
                case "SetVolume":
                    if (!int.TryParse(req.Arg("DesiredVolume"), out int volume) || volume < 0 || volume > 100)
                        throw new UpnpException(402, "Invalid Args");
                    await SetVolumeCoreAsync(volume);
                    return None;
                case "GetMute":
                    return new[] { ("CurrentMute", _muted ? "1" : "0") };
                case "SetMute":
                    string m = req.Arg("DesiredMute").Trim();
                    _muted = m is "1" or "true" or "True" or "yes";
                    await _player.SetVolumeAsync(_muted ? 0 : _volume);
                    return None;
                case "ListPresets":
                    return new[] { ("CurrentPresetNameList", "FactoryDefaults") };
                case "SelectPreset":
                    return None;
                default:
                    throw new UpnpException(401, "Invalid Action");
            }
        }

        private static IEnumerable<(string, string)> ConnectionManagerAction(SoapRequest req) => req.Action switch
        {
            "GetProtocolInfo" => new[] { ("Source", ""), ("Sink", RendererDescriptions.SinkProtocolInfo) },
            "GetCurrentConnectionIDs" => new[] { ("ConnectionIDs", "0") },
            "GetCurrentConnectionInfo" => new[]
            {
                ("RcsID", "0"), ("AVTransportID", "0"), ("ProtocolInfo", ""), ("PeerConnectionManager", ""),
                ("PeerConnectionID", "-1"), ("Direction", "Input"), ("Status", "OK"),
            },
            _ => throw new UpnpException(401, "Invalid Action"),
        };

        private static readonly (string, string)[] None = Array.Empty<(string, string)>();

        private static string StateName(TransportState s) => s switch
        {
            TransportState.Playing => "PLAYING",
            TransportState.Paused => "PAUSED_PLAYBACK",
            TransportState.Transitioning => "TRANSITIONING",
            TransportState.Stopped => "STOPPED",
            _ => "NO_MEDIA_PRESENT",
        };

        private string CurrentActions() => _state switch
        {
            TransportState.Playing or TransportState.Transitioning => "Pause,Stop,Seek",
            TransportState.Paused => "Play,Stop,Seek",
            TransportState.Stopped => "Play",
            _ => "",
        };

        // ================================================================== GENA events (SUBSCRIBE / NOTIFY)

        private sealed class Subscription
        {
            public required string Sid { get; init; }
            public required string Service { get; init; }
            public required Uri Callback { get; init; }
            public DateTime Expires { get; set; }
            public uint Seq;
        }

        private Task HandleSubscribeAsync(HttpContext ctx)
        {
            string service = (string)ctx.Request.RouteValues["service"]!;
            if (service is not ("AVTransport" or "RenderingControl" or "ConnectionManager")) { ctx.Response.StatusCode = 404; return Task.CompletedTask; }

            int seconds = 1800;
            string timeout = ctx.Request.Headers["TIMEOUT"].ToString();
            if (timeout.StartsWith("Second-", StringComparison.OrdinalIgnoreCase) && int.TryParse(timeout[7..], out int requested))
                seconds = Math.Clamp(requested, 60, 1800);

            string sidHeader = ctx.Request.Headers["SID"].ToString();
            if (!string.IsNullOrEmpty(sidHeader))
            {
                // Renewal
                if (!_subscriptions.TryGetValue(sidHeader, out var existing)) { ctx.Response.StatusCode = 412; return Task.CompletedTask; }
                existing.Expires = DateTime.UtcNow.AddSeconds(seconds);
                ctx.Response.Headers["SID"] = existing.Sid;
                ctx.Response.Headers["TIMEOUT"] = $"Second-{seconds}";
                return Task.CompletedTask;
            }

            // CALLBACK: <http://host:port/path>[<...>]. Only accept callbacks on the local network.
            string callbackHeader = ctx.Request.Headers["CALLBACK"].ToString();
            Uri? callback = callbackHeader.Split('<', '>', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => Uri.TryCreate(s.Trim(), UriKind.Absolute, out var u) ? u : null)
                .FirstOrDefault(u => u != null && u.Scheme == Uri.UriSchemeHttp);
            if (callback == null || ctx.Request.Headers["NT"].ToString() != "upnp:event" ||
                !System.Net.IPAddress.TryParse(callback.Host.Trim('[', ']'), out var cbIp) ||
                !NetworkHelper.IsLocalClient(cbIp, ctx.Connection.LocalIpAddress))
            {
                ctx.Response.StatusCode = 412;
                return Task.CompletedTask;
            }

            var sub = new Subscription
            {
                Sid = "uuid:" + Guid.NewGuid(),
                Service = service,
                Callback = callback,
                Expires = DateTime.UtcNow.AddSeconds(seconds),
            };
            // Keep the table small even if a client subscribes in a loop.
            foreach (var old in _subscriptions.Values.Where(s => s.Expires < DateTime.UtcNow).ToList()) _subscriptions.TryRemove(old.Sid, out _);
            if (_subscriptions.Count >= 50) { ctx.Response.StatusCode = 503; return Task.CompletedTask; }
            _subscriptions[sub.Sid] = sub;

            ctx.Response.Headers["SID"] = sub.Sid;
            ctx.Response.Headers["TIMEOUT"] = $"Second-{seconds}";
            ctx.Response.Headers["SERVER"] = $"LocalStream/{UpdateService.CurrentVersionText} UPnP/1.0";

            // The initial event goes out after the SUBSCRIBE response.
            ctx.Response.OnCompleted(() => { _ = SendInitialEventAsync(sub); return Task.CompletedTask; });
            return Task.CompletedTask;
        }

        private void HandleUnsubscribe(HttpContext ctx)
        {
            string sid = ctx.Request.Headers["SID"].ToString();
            ctx.Response.StatusCode = _subscriptions.TryRemove(sid, out _) ? 200 : 412;
        }

        private async Task SendInitialEventAsync(Subscription sub)
        {
            await Task.Delay(100);
            string body = sub.Service switch
            {
                "AVTransport" => AvTransportEventBody(),
                "RenderingControl" => RenderingControlEventBody(),
                _ => UpnpXml.PropertySet(new[] { ("SourceProtocolInfo", ""), ("SinkProtocolInfo", RendererDescriptions.SinkProtocolInfo), ("CurrentConnectionIDs", "0") }),
            };
            await NotifyAsync(sub, body);
        }

        private string AvTransportEventBody()
        {
            string lastChange = UpnpXml.LastChange("urn:schemas-upnp-org:metadata-1-0/AVT/", new[]
            {
                Val("TransportState", StateName(_state)),
                Val("TransportStatus", "OK"),
                Val("TransportPlaySpeed", "1"),
                Val("CurrentPlayMode", "NORMAL"),
                Val("PlaybackStorageMedium", _uri.Length > 0 ? "NETWORK" : "NONE"),
                Val("NumberOfTracks", _uri.Length > 0 ? "1" : "0"),
                Val("CurrentTrack", _uri.Length > 0 ? "1" : "0"),
                Val("AVTransportURI", _uri),
                Val("CurrentTrackURI", _uri),
                Val("AVTransportURIMetaData", _uriMetadata),
                Val("CurrentTrackMetaData", _uriMetadata),
                Val("NextAVTransportURI", _nextUri),
                Val("CurrentTrackDuration", UpnpXml.FormatTime(_duration)),
                Val("CurrentMediaDuration", UpnpXml.FormatTime(_duration)),
                Val("CurrentTransportActions", CurrentActions()),
            });
            return UpnpXml.PropertySet(new[] { ("LastChange", lastChange) });
        }

        private string RenderingControlEventBody()
        {
            string lastChange = UpnpXml.LastChange("urn:schemas-upnp-org:metadata-1-0/RCS/", new[]
            {
                new XElement("Volume", new XAttribute("channel", "Master"), new XAttribute("val", _volume)),
                new XElement("Mute", new XAttribute("channel", "Master"), new XAttribute("val", _muted ? 1 : 0)),
            });
            return UpnpXml.PropertySet(new[] { ("LastChange", lastChange) });
        }

        private static XElement Val(string name, string value) => new(name, new XAttribute("val", value));

        private async Task SendEventsIfChangedAsync()
        {
            if (_subscriptions.IsEmpty) return;
            string avt = AvTransportEventBody();
            string rcs = RenderingControlEventBody();
            var tasks = new List<Task>();
            if (avt != _lastAvtEvent)
            {
                _lastAvtEvent = avt;
                tasks.AddRange(_subscriptions.Values.Where(s => s.Service == "AVTransport").Select(s => NotifyAsync(s, avt)));
            }
            if (rcs != _lastRcsEvent)
            {
                _lastRcsEvent = rcs;
                tasks.AddRange(_subscriptions.Values.Where(s => s.Service == "RenderingControl").Select(s => NotifyAsync(s, rcs)));
            }
            await Task.WhenAll(tasks);
        }

        private async Task NotifyAsync(Subscription sub, string body)
        {
            if (sub.Expires < DateTime.UtcNow) { _subscriptions.TryRemove(sub.Sid, out _); return; }
            uint seq;
            lock (sub) { seq = sub.Seq; sub.Seq = sub.Seq == uint.MaxValue ? 1 : sub.Seq + 1; }

            using var request = new HttpRequestMessage(new HttpMethod("NOTIFY"), sub.Callback)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/xml"),
            };
            request.Content.Headers.ContentType!.CharSet = "utf-8";
            request.Headers.TryAddWithoutValidation("NT", "upnp:event");
            request.Headers.TryAddWithoutValidation("NTS", "upnp:propchange");
            request.Headers.TryAddWithoutValidation("SID", sub.Sid);
            request.Headers.TryAddWithoutValidation("SEQ", seq.ToString());
            try
            {
                using var response = await EventClient.SendAsync(request);
                if ((int)response.StatusCode == 412) _subscriptions.TryRemove(sub.Sid, out _);
            }
            catch
            {
                // Subscriber went away; it will expire.
            }
        }
    }
}
