using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using LocalStreamPC.Cast;
using LocalStreamPC.Models;
using LocalStreamPC.Services;

namespace LocalStreamPC.ViewModels
{
    /// <summary>The "Cast to this PC" card: receiver on/off and the Now Casting remote.</summary>
    public sealed class CastViewModel : ObservableObject
    {
        public const string PlayIcon = "M5 3l14 9-14 9V3z";
        public const string PauseIcon = "M6 4h4v16H6z M14 4h4v16h-4z";

        private readonly CastReceiver _receiver;
        private readonly ConfigService _config;
        private bool _refreshQueued;
        private bool _applyingSnapshot;

        public CastViewModel(CastReceiver receiver, ConfigService config)
        {
            _receiver = receiver;
            _config = config;
            _receiver.Changed += QueueRefresh;
            Refresh();
        }

        public string ReceiverName => RendererDescriptions.FriendlyName;

        private bool _isReceiving;
        public bool IsReceiving { get => _isReceiving; private set { if (SetField(ref _isReceiving, value)) { OnPropertyChanged(nameof(ToggleText)); OnPropertyChanged(nameof(StatusText)); } } }
        public string ToggleText => IsReceiving ? "Stop receiving" : "Start receiving";

        private bool _isBusy;
        public bool IsBusy { get => _isBusy; private set { if (SetField(ref _isBusy, value)) OnPropertyChanged(nameof(CanToggle)); } }
        public bool CanToggle => !IsBusy;

        private bool _hasMedia;
        public bool HasMedia { get => _hasMedia; private set { if (SetField(ref _hasMedia, value)) OnPropertyChanged(nameof(StatusText)); } }

        private string _title = "";
        public string Title { get => _title; private set => SetField(ref _title, value); }

        private string? _subtitle;
        /// <summary>"Artist · Album" for music.</summary>
        public string? Subtitle { get => _subtitle; private set { if (SetField(ref _subtitle, value)) OnPropertyChanged(nameof(HasSubtitle)); } }
        public bool HasSubtitle => !string.IsNullOrEmpty(_subtitle);

        private Avalonia.Media.Imaging.Bitmap? _cover;
        private byte[]? _coverSource;
        public Avalonia.Media.Imaging.Bitmap? Cover { get => _cover; private set { if (SetField(ref _cover, value)) OnPropertyChanged(nameof(HasCover)); } }
        public bool HasCover => _cover != null;

        private string? _upNext;
        public string? UpNext { get => _upNext; private set { if (SetField(ref _upNext, value)) OnPropertyChanged(nameof(HasUpNext)); } }
        public bool HasUpNext => !string.IsNullOrEmpty(_upNext);

        private bool _canNext, _canPrevious;
        public bool CanNext { get => _canNext; private set => SetField(ref _canNext, value); }
        public bool CanPrevious { get => _canPrevious; private set => SetField(ref _canPrevious, value); }

        private string _stateText = "";
        public string StateText { get => _stateText; private set => SetField(ref _stateText, value); }

        public string StatusText => !IsReceiving
            ? "Off. Turn it on to let phones and other PCs cast videos, music and photos here."
            : HasMedia ? "" : $"Ready. Pick \"{ReceiverName}\" in a casting app on the same Wi-Fi.";

        private bool _isPlaying;
        public bool IsPlaying { get => _isPlaying; private set { if (SetField(ref _isPlaying, value)) OnPropertyChanged(nameof(PlayPauseIcon)); } }
        public string PlayPauseIcon => IsPlaying ? PauseIcon : PlayIcon;

        private double _duration;
        public double DurationSeconds { get => _duration; private set { if (SetField(ref _duration, Math.Max(1, value))) OnPropertyChanged(nameof(DurationText)); } }
        public string DurationText => Format(TimeSpan.FromSeconds(_hasDuration ? _duration : 0));
        private bool _hasDuration;

        private double _position;
        /// <summary>Bound to the seek slider. The view sets <see cref="IsUserSeeking"/> while dragging.</summary>
        public double PositionSeconds
        {
            get => _position;
            set { if (SetField(ref _position, value)) OnPropertyChanged(nameof(PositionText)); }
        }
        public string PositionText => Format(TimeSpan.FromSeconds(_position));
        public bool IsUserSeeking { get; set; }

