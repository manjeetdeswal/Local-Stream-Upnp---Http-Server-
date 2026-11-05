using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Animation;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;

namespace LocalStreamPC.Cast
{
    /// <summary>
    /// The built-in player window: video fills the window, with a control bar along the bottom.
    /// Music shows its cover; photos are shown with a cross-fade and can run as a slideshow.
    /// In full screen (and for music and photos) the bar hides after a few seconds and comes back
    /// when the mouse moves or a key is pressed.
    ///
    /// Shortcuts: Space/K play-pause · F/F11 or double-click full screen · Esc leave full screen ·
    /// ←/→ (J/L) 10 s back/forward (photos: previous/next) · N/P next/previous · ↑/↓ volume ·
    /// C subtitles · A audio track.
    /// </summary>
    internal sealed class PlayerWindow : Window
    {
        private const string PlayIcon = "M6 4l14 8-14 8z";
        private const string PauseIcon = "M6 4h4v16H6z M14 4h4v16h-4z";
        private const string EnterFullscreenIcon = "M8 3H5a2 2 0 0 0-2 2v3 M21 8V5a2 2 0 0 0-2-2h-3 M3 16v3a2 2 0 0 0 2 2h3 M16 21h3a2 2 0 0 0 2-2v-3";
        private const string ExitFullscreenIcon = "M8 3v3a2 2 0 0 1-2 2H3 M21 8h-3a2 2 0 0 1-2-2V3 M3 16h3a2 2 0 0 1 2 2v3 M16 21v-3a2 2 0 0 1 2-2h3";
        private const string SubtitlesIcon = "M3 5h18v14H3z M7 13h3 M14 13h3 M7 16h10";
        private const string AudioTrackIcon = "M9 18V5l12-2v13 M6 18a3 3 0 1 1-6 0 3 3 0 0 1 6 0z M21 16a3 3 0 1 1-6 0 3 3 0 0 1 6 0z";
        private static readonly TimeSpan HideControlsAfter = TimeSpan.FromSeconds(3);
        private static readonly Geometry PlayGeometry = Geometry.Parse(PlayIcon);
        private static readonly Geometry PauseGeometry = Geometry.Parse(PauseIcon);
        private static readonly Cursor HiddenCursor = new(StandardCursorType.None);
        private bool _cursorHidden;

        private enum ViewMode { Video, Audio, Photo }

        private readonly EmbeddedPlayer _engine;
        private readonly MediaPlayer _player;
        private readonly VideoView _video;
        private readonly Border _bar;
        private readonly TextBlock _titleText = new() { Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _upNextText = new() { Foreground = new SolidColorBrush(Color.Parse("#CBD5E1")), FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 420, Margin = new Thickness(16, 0, 0, 0) };
        private readonly TextBlock _timeText = TimeLabel();
        private readonly TextBlock _durationText = TimeLabel();
        private readonly Slider _seek = new() { Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) };
        private readonly Slider _volume = new() { Minimum = 0, Maximum = 100, Width = 120, VerticalAlignment = VerticalAlignment.Center };
        private readonly Path _playPauseIcon = IconPath(PlayIcon, filled: true);
        private readonly Path _fullscreenIcon = IconPath(EnterFullscreenIcon);
        private readonly DispatcherTimer _tick;
        private Grid _seekRow = null!;
        private Button _back = null!, _forward = null!, _previous = null!, _next = null!, _subtitles = null!, _audioTracks = null!;

