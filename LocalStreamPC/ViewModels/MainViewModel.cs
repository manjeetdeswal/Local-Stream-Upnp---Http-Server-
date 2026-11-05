using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LocalStreamPC.Cast;
using LocalStreamPC.Models;
using LocalStreamPC.Server;
using LocalStreamPC.Services;
using QRCoder;

namespace LocalStreamPC.ViewModels
{
    public enum AppTab { Server, Servers, Settings, Remote, History }

    /// <summary>Values from the Settings form. Blank passwords mean "keep the current one".</summary>
    public sealed record SettingsInput(
        string Port, bool RunAtStartup, bool MinimizeToTray, bool AutoStartServer,
        bool EnableAuth, bool AllowDeletion, bool AllowDlnaWithoutLogin,
        string CastPort, bool CastFullscreen, string VlcPath, bool UseBuiltInPlayer,
        bool KeepAwake, bool CastResume, string SlideshowSeconds)
    {
        public bool EnableHttps { get; init; }
        public string HttpsPort { get; init; } = "8443";
        public string HttpsCertPath { get; init; } = "";
        public string HttpsKeyPath { get; init; } = "";
        public string HttpsCertPassword { get; init; } = "";
        public bool UseHardwareEncoding { get; init; } = true;
        public string SpeedLimitMbps { get; init; } = "0";
        public string TmdbApiKey { get; init; } = "";
        public string OpenSubtitlesApiKey { get; init; } = "";
        public string SubtitleLanguages { get; init; } = "en";
        public int CastSubtitleScale { get; init; } = 100;
    }

    /// <summary>
    /// State and behaviour of the main window. The window's code-behind only forwards clicks here
    /// and handles things that are purely visual (dialogs, clipboard, folder picker).
    /// </summary>
    public sealed class MainViewModel : ObservableObject
    {
        private readonly ConfigService _config;
        private readonly AppLog _log;
        private readonly ThumbnailService _thumbs;
        private readonly StartupService _startup;
        private readonly UpdateService _updates;
        private readonly VlcLauncher _vlc;
        private readonly DeviceDiscovery _discovery;
        private readonly SsdpService _ssdp;
        private readonly ServerServices _services;
        private readonly SleepGuard _sleep;
        private MediaServer? _server;

        private UpnpBrowseClient? _remote;
        private string _currentServerName = "";
        private string _currentRemoteContainer = "0";
        private int _loadVersion; // ignores results of a folder load that was superseded

        public MainViewModel(ConfigService config, AppLog log, ThumbnailService thumbs, StartupService startup,
                             UpdateService updates, VlcLauncher vlc, DeviceDiscovery discovery,
                             SsdpService ssdp, CastReceiver castReceiver, ServerServices services, SleepGuard sleep)
        {
            _ssdp = ssdp;
            _services = services;
            _sleep = sleep;
            Cast = new CastViewModel(castReceiver, config);
            Accounts = new AccountsViewModel(config, log);
            Streams = new StreamsViewModel(services.Streams);
            Tv = new TvViewModel(services.Tv, log);
            Shares = new SharesViewModel(config, () => config.Current.Port);
            History = new HistoryViewModel(services.Activity);
            Library = new LibraryViewModel(services.Library, config, log);
            _ = RefreshEncoderTextAsync();
            castReceiver.Changed += () => _sleep.Evaluate();
            _config = config;
            _log = log;
            _thumbs = thumbs;
            _startup = startup;
            _updates = updates;
            _vlc = vlc;
            _discovery = discovery;

            foreach (var f in config.Current.SharedFolders) SharedFolders.Add(f);
            RefreshTabOrderRows();
            SharedFolders.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(ServerListHint)); };
            _isListView = config.Current.IsListView;

