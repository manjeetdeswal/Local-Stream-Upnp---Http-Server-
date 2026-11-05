using System;
using System.Threading;
using System.Threading.Tasks;

namespace LocalStreamPC.Cast
{
    public enum PlayerState { Stopped, Playing, Paused }

    /// <param name="HasVideo">Whether the media has a picture; null while unknown or if the player can't tell.</param>
    public sealed record PlayerStatus(PlayerState State, TimeSpan Position, TimeSpan Duration, int Volume, bool Fullscreen, bool? HasVideo = null);

    /// <summary>Extra things to play with the media.</summary>
    /// <param name="SubtitleUrl">Subtitles from the sender (http).</param>
    /// <param name="SubtitlePath">Subtitles found on this PC next to the video.</param>
    /// <param name="IsImage">A photo: shown with a fade instead of played.</param>
    public sealed record PlayOptions(string? SubtitleUrl = null, string? SubtitlePath = null, bool IsImage = false);

    /// <summary>Buttons in the player window (and Windows' media keys) that the receiver handles.</summary>
    public enum PlayerCommand { Next, Previous, ToggleSlideshow, StartOver }

    /// <summary>
    /// What the cast receiver plays media with: the built-in player window (<see cref="EmbeddedPlayer"/>,
    /// LibVLC inside LocalStream) or an external VLC window (<see cref="VlcController"/>).
    /// </summary>
    public interface IPlaybackEngine : IDisposable
    {
        /// <summary>True while the player window is open.</summary>
        bool IsRunning { get; }

        /// <summary>True if the volume in <see cref="PlayerStatus"/> can be trusted (it may also be changed in the player itself).</summary>
        bool ReportsVolume { get; }

        /// <summary>Opens the player if needed. Returns false (and logs why) if it can't.</summary>
        Task<bool> EnsureStartedAsync(bool fullscreen, CancellationToken ct = default);

        Task PlayAsync(string url, string title, PlayOptions options, CancellationToken ct = default);

        /// <summary>Raised when the user presses next / previous / slideshow in the player.</summary>
        event Action<PlayerCommand>? CommandRequested;

        /// <summary>Shows "Resumed at 12:34 · Start over" in the player (built-in player only).</summary>
        Task ShowResumeNoticeAsync(TimeSpan position);

        /// <summary>Photo slideshow on/off, shown on the play/pause button for photos.</summary>
        Task SetSlideshowAsync(bool running);

        /// <summary>Title, artist and cover to show (the built-in player shows covers for music).</summary>
        Task SetNowPlayingAsync(NowPlayingInfo info);
        Task PauseAsync(CancellationToken ct = default);
        Task ResumeAsync(CancellationToken ct = default);

        /// <summary>Stops playback. The built-in player also closes its window, like a TV going back to idle.</summary>
        Task StopAsync(CancellationToken ct = default);

        Task SeekAsync(TimeSpan position, CancellationToken ct = default);

        /// <param name="volume">0-100</param>
        Task SetVolumeAsync(int volume, CancellationToken ct = default);

        Task SetFullscreenAsync(bool fullscreen, CancellationToken ct = default);

        /// <summary>Current status, or null if the player isn't open.</summary>
        Task<PlayerStatus?> TryGetStatusAsync(CancellationToken ct = default);

        /// <summary>Closes the player completely.</summary>
        Task ShutdownAsync();
    }
}