        // "Resumed at 12:34 · Start over"
        private readonly Border _resumeToast;
        private readonly TextBlock _resumeText = new() { Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        private DateTime _resumeShownAt = DateTime.MinValue;

        // Music view (shown instead of the video surface for audio casts)
        private readonly Border _overlay;
        private readonly Grid _audioView;
        private readonly Border _audioBarHolder = new() { VerticalAlignment = VerticalAlignment.Bottom };
        private readonly Image _coverImage = new() { Stretch = Stretch.UniformToFill };
        // Already blurred by CoverArt; just stretched over the window.
        private readonly Image _backdropImage = new() { Stretch = Stretch.UniformToFill, Opacity = 0.6 };
        private readonly Border _coverPlaceholder;
        private Border _coverFrame = null!;
        private const double CoverSize = 360;
        private readonly TextBlock _audioTitle = new() { Foreground = Brushes.White, FontSize = 30, FontWeight = FontWeight.Bold, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        private readonly TextBlock _audioArtist = new() { Foreground = new SolidColorBrush(Color.Parse("#E5E7EB")), FontSize = 18, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _audioAlbum = new() { Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")), FontSize = 15, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

        // Photo view: two images that cross-fade
        private readonly Grid _photoView;
        private readonly Border _photoBarHolder = new() { VerticalAlignment = VerticalAlignment.Bottom, ZIndex = 10 }; // above the photos
        private readonly Image _photoA = PhotoImage(), _photoB = PhotoImage();
        private bool _photoAOnTop;
        private readonly TextBlock _photoError = new() { Foreground = new SolidColorBrush(Color.Parse("#9CA3AF")), FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsVisible = false, Text = "This photo couldn't be shown." };
        private bool _slideshowRunning;

        private NowPlayingInfo? _pendingNowPlaying;
        private NowPlayingInfo? _info;
        private ViewMode _mode = ViewMode.Video;
        private int _modeVersion;
        private WindowsMediaControls? _mediaKeys;
        private WindowsMediaControls.Status _lastKeysStatus = (WindowsMediaControls.Status)(-1);

        private DateTime _lastActivity = DateTime.UtcNow;
        private bool _seeking, _updating, _closingAfterStop, _videoReadyRaised;

        /// <summary>Raised once the video surface exists (libvlc must not start before that).</summary>
        public event Action? VideoReady;

        public bool IsFullscreen => WindowState == WindowState.FullScreen;

        public PlayerWindow(EmbeddedPlayer engine, MediaPlayer player)
        {
            _engine = engine;
            _player = player;

            Title = "LocalStream Player";
            Width = 1280;
            Height = 720;
            MinWidth = 480;
            MinHeight = 270;
            Background = Brushes.Black;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://LocalStreamPC/Assets/app-icon.ico"))); } catch { }

            var startOver = new Button
            {
                Content = "Start over", Padding = new Thickness(12, 5), CornerRadius = new CornerRadius(6), Focusable = false,
                Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), Foreground = Brushes.White, Margin = new Thickness(12, 0, 0, 0),
            };
            startOver.Click += (_, _) => { _resumeToast!.IsVisible = false; _engine.RaiseCommand(PlayerCommand.StartOver); };
            _resumeToast = new Border
            {
                IsVisible = false, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 8),
                Background = new SolidColorBrush(Color.FromArgb(200, 30, 41, 59)),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _resumeText, startOver } },
            };

            _bar = BuildControlBar();

            // The video is a native surface, so normal controls can't be drawn on top of it.
            // VideoView.Content is shown in a small transparent window floating over the video instead.
            var overlay = _overlay = new Border
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                // Almost-transparent rather than transparent, so the strip still receives the mouse.
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                Child = _bar,
            };
            overlay.PointerMoved += (_, _) => NoteActivity();
            overlay.PointerEntered += (_, _) => NoteActivity();
            overlay.KeyDown += OnKeyDown;
            overlay.DoubleTapped += (s, e) => { if (e.Source is not Button) ToggleFullscreen(); };

            _video = new VideoView { MediaPlayer = player, Content = overlay };