            _log.Changed += OnLogChanged;
            OnLogChanged();
        }

        public AppConfig Config => _config.Current;

        /// <summary>The "Cast to this PC" receiver card.</summary>
        public CastViewModel Cast { get; }

        /// <summary>Settings → Web accounts.</summary>
        public AccountsViewModel Accounts { get; }

        /// <summary>The "Streaming now" card.</summary>
        public StreamsViewModel Streams { get; }

        /// <summary>"Playing on TV" card and the "Play on TV…" picker.</summary>
        public TvViewModel Tv { get; }

        /// <summary>"Share links" card.</summary>
        public SharesViewModel Shares { get; }

        /// <summary>The History page.</summary>
        public HistoryViewModel History { get; }

        /// <summary>The "Library" card.</summary>
        public LibraryViewModel Library { get; }

        // ------------------------------------------------------------ collections bound in XAML

        public ObservableCollection<string> SharedFolders { get; } = new();
        public ObservableCollection<ServerUrlItem> ActiveServerUrls { get; } = new();
        public ObservableCollection<UpnpDevice> DiscoveredDevices { get; } = new();
        public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = new();
        public ObservableCollection<RemoteItem> RemoteItems { get; } = new();
        public ObservableCollection<RemoteItem> RootItems { get; } = new();
        public ObservableCollection<RemoteItem> ContinueWatchingItems { get; } = new();
        public ObservableCollection<RemoteItem> RandomItems { get; } = new();

        // ------------------------------------------------------------ view state

        private AppTab _currentTab = AppTab.Server;
        public AppTab CurrentTab
        {
            get => _currentTab;
            set
            {
                if (!SetField(ref _currentTab, value)) return;
                OnPropertyChanged(nameof(IsServerTab));
                OnPropertyChanged(nameof(IsServersTab));
                OnPropertyChanged(nameof(IsSettingsTab));
                OnPropertyChanged(nameof(IsRemoteTab));
                OnPropertyChanged(nameof(IsHistoryTab));
                History.IsActive = value == AppTab.History;
                if (value == AppTab.History) History.Refresh();
            }
        }
        public bool IsHistoryTab => CurrentTab == AppTab.History;
        public bool IsServerTab => CurrentTab == AppTab.Server;
        public bool IsServersTab => CurrentTab == AppTab.Servers;
        public bool IsSettingsTab => CurrentTab == AppTab.Settings;
        public bool IsRemoteTab => CurrentTab == AppTab.Remote;

        // ------------------------------------------------------------ sidebar tabs

        /// <summary>Every sidebar tab: its tag in the window and its name in Settings.</summary>
        public static readonly IReadOnlyList<(string Tag, string Name)> AllTabs = new[]
        {
            ("TabLibrary", "Library"), ("TabServer", "Home (dashboard)"), ("TabHistory", "History"), ("TabSettings", "Settings"),
        };

        /// <summary>Settings → "Sidebar order" rows.</summary>
        public ObservableCollection<TabOrderRow> TabOrderRows { get; } = new();

        /// <summary>The window re-orders the sidebar when this fires.</summary>
        public event Action? TabOrderChanged;

        /// <summary>The saved order, with unknown tags dropped and any missing tab added at the end.</summary>
        public IReadOnlyList<string> TabOrder
        {
            get
            {
                var known = AllTabs.Select(t => t.Tag).ToList();
                var order = (Config.TabOrder ?? new()).Where(known.Contains).Distinct().ToList();
                order.AddRange(known.Where(t => !order.Contains(t)));
                return order;
            }
        }

        /// <summary>The tab the app opens on: the first one, but Home while no folder is shared yet.</summary>
        public string StartTab => TabOrder[0] == "TabLibrary" && SharedFolders.Count == 0 ? "TabServer" : TabOrder[0];

        public void MoveTab(string tag, int delta)
        {
            var order = TabOrder.ToList();
            int i = order.IndexOf(tag), j = i + delta;
            if (i < 0 || j < 0 || j >= order.Count) return;
            (order[i], order[j]) = (order[j], order[i]);
            Config.TabOrder = order;
            _config.Save();
            RefreshTabOrderRows();
            TabOrderChanged?.Invoke();
        }

        private void RefreshTabOrderRows()
        {
            var order = TabOrder;
            TabOrderRows.Clear();
            for (int i = 0; i < order.Count; i++)
                TabOrderRows.Add(new TabOrderRow
                {
                    Tag = order[i], Name = AllTabs.First(t => t.Tag == order[i]).Name,
                    Position = $"{i + 1}.", CanMoveUp = i > 0, CanMoveDown = i < order.Count - 1,
                });
        }

        // ------------------------------------------------------------ library tab

        private int _runningPort;
        private bool _browsingOwn;
        private bool _wantsOwnLibrary; // Library was opened before the server was running

        /// <summary>The server being browsed ("This PC" for our own).</summary>
        private string _browsingName = "";
        public string BrowsingName { get => _browsingName; private set => SetField(ref _browsingName, value); }

        private string _remoteError = "";
        public string RemoteError { get => _remoteError; private set { if (SetField(ref _remoteError, value)) OnPropertyChanged(nameof(HasRemoteError)); } }
        public bool HasRemoteError => RemoteError.Length > 0;

        /// <summary>The server list shows "Back to library" while a library is open.</summary>
        public bool IsConnected => _remote != null;

        public string ServerListHint => IsServerRunning ? "" : SharedFolders.Count == 0
            ? "Add a folder on the Home tab and start the server to browse your own library here."
            : "Your server is off. Start it to browse your own library here.";
        public bool HasServerListHint => !IsServerRunning;

        /// <summary>Our own server, browsed the same way as any other (through its DLNA address on this PC).</summary>
        public UpnpDevice? OwnDevice => !IsServerRunning ? null : new UpnpDevice
        {
            FriendlyName = $"Local Stream PC ({Environment.MachineName})", // same name as on the network, so history matches
            IpAddress = "127.0.0.1",
            BaseUrl = $"http://127.0.0.1:{_runningPort}",
            ContentDirectoryUrl = "/control/ContentDirectory",
            IsThisPc = true,
        };

        /// <summary>True when a discovered device is this PC's own server.</summary>
        public bool IsOwnDevice(UpnpDevice d)
        {
            if (!IsServerRunning || !Uri.TryCreate(d.BaseUrl, UriKind.Absolute, out var uri) || uri.Port != _runningPort) return false;
            if (uri.IsLoopback) return true;
            return NetworkHelper.GetActiveIPv4Addresses().Any(i => i.Ip == uri.Host) || NetworkHelper.GetTailscaleAddress() == uri.Host;
        }

        /// <summary>Library tab: what is open, else this PC's own library, else the server list.</summary>
        public void OpenLibrary()
        {
            if (_remote != null) { CurrentTab = AppTab.Remote; return; }
            if (OwnDevice is UpnpDevice own) { Connect(own); return; }
            _wantsOwnLibrary = true;
            ShowServerList();
        }

        /// <summary>"Other servers": the list of servers on the network (the open library stays open).</summary>
        public void ShowServerList()
        {
            CurrentTab = AppTab.Servers;
            OnPropertyChanged(nameof(IsConnected));
            if (DiscoveredDevices.Count == 0 || DiscoveredDevices.All(d => d.IsThisPc)) _ = ScanAsync();
        }

        public void ConnectOwn()
        {
            if (OwnDevice is UpnpDevice own) Connect(own);
        }

        /// <summary>The servers for the "switch server" menu: this PC first, then the rest of the network.</summary>
        public IReadOnlyList<UpnpDevice> SwitchableServers
        {
            get
            {
                var list = new List<UpnpDevice>();
                if (OwnDevice is UpnpDevice own) list.Add(own);
                list.AddRange(DiscoveredDevices.Where(d => !d.IsThisPc && !IsOwnDevice(d)));
                return list;
            }
        }

        private bool _isRemoteHomeVisible = true;
        public bool IsRemoteHomeVisible
        {
            get => _isRemoteHomeVisible;
            private set { if (SetField(ref _isRemoteHomeVisible, value)) OnPropertyChanged(nameof(IsRemoteFolderVisible)); }
        }
        public bool IsRemoteFolderVisible => !IsRemoteHomeVisible;

        private bool _isListView;
        public bool IsListView
        {
            get => _isListView;
            private set { if (SetField(ref _isListView, value)) OnPropertyChanged(nameof(IsGridView)); }
        }
        public bool IsGridView => !IsListView;

        private bool _hasContinueWatching;
        public bool HasContinueWatching { get => _hasContinueWatching; private set => SetField(ref _hasContinueWatching, value); }

        private bool _hasRecommendations;
        public bool HasRecommendations { get => _hasRecommendations; private set => SetField(ref _hasRecommendations, value); }

        private bool _isRemoteLoading;
        public bool IsRemoteLoading { get => _isRemoteLoading; private set => SetField(ref _isRemoteLoading, value); }

        private bool _isScanning;
        public bool IsScanning { get => _isScanning; private set => SetField(ref _isScanning, value); }

        private bool _isServerRunning;
        public bool IsServerRunning
        {
            get => _isServerRunning;
            private set
            {
                if (!SetField(ref _isServerRunning, value)) return;
                OnPropertyChanged(nameof(ServerListHint));
                OnPropertyChanged(nameof(HasServerListHint));
            }
        }

        private string _logText = "";
        public string LogText { get => _logText; private set => SetField(ref _logText, value); }

        private bool _logUpdateQueued;
        private void OnLogChanged()
        {
            // Coalesce bursts of log lines into one UI update.
            if (_logUpdateQueued) return;
            _logUpdateQueued = true;
            Dispatcher.UIThread.Post(() =>
            {
                _logUpdateQueued = false;
                LogText = _log.GetText();
            }, DispatcherPriority.Background);
        }

        public void Log(string message) => _log.Write(message);

        // ------------------------------------------------------------ shared folders

        public void AddSharedFolders(IEnumerable<string> paths)
        {
            foreach (var path in paths)
            {
                if (SharedFolders.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
                _config.AddSharedFolder(path);
                SharedFolders.Add(path);
                _log.Write($"Added: {path}");
            }
            _config.Save();
            if (IsServerRunning) _ = _services.Library.GetAsync(); // read the new folder now
            Library.Refresh();
        }

        public void RemoveSharedFolder(string path)
        {
            _config.RemoveSharedFolder(path);
            SharedFolders.Remove(path);
            _config.Save();
            _log.Write($"Removed: {path}");
            if (IsServerRunning) _ = _services.Library.GetAsync();
            Library.Refresh();
        }

        // ------------------------------------------------------------ server

        public async Task<bool> StartServerAsync()
        {
            if (IsServerRunning) return true;
            if (_config.SharedRoots.Count == 0)
            {
                _log.Write("Please add at least one folder first.");
                return false;
            }

            var ips = NetworkHelper.GetActiveIPv4Addresses();
            _server = new MediaServer(_config, _log, _thumbs, _ssdp, _services);
            if (!await _server.StartAsync(ips))
            {
                _server = null;
                return false;
            }

            ActiveServerUrls.Clear();
            var all = ips.ToList();
            if (NetworkHelper.GetTailscaleAddress() is string ts && all.All(i => i.Ip != ts)) all.Add((ts, "Tailscale"));
            foreach (var (ip, name) in all)
            {
                bool tailscale = NetworkHelper.IsTailscale(ip);
                string label = tailscale ? "Tailscale · works away from home on your Tailscale devices" : name;
                string url = $"http://{ip}:{Config.Port}/";
                ActiveServerUrls.Add(new ServerUrlItem { NetworkName = label, Url = url, QrBitmap = GenerateQrCode(url) });
                if (Config.EnableHttps)
                {
                    string secure = $"https://{ip}:{Config.HttpsPort}/";
                    ActiveServerUrls.Add(new ServerUrlItem { NetworkName = label + " · secure (HTTPS)", Url = secure, QrBitmap = GenerateQrCode(secure) });
                }
            }
            _log.Write($"Reachable at: {string.Join(", ", ips.Select(i => i.Ip))}");
            _runningPort = Config.Port;
            IsServerRunning = true;
            _ = _services.Library.GetAsync(); // build the library right away (Music, Movies, search are ready sooner)
            AddOwnToServerList();
            if (_wantsOwnLibrary && _remote == null && CurrentTab == AppTab.Servers) ConnectOwn();
            return true;
        }

        public async Task StopServerAsync()
        {
            if (_server != null)
            {
                await _server.StopAsync();
                _server = null;
            }
            ActiveServerUrls.Clear();
            IsServerRunning = false;
            foreach (var d in DiscoveredDevices.Where(d => d.IsThisPc).ToList()) DiscoveredDevices.Remove(d);
            if (_browsingOwn)
            {
                bool wasOpen = CurrentTab == AppTab.Remote;
                CloseRemote(showList: wasOpen);
                _wantsOwnLibrary = true;
            }
        }

        private void AddOwnToServerList()
        {
            if (OwnDevice is not UpnpDevice own || DiscoveredDevices.Any(d => d.IsThisPc)) return;
            foreach (var d in DiscoveredDevices.Where(IsOwnDevice).ToList()) DiscoveredDevices.Remove(d);
            DiscoveredDevices.Insert(0, own);
        }

        private Bitmap? GenerateQrCode(string url)
        {
            try
            {
                using var generator = new QRCodeGenerator();
                using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
                using var png = new PngByteQRCode(data);
                using var ms = new MemoryStream(png.GetGraphic(5));
                return new Bitmap(ms);
            }
            catch (Exception ex)
            {
                _log.Write($"QR code error: {ex.Message}");
                return null;
            }
        }

        // ------------------------------------------------------------ settings

        /// <returns>An error to show, or null when saved.</returns>
        public string? SaveSettings(SettingsInput input)
        {
            if (!int.TryParse(input.Port, out int port) || port < 1 || port > 65535)
                return "Port must be a number between 1 and 65535.";
            if (!int.TryParse(input.SlideshowSeconds, out int slideSeconds) || slideSeconds < 2 || slideSeconds > 120)
                return "Slideshow time must be between 2 and 120 seconds.";
            if (!int.TryParse(input.CastPort, out int castPort) || castPort < 1 || castPort > 65535)
                return "Cast receiver port must be a number between 1 and 65535.";
            if (castPort == port)
                return "The cast receiver needs a different port from the server.";
            string vlcPath = input.VlcPath.Trim().Trim('"');
            if (vlcPath.Length > 0 && !System.IO.File.Exists(vlcPath))
                return "VLC wasn't found at that location. Leave it blank to detect VLC automatically.";
            if (!int.TryParse(input.HttpsPort, out int httpsPort) || httpsPort < 1 || httpsPort > 65535)
                return "The HTTPS port must be a number between 1 and 65535.";
            if (input.EnableHttps && (httpsPort == port || httpsPort == castPort))
                return "HTTPS needs its own port (different from the server and cast receiver ports).";
            string certPath = input.HttpsCertPath.Trim().Trim('"'), keyPath = input.HttpsKeyPath.Trim().Trim('"');
            if (certPath.Length > 0 && !System.IO.File.Exists(certPath))
                return "The certificate file wasn't found. Leave it blank to use a self-signed certificate.";
            if (keyPath.Length > 0 && !System.IO.File.Exists(keyPath))
                return "The certificate key file wasn't found.";
            if (!int.TryParse(input.SpeedLimitMbps, out int speed) || speed < 0 || speed > 10000)
                return "The speed limit must be a number of megabits per second (0 = no limit).";
            string langs = string.Join(",", input.SubtitleLanguages.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                                .Select(l => l.Trim().ToLowerInvariant()).Where(l => l.Length is >= 2 and <= 7).Distinct());
            if (langs.Length == 0) langs = "en";

            bool httpsChanged = input.EnableHttps != Config.EnableHttps || httpsPort != Config.HttpsPort || certPath != (Config.HttpsCertPath ?? "") || keyPath != (Config.HttpsKeyPath ?? "") || input.HttpsCertPassword != (Config.HttpsCertPassword ?? "");
            bool tmdbChanged = input.TmdbApiKey.Trim() != (Config.TmdbApiKey ?? "");
            Config.EnableHttps = input.EnableHttps;
            Config.HttpsPort = httpsPort;
            Config.HttpsCertPath = certPath;
            Config.HttpsKeyPath = keyPath;
            Config.HttpsCertPassword = input.HttpsCertPassword;
            if (Config.UseHardwareEncoding != input.UseHardwareEncoding) _log.Write(input.UseHardwareEncoding ? "Live conversion will use the graphics card when it can." : "Live conversion will use the processor.");
            Config.UseHardwareEncoding = input.UseHardwareEncoding;
            Config.StreamSpeedLimitMbps = speed;
            Config.TmdbApiKey = input.TmdbApiKey.Trim();
            Config.OpenSubtitlesApiKey = input.OpenSubtitlesApiKey.Trim();
            Config.SubtitleLanguages = langs;
            Config.CastSubtitleScale = Math.Clamp(input.CastSubtitleScale, 50, 300);
            if (tmdbChanged) _services.Tmdb.RetryMisses();

            bool portChanged = port != Config.Port;
            bool castPortChanged = castPort != Config.CastPort;
            Config.CastPort = castPort;
            Config.CastFullscreen = input.CastFullscreen;
            Config.UseBuiltInPlayer = input.UseBuiltInPlayer;
            Config.VlcPath = vlcPath.Length > 0 ? vlcPath : null;
            Config.Port = port;
            Config.RunAtStartup = input.RunAtStartup;
            Config.MinimizeToTray = input.MinimizeToTray;
            Config.AutoStartServer = input.AutoStartServer;
            Config.EnableAuth = input.EnableAuth;
            Config.AllowDeletion = input.AllowDeletion;
            Config.AllowDlnaWithoutLogin = input.AllowDlnaWithoutLogin;
            Config.KeepAwakeWhileStreaming = input.KeepAwake;
            Config.CastResume = input.CastResume;
            Config.SlideshowSeconds = slideSeconds;
            _config.Save();
            _sleep.Evaluate();
            _startup.SetStartup(Config.RunAtStartup);

            _ = Cast.ApplySettingsAsync();
            _ = RefreshEncoderTextAsync();
            _log.Write("Settings saved." + ((portChanged || httpsChanged) && IsServerRunning ? " Restart the server to use the new port or HTTPS settings." : "")
                                         + (castPortChanged && Cast.IsReceiving ? " Turn the cast receiver off and on to use its new port." : ""));
            if (!Config.EnableAuth) _log.Write("Note: web login is off - anyone on your network can manage your shared files.");
            return null;
        }

        private string _encoderText = "";
        /// <summary>What live conversion uses on this PC ("NVIDIA graphics (NVENC)", "the processor (x264)").</summary>
        public string EncoderText { get => _encoderText; private set => SetField(ref _encoderText, value); }

        public async Task RefreshEncoderTextAsync()
        {
            if (!FfmpegTools.CanConvert) { EncoderText = "ffmpeg is missing, so nothing is converted."; return; }
            string? e = await _services.Transcoder.EncoderAsync();
            string text = $"Converting with {FfmpegTools.Describe(e)}.";
            if (Config.UseHardwareEncoding && !FfmpegTools.IsHardware(e)) text += " No graphics card encoder works on this PC.";
            Dispatcher.UIThread.Post(() => EncoderText = text);
        }

        public void SetDarkMode(bool isDark)
        {
            Config.IsDarkMode = isDark;
            _config.Save();
        }

        public void SaveGridSize(int index)
        {
            if (Config.GridSizeIndex == index) return;
            Config.GridSizeIndex = index;
            _config.Save();
        }

        public void ToggleListView()
        {
            IsListView = !IsListView;
            Config.IsListView = IsListView;
            _config.Save();
        }

        public Task<UpdateInfo> CheckForUpdatesAsync() => _updates.CheckAsync();

        public Task<string> DownloadUpdateAsync(UpdateInfo info, IProgress<double> progress)
            => _updates.DownloadInstallerAsync(info, progress, System.Threading.CancellationToken.None);

        public void SkipUpdate(string version)
        {
            Config.SkippedUpdate = version;
            _config.Save();
            _log.Write($"Version {version} will not be offered again. Use the update button to check by hand.");
        }

        // ------------------------------------------------------------ discovery

        public async Task ScanAsync()
        {
            if (IsScanning) return;
            IsScanning = true;
            DiscoveredDevices.Clear();
            AddOwnToServerList();
            try
            {
                await _discovery.ScanAsync(device => Dispatcher.UIThread.Post(() =>
                {
                    if (IsOwnDevice(device)) return; // already listed first as "This PC"
                    if (!DiscoveredDevices.Any(d => d.FriendlyName == device.FriendlyName && d.BaseUrl == device.BaseUrl))
                        DiscoveredDevices.Add(device);
                }));
            }
            catch (Exception ex)
            {
                _log.Write($"Scan failed: {ex.Message}");
            }
            finally
            {
                IsScanning = false;
            }
        }

        // ------------------------------------------------------------ remote browsing

        public void Connect(UpnpDevice device)
        {
            if (string.IsNullOrEmpty(device.ContentDirectoryUrl)) return;
            _loadVersion++;
            _remote = new UpnpBrowseClient(device.BaseUrl, device.ContentDirectoryUrl);
            _currentServerName = device.FriendlyName;
            _browsingOwn = device.IsThisPc || IsOwnDevice(device);
            _wantsOwnLibrary = false;
            BrowsingName = _browsingOwn ? "This PC" : device.FriendlyName;
            OnPropertyChanged(nameof(IsConnected));

            Breadcrumbs.Clear();
            Breadcrumbs.Add(new BreadcrumbItem { Id = "0", Title = _browsingOwn ? "My library" : device.FriendlyName, IsNotFirst = false, IsLast = true });
            RemoteItems.Clear();
            RootItems.Clear();
            CurrentTab = AppTab.Remote;
            _ = LoadRemoteFolderAsync("0");
        }

        public void CloseRemote(bool showList = true)
        {
            _loadVersion++;
            if (showList) CurrentTab = AppTab.Servers;
            Breadcrumbs.Clear();
            RemoteItems.Clear();
            RootItems.Clear();
            _remote = null;
            _browsingOwn = false;
            BrowsingName = "";
            RemoteError = "";
            OnPropertyChanged(nameof(IsConnected));
        }

        public void OpenItem(RemoteItem item)
        {
            if (item.IsFolder)
            {
                if (Breadcrumbs.Count > 0) Breadcrumbs.Last().IsLast = false;
                Breadcrumbs.Add(new BreadcrumbItem { Id = item.Id, Title = item.Title, IsNotFirst = true, IsLast = true });
                _ = LoadRemoteFolderAsync(item.Id);
            }
            else
            {
                Play(new[] { item });
            }
        }

        public void NavigateToBreadcrumb(string id)
        {
            var crumb = Breadcrumbs.FirstOrDefault(b => b.Id == id);
            if (crumb == null || crumb.IsLast) return;
            int index = Breadcrumbs.IndexOf(crumb);
            while (Breadcrumbs.Count > index + 1) Breadcrumbs.RemoveAt(Breadcrumbs.Count - 1);
            crumb.IsLast = true;
            _ = LoadRemoteFolderAsync(id);
        }

        /// <summary>Mouse "back" button: up one folder, or out of the server at its root.</summary>
        public void GoBack()
        {
            if (!IsRemoteTab) return;
            if (Breadcrumbs.Count > 1)
            {
                Breadcrumbs.RemoveAt(Breadcrumbs.Count - 1);
                var parent = Breadcrumbs.Last();
                parent.IsLast = true;
                _ = LoadRemoteFolderAsync(parent.Id);
            }
            else
            {
                ShowServerList();
            }
        }

        public void ShowAllHistory()
        {
            PushVirtualFolder("HISTORY_ALL", "Continue Watching");
            foreach (var item in HistoryItems(int.MaxValue)) RemoteItems.Add(item);
        }

        public void ShowAllRecommendations()
        {
            var items = RandomItems.ToList();
            PushVirtualFolder("RANDOM_ALL", "Discover");
            foreach (var item in items) RemoteItems.Add(item);
        }

        private void PushVirtualFolder(string id, string title)
        {
            _loadVersion++;
            if (Breadcrumbs.Count > 0) Breadcrumbs.Last().IsLast = false;
            Breadcrumbs.Add(new BreadcrumbItem { Id = id, Title = title, IsNotFirst = true, IsLast = true });
            IsRemoteHomeVisible = false;
            RemoteItems.Clear();
            _currentRemoteContainer = id;
        }

        private async Task LoadRemoteFolderAsync(string containerId)
        {
            int version = ++_loadVersion;
            bool isRoot = containerId == "0";
            IsRemoteHomeVisible = isRoot;

            if (containerId.EndsWith("_ALL"))
            {
                // Virtual "See all" folders are rebuilt from local data.
                if (containerId == "HISTORY_ALL") { Breadcrumbs.RemoveAt(Breadcrumbs.Count - 1); ShowAllHistory(); }
                else if (containerId == "RANDOM_ALL") { Breadcrumbs.RemoveAt(Breadcrumbs.Count - 1); ShowAllRecommendations(); }
                return;
            }

            var target = isRoot ? RootItems : RemoteItems;
            target.Clear();
            if (isRoot)
            {
                LoadContinueWatching();
                _ = LoadRecommendationsAsync(version);
            }

            var remote = _remote;
            if (remote == null) return;

            RemoteError = "";
            IsRemoteLoading = true;
            try
            {
                var result = await remote.BrowseAsync(containerId);
                if (version != _loadVersion) return; // user already navigated elsewhere

                target.Clear();
                foreach (var c in result.Containers)
                {
                    var folder = new RemoteItem
                    {
                        Id = c.Id, Title = c.Title, IsFolder = true, TypeIcon = Icons.Folder,
                        Details = string.IsNullOrEmpty(c.ChildCount) ? "Folder" : $"{c.ChildCount} items",
                        ThumbnailUrl = c.ThumbnailUrl,
                    };
                    target.Add(folder);
                    _ = string.IsNullOrEmpty(folder.ThumbnailUrl) ? LoadFolderCoverAsync(remote, folder) : folder.LoadThumbnailAsync();
                }
                foreach (var i in result.Items)
                {
                    var item = new RemoteItem
                    {
                        Id = i.Id, Title = i.Title, IsFolder = false, Url = i.Url, Size = i.Size,
                        TypeIcon = IconForClass(i.UpnpClass), Details = "Media File", ThumbnailUrl = i.ThumbnailUrl,
                    };
                    var history = Config.History.FirstOrDefault(h => h.Url == item.Url);
                    if (history != null) item.WatchProgress = history.Progress;
                    if (!string.IsNullOrEmpty(item.ThumbnailUrl)) _ = item.LoadThumbnailAsync();
                    target.Add(item);
                }
                _currentRemoteContainer = containerId;
            }
            catch (Exception ex)
            {
                if (version == _loadVersion)
                {
                    _log.Write($"Network error: {ex.Message}");
                    bool denied = ex.Message.Contains("401") || ex.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase);
                    RemoteError = denied && _browsingOwn
                        ? "Your library needs a login here. Turn on \"TVs and players can stream without a login\" in Settings to browse it in this window."
                        : $"Couldn't open this folder: {ex.Message}";
                }
            }
            finally
            {
                if (version == _loadVersion) IsRemoteLoading = false;
            }
        }

        /// <summary>Uses the first item's thumbnail inside a folder as the folder's cover.</summary>
        private static async Task LoadFolderCoverAsync(UpnpBrowseClient remote, RemoteItem folder)
        {
            try
            {
                var result = await remote.BrowseAsync(folder.Id, requestedCount: 1, timeout: TimeSpan.FromSeconds(5));
                string? thumb = result.Items.FirstOrDefault()?.ThumbnailUrl;
                if (string.IsNullOrEmpty(thumb)) return;
                folder.ThumbnailUrl = thumb;
                await folder.LoadThumbnailAsync();
            }
            catch { /* keep the folder icon */ }
        }

        private static string IconForClass(string upnpClass)
        {
            if (upnpClass.Contains("video", StringComparison.OrdinalIgnoreCase)) return Icons.Video;
            if (upnpClass.Contains("audio", StringComparison.OrdinalIgnoreCase) || upnpClass.Contains("music", StringComparison.OrdinalIgnoreCase)) return Icons.Audio;
            if (upnpClass.Contains("image", StringComparison.OrdinalIgnoreCase)) return Icons.Image;
            return Icons.File;
        }

        private IEnumerable<RemoteItem> HistoryItems(int max)
        {
            foreach (var h in Config.History.Where(h => h.ServerName == _currentServerName).OrderByDescending(h => h.LastPlayed).Take(max))
            {
                var item = new RemoteItem { Title = h.Title, Url = h.Url, IsFolder = false, ThumbnailUrl = h.ThumbnailUrl, WatchProgress = h.Progress, TypeIcon = Icons.Video };
                _ = item.LoadThumbnailAsync();
                yield return item;
            }
        }

        private void LoadContinueWatching()
        {
            ContinueWatchingItems.Clear();
            foreach (var item in HistoryItems(10)) ContinueWatchingItems.Add(item);
            HasContinueWatching = ContinueWatchingItems.Count > 0;
        }

        private async Task LoadRecommendationsAsync(int version)
        {
            RandomItems.Clear();
            HasRecommendations = false;
            var remote = _remote;
            if (remote == null) return;

            try
            {
                // LocalStream servers support a recursive browse; others need a small crawl.
                var items = (await remote.BrowseAsync("0", recursive: true)).Items
                    .Where(i => !string.IsNullOrEmpty(i.Url)).ToList();
                if (items.Count == 0) items = await CrawlForMediaAsync(remote, "0", 0);
                if (version != _loadVersion) return;

                var pool = items.Where(x => x.Size > 10 * 1024 * 1024).ToList();
                if (pool.Count == 0) pool = items;

                foreach (var i in pool.OrderBy(_ => Random.Shared.Next()).Take(15))
                {
                    var item = new RemoteItem { Id = i.Id, Title = i.Title, Url = i.Url, Size = i.Size, ThumbnailUrl = i.ThumbnailUrl, TypeIcon = Icons.Video };
                    if (!string.IsNullOrEmpty(item.ThumbnailUrl)) _ = item.LoadThumbnailAsync();
                    RandomItems.Add(item);
                }
                HasRecommendations = RandomItems.Count > 0;
            }
            catch { /* recommendations are optional */ }
        }

        private static async Task<List<DidlItem>> CrawlForMediaAsync(UpnpBrowseClient remote, string containerId, int depth)
        {
            var results = new List<DidlItem>();
            if (depth > 4) return results;

            DidlResult children;
            try { children = await remote.BrowseAsync(containerId); }
            catch { return results; }

            results.AddRange(children.Items.Where(i => !string.IsNullOrEmpty(i.Url)));
            var folders = children.Containers
                .OrderByDescending(c => c.Title.Contains("Video", StringComparison.OrdinalIgnoreCase) || c.Title.Contains("All", StringComparison.OrdinalIgnoreCase))
                .Take(3);
            foreach (var folder in folders)
            {
                results.AddRange(await CrawlForMediaAsync(remote, folder.Id, depth + 1));
                if (results.Count >= 30) break;
            }
            return results;
        }

        // ------------------------------------------------------------ playback

        public async Task PlayCurrentFolderAsync()
        {
            var remote = _remote;
            if (remote == null) return;
            IsRemoteLoading = true;
            try
            {
                List<RemoteItem> items;
                if (_currentRemoteContainer.EndsWith("_ALL"))
                {
                    items = RemoteItems.Where(i => !i.IsFolder && !string.IsNullOrEmpty(i.Url)).ToList();
                }
                else
                {
                    var result = await remote.BrowseAsync(_currentRemoteContainer, recursive: true);
                    var media = result.Items.Where(i => !string.IsNullOrEmpty(i.Url)).ToList();
                    if (media.Count == 0) media = await CrawlForMediaAsync(remote, _currentRemoteContainer, 0);
                    items = media.Select(i => new RemoteItem { Title = i.Title, Url = i.Url, Size = i.Size, ThumbnailUrl = i.ThumbnailUrl }).ToList();
                }

                if (items.Count > 0) Play(items);
                else _log.Write("No playable files found.");
            }
            catch (Exception ex)
            {
                _log.Write($"Fetch error: {ex.Message}");
            }
            finally
            {
                IsRemoteLoading = false;
            }
        }

        public void Play(IReadOnlyList<RemoteItem> items)
        {
            if (items.Count == 0) return;
            foreach (var item in items)
            {
                var existing = Config.History.FirstOrDefault(h => h.Url == item.Url) ??
                               Config.History.FirstOrDefault(h => h.Title == item.Title && h.ServerName == _currentServerName);
                if (existing != null)
                {
                    existing.LastPlayed = DateTime.Now;
                    existing.Progress = 50;
                    existing.ServerName = _currentServerName;
                    existing.Url = item.Url;
                }
                else
                {
                    Config.History.Add(new PlaybackHistory
                    {
                        Title = item.Title, Url = item.Url, ThumbnailUrl = item.ThumbnailUrl,
                        Progress = 50, LastPlayed = DateTime.Now, ServerName = _currentServerName,
                    });
                }
                item.WatchProgress = 50;
            }

            // Keep the history from growing forever.
            if (Config.History.Count > 500)
                Config.History = Config.History.OrderByDescending(h => h.LastPlayed).Take(500).ToList();

            _config.Save();
            _vlc.Play(items);
        }

        public void RemoveFromHistory(RemoteItem item)
        {
            var match = Config.History.FirstOrDefault(h => h.Url == item.Url);
            if (match != null)
            {
                Config.History.Remove(match);
                _config.Save();
            }
            ContinueWatchingItems.Remove(item);
            RemoteItems.Remove(item);
            HasContinueWatching = ContinueWatchingItems.Count > 0;
        }

        // ------------------------------------------------------------ lifetime

        public async Task ShutdownAsync()
        {
            _log.Changed -= OnLogChanged;
            await Cast.ShutdownAsync();
            await StopServerAsync();
            _services.History.Flush();
            _services.Marks.Save();
            _services.Tmdb.Save();
            _services.Activity.Save();
            _services.Library.Dispose();
            await _services.Tv.StopAllAsync();
            _services.Tv.Dispose();
            _sleep.Dispose();
        }
    }
}