        private double _volume = 100;
        public double Volume
        {
            get => _volume;
            set
            {
                if (!SetField(ref _volume, Math.Round(value))) return;
                if (!_applyingSnapshot) _ = _receiver.SetVolumeAsync((int)_volume);
            }
        }

        private bool _isFullscreen;
        public bool IsFullscreen { get => _isFullscreen; private set { if (SetField(ref _isFullscreen, value)) OnPropertyChanged(nameof(FullscreenText)); } }
        public string FullscreenText => IsFullscreen ? "Exit full screen" : "Full screen";

        // ------------------------------------------------------------ commands

        public async Task<bool> SetReceivingAsync(bool on)
        {
            if (IsBusy) return IsReceiving;
            IsBusy = true;
            try
            {
                if (on)
                {
                    bool ok = await _receiver.StartAsync(NetworkHelper.GetActiveIPv4Addresses());
                    if (ok) { _config.Current.CastReceiverEnabled = true; _config.Save(); }
                }
                else
                {
                    await _receiver.StopAsync();
                    _config.Current.CastReceiverEnabled = false;
                    _config.Save();
                }
            }
            finally
            {
                IsBusy = false;
                Refresh();
            }
            return IsReceiving;
        }

        public Task ToggleReceivingAsync() => SetReceivingAsync(!IsReceiving);
        public Task PlayPauseAsync() => _receiver.PlayPauseAsync();
        public Task StopAsync() => _receiver.StopPlaybackAsync();
        public Task NextAsync() => _receiver.NextAsync();
        public Task PreviousAsync() => _receiver.PreviousAsync();
        public Task SeekAsync(double seconds) => _receiver.SeekAsync(TimeSpan.FromSeconds(Math.Max(0, seconds)));
        public Task ToggleFullscreenAsync() => _receiver.SetFullscreenAsync(!IsFullscreen);
        public Task ApplySettingsAsync() => _receiver.ApplySettingsAsync();
        public Task ShutdownAsync() => _receiver.StopAsync();

        // ------------------------------------------------------------ state from the receiver

        private void QueueRefresh()
        {
            if (_refreshQueued) return;
            _refreshQueued = true;
            Dispatcher.UIThread.Post(() => { _refreshQueued = false; Refresh(); });
        }

        private void Refresh()
        {
            var s = _receiver.Snapshot;
            _applyingSnapshot = true;
            try
            {
                IsReceiving = s.IsReceiving;
                HasMedia = s.HasMedia;
                Title = s.Title;
                Subtitle = string.Join(" · ", new[] { s.Artist, s.Album }.Where(x => !string.IsNullOrWhiteSpace(x)));
                if (!ReferenceEquals(s.Artwork, _coverSource))
                {
                    _coverSource = s.Artwork;
                    var old = Cover;
                    Cover = DecodeCover(s.Artwork);
                    old?.Dispose();
                }
                IsPlaying = s.State is TransportState.Playing or TransportState.Transitioning;
                UpNext = s.UpNext is { Length: > 0 } next ? "Up next: " + next : null;
                CanNext = s.CanNext;
                CanPrevious = s.CanPrevious;
                IsFullscreen = s.Fullscreen;
                StateText = s.State switch
                {
                    TransportState.Playing => (s.IsImage ? (s.Slideshow ? "Slideshow" : "Showing photo") : "Playing") + (s.Sender != null ? $" · from {s.Sender}" : ""),
                    TransportState.Paused => "Paused",
                    TransportState.Transitioning => "Loading…",
                    TransportState.Stopped => "Stopped",
                    _ => "",
                };
                _hasDuration = s.Duration > TimeSpan.Zero;
                DurationSeconds = s.Duration.TotalSeconds;
                OnPropertyChanged(nameof(DurationText));
                if (!IsUserSeeking) PositionSeconds = Math.Min(s.Position.TotalSeconds, DurationSeconds);
                Volume = s.Muted ? 0 : s.Volume;
            }
            finally
            {
                _applyingSnapshot = false;
            }
        }

        private static Avalonia.Media.Imaging.Bitmap? DecodeCover(byte[]? data)
        {
            if (data is not { Length: > 0 }) return null;
            try { using var ms = new System.IO.MemoryStream(data); return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(ms, 112); }
            catch { return null; }
        }

        private static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