            _coverPlaceholder = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#1F2937")),
                Child = new Path
                {
                    Data = Geometry.Parse("M9 18V5l12-2v13 M6 18a3 3 0 1 1-6 0 3 3 0 0 1 6 0z M21 16a3 3 0 1 1-6 0 3 3 0 0 1 6 0z"),
                    Width = 110, Height = 110, Stretch = Stretch.Uniform, Stroke = new SolidColorBrush(Color.Parse("#6B7280")),
                    StrokeThickness = 1.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            // Smooth scaling for the cover and backdrop (the default is a cheaper, blurrier filter).
            RenderOptions.SetBitmapInterpolationMode(_coverImage, BitmapInterpolationMode.HighQuality);
            RenderOptions.SetBitmapInterpolationMode(_backdropImage, BitmapInterpolationMode.HighQuality);
            _audioView = BuildAudioView();
            _audioView.IsVisible = false;
            _photoView = new Grid { Background = Brushes.Black, IsVisible = false, Children = { _photoA, _photoB, _photoError, _photoBarHolder } };

            // The video surface stays in the tree (hidden for music and photos) so libvlc keeps its window.
            var root = new Grid { Children = { _audioView, _photoView, _video } };
            Content = root;

            PointerMoved += (_, _) => NoteActivity(); // reaches us in the music/photo views (no native surface on top)
            DoubleTapped += (_, e) => { if (_mode != ViewMode.Video && e.Source is not Button && e.Source is not Slider) ToggleFullscreen(); };
            KeyDown += OnKeyDown;
            Closing += OnClosing;
            Closed += (_, _) => { _mediaKeys?.Dispose(); _mediaKeys = null; };
            Opened += (_, _) => { WaitForVideoSurface(); NudgeOverlay(); FitCoverToPicture(); SetUpMediaKeys(); };
            ScalingChanged += (_, _) => FitCoverToPicture(); // moved to a screen with other DPI
            PropertyChanged += (_, e) =>
            {
                if (e.Property == WindowStateProperty) { UpdateFullscreenButton(); NoteActivity(); NudgeOverlay(); }
            };

            _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => Refresh());
            _tick.Start();
            UpdateFullscreenButton();
        }

        /// <summary>Title/artist/cover/queue for the current media; music and photos switch views.</summary>
        /// <param name="art">Owned by the caller, which disposes it after replacing it here.</param>
        public void SetNowPlaying(NowPlayingInfo info, CoverArt? art)
        {
            _info = info;
            SetTitle(info.Title);
            _audioTitle.Text = info.Title;
            _audioArtist.Text = info.Artist ?? "";
            _audioArtist.IsVisible = !string.IsNullOrEmpty(info.Artist);
            _audioAlbum.Text = info.Album ?? "";
            _audioAlbum.IsVisible = !string.IsNullOrEmpty(info.Album);
            _upNextText.Text = string.IsNullOrEmpty(info.UpNext) ? "" : "Up next: " + info.UpNext;
            _upNextText.IsVisible = !string.IsNullOrEmpty(info.UpNext);
            _previous.IsVisible = info.CanPrevious;
            _next.IsVisible = info.CanNext;

            _coverImage.Source = art?.Cover;
            _backdropImage.Source = art?.Backdrop;
            _coverPlaceholder.IsVisible = art == null;
            FitCoverToPicture();
            UpdateMediaKeysInfo();

            // Switch views only once the video surface exists (libvlc must attach to it first).
            var mode = info.IsImage ? ViewMode.Photo : info.IsAudio ? ViewMode.Audio : ViewMode.Video;
            if (_videoReadyRaised) SetMode(mode);
            else _pendingNowPlaying = info;
        }

        /// <summary>Shows a photo, cross-fading from the previous one. Null shows "couldn't be shown".</summary>
        public void ShowPhoto(Bitmap? photo)
        {
            var incoming = _photoAOnTop ? _photoB : _photoA;
            var outgoing = _photoAOnTop ? _photoA : _photoB;
            var old = incoming.Source as Bitmap; // the picture before the current one
            incoming.Source = photo;
            incoming.ZIndex = 1;
            outgoing.ZIndex = 0;
            incoming.Opacity = 1;
            outgoing.Opacity = 0;
            _photoAOnTop = !_photoAOnTop;
            _photoError.IsVisible = photo == null;
            if (old != null && !ReferenceEquals(old, photo)) DispatcherTimer.RunOnce(old.Dispose, TimeSpan.FromSeconds(1));
            if (!_videoReadyRaised) _pendingNowPlaying ??= _info;
            else SetMode(ViewMode.Photo);
        }

