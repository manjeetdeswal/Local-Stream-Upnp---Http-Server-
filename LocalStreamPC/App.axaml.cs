using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LocalStreamPC.Server;
using LocalStreamPC.Services;
using LocalStreamPC.ViewModels;

namespace LocalStreamPC
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Closing the dashboard quits the app, even while the player window is open.
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                desktop.MainWindow = new MainWindow(CreateViewModel());
            }

            base.OnFrameworkInitializationCompleted();
        }

        /// <summary>Builds the services and the main ViewModel (the app's composition root).</summary>
        public static MainViewModel CreateViewModel()
        {
            var log = new AppLog();
            var config = new ConfigService(log);
            config.Load();

            var thumbs = new ThumbnailService(log);
            thumbs.CleanCacheInBackground();
            EnsureFfmpegExecutable(log);

            log.Write($"LocalStream {UpdateService.CurrentVersionText} started. Settings: {AppPaths.ConfigFile}");
            if (!FfmpegTools.CanConvert) log.Write("ffmpeg wasn't found: video thumbnails and live conversion are off.");
            var ssdp = new SsdpService(log);
            var cast = new Cast.CastReceiver(config, log, ssdp);
            var transcoder = new Transcoder(log, () => config.Current.UseHardwareEncoding);
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                if (FfmpegTools.CanConvert) log.Write($"Live conversion uses {FfmpegTools.Describe(await transcoder.EncoderAsync())}.");
            });
            var services = new ServerServices
            {
                Streams = new StreamTracker(() => config.Current.StreamSpeedLimitMbps),
                Sessions = new SessionStore(),
                Activity = new ActivityLog(),
                Transcoder = transcoder,
                Hls = new HlsService(log, transcoder),
                Probe = new MediaProbe(),
                Library = new LibraryIndex(config, log),
                History = new WatchHistory(),
                Marks = new UserLibrary(),
                Tmdb = new TmdbClient(() => config.Current.TmdbApiKey, log),
                Subtitles = new OpenSubtitlesClient(() => config.Current.OpenSubtitlesApiKey),
                Covers = new CoverFinder(() => config.Current.FindCoversOnline, log),
                Tv = new TvCastService(log, new RendererDiscovery(() => config.Current.RendererUuid)),
            };

            services.WireHistory();
            thumbs.AudioCoverFallback = path => services.Covers.FindFile(path, services.Library.TagsForPath(path));

            // Stay awake while anything plays; keep the screen on too while a cast video plays here.
            var sleep = new SleepGuard(log, () =>
            {
                if (!config.Current.KeepAwakeWhileStreaming) return SleepGuard.Level.None;
                var c = cast.Snapshot;
                if (c.State == Cast.TransportState.Playing && !c.IsAudio) return SleepGuard.Level.Display;
                bool busy = services.Streams.IsBusy || services.Transcoder.Running > 0 || services.Hls.Running > 0 || services.Tv.IsPlayingSomething
                            || c.State is Cast.TransportState.Playing or Cast.TransportState.Transitioning;
                return busy ? SleepGuard.Level.System : SleepGuard.Level.None;
            });

            return new MainViewModel(config, log, thumbs, new StartupService(log), new UpdateService(),
                                     new VlcLauncher(log), new DeviceDiscovery(log), ssdp, cast, services, sleep);
        }

        /// <summary>Unzipping on Linux/macOS often drops the execute bit from the bundled ffmpeg.</summary>
        private static void EnsureFfmpegExecutable(AppLog log)
        {
            if (System.OperatingSystem.IsWindows()) return;
            foreach (var path in new[] { AppPaths.FfmpegPath, AppPaths.FfprobePath })
            {
                try
                {
                    if (!System.IO.File.Exists(path)) continue;
                    var mode = System.IO.File.GetUnixFileMode(path);
                    const System.IO.UnixFileMode exec = System.IO.UnixFileMode.UserExecute | System.IO.UnixFileMode.GroupExecute | System.IO.UnixFileMode.OtherExecute;
                    if ((mode & System.IO.UnixFileMode.UserExecute) == 0)
                        System.IO.File.SetUnixFileMode(path, mode | exec);
                }
                catch (System.Exception ex)
                {
                    log.Write($"Could not make {System.IO.Path.GetFileName(path)} executable: {ex.Message}");
                }
            }
        }
    }
}