        public void SetSlideshow(bool running)
        {
            _slideshowRunning = running;
            if (_mode == ViewMode.Photo) _playPauseIcon.Data = running ? PauseGeometry : PlayGeometry;
            ToolTip.SetTip(_playPauseIcon.Parent as Control ?? _bar, _mode == ViewMode.Photo ? (running ? "Stop slideshow (Space)" : "Start slideshow (Space)") : "Play / Pause (Space)");
        }

        public void ShowResumeNotice(TimeSpan at)
        {
            _resumeText.Text = $"Resumed at {Format((long)at.TotalMilliseconds)}";
            _resumeToast.IsVisible = true;
            _resumeShownAt = DateTime.UtcNow;
            NoteActivity();
        }

        /// <summary>
        /// A small picture stretched to 360 px looks smeared, so the cover box shrinks for small
        /// pictures (to at most 1.25× their real size, but never below 240 px).
        /// </summary>
        private void FitCoverToPicture()
        {
            double size = CoverSize;
            if (_coverImage.Source is Bitmap b)
            {
                double scaling = RenderScaling > 0 ? RenderScaling : 1;
                double native = Math.Min(b.PixelSize.Width, b.PixelSize.Height) / scaling;
                size = Math.Clamp(Math.Round(native * 1.25), 240, CoverSize);
            }
            _coverFrame.Width = _coverFrame.Height = size;
        }

        private void SetMode(ViewMode mode)
        {
            if (_mode == mode) return;
            _mode = mode;
            // The bar lives in the video's floating overlay window or in this window. Avalonia can't
            // move a control between two windows inside one layout pass (it crashes), so take it out
            // now and put it in its new place once the old window has finished laying out.
            _overlay.Child = null;
            _audioBarHolder.Child = null;
            _photoBarHolder.Child = null;
            int version = ++_modeVersion;
            DispatcherTimer.RunOnce(() =>
            {
                if (version != _modeVersion || _bar.Parent != null) return;
                var holder = _mode switch { ViewMode.Audio => _audioBarHolder, ViewMode.Photo => _photoBarHolder, _ => (Border)_overlay };
                holder.Child = _bar;
                if (_mode == ViewMode.Video) NudgeOverlay();
            }, TimeSpan.FromMilliseconds(60));
            switch (mode)
            {
                case ViewMode.Video:
                    _audioView.IsVisible = _photoView.IsVisible = false;
                    _video.IsVisible = true;
                    break;
                case ViewMode.Audio:
                    _video.IsVisible = false;   // also hides VideoView's floating overlay window
                    _photoView.IsVisible = false;
                    _audioView.IsVisible = true;
                    break;
                case ViewMode.Photo:
                    _video.IsVisible = false;
                    _audioView.IsVisible = false;
                    _photoView.IsVisible = true;
                    break;
            }
            // Music shows its title big under the cover; photos have no timeline.
            _titleText.IsVisible = mode != ViewMode.Audio;
            _seekRow.IsVisible = _back.IsVisible = _forward.IsVisible = mode != ViewMode.Photo;
            SetSlideshow(_slideshowRunning);
            if (mode != ViewMode.Photo) { _photoA.Source = _photoB.Source = null; }
            UpdateMediaKeysInfo();
            NoteActivity();
        }

        private Grid BuildAudioView()
        {
            var cover = _coverFrame = new Border
            {
                Width = CoverSize, Height = CoverSize, CornerRadius = new CornerRadius(14), ClipToBounds = true,
                BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 18, Blur = 50, Color = Color.FromArgb(150, 0, 0, 0) }),
                Child = new Grid { Children = { _coverPlaceholder, _coverImage } },
            };
            var info = new StackPanel
            {
                Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(40, 0, 40, 150), MaxWidth = 900,
                Children = { cover, new Border { Height = 18 }, _audioTitle, _audioArtist, _audioAlbum },
            };
            return new Grid
            {
                ClipToBounds = true,
                Background = Brushes.Black,
                Children =
                {
                    _backdropImage,
                    new Border { Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)) }, // darken the backdrop
                    info,
                    _audioBarHolder,
                },
            };
        }

        private static Image PhotoImage()
        {
            var image = new Image
            {
                Stretch = Stretch.Uniform, Opacity = 0,
                Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(700) } },
            };
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
            return image;
        }

        public void SetTitle(string title)
        {
            _titleText.Text = title;
            Title = string.IsNullOrEmpty(title) ? "LocalStream Player" : $"{title} - LocalStream Player";
        }

        public void SetFullscreen(bool fullscreen)
        {
            WindowState = fullscreen ? WindowState.FullScreen : WindowState.Normal;
            if (fullscreen) Activate();
        }

        public void ToggleFullscreen() => SetFullscreen(!IsFullscreen);

        /// <summary>Closes the window once playback has been stopped.</summary>
        public void CloseAfterStop()
        {
            _closingAfterStop = true;
            Close();
        }

        // ------------------------------------------------------------------ layout

        private Border BuildControlBar()
        {
            _back = BarButton(IconPath("M11 17l-5-5 5-5 M18 17l-5-5 5-5"), "Back 10 seconds (←)", (_, _) => _ = _engine.SeekByAsync(TimeSpan.FromSeconds(-10)));
            var playPause = BarButton(_playPauseIcon, "Play / Pause (Space)", (_, _) => TogglePlay());
            playPause.Background = new SolidColorBrush(Color.Parse("#3B82F6"));
            _forward = BarButton(IconPath("M13 17l5-5-5-5 M6 17l5-5-5-5"), "Forward 10 seconds (→)", (_, _) => _ = _engine.SeekByAsync(TimeSpan.FromSeconds(10)));
            _previous = BarButton(IconPath("M19 20L9 12l10-8v16z M5 19V5"), "Previous (P)", (_, _) => _engine.RaiseCommand(PlayerCommand.Previous));
            _next = BarButton(IconPath("M5 4l10 8-10 8V4z M19 5v14"), "Next (N)", (_, _) => _engine.RaiseCommand(PlayerCommand.Next));
            _previous.IsVisible = _next.IsVisible = false;
            var stop = BarButton(IconPath("M6 6h12v12H6z"), "Stop", async (_, _) => await _engine.StopAsync());
            var fullscreen = BarButton(_fullscreenIcon, "Full screen (F)", (_, _) => ToggleFullscreen());
            _subtitles = BarButton(IconPath(SubtitlesIcon), "Subtitles (C)", (s, _) => ShowTrackMenu((Control)s!, subtitles: true));
            _audioTracks = BarButton(IconPath(AudioTrackIcon), "Audio track (A)", (s, _) => ShowTrackMenu((Control)s!, subtitles: false));
            _subtitles.IsVisible = _audioTracks.IsVisible = false;

            // Seek when the slider is released, not on every step while dragging.
            _seek.AddHandler(PointerPressedEvent, (_, _) => _seeking = true, RoutingStrategies.Tunnel, handledEventsToo: true);
            _seek.AddHandler(PointerReleasedEvent, async (_, _) =>
            {
                await _engine.SeekAsync(TimeSpan.FromSeconds(_seek.Value));
                _seeking = false;
            }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            _volume.PropertyChanged += (_, e) =>
            {
                if (e.Property == RangeBase.ValueProperty && !_updating) _ = _engine.SetVolumeAsync((int)_volume.Value);
            };

            _seekRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            _seekRow.Children.Add(_timeText);
            Grid.SetColumn(_seek, 1); _seekRow.Children.Add(_seek);
            Grid.SetColumn(_durationText, 2); _seekRow.Children.Add(_durationText);

            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Children = { _titleText, _upNextText } };
            var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _previous, _back, playPause, _forward, _next, stop } };
            var right = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right,
                Children = { _subtitles, _audioTracks, IconPath("M11 5L6 9H2v6h4l5 4V5z M15.54 8.46a5 5 0 0 1 0 7.07 M19.07 4.93a10 10 0 0 1 0 14.14"), _volume, fullscreen },
            };
            var buttonRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            buttonRow.Children.Add(left);
            Grid.SetColumn(right, 2); buttonRow.Children.Add(right);

            return new Border
            {
                Padding = new Thickness(28, 36, 28, 20),
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0), new GradientStop(Color.FromArgb(220, 0, 0, 0), 0.6) },
                },
                Child = new StackPanel { Spacing = 10, Children = { _resumeToast, titleRow, _seekRow, buttonRow } },
                Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(250) } },
            };
        }

        /// <summary>Subtitle or audio track choices, straight from libvlc's list for this media.</summary>
        private void ShowTrackMenu(Control anchor, bool subtitles)
        {
            LibVLCSharp.Shared.Structures.TrackDescription[] tracks;
            int current;
            try
            {
                tracks = subtitles ? _player.SpuDescription : _player.AudioTrackDescription;
                current = subtitles ? _player.Spu : _player.AudioTrack;
            }
            catch { return; }
            if (tracks.Length == 0) return;

            var menu = new ContextMenu();
            int number = 0;
            foreach (var t in tracks)
            {
                int id = t.Id;
                if (id >= 0) number++;
                string name = id < 0 ? (subtitles ? "Off" : "No sound")
                            : string.IsNullOrWhiteSpace(t.Name) || t.Name.StartsWith("Track ", StringComparison.Ordinal) ? (subtitles ? $"Subtitles {number}" : $"Audio {number}") : t.Name;
                var item = new MenuItem { Header = (id == current ? "✓  " : "     ") + name };
                item.Click += (_, _) =>
                {
                    _ = System.Threading.Tasks.Task.Run(() =>
                    {
                        try { if (subtitles) _player.SetSpu(id); else _player.SetAudioTrack(id); } catch { }
                    });
                };
                menu.Items.Add(item);
            }
            menu.Open(anchor);
            NoteActivity();
        }

        private static Button BarButton(Path icon, string tip, EventHandler<RoutedEventArgs> onClick)
        {
            var b = new Button
            {
                Content = icon,
                Padding = new Thickness(12, 9),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
                Focusable = false, // keep keyboard shortcuts working after a click
            };
            ToolTip.SetTip(b, tip);
            b.Click += onClick;
            return b;
        }

        private static Path IconPath(string data, bool filled = false) => new()
        {
            Data = Geometry.Parse(data),
            Width = 18,
            Height = 18,
            Stretch = Stretch.Uniform,
            Stroke = Brushes.White,
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Fill = filled ? Brushes.White : Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
        };

        private static TextBlock TimeLabel() => new()
        {
            Foreground = new SolidColorBrush(Color.Parse("#D1D5DB")), FontSize = 13, MinWidth = 52,
            VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, Text = "0:00",
        };

        // ------------------------------------------------------------------ behaviour

        private void TogglePlay()
        {
            if (_mode == ViewMode.Photo) _engine.RaiseCommand(PlayerCommand.ToggleSlideshow);
            else _ = _engine.TogglePauseAsync();
        }

        private void WaitForVideoSurface()
        {
            var started = DateTime.UtcNow;
            var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            poll.Tick += (_, _) =>
            {
                bool ready = OperatingSystem.IsWindows() ? _player.Hwnd != IntPtr.Zero
                           : OperatingSystem.IsLinux() ? _player.XWindow != 0
                           : _player.NsObject != IntPtr.Zero;
                if (ready || DateTime.UtcNow - started > TimeSpan.FromSeconds(3))
                {
                    poll.Stop();
                    if (!_videoReadyRaised)
                    {
                        _videoReadyRaised = true;
                        VideoReady?.Invoke();
                        if (_pendingNowPlaying is { } pending)
                        {
                            SetMode(pending.IsImage ? ViewMode.Photo : pending.IsAudio ? ViewMode.Audio : ViewMode.Video);
                            _pendingNowPlaying = null;
                        }
                    }
                }
            };
            poll.Start();
        }

        /// <summary>
        /// VideoView only re-positions its floating controls on a layout pass of this window, and the
        /// first pass happens before the controls have measured themselves. Trigger a few more passes
        /// after opening and after full screen changes so the bar lands at the bottom.
        /// </summary>
        private void NudgeOverlay()
        {
            // VideoView shows its floating controls before this window is on screen, so this window
            // can end up stacked above them. Show them again (owned by this window) now that it's open.
            DispatcherTimer.RunOnce(RaiseOverlay, TimeSpan.FromMilliseconds(150));
            foreach (int ms in new[] { 50, 250, 600, 1200 })
                DispatcherTimer.RunOnce(() => { _video.InvalidateMeasure(); InvalidateMeasure(); }, TimeSpan.FromMilliseconds(ms));
        }

        private void RaiseOverlay()
        {
            foreach (var owned in OwnedWindows)
            {
                if (owned is not Window overlay || !overlay.IsVisible) continue;
                overlay.Hide();
                overlay.Show(this);
            }
        }

        private void Refresh()
        {
            long time = Math.Max(0, _player.Time);
            long length = Math.Max(0, _player.Length);
            var state = _player.State;
            bool playing = _mode == ViewMode.Photo ? _slideshowRunning : state is VLCState.Playing or VLCState.Buffering;

            _updating = true;
            try
            {
                var icon = playing ? PauseGeometry : PlayGeometry;
                if (!ReferenceEquals(_playPauseIcon.Data, icon)) _playPauseIcon.Data = icon;
                if (_mode != ViewMode.Photo)
                {
                    if (!_seeking)
                    {
                        _seek.Maximum = Math.Max(1, length / 1000.0);
                        _seek.Value = Math.Min(_seek.Maximum, time / 1000.0);
                        _timeText.Text = Format(time);
                    }
                    _durationText.Text = Format(length);
                    // Track buttons only when there is a choice (libvlc lists "Disable" too).
                    if (state is VLCState.Playing or VLCState.Paused)
                    {
                        _subtitles.IsVisible = _player.SpuCount > 0;
                        _audioTracks.IsVisible = _player.AudioTrackCount > 2;
                    }
                }
                if (!_volume.IsPointerOver) _volume.Value = Math.Clamp(_player.Volume, 0, 100);
            }
            finally { _updating = false; }

            if (_resumeToast.IsVisible && DateTime.UtcNow - _resumeShownAt > TimeSpan.FromSeconds(10)) _resumeToast.IsVisible = false;

            // In full screen (and always for music and photos), tuck the controls and cursor away.
            bool hide = (IsFullscreen || _mode != ViewMode.Video) && !_seeking && !_bar.IsPointerOver && !_resumeToast.IsVisible
                        && DateTime.UtcNow - _lastActivity > HideControlsAfter && playing;
            _bar.Opacity = hide ? 0 : 1;
            SetCursorHidden(hide);

            var keysStatus = playing ? WindowsMediaControls.Status.Playing
                           : state == VLCState.Paused || _mode == ViewMode.Photo ? WindowsMediaControls.Status.Paused
                           : WindowsMediaControls.Status.Changing;
            if (_mediaKeys != null && keysStatus != _lastKeysStatus)
            {
                _lastKeysStatus = keysStatus;
                try { _mediaKeys.SetStatus(keysStatus); } catch { }
            }
        }

        // ------------------------------------------------------------------ Windows media keys / overlay

        private void SetUpMediaKeys()
        {
            if (!OperatingSystem.IsWindows()) return;
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            _mediaKeys = WindowsMediaControls.TryCreate(hwnd, button => Dispatcher.UIThread.Post(() =>
            {
                switch (button)
                {
                    case WindowsMediaControls.Button.Play:
                    case WindowsMediaControls.Button.Pause: TogglePlay(); break;
                    case WindowsMediaControls.Button.Stop: _ = _engine.StopAsync(); break;
                    case WindowsMediaControls.Button.Next: _engine.RaiseCommand(PlayerCommand.Next); break;
                    case WindowsMediaControls.Button.Previous: _engine.RaiseCommand(PlayerCommand.Previous); break;
                }
            }), _engine.Log);
            UpdateMediaKeysInfo();
        }

        private void UpdateMediaKeysInfo()
        {
            if (_mediaKeys == null || _info == null) return;
            try
            {
                _mediaKeys.SetNextPrevious(_info.CanPrevious, _info.CanNext);
                _mediaKeys.SetInfo(_info.Title, _info.Artist, _info.Album, _info.IsAudio, _info.IsImage, _engine.CoverUrl?.Invoke());
            }
            catch (Exception ex) { _engine.Log($"Windows media overlay: {ex.Message}"); }
        }

        private void NoteActivity()
        {
            _lastActivity = DateTime.UtcNow;
            _bar.Opacity = 1;
            SetCursorHidden(false);
        }

        private void SetCursorHidden(bool hidden)
        {
            if (_cursorHidden == hidden) return;
            _cursorHidden = hidden;
            Cursor = hidden ? HiddenCursor : Cursor.Default;
        }

        private void UpdateFullscreenButton()
        {
            _fullscreenIcon.Data = Geometry.Parse(IsFullscreen ? ExitFullscreenIcon : EnterFullscreenIcon);
            if (_fullscreenIcon.Parent?.Parent is Button b) ToolTip.SetTip(b, IsFullscreen ? "Exit full screen (Esc)" : "Full screen (F)");
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            NoteActivity();
            bool photo = _mode == ViewMode.Photo;
            switch (e.Key)
            {
                case Key.Space or Key.K or Key.MediaPlayPause: TogglePlay(); break;
                case Key.F or Key.F11: ToggleFullscreen(); break;
                case Key.Escape when IsFullscreen: SetFullscreen(false); break;
                case Key.Left or Key.J when photo: _engine.RaiseCommand(PlayerCommand.Previous); break;
                case Key.Right or Key.L when photo: _engine.RaiseCommand(PlayerCommand.Next); break;
                case Key.Left or Key.J: _ = _engine.SeekByAsync(TimeSpan.FromSeconds(-10)); break;
                case Key.Right or Key.L: _ = _engine.SeekByAsync(TimeSpan.FromSeconds(10)); break;
                case Key.N or Key.MediaNextTrack: _engine.RaiseCommand(PlayerCommand.Next); break;
                case Key.P or Key.MediaPreviousTrack: _engine.RaiseCommand(PlayerCommand.Previous); break;
                case Key.C when _subtitles.IsVisible: ShowTrackMenu(_subtitles, subtitles: true); break;
                case Key.A when _audioTracks.IsVisible: ShowTrackMenu(_audioTracks, subtitles: false); break;
                case Key.Up: _ = _engine.SetVolumeAsync(Math.Min(100, _player.Volume + 5)); break;
                case Key.Down: _ = _engine.SetVolumeAsync(Math.Max(0, _player.Volume - 5)); break;
                case Key.MediaStop: _ = _engine.StopAsync(); break;
                default: return;
            }
            e.Handled = true;
        }

        private async void OnClosing(object? sender, WindowClosingEventArgs e)
        {
            _tick.Stop();
            if (_closingAfterStop) return;

            // Closed by the user: stop playback before the video surface goes away.
            e.Cancel = true;
            await _engine.StopFromWindowAsync();
            _closingAfterStop = true;
            Close();
        }

        private static string Format(long ms)
        {
            var t = TimeSpan.FromMilliseconds(ms);
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }
    }
}
