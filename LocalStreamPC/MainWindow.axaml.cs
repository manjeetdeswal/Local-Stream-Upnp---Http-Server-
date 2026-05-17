using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage; // For Cross-platform folder picking
using Avalonia.Threading; // For UI Thread updates
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices; // To detect OS (Windows vs Linux)
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using TagLib; // For Audio Metadata
using Xabe.FFmpeg;
using File = System.IO.File; // For Video Snapshots

// Note: You still need the "System.Drawing.Common" and "Microsoft.Win32.Registry" NuGet packages 
// installed for the specific Windows logic to work, but the code guards against crashes on Linux.

namespace LocalStreamPC
{
    public partial class MainWindow : Window
    {
        private HttpListener _httpListener;
        private CancellationTokenSource _serverTokenSource;
        private string _sharedPath => Config.SharedFolders.Count > 0
                               ? Config.SharedFolders[0]
                               : AppDomain.CurrentDomain.BaseDirectory;
        private bool _isServerRunning = false;
        private const int PORT = 8080;
        private static readonly string UDN = "uuid:" + Guid.NewGuid().ToString();
        private string _localIp;
        // Limit FFmpeg to 1 active process to prevent server freeze
        private static readonly SemaphoreSlim _thumbLock = new SemaphoreSlim(1, 1);
        public ObservableCollection<ServerUrlItem> ActiveServerUrls { get; set; } = new ObservableCollection<ServerUrlItem>();
        private List<(string Ip, string Name)> _localIps = new List<(string Ip, string Name)>();


        public static AppConfig Config { get; private set; }
        private string _configPath = "config.json";

       

        private string _serverUuid = Guid.NewGuid().ToString();

        public ObservableCollection<UpnpDevice> DiscoveredDevices { get; set; } = new ObservableCollection<UpnpDevice>();

        public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; set; } = new ObservableCollection<BreadcrumbItem>();

        // remote var

        private string _remoteControlUrl;
        private string _remoteBaseUrl;
        private Stack<string> _remoteHistory = new Stack<string>();
        private string _currentRemoteContainer = "0";
        private bool _isRemoteGridView = false;
        public ObservableCollection<RemoteItem> RemoteItems { get; set; } = new ObservableCollection<RemoteItem>();

        private bool _isListView = false;

        private string _currentServerName = "";


        // Collections for the Home Page
        public ObservableCollection<RemoteItem> RootItems { get; set; } = new ObservableCollection<RemoteItem>();
        public ObservableCollection<RemoteItem> ContinueWatchingItems { get; set; } = new ObservableCollection<RemoteItem>();
        public ObservableCollection<RemoteItem> RandomItems { get; set; } = new ObservableCollection<RemoteItem>();

        // Grid Size Properties
        public static readonly StyledProperty<double> GridItemWidthProperty = AvaloniaProperty.Register<MainWindow, double>(nameof(GridItemWidth), 180.0);
        public double GridItemWidth
        {
            get => GetValue(GridItemWidthProperty);
            set { SetValue(GridItemWidthProperty, value); GridItemHeight = value / 1.777; } // Keep 16:9 aspect ratio
        }

        public static readonly StyledProperty<double> GridItemHeightProperty = AvaloniaProperty.Register<MainWindow, double>(nameof(GridItemHeight), 101.0);
        public double GridItemHeight
        {
            get => GetValue(GridItemHeightProperty);
            set => SetValue(GridItemHeightProperty, value);
        }






        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            LoadConfig();
            Xabe.FFmpeg.FFmpeg.SetExecutablesPath(AppDomain.CurrentDomain.BaseDirectory);

            string os = "win";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) os = "linux";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) os = "mac";

            // 2. Define Executable Names
            string ffmpegName = os == "win" ? "ffmpeg.exe" : "ffmpeg";
            string ffprobeName = os == "win" ? "ffprobe.exe" : "ffprobe";

            // 3. Set Path
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string ffmpegPath = Path.Combine(baseDir, ffmpegName);

            if (System.IO.File.Exists(ffmpegPath))
            {
                // Tell Xabe where to look
                Xabe.FFmpeg.FFmpeg.SetExecutablesPath(baseDir);

                // On Linux/Mac, we must grant Execute permissions programmatically 
                // because unzipping often removes them.
                if (os != "win")
                {
                    try
                    {
                        System.Diagnostics.Process.Start("chmod", $"+x \"{Path.Combine(baseDir, ffmpegName)}\"");
                        System.Diagnostics.Process.Start("chmod", $"+x \"{Path.Combine(baseDir, ffprobeName)}\"");
                    }
                    catch { /* Ignore if chmod fails */ }
                }
            }


            // 1. Initialize ListBox
            ListDevices.ItemsSource = DiscoveredDevices;
            ListDevices.DoubleTapped += ListDevices_DoubleTapped;

            // 2. Setup Server
            _localIp = GetLocalIpAddress();
            Log($"App initialized. Local IP: {_localIp}");
            Xabe.FFmpeg.FFmpeg.SetExecutablesPath(AppDomain.CurrentDomain.BaseDirectory);
            // 3. Create Tray Icon via Code (Fixes XAML Error)
            CreateTrayIcon();

            CleanThumbnailCache();

            // 4. Check Startup Args
            string[] args = Environment.GetCommandLineArgs();
            if (args.Contains("-minimized") && Config.MinimizeToTray)
            {
                this.WindowState = WindowState.Minimized;
                this.Hide();
            }

            Closing += Window_Closing;
        }



        private void RefreshActiveNetworkIps()
        {
            _localIps.Clear();
            foreach (var netInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (netInterface.OperationalStatus == OperationalStatus.Up &&
                    netInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                {
                    var props = netInterface.GetIPProperties();
                    foreach (var addr in props.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            // Save BOTH the IP and the Network Name
                            _localIps.Add((addr.Address.ToString(), netInterface.Name));
                        }
                    }
                }
            }

            if (_localIps.Count == 0) _localIps.Add(("127.0.0.1", "Localhost"));
        }

        private void CmbGridSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbGridSize == null) return;

            Config.GridSizeIndex = CmbGridSize.SelectedIndex;
            SaveConfig();

            // Updated pixel sizes for the new layout
            switch (CmbGridSize.SelectedIndex)
            {
                case 0: GridItemWidth = 140; break;  // Small
                case 1: GridItemWidth = 180; break;  // Medium
                case 2: GridItemWidth = 240; break;  // Large
                case 3: GridItemWidth = 320; break;  // XL
                case 4: GridItemWidth = 450; break;  // XXL (Massive)
            }
        }



        // Updated parser to target correct lists
        private void ParseRemoteDidl(string xml, bool isRoot)
        {
            try
            {
                var doc = XDocument.Parse(xml);
                var containers = doc.Descendants().Where(e => e.Name.LocalName == "container").ToList();
                var items = doc.Descendants().Where(e => e.Name.LocalName == "item").ToList();

                var targetList = isRoot ? RootItems : RemoteItems;

                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    // CRITICAL FIX: Clear the list immediately before populating to prevent duplicates!
                    targetList.Clear();

                    foreach (var c in containers)
                    {
                        string title = c.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value ?? "Folder";
                        string countStr = c.Attribute("childCount")?.Value;
                        var folder = new RemoteItem { Id = c.Attribute("id")?.Value, Title = title, IsFolder = true, TypeIcon = "📁", Details = string.IsNullOrEmpty(countStr) ? "Folder" : $"{countStr} items" };

                        targetList.Add(folder);
                        _ = FetchFolderThumbnailAsync(folder);
                    }

                    foreach (var i in items)
                    {
                        string title = i.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value ?? "File";
                        var res = i.Elements().FirstOrDefault(e => e.Name.LocalName == "res");
                        var upnpClass = i.Elements().FirstOrDefault(e => e.Name.LocalName == "class")?.Value ?? "";
                        string thumbUrl = i.Elements().FirstOrDefault(e => e.Name.LocalName == "albumArtURI")?.Value;

                        if (!string.IsNullOrEmpty(thumbUrl) && !thumbUrl.StartsWith("http"))
                            thumbUrl = thumbUrl.StartsWith("/") ? _remoteBaseUrl.TrimEnd('/') + thumbUrl : _remoteBaseUrl.TrimEnd('/') + "/" + thumbUrl;

                        string icon = "📄";
                        if (upnpClass.Contains("video")) icon = "🎬";
                        else if (upnpClass.Contains("audio") || upnpClass.Contains("music")) icon = "🎵";
                        else if (upnpClass.Contains("image")) icon = "🖼️";

                        var newItem = new RemoteItem { Id = i.Attribute("id")?.Value, Title = title, IsFolder = false, Url = res?.Value, TypeIcon = icon, Details = "Media File", ThumbnailUrl = thumbUrl };

                        var historyMatch = Config.History?.FirstOrDefault(h => h.Url == newItem.Url);
                        if (historyMatch != null) newItem.WatchProgress = historyMatch.Progress;

                        if (!string.IsNullOrEmpty(thumbUrl)) _ = newItem.LoadThumbnailAsync();
                        targetList.Add(newItem);
                    }
                });
            }
            catch { }
        }



        private void CleanThumbnailCache()
        {
            // Run in background so startup isn't slowed down
            Task.Run(() =>
            {
                try
                {
                    string cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cache");
                    if (!Directory.Exists(cacheDir)) return;

                    DirectoryInfo dir = new DirectoryInfo(cacheDir);
                    FileInfo[] files = dir.GetFiles();

                    // 1. Delete files older than 7 days
                    foreach (var file in files)
                    {
                        if (DateTime.Now - file.LastWriteTime > TimeSpan.FromDays(7))
                        {
                            try { file.Delete(); } catch { }
                        }
                    }

                    // 2. Refresh list and check size limit (e.g., 500 MB)
                    files = dir.GetFiles(); // Re-fetch after deletions
                    long totalSize = files.Sum(f => f.Length);
                    long limitBytes = 500 * 1024 * 1024; // 500 MB

                    if (totalSize > limitBytes)
                    {
                        // Delete oldest accessed files first until we are under the limit
                        var sortedFiles = files.OrderBy(f => f.LastAccessTime).ToList();
                        foreach (var file in sortedFiles)
                        {
                            try
                            {
                                file.Delete();
                                totalSize -= file.Length;
                                if (totalSize < limitBytes) break; // Done
                            }
                            catch { }
                        }
                    }

                    // Log($" Cache cleaned. Current size: {totalSize / 1024 / 1024} MB");
                }
                catch (Exception ex)
                {
                    Log($" Cache Cleanup Error: {ex.Message}");
                }
            });
        }






        private void CreateTrayIcon()
        {
            try
            {
                var trayIcon = new TrayIcon
                {
                    // This loads the icon from your project assets
                    Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://LocalStreamPC/Assets/app-icon.ico"))),
                    ToolTipText = "LocalStream Server",
                    IsVisible = true
                };

                // Create Menu
                var menu = new NativeMenu();

                var openItem = new NativeMenuItem("Open Dashboard");
                openItem.Click += (s, e) => {
                    this.Show();
                    this.WindowState = WindowState.Normal;
                    this.Activate();
                };
                menu.Add(openItem);

                var exitItem = new NativeMenuItem("Exit Server");
                exitItem.Click += (s, e) => {
                    Config.MinimizeToTray = false;
                    StopServer();
                    this.Close();
                };
                menu.Add(exitItem);

                trayIcon.Menu = menu;
            }
            catch (Exception ex)
            {
                Log("Error creating tray icon: " + ex.Message);
            }
        }


        private void BtnCopyIp_Click(object sender, RoutedEventArgs e)
        {
            if (_isServerRunning)
            {
                string serverUrl = $"http://{_localIp}:{Config.Port}/";
                var topLevel = TopLevel.GetTopLevel(this);
                topLevel?.Clipboard?.SetTextAsync(serverUrl);
                Log("Server URL copied to clipboard.");
            }
            else
            {
                Log("Please start the server first.");
            }
        }


        private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                btn.Content = "⏳";
                btn.IsEnabled = false;

                try
                {
                    using (var client = new HttpClient())
                    {
                        client.DefaultRequestHeaders.Add("User-Agent", "LocalStream-App");

                        string apiUrl = "https://api.github.com/repos/manjeetdeswal/Local-Stream-Upnp---Http-Server-/releases/latest";
                        string releaseUrl = "https://github.com/manjeetdeswal/Local-Stream-Upnp---Http-Server-/releases/latest";

                        var response = await client.GetStringAsync(apiUrl);

                        using (JsonDocument doc = JsonDocument.Parse(response))
                        {
                            string latestVersion = doc.RootElement.GetProperty("tag_name").GetString();
                            string currentVersion = "1.1";

                            if (latestVersion != currentVersion)
                            {
                                Log($"Update available! Latest: {latestVersion} (Current: {currentVersion})");

                                Dispatcher.UIThread.InvokeAsync(() => {
                                    btn.Content = "⭐";
                                    ToolTip.SetTip(btn, "Update Available!"); // FIXED: Used SetTip method
                                    btn.Background = Avalonia.Media.Brushes.ForestGreen;
                                });

                                // Open Browser
                                try
                                {
                                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                    {
                                        FileName = releaseUrl,
                                        UseShellExecute = true
                                    });
                                }
                                catch (Exception ex)
                                {
                                    Log($"Failed to open browser automatically: {ex.Message}");
                                }
                            }
                            else
                            {
                                Log("You are running the latest version.");
                                Dispatcher.UIThread.InvokeAsync(() => {
                                    btn.Content = "✔️";
                                    ToolTip.SetTip(btn, "Up to date"); // FIXED
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Update check failed: {ex.Message}");
                    Dispatcher.UIThread.InvokeAsync(() => {
                        btn.Content = "❌";
                        ToolTip.SetTip(btn, "Check Failed"); // FIXED
                    });
                }
                finally
                {
                    await Task.Delay(4000);
                    Dispatcher.UIThread.InvokeAsync(() => {
                        if (btn.Content?.ToString() != "⭐")
                        {
                            btn.Content = "🔄";
                            ToolTip.SetTip(btn, "Check for Updates"); // FIXED
                        }
                        btn.IsEnabled = true;
                    });
                }
            }
        }




        private async void BtnAddFolder_Click(object sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Add Shared Folder",
                AllowMultiple = true
            });

            foreach (var folder in folders)
            {
                string path = folder.Path.IsAbsoluteUri ? folder.Path.LocalPath : folder.Path.OriginalString;
                path = Uri.UnescapeDataString(path);

                if (!Config.SharedFolders.Contains(path))
                {
                    Config.SharedFolders.Add(path);
                    Log($" Added: {path}");
                }
            }

            // Refresh List UI
            ListSharedFolders.ItemsSource = null;
            ListSharedFolders.ItemsSource = Config.SharedFolders;
            SaveConfig();
        }

        // 2. REMOVE FOLDER
        private void BtnRemoveFolder_Click(object sender, RoutedEventArgs e)
        {
            if (ListSharedFolders.SelectedItem is string path)
            {
                Config.SharedFolders.Remove(path);
                ListSharedFolders.ItemsSource = null;
                ListSharedFolders.ItemsSource = Config.SharedFolders;
                SaveConfig();
                Log($" Removed: {path}");
            }
        }
       

        private async void BtnScan_Click(object sender, RoutedEventArgs e)
        {
            DiscoveredDevices.Clear();
            BtnScan.Content = "Scanning...";
            BtnScan.IsEnabled = false;
            await ScanForDevices();
            BtnScan.Content = "Scan for Devices";
            BtnScan.IsEnabled = true;
        }

        private void BtnCopyLogs_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(TxtServerLogs.Text))
            {
                // Cross-platform Clipboard access
                var topLevel = TopLevel.GetTopLevel(this);
                topLevel?.Clipboard?.SetTextAsync(TxtServerLogs.Text);
                Log("Logs copied to clipboard.");
            }
        }

        private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
        {
            SaveConfig();
            SetStartup(Config.RunAtStartup);
            Log("Settings saved!");

        }

        private void Nav_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton btn && btn.Tag is string tag)
            {
                TabServer.IsVisible = false;
                var tabServers = this.FindControl<Grid>("TabServers"); // Updated Name
                if (tabServers != null) tabServers.IsVisible = false;
                TabSettings.IsVisible = false;

                var tabRemote = this.FindControl<Grid>("TabRemoteBrowser");
                if (tabRemote != null) tabRemote.IsVisible = false;

                if (tag == "TabServer") TabServer.IsVisible = true;
                if (tag == "TabServers")
                {
                    if (tabServers != null) tabServers.IsVisible = true;

                    // AUTO-SCAN LOGIC: Trigger a scan if the list is empty when clicking the tab
                    if (DiscoveredDevices.Count == 0)
                    {
                        BtnScan_Click(BtnScan, new RoutedEventArgs());
                    }
                }
                if (tag == "TabSettings") TabSettings.IsVisible = true;
            }
        }
        private void BtnConnectServer_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is UpnpDevice device)
            {
                // Re-use your existing connection logic
                ListDevices.SelectedItem = device;
                ListDevices_DoubleTapped(null, null);
            }
        }

        private void ListDevices_DoubleTapped(object sender, Avalonia.Input.TappedEventArgs e)
        {
            if (ListDevices.SelectedItem is UpnpDevice device)
            {
                if (string.IsNullOrEmpty(device.ContentDirectoryUrl)) return;

                _remoteControlUrl = device.ContentDirectoryUrl;
                _remoteBaseUrl = device.BaseUrl;

                // Initialize Breadcrumbs with Root Name
                Breadcrumbs.Clear();
                Breadcrumbs.Add(new BreadcrumbItem { Id = "0", Title = device.FriendlyName, IsNotFirst = false, IsLast = true });

                var listList = this.FindControl<ListBox>("ListRemoteFiles_List");
                var listGrid = this.FindControl<ListBox>("ListRemoteFiles_Grid");
                if (listList != null) listList.ItemsSource = RemoteItems;
                if (listGrid != null) listGrid.ItemsSource = RemoteItems;

                var tabServers = this.FindControl<Grid>("TabServers");
                if (tabServers != null) tabServers.IsVisible = false;

                var tabRemote = this.FindControl<Grid>("TabRemoteBrowser");
                if (tabRemote != null) tabRemote.IsVisible = true;

                LoadRemoteFolder("0");
            }
        }



        private async void LoadRemoteFolder(string containerId)
        {
            var loading = this.FindControl<ProgressBar>("RemoteLoadingBar");
            var homeView = this.FindControl<ScrollViewer>("RemoteHomeScroller");
            var folderView = this.FindControl<Panel>("RemoteFolderView");

            if (loading != null) loading.IsVisible = true;

            // Check if we are at root OR if we clicked a "See All" dummy ID
            bool isRoot = containerId == "0";
            if (containerId.Contains("_ALL")) isRoot = false;

            if (homeView != null) homeView.IsVisible = isRoot;
            if (folderView != null) folderView.IsVisible = !isRoot;

            if (isRoot)
            {
                RootItems.Clear();
                LoadContinueWatching();
                _ = FetchRandomRecommendationsAsync();
            }
            else
            {
                RemoteItems.Clear();
                if (containerId.Contains("_ALL")) return; // Don't run UPnP fetch for "See All" fake folders
            }

            try
            {
                string fullUrl = _remoteBaseUrl.TrimEnd('/') + "/" + _remoteControlUrl.TrimStart('/');
                string soap = $@"<?xml version=""1.0""?>
<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"" s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
  <s:Body>
    <u:Browse xmlns:u=""urn:schemas-upnp-org:service:ContentDirectory:1"">
      <ObjectID>{containerId}</ObjectID>
      <BrowseFlag>BrowseDirectChildren</BrowseFlag>
      <Filter>*</Filter>
      <StartingIndex>0</StartingIndex>
      <RequestedCount>0</RequestedCount>
      <SortCriteria></SortCriteria>
    </u:Browse>
  </s:Body>
</s:Envelope>";

                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
                {
                    var content = new StringContent(soap, Encoding.UTF8, "text/xml");
                    content.Headers.Add("SOAPAction", "\"urn:schemas-upnp-org:service:ContentDirectory:1#Browse\"");
                    var response = await client.PostAsync(fullUrl, content);
                    if (response.IsSuccessStatusCode)
                    {
                        var responseString = await response.Content.ReadAsStringAsync();
                        var soapDoc = XDocument.Parse(responseString);
                        var resultNode = soapDoc.Descendants().FirstOrDefault(n => n.Name.LocalName == "Result");

                        // Pass the 'isRoot' flag so we know which List to populate
                        if (resultNode != null) ParseRemoteDidl(resultNode.Value, isRoot);
                    }
                }
                _currentRemoteContainer = containerId;
            }
            catch (Exception ex) { Log($"Network Error: {ex.Message}"); }
            finally { if (loading != null) loading.IsVisible = false; }
        }

        private void LoadContinueWatching()
        {
            ContinueWatchingItems.Clear();
            if (Config.History == null) return;

            // Filter by ServerName. (We keep a fallback to IP matching just in case you have older history entries)
            var host = "";
            try { host = new Uri(_remoteBaseUrl).Host; } catch { }

            var recent = Config.History
                .Where(h => h.ServerName == _currentServerName || (!string.IsNullOrEmpty(h.Url) && !string.IsNullOrEmpty(host) && h.Url.Contains(host)))
                .OrderByDescending(h => h.LastPlayed)
                .Take(10);

            foreach (var hist in recent)
            {
                var item = new RemoteItem { Title = hist.Title, Url = hist.Url, IsFolder = false, ThumbnailUrl = hist.ThumbnailUrl, WatchProgress = hist.Progress, TypeIcon = "🎬" };
                _ = item.LoadThumbnailAsync();
                ContinueWatchingItems.Add(item);
            }

            var section = this.FindControl<StackPanel>("SectionContinueWatching");
            if (section != null) section.IsVisible = ContinueWatchingItems.Count > 0;
        }



        private async Task FetchRandomRecommendationsAsync()
        {
            RandomItems.Clear();
            try
            {
                // 1. Try deep scan first
                var items = await FetchRecursiveUrlsAsync("0");

                // 2. FALLBACK: Targeted Manual Crawl if deep scan blocked
                if (items.Count == 0)
                {
                   
                    items = await CrawlForMediaAsync("0", 0);
                }

                var largeItems = items.Where(x => x.Size > 10485760).ToList();
                if (largeItems.Count == 0) largeItems = items.Where(x => !x.IsFolder).ToList();

                var random = new Random();
                var shuffled = largeItems.OrderBy(x => random.Next()).Take(15).ToList();

                Dispatcher.UIThread.InvokeAsync(() => {
                    foreach (var item in shuffled)
                    {
                        item.TypeIcon = "🎬";
                        item.IsFolder = false;
                        if (!string.IsNullOrEmpty(item.ThumbnailUrl)) _ = item.LoadThumbnailAsync();
                        RandomItems.Add(item);
                    }

                    var section = this.FindControl<StackPanel>("SectionRecommendations");
                    if (section != null) section.IsVisible = RandomItems.Count > 0;
                });
            }
            catch { }
        }



        



        // --- NEW HELPER: Fetch Direct Children (Shallow Crawl) ---
        private async Task<List<RemoteItem>> FetchDirectChildrenAsync(string containerId)
        {
            var itemsList = new List<RemoteItem>();
            try
            {
                string fullUrl = _remoteBaseUrl.TrimEnd('/') + "/" + _remoteControlUrl.TrimStart('/');
                // Changed RequestedCount to 0 (Some UPnP servers reject requests that try to paginate)
                string soap = $@"<?xml version=""1.0""?>
<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"" s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
  <s:Body>
    <u:Browse xmlns:u=""urn:schemas-upnp-org:service:ContentDirectory:1"">
      <ObjectID>{containerId}</ObjectID>
      <BrowseFlag>BrowseDirectChildren</BrowseFlag>
      <Filter>*</Filter>
      <StartingIndex>0</StartingIndex>
      <RequestedCount>0</RequestedCount>
      <SortCriteria></SortCriteria>
    </u:Browse>
  </s:Body>
</s:Envelope>";

                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
                {
                    var content = new StringContent(soap, Encoding.UTF8, "text/xml");
                    content.Headers.Add("SOAPAction", "\"urn:schemas-upnp-org:service:ContentDirectory:1#Browse\"");
                    var response = await client.PostAsync(fullUrl, content);

                    if (response.IsSuccessStatusCode)
                    {
                        var responseString = await response.Content.ReadAsStringAsync();
                        var soapDoc = XDocument.Parse(responseString);
                        var resultNode = soapDoc.Descendants().FirstOrDefault(n => n.Name.LocalName == "Result");

                        if (resultNode != null)
                        {
                            var doc = XDocument.Parse(resultNode.Value);
                            var xmlItems = doc.Descendants().Where(e => e.Name.LocalName == "item");
                            var xmlContainers = doc.Descendants().Where(e => e.Name.LocalName == "container");

                            foreach (var c in xmlContainers)
                            {
                                var title = c.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value ?? "Folder";
                                itemsList.Add(new RemoteItem { Id = c.Attribute("id")?.Value, Title = title, IsFolder = true });
                            }

                            foreach (var i in xmlItems)
                            {
                                var res = i.Elements().FirstOrDefault(e => e.Name.LocalName == "res");
                                var title = i.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value ?? "Unknown";
                                var thumbUrl = i.Elements().FirstOrDefault(e => e.Name.LocalName == "albumArtURI")?.Value;

                                if (res != null && !string.IsNullOrEmpty(res.Value))
                                {
                                    if (!string.IsNullOrEmpty(thumbUrl) && !thumbUrl.StartsWith("http"))
                                        thumbUrl = thumbUrl.StartsWith("/") ? _remoteBaseUrl.TrimEnd('/') + thumbUrl : _remoteBaseUrl.TrimEnd('/') + "/" + thumbUrl;

                                    var sizeAttr = res.Attribute("size")?.Value;
                                    long.TryParse(sizeAttr ?? "0", out long sizeBytes);

                                    itemsList.Add(new RemoteItem { Url = res.Value, Title = title, Size = sizeBytes, ThumbnailUrl = thumbUrl, IsFolder = false, TypeIcon = "🎬" });
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return itemsList;
        }

        private async Task<List<RemoteItem>> CrawlForMediaAsync(string containerId, int depth)
        {
            var results = new List<RemoteItem>();

            // Increased Depth to 4 to cut through deeply nested mobile trees (Root > Video > All Video > Files)
            if (depth > 4 || results.Count > 30) return results;

            var children = await FetchDirectChildrenAsync(containerId);
            results.AddRange(children.Where(c => !c.IsFolder));

            // Smart Crawler: Prioritize folders named "Video", "Movie", or "All"
            var foldersToCrawl = children.Where(c => c.IsFolder)
                .OrderByDescending(c => c.Title.Contains("Video", StringComparison.OrdinalIgnoreCase) || c.Title.Contains("All", StringComparison.OrdinalIgnoreCase))
                .Take(3);

            foreach (var folder in foldersToCrawl)
            {
                var subFiles = await CrawlForMediaAsync(folder.Id, depth + 1);
                results.AddRange(subFiles);
                if (results.Count >= 30) break;
            }
            return results;
        }

        // --- SEE ALL BUTTON HANDLERS ---
        private void BtnSeeAllHistory_Click(object sender, RoutedEventArgs e)
        {
            if (Breadcrumbs.Count > 0) Breadcrumbs.Last().IsLast = false;
            Breadcrumbs.Add(new BreadcrumbItem { Id = "HISTORY_ALL", Title = "Continue Watching", IsNotFirst = true, IsLast = true });

            LoadRemoteFolder("HISTORY_ALL");

            var host = "";
            try { host = new Uri(_remoteBaseUrl).Host; } catch { }

            var allHistory = Config.History
                .Where(h => h.ServerName == _currentServerName || (!string.IsNullOrEmpty(h.Url) && !string.IsNullOrEmpty(host) && h.Url.Contains(host)))
                .OrderByDescending(h => h.LastPlayed);

            foreach (var hist in allHistory)
            {
                var item = new RemoteItem { Title = hist.Title, Url = hist.Url, IsFolder = false, ThumbnailUrl = hist.ThumbnailUrl, WatchProgress = hist.Progress, TypeIcon = "🎬" };
                _ = item.LoadThumbnailAsync();
                RemoteItems.Add(item);
            }
        }

        private void BtnSeeAllRandom_Click(object sender, RoutedEventArgs e)
        {
            if (Breadcrumbs.Count > 0) Breadcrumbs.Last().IsLast = false;
            Breadcrumbs.Add(new BreadcrumbItem { Id = "RANDOM_ALL", Title = "Discover", IsNotFirst = true, IsLast = true });

            LoadRemoteFolder("RANDOM_ALL");
            foreach (var item in RandomItems) RemoteItems.Add(item);
        }

        // --- CONTEXT MENU CLICK HANDLERS ---
        private void CtxMenuPlay_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.DataContext is RemoteItem item)
                PlayUrlsInVlc(new List<RemoteItem> { item });
        }

        private void CtxMenuRemoveHistory_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.DataContext is RemoteItem item)
            {
                var match = Config.History?.FirstOrDefault(h => h.Url == item.Url);
                if (match != null)
                {
                    Config.History.Remove(match);
                    SaveConfig();
                }

                if (ContinueWatchingItems.Contains(item)) ContinueWatchingItems.Remove(item);
                if (RemoteItems.Contains(item)) RemoteItems.Remove(item); // Remove if inside "See All" view

                var section = this.FindControl<StackPanel>("SectionContinueWatching");
                if (section != null) section.IsVisible = ContinueWatchingItems.Count > 0;
            }
        }

        private void SwitchToSeeAllView()
        {
            var homeView = this.FindControl<ScrollViewer>("RemoteHomeScroller");
            var folderView = this.FindControl<Panel>("RemoteFolderView");
            var btnBack = this.FindControl<Button>("BtnRemoteBack");

            if (homeView != null) homeView.IsVisible = false;
            if (folderView != null) folderView.IsVisible = true;
            if (btnBack != null) btnBack.IsVisible = true;

            _remoteHistory.Push("0"); // "0" tells the Back button to go to Home Page
        }

       




        // Add this brand new method to handle peeking inside folders for images
        private async Task FetchFolderThumbnailAsync(RemoteItem folderItem)
        {
            if (string.IsNullOrEmpty(folderItem.Id)) return;

            try
            {
                string fullUrl = _remoteBaseUrl.TrimEnd('/') + "/" + _remoteControlUrl.TrimStart('/');
                // Request exactly 1 child item to grab its thumbnail for the folder cover
                string soap = $@"<?xml version=""1.0""?>
        <s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"" s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
          <s:Body>
            <u:Browse xmlns:u=""urn:schemas-upnp-org:service:ContentDirectory:1"">
              <ObjectID>{folderItem.Id}</ObjectID>
              <BrowseFlag>BrowseDirectChildren</BrowseFlag>
              <Filter>*</Filter>
              <StartingIndex>0</StartingIndex>
              <RequestedCount>1</RequestedCount>
              <SortCriteria></SortCriteria>
            </u:Browse>
          </s:Body>
        </s:Envelope>";

                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                {
                    var content = new StringContent(soap, Encoding.UTF8, "text/xml");
                    content.Headers.Add("SOAPAction", "\"urn:schemas-upnp-org:service:ContentDirectory:1#Browse\"");
                    var response = await client.PostAsync(fullUrl, content);

                    if (response.IsSuccessStatusCode)
                    {
                        var responseString = await response.Content.ReadAsStringAsync();
                        var soapDoc = XDocument.Parse(responseString);
                        var resultNode = soapDoc.Descendants().FirstOrDefault(n => n.Name.LocalName == "Result");
                        if (resultNode != null)
                        {
                            var innerDoc = XDocument.Parse(resultNode.Value);
                            var firstItem = innerDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "item");
                            if (firstItem != null)
                            {
                                string thumbUrl = firstItem.Elements().FirstOrDefault(e => e.Name.LocalName == "albumArtURI")?.Value;
                                if (!string.IsNullOrEmpty(thumbUrl))
                                {
                                    if (!thumbUrl.StartsWith("http"))
                                    {
                                        thumbUrl = thumbUrl.StartsWith("/") ? _remoteBaseUrl.TrimEnd('/') + thumbUrl : _remoteBaseUrl.TrimEnd('/') + "/" + thumbUrl;
                                    }
                                    folderItem.ThumbnailUrl = thumbUrl;
                                    await folderItem.LoadThumbnailAsync();
                                }
                            }
                        }
                    }
                }
            }
            catch { /* Ignore failures to gracefully fall back to default icon */ }
        }

        // --- UI BUTTON EVENTS ---
        private void BtnRemoteBack_Click(object sender, RoutedEventArgs e)
        {
            if (_remoteHistory.Count > 0) LoadRemoteFolder(_remoteHistory.Pop());
        }

        private void BtnCloseRemote_Click(object sender, RoutedEventArgs e)
        {
            var tabRemote = this.FindControl<Grid>("TabRemoteBrowser");
            if (tabRemote != null) tabRemote.IsVisible = false;

            var tabServers = this.FindControl<Grid>("TabServers");
            if (tabServers != null) tabServers.IsVisible = true;

            Breadcrumbs.Clear(); // Replaces _remoteHistory.Clear()
            RemoteItems.Clear();
        }

        private void BtnRemoteToggleView_Click(object sender, RoutedEventArgs e)
        {
            _isListView = !_isListView;

            // Save to Config
            Config.IsListView = _isListView;
            SaveConfig();

            UpdateViewMode();
        }

        private void ListRemoteFiles_DoubleTapped2(object sender, Avalonia.Input.TappedEventArgs e)
        {
            if (sender is ListBox list && list.SelectedItem is RemoteItem item)
            {
                if (item.IsFolder)
                {
                    if (Breadcrumbs.Count > 0) Breadcrumbs.Last().IsLast = false;
                    Breadcrumbs.Add(new BreadcrumbItem { Id = item.Id, Title = item.Title, IsNotFirst = true, IsLast = true });
                    LoadRemoteFolder(item.Id);
                }
                else
                {
                    PlayUrlsInVlc(new List<RemoteItem> { item });
                }
            }
        }
        private void BtnBreadcrumb_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id)
            {
                var item = Breadcrumbs.FirstOrDefault(b => b.Id == id);
                if (item == null || item.IsLast) return;

                // Slice off the breadcrumbs after the clicked item
                int index = Breadcrumbs.IndexOf(item);
                while (Breadcrumbs.Count > index + 1) Breadcrumbs.RemoveAt(Breadcrumbs.Count - 1);

                item.IsLast = true;
                LoadRemoteFolder(id);
            }
        }

        private async void BtnPlayAll_Click(object sender, RoutedEventArgs e)
        {
            var loading = this.FindControl<ProgressBar>("RemoteLoadingBar");
            if (loading != null) loading.IsVisible = true;

            var items = await FetchRecursiveUrlsAsync(_currentRemoteContainer);
            if (items.Count > 0) PlayUrlsInVlc(items);
            else Log("No playable files found.");

            if (loading != null) loading.IsVisible = false;
        }

        private async void CtxPlaySelected_Click(object sender, RoutedEventArgs e)
        {
            var list = _isRemoteGridView ? this.FindControl<ListBox>("ListRemoteFiles_Grid") : this.FindControl<ListBox>("ListRemoteFiles_List");
            if (list?.SelectedItems == null || list.SelectedItems.Count == 0) return;

            var loading = this.FindControl<ProgressBar>("RemoteLoadingBar");
            if (loading != null) loading.IsVisible = true;

            var finalItems = new List<RemoteItem>();
            foreach (RemoteItem item in list.SelectedItems)
            {
                if (item.IsFolder) finalItems.AddRange(await FetchRecursiveUrlsAsync(item.Id));
                else if (!string.IsNullOrEmpty(item.Url)) finalItems.Add(item);
            }

            if (finalItems.Count > 0) PlayUrlsInVlc(finalItems);
            else Log("No playable files in selection.");

            if (loading != null) loading.IsVisible = false;
        }

        private async Task<List<RemoteItem>> FetchRecursiveUrlsAsync(string containerId)
        {
            var itemsList = new List<RemoteItem>();
            try
            {
                string fullUrl = _remoteBaseUrl.TrimEnd('/') + "/" + _remoteControlUrl.TrimStart('/');
                string soap = $@"<?xml version=""1.0""?>
<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"" s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
  <s:Body>
    <u:Browse xmlns:u=""urn:schemas-upnp-org:service:ContentDirectory:1"">
      <ObjectID>{containerId}</ObjectID>
      <BrowseFlag>BrowseRecursive</BrowseFlag>
      <Filter>*</Filter>
      <StartingIndex>0</StartingIndex>
      <RequestedCount>0</RequestedCount>
      <SortCriteria></SortCriteria>
    </u:Browse>
  </s:Body>
</s:Envelope>";

                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
                {
                    var content = new StringContent(soap, Encoding.UTF8, "text/xml");
                    content.Headers.Add("SOAPAction", "\"urn:schemas-upnp-org:service:ContentDirectory:1#Browse\"");
                    var response = await client.PostAsync(fullUrl, content);

                    if (response.IsSuccessStatusCode)
                    {
                        var responseString = await response.Content.ReadAsStringAsync();
                        var soapDoc = XDocument.Parse(responseString);
                        var resultNode = soapDoc.Descendants().FirstOrDefault(n => n.Name.LocalName == "Result");

                        if (resultNode != null)
                        {
                            var doc = XDocument.Parse(resultNode.Value);
                            var xmlItems = doc.Descendants().Where(e => e.Name.LocalName == "item");

                            foreach (var i in xmlItems)
                            {
                                var res = i.Elements().FirstOrDefault(e => e.Name.LocalName == "res");
                                var title = i.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value ?? "Unknown File";
                                var thumbUrl = i.Elements().FirstOrDefault(e => e.Name.LocalName == "albumArtURI")?.Value;

                                if (res != null && !string.IsNullOrEmpty(res.Value))
                                {
                                    // Fix thumbnail absolute URL
                                    if (!string.IsNullOrEmpty(thumbUrl) && !thumbUrl.StartsWith("http"))
                                        thumbUrl = thumbUrl.StartsWith("/") ? _remoteBaseUrl.TrimEnd('/') + thumbUrl : _remoteBaseUrl.TrimEnd('/') + "/" + thumbUrl;

                                    // Safely extract file size
                                    var sizeAttr = res.Attribute("size")?.Value;
                                    long.TryParse(sizeAttr ?? "0", out long sizeBytes);

                                    itemsList.Add(new RemoteItem
                                    {
                                        Url = res.Value,
                                        Title = title,
                                        Size = sizeBytes,
                                        ThumbnailUrl = thumbUrl
                                    });
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Log($"Fetch Error: {ex.Message}"); }
            return itemsList;
        }

       

        private void PlayUrlsInVlc(List<RemoteItem> items)
        {
            if (items == null || items.Count == 0) return;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("#EXTM3U");
            foreach (var item in items)
            {
                sb.AppendLine($"#EXTINF:-1,{item.Title}");
                sb.AppendLine(item.Url);

                // Update existing history or add new
                var existing = Config.History.FirstOrDefault(h => h.Url == item.Url || h.Title == item.Title);
                if (existing != null)
                {
                    existing.LastPlayed = DateTime.Now;
                    existing.Progress = 50;
                    existing.ServerName = _currentServerName; // <-- Update the name
                }
                else
                {
                    Config.History.Add(new PlaybackHistory
                    {
                        Title = item.Title,
                        Url = item.Url,
                        ThumbnailUrl = item.ThumbnailUrl,
                        Progress = 50,
                        LastPlayed = DateTime.Now,
                        ServerName = _currentServerName // <-- Save the name here
                    });
                }
                item.WatchProgress = 50;
            }

            SaveConfig();

            string tempPlaylist = Path.Combine(Path.GetTempPath(), $"LocalStream_{DateTime.Now.Ticks}.m3u");
            System.IO.File.WriteAllText(tempPlaylist, sb.ToString());

            string vlcPath = "vlc";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string[] paths = { @"C:\Program Files\VideoLAN\VLC\vlc.exe", @"C:\Program Files (x86)\VideoLAN\VLC\vlc.exe" };
                foreach (var p in paths) if (System.IO.File.Exists(p)) { vlcPath = p; break; }

                // Fallback check in Windows Registry
                if (vlcPath == "vlc")
                {
                    try
                    {
                        using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\VideoLAN\VLC"))
                            if (key?.GetValue("InstallDir") is string dir && System.IO.File.Exists(Path.Combine(dir, "vlc.exe")))
                                vlcPath = Path.Combine(dir, "vlc.exe");
                    }
                    catch { }
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                vlcPath = "/Applications/VLC.app/Contents/MacOS/VLC";
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = vlcPath,
                    Arguments = $"\"{tempPlaylist}\"",
                    UseShellExecute = false
                });
            }
            catch (Exception ex) { Log($"VLC Error: {ex.Message}. Make sure VLC is installed."); }
        }

        // ===========================
        //    WINDOW & TRAY LOGIC
        // ===========================

        private void Window_Closing(object sender, WindowClosingEventArgs e)
        {
            if (Config.MinimizeToTray)
            {
                e.Cancel = true; // Stop close
                this.Hide();     // Hide window
                // Note: Balloon tips aren't fully supported on all Linux distros in Avalonia yet, 
                // but the logic would go here if using a specific library.
            }
        }

        // These methods are hooked up in the XAML TrayIcon NativeMenu
        public void TrayOpen_Click(object sender, EventArgs e)
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
        }

        public void TrayExit_Click(object sender, EventArgs e)
        {
            // Force close ignoring the minimize setting
            Config.MinimizeToTray = false;
            StopServer();
            this.Close();
        }

        // ===========================
        //    CROSS-PLATFORM STARTUP
        // ===========================

        private void SetStartup(bool enable)
        {
            try
            {
                // 1. WINDOWS LOGIC (Uses Registry)
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Using Microsoft.Win32 explicitly here
                    string runKey = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
                    using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey, true))
                    {
                        if (enable)
                        {
                            string path = $"\"{Environment.ProcessPath}\" -minimized";
                            key.SetValue("LocalStreamPC", path);
                            Log("Added to Windows Startup.");
                        }
                        else
                        {
                            key.DeleteValue("LocalStreamPC", false);
                            Log("Removed from Windows Startup.");
                        }
                    }
                }
                // 2. LINUX LOGIC (Uses .desktop files)
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    string autostartDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "autostart");
                    if (!Directory.Exists(autostartDir)) Directory.CreateDirectory(autostartDir);

                    string desktopFile = Path.Combine(autostartDir, "localstream.desktop");

                    if (enable)
                    {
                        string content = $"[Desktop Entry]\nType=Application\nName=LocalStream\nExec={Environment.ProcessPath} -minimized\nTerminal=false\n";
                        System.IO.File.WriteAllText(desktopFile, content);
                        Log("Added to Linux Autostart.");
                    }
                    else
                    {
                        if (System.IO.File.Exists(desktopFile)) System.IO.File.Delete(desktopFile);
                        Log("Removed from Linux Autostart.");
                    }
                }
            }
            catch (Exception ex) { Log("Startup Error: " + ex.Message); }
        }

    
        //    SERVER & CONFIG LOGIC
      


        public class PlaybackHistory
        {
            public string Title { get; set; }
            public string Url { get; set; }
            public string ThumbnailUrl { get; set; }
            public double Progress { get; set; }
            public DateTime LastPlayed { get; set; }
            public string ServerName { get; set; }
        }

    
        public class AppConfig
        {
            public List<string> SharedFolders { get; set; } = new List<string>();
            public int Port { get; set; } = 8080;
            public bool RunAtStartup { get; set; } = false;
            public bool MinimizeToTray { get; set; } = false;
            public bool AutoStartServer { get; set; } = false;
            public bool IsDarkMode { get; set; } = true;

            public bool EnableAuth { get; set; } = false;
            public bool AllowDeletion { get; set; } = false;

         

            public string AdminUsername { get; set; } = "admin";
            public string AdminPassword { get; set; } = "admin123";
            public string ViewerUsername { get; set; } = "viewer";
            public string ViewerPassword { get; set; } = "guest";


            public int GridSizeIndex { get; set; } = 2; 
            public bool IsListView { get; set; } = false;



            public List<PlaybackHistory> History { get; set; } = new List<PlaybackHistory>();
        }

        // 1. UPDATE LOAD CONFIG
        private void LoadConfig()
        {
            if (System.IO.File.Exists(_configPath))
            {
                try
                {
                    string json = System.IO.File.ReadAllText(_configPath);
                    Config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                }
                catch { Config = new AppConfig(); }
            }
            else Config = new AppConfig();

            // Ensure List is never null to prevent crashes
            // Ensure list exists FIRST
            if (Config.SharedFolders == null) Config.SharedFolders = new List<string>();

            // THEN bind it
            ListSharedFolders.ItemsSource = null;
            ListSharedFolders.ItemsSource = Config.SharedFolders;

            CmbGridSize.SelectedIndex = Config.GridSizeIndex;
            _isListView = Config.IsListView;
            UpdateViewMode();

            TxtPort.Text = Config.Port.ToString();
            ChkRunAtStartup.IsChecked = Config.RunAtStartup;
            ChkMinimizeToTray.IsChecked = Config.MinimizeToTray;
            ChkAutoStartServer.IsChecked = Config.AutoStartServer;
            ApplyTheme(Config.IsDarkMode);


            ChkEnableAuth.IsChecked = Config.EnableAuth;
            ChkAllowDeletion.IsChecked = Config.AllowDeletion;
            TxtAdminPassword.Text = Config.AdminPassword;
            TxtViewerPassword.Text = Config.ViewerPassword;
            TxtAdminUsername.Text = Config.AdminUsername; 
            TxtViewerUsername.Text = Config.ViewerUsername;

            // Auto-Start Logic
            if (Config.AutoStartServer && Config.SharedFolders.Count > 0)
            {
                Log(" Auto-starting Server...");
                BtnToggleServer_Click(null, null);
            }
        }
        private void UpdateViewMode()
        {
            var grid = this.FindControl<ListBox>("ListRemoteFiles_Grid");
            var list = this.FindControl<ListBox>("ListRemoteFiles_List");

            if (grid != null) grid.IsVisible = !_isListView;
            if (list != null) list.IsVisible = _isListView;
        }
        // Helper: Calculate Folder Size
        private long GetDirectorySize(string folderPath)
        {
            try
            {
                DirectoryInfo di = new DirectoryInfo(folderPath);
                // We only scan top-level files to prevent server freeze on massive hard drives
                return di.EnumerateFiles("*", SearchOption.TopDirectoryOnly).Sum(fi => fi.Length);
            }
            catch { return 0; }
        }

        // Helper: Delete File/Folder Endpoint
        private void HandleDeleteFile(HttpListenerContext context)
        {
            try
            {
                if (!Config.AllowDeletion)
                {
                    Log(" Delete blocked: 'Allow Admin to Delete' is unchecked in Settings.");
                    context.Response.StatusCode = 403; // Forbidden
                    return;
                }

                string pathParam = context.Request.QueryString["path"] ?? "";
                if (string.IsNullOrEmpty(pathParam) || pathParam.Contains(".."))
                {
                    Log("Delete blocked: Invalid path requested.");
                    context.Response.StatusCode = 400;
                    return;
                }

                // Security check
                if (!Config.SharedFolders.Any(root => pathParam.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                {
                    Log($" Delete blocked: Path '{pathParam}' is outside your shared folders.");
                    context.Response.StatusCode = 403;
                    return;
                }

                if (File.Exists(pathParam))
                {
                    File.Delete(pathParam);
                    Log($" Deleted file: {Path.GetFileName(pathParam)}");
                }
                else if (Directory.Exists(pathParam))
                {
                    Directory.Delete(pathParam, true);
                    Log($" Deleted folder: {Path.GetFileName(pathParam)}");
                }
                else
                {
                    Log($"Delete failed: Item not found on disk ({pathParam})");
                }

                context.Response.StatusCode = 200;
            }
            catch (Exception ex)
            {
                Log($" Delete Error: {ex.Message}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }


        private string AuthenticateUser(HttpListenerContext context)
        {
            if (!Config.EnableAuth) return "Admin";

            string authHeader = context.Request.Headers["Authorization"];
            if (authHeader != null && authHeader.StartsWith("Basic"))
            {
                string encoded = authHeader.Substring("Basic ".Length).Trim();
                Encoding encoding = Encoding.GetEncoding("iso-8859-1");
                string usernamePassword = encoding.GetString(Convert.FromBase64String(encoded));
                int sepIndex = usernamePassword.IndexOf(':');

                string username = usernamePassword.Substring(0, sepIndex);
                string password = usernamePassword.Substring(sepIndex + 1);

                
                if (username.Equals(Config.AdminUsername, StringComparison.OrdinalIgnoreCase) && password == Config.AdminPassword) return "Admin";
                if (username.Equals(Config.ViewerUsername, StringComparison.OrdinalIgnoreCase) && password == Config.ViewerPassword) return "Viewer";
            }

            context.Response.StatusCode = 401;
            context.Response.AddHeader("WWW-Authenticate", "Basic realm=\"LocalStream Secure Login\"");
            context.Response.Close();
            return null;
        }

        private void BtnToggleAdminPwd_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Content is Avalonia.Controls.TextBlock txt)
            {
                if (TxtAdminPassword.PasswordChar == '*')
                {
                    TxtAdminPassword.PasswordChar = '\0'; // Show password
                    txt.Text = "🙈"; // Change icon
                }
                else
                {
                    TxtAdminPassword.PasswordChar = '*'; // Hide password
                    txt.Text = "👁️"; // Change icon back
                }
            }
        }

        private void BtnToggleViewerPwd_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Content is Avalonia.Controls.TextBlock txt)
            {
                if (TxtViewerPassword.PasswordChar == '*')
                {
                    TxtViewerPassword.PasswordChar = '\0';
                    txt.Text = "🙈";
                }
                else
                {
                    TxtViewerPassword.PasswordChar = '*';
                    txt.Text = "👁️";
                }
            }
        }


        private void BtnToggleTheme_Click(object sender, RoutedEventArgs e)
        {
            
            Config.IsDarkMode = !Config.IsDarkMode;

       
            ApplyTheme(Config.IsDarkMode);

         
            SaveConfig();
        }

        private void ApplyTheme(bool isDark)
        {
            var themePath = this.FindControl<Avalonia.Controls.Shapes.Path>("ThemeIconPath");

            if (isDark)
            {
                RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
                ToolTip.SetTip(BtnToggleTheme, "Switch to Light Mode");
                if (themePath != null) themePath.Data = Avalonia.Media.Geometry.Parse("M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z"); // Moon

                BtnToggleTheme.Background = Avalonia.Media.Brush.Parse("#374151");
                if (themePath != null) themePath.Stroke = Avalonia.Media.Brushes.White;
            }
            else
            {
                RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
                ToolTip.SetTip(BtnToggleTheme, "Switch to Dark Mode");
                if (themePath != null) themePath.Data = Avalonia.Media.Geometry.Parse("M12 1v2M12 21v2M4.22 4.22l1.42 1.42M18.36 18.36l1.42 1.42M1 12h2M21 12h2M4.22 19.78l1.42-1.42M18.36 5.64l1.42-1.42M12 17a5 5 0 1 0 0-10 5 5 0 0 0 0 10z"); // Sun

                BtnToggleTheme.Background = Avalonia.Media.Brush.Parse("#E5E7EB");
                if (themePath != null) themePath.Stroke = Avalonia.Media.Brushes.Black;
            }
        }


        // 4. TOGGLE SERVER (Updated Check)
        private async void BtnToggleServer_Click(object sender, RoutedEventArgs e)
        {
            if (_isServerRunning)
            {
                StopServer();
                BtnToggleServer.Content = "Start Server";
                BtnToggleServer.Background = Avalonia.Media.Brushes.ForestGreen;
                _isServerRunning = false;

                ActiveServerUrls.Clear(); // Clear the IPs when stopped
            }
            else
            {
                if (Config.SharedFolders.Count == 0)
                {
                    Log("Please add at least one folder first.");
                    return;
                }

                RefreshActiveNetworkIps();

                
                ActiveServerUrls.Clear();
                foreach (var item in _localIps)
                {
                    ActiveServerUrls.Add(new ServerUrlItem
                    {
                        NetworkName = item.Name,
                        Url = $"http://{item.Ip}:{Config.Port}/"
                    });
                }

                Log($"App initialized on networks: {string.Join(", ", _localIps.Select(i => i.Ip))}");

                BtnToggleServer.Content = "Stop Server";
                BtnToggleServer.Background = Avalonia.Media.Brushes.Crimson;
                _isServerRunning = true;
                _serverTokenSource = new CancellationTokenSource();

                try
                {
                    await Task.WhenAll(
                        StartHttpServer(_serverTokenSource.Token),
                        StartSsdpServer(_serverTokenSource.Token)
                    );
                }
                catch (OperationCanceledException) { }
            }
        }

        private async void BtnCopySpecificIp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string url)
            {
                try
                {
                    // Avalonia 11 Clipboard API
                    var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                    if (clipboard != null)
                    {
                        await clipboard.SetTextAsync(url);

                        // Visual feedback
                        var oldContent = btn.Content;
                        btn.Content = "Copied!";
                        btn.Background = Avalonia.Media.Brushes.ForestGreen;

                        await Task.Delay(2000);

                        // Reset button
                        btn.Content = oldContent;
                        btn.Background = Avalonia.Media.Brush.Parse("#3B82F6");
                    }
                }
                catch (Exception ex)
                {
                    Log($"Failed to copy to clipboard: {ex.Message}");
                }
            }
        }

        private void SaveConfig()
        {
            // 1. FOLDERS: No need to save manually here. 
            // The Add/Remove buttons already updated the Config.SharedFolders list.

            // 2. OTHER SETTINGS
            if (int.TryParse(TxtPort.Text, out int p)) Config.Port = p;
            Config.RunAtStartup = ChkRunAtStartup.IsChecked == true;
            Config.MinimizeToTray = ChkMinimizeToTray.IsChecked == true;
            Config.AutoStartServer = ChkAutoStartServer.IsChecked == true;

            Config.EnableAuth = ChkEnableAuth.IsChecked == true;
            Config.AllowDeletion = ChkAllowDeletion.IsChecked == true;
            Config.AdminPassword = TxtAdminPassword.Text;
            Config.ViewerPassword = TxtViewerPassword.Text;
            Config.AdminUsername = TxtAdminUsername.Text;
            Config.ViewerUsername = TxtViewerUsername.Text;

            // 3. WRITE TO FILE
            try
            {
                string json = JsonSerializer.Serialize(Config);
                System.IO.File.WriteAllText(_configPath, json);
            }
            catch (Exception ex) { Log("Error saving settings: " + ex.Message); }

          
        }

        // ===========================
        //    HTTP SERVER LOGIC
        // ===========================

        private async Task StartHttpServer(CancellationToken token)
        {
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add($"http://*:{Config.Port}/");

            try
            {
                _httpListener.Start();
                Log($"HTTP Server active on Port {Config.Port} (All Interfaces)");
            }
            catch (HttpListenerException ex)
            {
                Log($"** Error starting HTTP: {ex.Message}");
                StopServer();
                return;
            }

            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        // This waits for a request. If we stop the server, this line throws an error.
                        var context = await _httpListener.GetContextAsync();
                        _ = Task.Run(() => HandleHttpRequest(context));
                    }
                    catch (HttpListenerException)
                    {
                        // Error 995: IO Aborted (Happens naturally when stopping). Ignore it.
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        // Listener was closed. Ignore it.
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ Server Loop Error: {ex.Message}");
            }
        }

        private void HandleHttpRequest(HttpListenerContext context)
        {
            string rawUrl = context.Request.Url.AbsolutePath;
            string method = context.Request.HttpMethod;

            bool isUpnp = rawUrl.Contains("/description.xml") ||
                   rawUrl.Contains("/scpd/") ||
                   rawUrl.Contains("/control/") ||
                   rawUrl.StartsWith("/thumb/") || 
                   rawUrl.StartsWith("/file/");
            string userRole = isUpnp ? "Viewer" : AuthenticateUser(context);

            if (userRole == null && !isUpnp) return;

            try
            {
                if (rawUrl == "/")
                {
                    string relativePath = context.Request.QueryString["path"] ?? "";
                    ServeWebBrowser(context, relativePath, userRole); // Pass the role here!
                }
                else if (rawUrl == "/upload" && method == "POST" && userRole == "Admin") HandleFileUpload(context);
                else if (rawUrl == "/delete" && method == "POST" && userRole == "Admin") HandleDeleteFile(context);
                else if (rawUrl == "/upload" && method == "POST") HandleFileUpload(context);
                else if (rawUrl == "/zip") ServeZipDownload(context);
                else if (rawUrl == "/description.xml") ServeDeviceDescription(context);
                else if (rawUrl == "/scpd/ContentDirectory.xml") ServeServiceDescription(context);
                else if (rawUrl == "/control/ContentDirectory" && method == "POST") HandleSoapBrowse(context);

                // ✅ NEW: Thumbnail HandlerHandler
                else if (rawUrl.StartsWith("/thumb/")) ServeThumbnail(context, rawUrl);

                else if (rawUrl.StartsWith("/file/")) ServeMediaFile(context, rawUrl);
                else
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                }
            }
            catch (Exception ex)
            {
                Log($"Error serving {rawUrl}: {ex.Message}");
                try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
            }
        }

        private async void ServeThumbnail(HttpListenerContext context, string path)
        {
            string fullPath = "";
            try
            {
                // 1. Decode Path
                string base64Param = path.Substring(7); // Removes "/thumb/"
                fullPath = Encoding.UTF8.GetString(Convert.FromBase64String(base64Param));

                if (!System.IO.File.Exists(fullPath))
                {
                    Log($" Thumb 404: File not found {fullPath}");
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                // 2. Check Cache
                string cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cache");
                if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);

                string fileHash = Convert.ToBase64String(Encoding.UTF8.GetBytes(fullPath)).Replace("/", "_").Replace("+", "-");
                string cachedThumbPath = Path.Combine(cacheDir, fileHash + ".jpg");

                if (System.IO.File.Exists(cachedThumbPath))
                {
                    byte[] cachedBytes = await System.IO.File.ReadAllBytesAsync(cachedThumbPath);
                    context.Response.ContentType = "image/jpeg";
                    context.Response.ContentLength64 = cachedBytes.Length;
                    context.Response.OutputStream.Write(cachedBytes, 0, cachedBytes.Length);
                    context.Response.Close();
                    return;
                }

                // 3. Generate New Thumbnail
                string ext = Path.GetExtension(fullPath).ToLower();
                byte[] imageBytes = null;

                // === IMAGES ===
                if (new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp" }.Contains(ext))
                {
                    try
                    {
                        // Try resizing first
                        using (var stream = System.IO.File.OpenRead(fullPath))
                        {
                            var bitmap = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 200);
                            using (var outStream = new MemoryStream())
                            {
                                bitmap.Save(outStream);
                                imageBytes = outStream.ToArray();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($" Image Resize Failed (Sending Original): {ex.Message}");
                        // Fallback: Just send the original file if resizing crashes
                        imageBytes = await System.IO.File.ReadAllBytesAsync(fullPath);
                    }
                }
                // === AUDIO ===
                else if (new[] { ".mp3", ".flac", ".ogg", ".m4a" }.Contains(ext))
                {
                    try
                    {
                        var tfile = TagLib.File.Create(fullPath);
                        if (tfile.Tag.Pictures.Length > 0)
                        {
                            imageBytes = tfile.Tag.Pictures[0].Data.Data;
                        }
                    }
                    catch { /* No Album Art found or file locked */ }
                }
                // === VIDEO ===
                else if (new[] { ".mp4", ".mkv", ".avi", ".mov", ".webm" }.Contains(ext))
                {
                    await _thumbLock.WaitAsync();
                    try
                    {
                        string tempThumb = Path.Combine(Path.GetTempPath(), $"tmp_{Guid.NewGuid()}.jpg");
                        // Take snapshot at 5 seconds
                        var conversion = await FFmpeg.Conversions.FromSnippet.Snapshot(fullPath, tempThumb, TimeSpan.FromSeconds(5));
                        await conversion.Start();

                        if (System.IO.File.Exists(tempThumb))
                        {
                            // Resize the snapshot to save bandwidth
                            using (var stream = System.IO.File.OpenRead(tempThumb))
                            {
                                var bitmap = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 300);
                                using (var outStream = new MemoryStream())
                                {
                                    bitmap.Save(outStream);
                                    imageBytes = outStream.ToArray();
                                }
                            }
                            System.IO.File.Delete(tempThumb);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($" FFmpeg Error: {ex.Message}");
                    }
                    finally
                    {
                        _thumbLock.Release();
                    }
                }

                // 4. Send Response
                if (imageBytes != null)
                {
                    // Save to cache for next time
                    try { await System.IO.File.WriteAllBytesAsync(cachedThumbPath, imageBytes); } catch { }

                    context.Response.ContentType = "image/jpeg";
                    context.Response.ContentLength64 = imageBytes.Length;
                    context.Response.OutputStream.Write(imageBytes, 0, imageBytes.Length);
                }
                else
                {
                    // No thumbnail available -> 404 (Client shows default icon)
                    context.Response.StatusCode = 404;
                }
            }
            catch (Exception ex)
            {
                Log($" Critical Thumb Error: {ex.Message}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        // IMPORTANT: Avalonia UI Thread Helper
        private void Log(string message)
        {
            // Avalonia requires Dispatcher.UIThread instead of just Dispatcher
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                TxtServerLogs.Text += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
                // TxtServerLogs.CaretIndex = TxtServerLogs.Text.Length; // Scroll to end equivalent
            });
        }

        private void StopServer()
        {
            // 1. Tell the loops to stop
            if (_serverTokenSource != null && !_serverTokenSource.IsCancellationRequested)
            {
                _serverTokenSource.Cancel();
            }

            // 2. Stop HTTP Listener
            if (_httpListener != null)
            {
                try
                {
                    if (_httpListener.IsListening)
                    {
                        _httpListener.Stop(); // This triggers the exception in the loop above
                    }
                    _httpListener.Close();
                }
                catch { /* Ignore errors during shutdown */ }
                finally
                {
                    _httpListener = null;
                }
            }

            Log("Server stopped.");
        }
        // ===========================
        //    NETWORKING HELPERS
        // ===========================

        private string GetLocalIpAddress()
        {
            try
            {
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect("8.8.8.8", 65530);
                    IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
                    return endPoint.Address.ToString();
                }
            }
            catch { return "127.0.0.1"; }

        }

        private void ServeWebBrowser(HttpListenerContext context, string pathParam, string userRole)
        {
            if (pathParam.Contains("..")) pathParam = "";
            bool isRoot = string.IsNullOrEmpty(pathParam);
            string currentPath = pathParam;
            bool isAdmin = userRole == "Admin";

            if (!isRoot)
            {
                bool isAllowed = Config.SharedFolders.Any(root => currentPath.StartsWith(root, StringComparison.OrdinalIgnoreCase));
                if (!isAllowed || !Directory.Exists(currentPath))
                {
                    context.Response.StatusCode = 404; context.Response.Close(); return;
                }
            }

            var html = new StringBuilder();
            html.Append("<!DOCTYPE html><html data-theme='light'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width, initial-scale=1'>");
            html.Append($"<title>LocalStream</title>");
            html.Append("<style>");

            // -- CSS Variables --
            html.Append(":root { --bg: #f8fafc; --surface: #ffffff; --text: #0f172a; --text-muted: #64748b; --primary: #3b82f6; --primary-hover: #2563eb; --danger: #ef4444; --success: #10b981; --border: #e2e8f0; --radius: 12px; --grid-size: 200px; }");
            html.Append("[data-theme='dark'] { --bg: #0f172a; --surface: #1e293b; --text: #f8fafc; --text-muted: #94a3b8; --border: #334155; }");

            html.Append("body { font-family: system-ui, sans-serif; background: var(--bg); margin: 0; padding: 1.5rem; padding-bottom: 120px; color: var(--text); }");
            html.Append("* { box-sizing: border-box; }");

            // -- Global SVG Safety --
            html.Append("svg { flex-shrink: 0; }");

            // -- Top Bar & Controls --
            html.Append(".breadcrumbs { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; font-size: 1.4rem; font-weight: 600; margin-bottom: 1.5rem; }");
            html.Append(".breadcrumbs a { color: var(--primary); text-decoration: none; padding: 4px 8px; border-radius: 8px; transition: 0.2s; display: flex; align-items: center; gap: 6px; }");
            html.Append(".breadcrumbs a:hover { background: rgba(59, 130, 246, 0.1); }");
            html.Append(".breadcrumbs span { color: var(--text-muted); }");

            html.Append(".top-bar { display: flex; flex-wrap: wrap; justify-content: space-between; align-items: center; gap: 1rem; margin-bottom: 1.5rem; background: var(--surface); padding: 1rem 1.5rem; border-radius: var(--radius); border: 1px solid var(--border); }");
            // Explicit flex controls to prevent "v i d e o" vertical wrapping
            html.Append("h1 { margin: 0; font-size: 1.3rem; display: flex; align-items: center; gap: 10px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; max-width: 100%; }");
            html.Append(".controls { display: flex; flex-wrap: wrap; gap: 0.75rem; align-items: center; }");

            html.Append(".input-group { display: flex; align-items: center; background: var(--bg); border: 1px solid var(--border); border-radius: 8px; padding: 0 10px; }");
            html.Append(".search-input, .select-dropdown { border: none; background: transparent; padding: 10px; outline: none; color: var(--text); font-family: inherit; }");
            html.Append(".select-dropdown { border: 1px solid var(--border); border-radius: 8px; background: var(--surface); }");

            html.Append(".btn { padding: 8px 12px; border: none; border-radius: 8px; cursor: pointer; font-weight: 500; color: white; display: inline-flex; align-items: center; justify-content: center; gap: 6px; transition: 0.1s;}");
            html.Append(".btn:active { transform: scale(0.98); }");
            html.Append(".btn-primary { background: var(--primary); } .btn-success { background: var(--success); } .btn-danger { background: var(--danger); }");
            html.Append(".btn-secondary { background: var(--bg); color: var(--text); border: 1px solid var(--border); }");
            html.Append(".icon-btn { padding: 8px 10px; border-radius: 8px; background: var(--surface); color: var(--text); border: 1px solid var(--border); cursor: pointer; display: flex; align-items: center; justify-content: center; transition: 0.2s; }");
            html.Append(".icon-btn:hover { background: var(--bg); }");

            // -- Layouts --
            html.Append(".container { display: flex; flex-direction: column; gap: 10px; }");
            html.Append(".grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(var(--grid-size), 1fr)); gap: 1.2rem; }");

            html.Append(".item { background: var(--surface); border-radius: var(--radius); border: 1px solid var(--border); position: relative; overflow: hidden; display: flex; transition: outline 0.1s; }");

            // --- CHECKBOX LOGIC (Fixed to keep actions visible) ---
            html.Append(".checkbox { display: none; width: 22px; height: 22px; accent-color: var(--primary); cursor: pointer; flex-shrink: 0;}");
            html.Append("body.select-mode .checkbox { display: block; }");
            html.Append("body.select-mode .item { cursor: pointer; }");
            html.Append(".item.selected { outline: 3px solid var(--primary); background: rgba(59,130,246,0.05); }");

            html.Append(".preview-box { display: flex; justify-content: center; align-items: center; background: var(--bg); flex-shrink: 0; position: relative; }");
            html.Append(".preview-img { width: 100%; height: 100%; object-fit: cover; position: absolute; top: 0; left: 0; }");

            html.Append(".list .item { align-items: center; padding: 12px 16px; gap: 15px; }");
            html.Append(".list .preview-box { width: 48px; height: 48px; border-radius: 8px; overflow: hidden; }");
            html.Append(".list .details { flex-grow: 1; display: flex; justify-content: space-between; align-items: center; }");
            html.Append(".list .details-wrapper { display: flex; align-items: center; gap: 12px; width: 100%; overflow: hidden; }");
            html.Append(".list .actions { display: flex; gap: 8px; margin-left: 15px; }");

            html.Append(".grid .item { flex-direction: column; height: 100%; }");
            html.Append(".grid .preview-box { width: 100%; aspect-ratio: 16/9; border-bottom: 1px solid var(--border); }");
            html.Append(".grid .details { padding: 12px; display: flex; flex-direction: column; gap: 10px; flex-grow: 1; }");
            html.Append(".grid .details-wrapper { display: flex; align-items: center; gap: 10px; width: 100%; overflow: hidden; }");
            html.Append(".grid .actions { display: flex; gap: 8px; margin-top: auto; width: 100%; }");

            html.Append(".btn-action { flex: 1; background: var(--bg); color: var(--text); border: 1px solid var(--border); border-radius: 8px; display: flex; align-items: center; justify-content: center; padding: 8px; transition: 0.2s; cursor:pointer;}");
            html.Append(".btn-action:hover { background: var(--primary); color: white; }");
            html.Append(".btn-danger-action:hover { background: var(--danger); color: white; border-color: var(--danger); }");

            html.Append(".name { font-weight: 600; font-size: 0.95rem; text-decoration: none; color: var(--text); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; display: block; }");
            html.Append(".meta { color: var(--text-muted); font-size: 0.8rem; margin-top: 4px; display:block;}");
            html.Append(".list .grid-select-wrapper { display: none !important; }");

            // -- BOTTOM SELECTION BAR (Fixed Visibility Logic) --
            html.Append(".selection-bar { position:fixed; bottom:0; left:0; right:0; background:var(--surface); border-top:1px solid var(--border); padding:1rem 2rem; display:none; justify-content:space-between; align-items:center; z-index: 9999; box-shadow:0 -4px 15px rgba(0,0,0,0.1); }");
            html.Append("</style>");

            // -- Javascript --
            html.Append("<script>");
            html.Append("function initSettings() { var mode = localStorage.getItem('viewMode') || 'grid'; var theme = localStorage.getItem('theme') || 'light'; var gSize = localStorage.getItem('gridSize') || '200'; document.getElementById('container').className = 'container ' + mode; document.documentElement.setAttribute('data-theme', theme); if(document.getElementById('gridSizeSelect')) document.getElementById('gridSizeSelect').value = gSize; document.documentElement.style.setProperty('--grid-size', gSize + 'px'); sortItems(); }");
            html.Append("function toggleTheme() { var next = document.documentElement.getAttribute('data-theme') === 'dark' ? 'light' : 'dark'; document.documentElement.setAttribute('data-theme', next); localStorage.setItem('theme', next); }");
            html.Append("function updateGridSize() { var val = document.getElementById('gridSizeSelect').value; document.documentElement.style.setProperty('--grid-size', val + 'px'); localStorage.setItem('gridSize', val); }");
            html.Append("function toggleView() { var c = document.getElementById('container'); var next = c.classList.contains('grid') ? 'list' : 'grid'; c.className = 'container ' + next; localStorage.setItem('viewMode', next); }");

            // Fixed Select Logic
            html.Append("function toggleSelectMode() { var b = document.body; b.classList.toggle('select-mode'); var btn = document.getElementById('btnSelectMode'); if(b.classList.contains('select-mode')) { btn.style.background = 'var(--primary)'; btn.style.color = 'white'; } else { btn.style.background = ''; btn.style.color = ''; document.querySelectorAll('.file-check').forEach(c => c.checked = false); document.querySelectorAll('.item').forEach(i => i.classList.remove('selected')); updateSelection(); } }");

            // Safely handles clicking the card vs clicking the play button
            html.Append("function handleItemClick(e, el) { if(document.body.classList.contains('select-mode') && !e.target.closest('.btn-action')) { e.preventDefault(); var chk = el.querySelector('.file-check'); if(!chk) return; if(e.target !== chk) chk.checked = !chk.checked; if(chk.checked) el.classList.add('selected'); else el.classList.remove('selected'); updateSelection(); } else if(!e.target.closest('.btn-action') && el.dataset.href && !document.body.classList.contains('select-mode')) { window.location = el.dataset.href; } }");

            html.Append("function handleCheckClick(e, chk) { e.stopPropagation(); var el = chk.closest('.item'); if(chk.checked) el.classList.add('selected'); else el.classList.remove('selected'); updateSelection(); }");

            // Updates bottom bar display
            html.Append("function updateSelection() { var count = document.querySelectorAll('.file-check:checked').length; var bar = document.getElementById('selectionBar'); if(bar) { bar.style.display = count > 0 ? 'flex' : 'none'; document.getElementById('selCount').innerText = count + ' item(s) selected'; } }");

            html.Append("function downloadSelected() { var checks = document.querySelectorAll('.file-check:checked'); var files = []; for(var i=0; i<checks.length; i++){ if(checks[i].value) files.push(checks[i].value); } if(files.length===0) { alert('Select files to zip.'); return; } window.location = '/zip?path=" + WebUtility.UrlEncode(currentPath) + "&files=' + encodeURIComponent(files.join('|')); }");
            html.Append("function downloadLinewise() { var checks = document.querySelectorAll('.file-check:checked'); checks.forEach(c => { var item = c.closest('.item'); var dl = item.querySelector('a[download]') || item.querySelector('a[href^=\"/zip\"]'); if(dl) dl.click(); }); }");
            html.Append("function deleteSelected() { var checks = document.querySelectorAll('.file-check:checked'); var paths = []; for(var i=0; i<checks.length; i++){ paths.push(decodeURIComponent(checks[i].dataset.fullpath)); } if(paths.length===0) return; if(confirm('Delete ' + paths.length + ' selected items? This cannot be undone.')) { Promise.all(paths.map(p => fetch('/delete?path=' + encodeURIComponent(p), {method:'POST'}))).then(responses => { if(responses.some(r => r.status === 403)) alert('Delete Blocked: Please enable \"Allow Admin to Delete\" in your LocalStream App Settings.'); else window.location.reload(); }); } }");
            html.Append("function deleteItem(path) { if(confirm('Are you sure you want to delete this?')) { fetch('/delete?path=' + encodeURIComponent(path), {method:'POST'}).then(res => { if(res.status === 403) alert('Delete Blocked: Please enable \"Allow Admin to Delete\" in your LocalStream App Settings.'); else window.location.reload(); }); } }"); html.Append("function handleUpload(input) { if(input.files.length === 0) return; var fd = new FormData(); for(var i=0; i<input.files.length; i++) fd.append('files', input.files[i]); fetch('/upload?path=" + WebUtility.UrlEncode(currentPath) + "', {method:'POST', body:fd}).then(() => window.location.reload()); }");
            html.Append("function filterItems() { var input = document.getElementById('searchInput').value.toLowerCase(); document.querySelectorAll('.item:not(.up-item)').forEach(i => i.style.display = i.dataset.name.includes(input) ? '' : 'none'); }");
            html.Append("function sortItems() { var sel = document.getElementById('sortSelect'); if(!sel) return; var val = sel.value; var c = document.getElementById('container'); var items = Array.from(document.querySelectorAll('.item:not(.up-item)')); var up = document.querySelector('.up-item'); items.sort((a,b) => { var tA=a.dataset.type, tB=b.dataset.type; if(tA!==tB) return tA==='folder'?-1:1; var nA=a.dataset.name, nB=b.dataset.name, sA=parseInt(a.dataset.size), sB=parseInt(b.dataset.size); if(val==='name-asc') return nA.localeCompare(nB); if(val==='name-desc') return nB.localeCompare(nA); if(val==='size-desc') return sB-sA; return sA-sB; }); c.innerHTML=''; if(up) c.appendChild(up); items.forEach(i=>c.appendChild(i)); }");
            html.Append("window.onload = initSettings;");
            html.Append("</script></head><body>");

            // ==========================================
            // BREADCRUMBS NAVIGATION
            // ==========================================
            html.Append("<div class='breadcrumbs'>");
            html.Append($"<a href='/'>{GetModernIcon("server")} Home</a>");

            if (!isRoot)
            {
                string baseFolder = Config.SharedFolders.FirstOrDefault(f => currentPath.StartsWith(f, StringComparison.OrdinalIgnoreCase)) ?? "";
                string relative = currentPath.Substring(baseFolder.Length).Trim(Path.DirectorySeparatorChar);

                string buildPath = baseFolder;
                html.Append($"<span>/</span><a href='/?path={WebUtility.UrlEncode(buildPath)}'>{new DirectoryInfo(baseFolder).Name}</a>");

                if (!string.IsNullOrEmpty(relative))
                {
                    string[] parts = relative.Split(Path.DirectorySeparatorChar);
                    foreach (string part in parts)
                    {
                        buildPath = Path.Combine(buildPath, part);
                        html.Append($"<span>/</span><a href='/?path={WebUtility.UrlEncode(buildPath)}'>{part}</a>");
                    }
                }
            }
            html.Append("</div>");

            // ==========================================
            // TOP BAR CONTROLS
            // ==========================================
            html.Append("<div class='top-bar'>");
            // Replaced <h1> with a stable Div to prevent wrapping issues completely
            string topIcon = isRoot ? GetModernIcon("server") : GetModernIcon("folder");
            string folderName = isRoot ? "Shared Libraries" : new DirectoryInfo(currentPath).Name;

            html.Append($"<div style='display:flex; align-items:center; gap:10px; font-size:1.3rem; font-weight:bold; color:var(--text); white-space:nowrap; overflow:hidden; text-overflow:ellipsis;'>{topIcon} {folderName}</div>");

            html.Append("<div class='controls'>");

            html.Append($"<div class='input-group'>{GetModernIcon("search")}<input type='text' id='searchInput' class='search-input' placeholder='Search...' onkeyup='filterItems()'></div>");

            if (!isRoot)
            {
                html.Append($"<button id='btnSelectMode' onclick='toggleSelectMode()' class='icon-btn' style='padding: 8px 12px; gap: 6px; font-weight: 500;' title='Select Multiple Files'>{GetModernIcon("check-square")} Select</button>");
            }

            html.Append("<select id='sortSelect' class='select-dropdown' onchange='sortItems()'><option value='name-asc'>Name (A-Z)</option><option value='name-desc'>Name (Z-A)</option><option value='size-desc'>Size (Largest)</option><option value='size-asc'>Size (Smallest)</option></select>");
            html.Append("<div class='grid-select-wrapper'><select id='gridSizeSelect' class='select-dropdown' onchange='updateGridSize()'><option value='150'>Small Grid</option><option value='200'>Medium Grid</option><option value='280'>Large Grid</option><option value='350'>Extra Large</option></select></div>");

            html.Append($"<button onclick='toggleTheme()' class='icon-btn' title='Theme'>{GetModernIcon("moon")}</button>");
            html.Append($"<button onclick='toggleView()' class='icon-btn' title='View'>{GetModernIcon("grid")}</button>");

            if (!isRoot && isAdmin)
            {
                html.Append("<input type='file' id='fileInput' multiple style='display:none' onchange='handleUpload(this)'>");
                html.Append($"<button class='btn btn-success' onclick='document.getElementById(\"fileInput\").click()'>{GetModernIcon("upload")} Upload</button>");
            }

            html.Append("</div></div>");
            html.Append("<div id='container' class='container grid'>");

            // ==========================================
            // RENDER FILES & FOLDERS
            // ==========================================
            if (isRoot)
            {
                foreach (var folder in Config.SharedFolders.Where(Directory.Exists))
                {
                    var info = new DirectoryInfo(folder);
                    long fSize = GetDirectorySize(folder);
                    string link = $"/?path={WebUtility.UrlEncode(folder)}";
                    string safeName = System.Security.SecurityElement.Escape(info.Name);

                    html.Append($"<div class='item' data-name='{safeName.ToLower()}' data-type='folder' data-size='{fSize}' data-href='{link}' onclick='handleItemClick(event, this)'>");
                    html.Append($"<div class='preview-box'>{GetModernIcon("drive")}</div>");

                    html.Append("<div class='details'>");
                    html.Append("<div class='details-wrapper'>");
                    html.Append($"<div style='flex-grow:1; min-width:0;'><a href='{link}' class='name'>{info.Name}</a><span class='meta'>{FormatSize(fSize)}</span></div>");
                    html.Append("</div>");

                    html.Append("<div class='actions' onclick='event.stopPropagation()'>");
                    html.Append($"<a href='{link}' class='btn-action' title='Open Library'>{GetModernIcon("folder")}</a>");
                    html.Append($"<a href='/zip?path={WebUtility.UrlEncode(folder)}' class='btn-action' title='Download Library as Zip'>{GetModernIcon("download")}</a>");
                    html.Append("</div></div></div>");
                }
            }
            else
            {
                string parent = Path.GetDirectoryName(currentPath);
                bool parentIsAllowed = parent != null && Config.SharedFolders.Any(root => parent.StartsWith(root));
                string upLink = parentIsAllowed ? $"/?path={WebUtility.UrlEncode(parent)}" : "/";

                html.Append($"<div class='item up-item' onclick=\"window.location='{upLink}'\" style='cursor:pointer;'>");
                html.Append($"<div class='preview-box'>{GetModernIcon("corner-up-left")}</div>");
                html.Append("<div class='details'><div class='details-wrapper'><div><a class='name'>..</a><span class='meta'>Go Up</span></div></div></div></div>");

                // FOLDERS
                foreach (var dir in Directory.GetDirectories(currentPath))
                {
                    var info = new DirectoryInfo(dir);
                    long fSize = GetDirectorySize(dir);
                    string safeName = System.Security.SecurityElement.Escape(info.Name);

                    html.Append($"<div class='item' data-name='{safeName.ToLower()}' data-type='folder' data-size='{fSize}' data-href='/?path={WebUtility.UrlEncode(dir)}' onclick='handleItemClick(event, this)'>");
                    html.Append($"<div class='preview-box'>{GetModernIcon("folder")}</div>");

                    html.Append("<div class='details'>");
                    html.Append("<div class='details-wrapper'>");
                    html.Append($"<input type='checkbox' class='checkbox file-check' value='' data-fullpath='{WebUtility.UrlEncode(dir)}' onclick='handleCheckClick(event, this)'>");
                    html.Append($"<div style='flex-grow:1; min-width:0;'><a href='/?path={WebUtility.UrlEncode(dir)}' class='name'>{safeName}</a><span class='meta'>{FormatSize(fSize)}</span></div>");
                    html.Append("</div>");

                    html.Append("<div class='actions' onclick='event.stopPropagation()'>");
                    html.Append($"<a href='/?path={WebUtility.UrlEncode(dir)}' class='btn-action' title='Open Folder'>{GetModernIcon("folder")}</a>");
                    html.Append($"<a href='/zip?path={WebUtility.UrlEncode(dir)}' class='btn-action' title='Download Folder as Zip'>{GetModernIcon("download")}</a>");
                    if (isAdmin)
                    {
                        string safeDirJs = dir.Replace("\\", "\\\\"); // Fixes the JS backslash crash
                        html.Append($"<button onclick=\"deleteItem('{System.Security.SecurityElement.Escape(safeDirJs)}')\" class='btn-action btn-danger-action' title='Delete Folder'>{GetModernIcon("trash")}</button>");
                    }
                    html.Append("</div></div></div>");
                }

                // FILES
                foreach (var file in Directory.GetFiles(currentPath))
                {
                    var info = new FileInfo(file);
                    string fullPathB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(file));
                    string url = $"/file/{fullPathB64}";
                    string thumbUrl = $"/thumb/{fullPathB64}";
                    string safeName = System.Security.SecurityElement.Escape(info.Name);
                    bool isMedia = IsMediaFile(info.Name);

                    html.Append($"<div class='item' data-name='{safeName.ToLower()}' data-type='file' data-size='{info.Length}' data-href='{url}' onclick='handleItemClick(event, this)'>");

                    html.Append("<div class='preview-box'>");
                    html.Append(GetModernFileIcon(info.Extension));
                    if (isMedia)
                    {
                        html.Append($"<img src='{thumbUrl}' class='preview-img' loading='lazy' onerror=\"this.style.display='none'\"/>");
                    }
                    html.Append("</div>");

                    html.Append("<div class='details'>");
                    html.Append("<div class='details-wrapper'>");
                    html.Append($"<input type='checkbox' class='checkbox file-check' value='{WebUtility.UrlEncode(info.Name)}' data-fullpath='{WebUtility.UrlEncode(file)}' onclick='handleCheckClick(event, this)'>");
                    html.Append($"<div style='flex-grow:1; min-width:0;'><a href='{url}' target='_blank' class='name'>{safeName}</a><span class='meta'>{FormatSize(info.Length)}</span></div>");
                    html.Append("</div>");

                    html.Append("<div class='actions' onclick='event.stopPropagation()'>");
                    html.Append($"<a href='{url}' target='_blank' class='btn-action' title='Play / View'>{GetModernIcon("play")}</a>");
                    html.Append($"<a href='{url}' download class='btn-action' title='Download'>{GetModernIcon("download")}</a>");
                    if (isAdmin)
                    {
                        string safeFileJs = file.Replace("\\", "\\\\"); // Fixes the JS backslash crash
                        html.Append($"<button onclick=\"deleteItem('{System.Security.SecurityElement.Escape(safeFileJs)}')\" class='btn-action btn-danger-action' title='Delete'>{GetModernIcon("trash")}</button>");
                    }
                    html.Append("</div></div></div>");
                }

                // BOTTOM SELECTION BAR
                html.Append("</div>"); // End container

                // Ensure Selection Bar is OUTSIDE the grid container, appended at the end of the body
                html.Append("<div id='selectionBar' class='selection-bar'>");
                html.Append("<span id='selCount' style='font-weight:600;'>0 selected</span>");

                html.Append("<div style='display: flex; gap: 10px; flex-wrap: wrap;'>");
                html.Append($"<button class='btn btn-secondary' onclick='downloadLinewise()'>{GetModernIcon("download")} Download 1-by-1</button>");
                html.Append($"<button class='btn btn-primary' onclick='downloadSelected()'>{GetModernIcon("download")} Zip Selected Files</button>");
                if (isAdmin)
                {
                    html.Append($"<button class='btn btn-danger' onclick='deleteSelected()'>{GetModernIcon("trash")} Delete Selected</button>");
                }
                html.Append("</div></div>");
            }

            if (isRoot) html.Append("</div>"); // Close root grid container

            html.Append("</body></html>");

            byte[] buffer = Encoding.UTF8.GetBytes(html.ToString());
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = buffer.Length;
            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            context.Response.Close();
        }


        private string GetModernFileIcon(string extension)
        {
            string ext = extension.ToLower();

            if (new[] { ".mp4", ".mkv", ".avi", ".mov", ".webm", ".wmv" }.Contains(ext))
                return "<svg width='24' height='24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round' viewBox='0 0 24 24'><rect x='2' y='2' width='20' height='20' rx='2.18' ry='2.18'></rect><line x1='7' y1='2' x2='7' y2='22'></line><line x1='17' y1='2' x2='17' y2='22'></line><line x1='2' y1='12' x2='22' y2='12'></line><line x1='2' y1='7' x2='7' y2='7'></line><line x1='2' y1='17' x2='7' y2='17'></line><line x1='17' y1='17' x2='22' y2='17'></line><line x1='17' y1='7' x2='22' y2='7'></line></svg>";

            if (new[] { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac" }.Contains(ext))
                return "<svg width='24' height='24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round' viewBox='0 0 24 24'><path d='M9 18V5l12-2v13'></path><circle cx='6' cy='18' r='3'></circle><circle cx='18' cy='16' r='3'></circle></svg>";

            if (new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" }.Contains(ext))
                return "<svg width='24' height='24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round' viewBox='0 0 24 24'><rect x='3' y='3' width='18' height='18' rx='2' ry='2'></rect><circle cx='8.5' cy='8.5' r='1.5'></circle><polyline points='21 15 16 10 5 21'></polyline></svg>";

            if (new[] { ".zip", ".rar", ".7z", ".tar", ".gz" }.Contains(ext))
                return "<svg width='24' height='24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round' viewBox='0 0 24 24'><path d='M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z'></path><polyline points='3.27 6.96 12 12.01 20.73 6.96'></polyline><line x1='12' y1='22.08' x2='12' y2='12'></line></svg>";

            if (new[] { ".pdf", ".doc", ".docx", ".txt", ".xlsx" }.Contains(ext))
                return "<svg width='24' height='24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round' viewBox='0 0 24 24'><path d='M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z'></path><polyline points='14 2 14 8 20 8'></polyline><line x1='16' y1='13' x2='8' y2='13'></line><line x1='16' y1='17' x2='8' y2='17'></line><polyline points='10 9 9 9 8 9'></polyline></svg>";

            return "<svg width='24' height='24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round' viewBox='0 0 24 24'><path d='M13 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9z'></path><polyline points='13 2 13 9 20 9'></polyline></svg>";
        }

        private string GetModernIcon(string name)
        {
            return name switch
            {
                "folder" => "<svg width='24' height='24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round' viewBox='0 0 24 24'><path d='M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z'></path></svg>",
                "corner-up-left" => "<svg width='24' height='24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round' viewBox='0 0 24 24'><polyline points='9 14 4 9 9 4'></polyline><path d='M20 20v-7a4 4 0 0 0-4-4H4'></path></svg>",
                "search" => "<svg width='24' height='24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round' viewBox='0 0 24 24'><circle cx='11' cy='11' r='8'></circle><line x1='21' y1='21' x2='16.65' y2='16.65'></line></svg>",
                "upload" => "<svg width='24' height='24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round' viewBox='0 0 24 24'><path d='M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4'></path><polyline points='17 8 12 3 7 8'></polyline><line x1='12' y1='3' x2='12' y2='15'></line></svg>",
                "download" => "<svg width='24' height='24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round' viewBox='0 0 24 24'><path d='M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4'></path><polyline points='7 10 12 15 17 10'></polyline><line x1='12' y1='15' x2='12' y2='3'></line></svg>",
                "grid" => "<svg width='24' height='24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round' viewBox='0 0 24 24'><rect x='3' y='3' width='7' height='7'></rect><rect x='14' y='3' width='7' height='7'></rect><rect x='14' y='14' width='7' height='7'></rect><rect x='3' y='14' width='7' height='7'></rect></svg>",
                "play" => "<svg width='24' height='24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round' viewBox='0 0 24 24'><circle cx='12' cy='12' r='10'></circle><polygon points='10 8 16 12 10 16 10 8'></polygon></svg>",
                "server" => "<svg width='24' height='24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round' viewBox='0 0 24 24'><rect x='2' y='2' width='20' height='8' rx='2' ry='2'></rect><rect x='2' y='14' width='20' height='8' rx='2' ry='2'></rect><line x1='6' y1='6' x2='6.01' y2='6'></line><line x1='6' y1='18' x2='6.01' y2='18'></line></svg>",
                "drive" => "<svg width='24' height='24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round' viewBox='0 0 24 24'><rect x='2' y='7' width='20' height='14' rx='2' ry='2'></rect><path d='M16 21V5a2 2 0 0 0-2-2h-4a2 2 0 0 0-2 2v16'></path></svg>",
                "moon" => "<svg width='24' height='24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round' viewBox='0 0 24 24'><path d='M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z'></path></svg>",
                "image" => "<svg width='24' height='24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round' viewBox='0 0 24 24'><rect x='3' y='3' width='18' height='18' rx='2' ry='2'></rect><circle cx='8.5' cy='8.5' r='1.5'></circle><polyline points='21 15 16 10 5 21'></polyline></svg>",
                "check-square" => "<svg width='24' height='24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round' viewBox='0 0 24 24'><polyline points='9 11 12 14 22 4'></polyline><path d='M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11'></path></svg>",
                "trash" => "<svg width='24' height='24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round' viewBox='0 0 24 24'><polyline points='3 6 5 6 21 6'></polyline><path d='M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2'></path></svg>",
                _ => ""
            };
        }
        // ================= UPLOAD HANDLER ================= //

        private void HandleFileUpload(HttpListenerContext context)
        {
            try
            {
                // 1. Get the Absolute Path from the Browser
                string pathParam = context.Request.QueryString["path"] ?? "";

                // 2. Security: Validate Path
                if (string.IsNullOrEmpty(pathParam) || pathParam.Contains(".."))
                {
                    Log(" Upload blocked: Invalid path.");
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                // 3. Security: Ensure the path is actually inside a Shared Folder
                // This prevents people from uploading to C:\Windows or other unshared places
                bool isAllowed = Config.SharedFolders.Any(root => pathParam.StartsWith(root, StringComparison.OrdinalIgnoreCase));

                if (!isAllowed)
                {
                    Log($" Upload blocked: '{pathParam}' is not a shared folder.");
                    context.Response.StatusCode = 403; // Forbidden
                    context.Response.Close();
                    return;
                }

                string targetFolder = pathParam;

                if (!Directory.Exists(targetFolder))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                // 4. Handle Multipart Data
                string contentType = context.Request.ContentType;
                if (contentType != null && contentType.Contains("multipart/form-data"))
                {
                    // Extract Boundary
                    string boundary = contentType.Split(new[] { "boundary=" }, StringSplitOptions.None)[1];
                    // Remove quotes if present
                    boundary = boundary.Trim('"');
                    byte[] boundaryBytes = Encoding.ASCII.GetBytes("--" + boundary);

                    using (var ms = new MemoryStream())
                    {
                        // Note: Copying entire stream to memory is fine for small files (images/songs).
                        // For very large video uploads (>500MB), this might use too much RAM.
                        context.Request.InputStream.CopyTo(ms);
                        byte[] data = ms.ToArray();

                        Log($" Upload received ({FormatSize(data.Length)}). Processing...");

                        // Find Filename
                        // We peek at the first 2KB of headers to find the name
                        string rawStr = Encoding.UTF8.GetString(data.Take(2000).ToArray());
                        string filename = "Upload_" + DateTime.Now.Ticks + ".bin";

                        if (rawStr.Contains("filename=\""))
                        {
                            int start = rawStr.IndexOf("filename=\"") + 10;
                            int end = rawStr.IndexOf("\"", start);
                            filename = rawStr.Substring(start, end - start);
                        }

                        // Locate Content Start (Double Newline: \r\n\r\n)
                        int headerEnd = IndexOf(data, new byte[] { 13, 10, 13, 10 });

                        if (headerEnd > 0)
                        {
                            int contentStart = headerEnd + 4;
                            // Find end boundary (approximate)
                            int contentEnd = data.Length - boundaryBytes.Length - 4;

                            // 5. Save the File
                            string savePath = Path.Combine(targetFolder, filename);

                            // Prevent overwriting existing files
                            if (File.Exists(savePath))
                            {
                                string nameNoExt = Path.GetFileNameWithoutExtension(filename);
                                string ext = Path.GetExtension(filename);
                                savePath = Path.Combine(targetFolder, $"{nameNoExt}_{DateTime.Now.Ticks}{ext}");
                            }

                            using (var fs = new FileStream(savePath, FileMode.Create))
                            {
                                // Calculate length carefully to avoid crash if boundary detection is slightly off
                                int length = Math.Max(0, contentEnd - contentStart);
                                if (length > 0)
                                {
                                    fs.Write(data, contentStart, length);
                                    Log($" Saved: {filename}");
                                }
                            }
                        }
                    }
                    context.Response.StatusCode = 200;
                }
                else
                {
                    context.Response.StatusCode = 415; // Unsupported Media Type
                }
            }
            catch (Exception ex)
            {
                Log($" Upload Error: {ex.Message}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }
        private int IndexOf(byte[] src, byte[] pattern)
        {
            int max = src.Length - pattern.Length;
            for (int i = 0; i <= max; i++)
            {
                if (src[i] != pattern[0]) continue;
                bool match = true;
                for (int j = 1; j < pattern.Length; j++)
                {
                    if (src[i + j] != pattern[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        // ================= ZIP DOWNLOAD HANDLER ================= //

        private void ServeZipDownload(HttpListenerContext context)
        {
            try
            {
                string pathParam = context.Request.QueryString["path"] ?? "";
                if (string.IsNullOrEmpty(pathParam) || pathParam.Contains("..")) return;

                // Security Check
                if (!Config.SharedFolders.Any(root => pathParam.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                {
                    context.Response.StatusCode = 403; context.Response.Close(); return;
                }

                string sourceFolder = pathParam; // It is now the full path
                string filesParam = context.Request.QueryString["files"];
                string[] selectedFiles = string.IsNullOrEmpty(filesParam) ? null : WebUtility.UrlDecode(filesParam).Split('|');

                context.Response.ContentType = "application/zip";
                context.Response.AddHeader("Content-Disposition", $"attachment; filename=\"Download_{DateTime.Now.Ticks}.zip\"");

                using (var zipArchive = new ZipArchive(context.Response.OutputStream, ZipArchiveMode.Create, true))
                {
                    var filesToZip = selectedFiles != null
                        ? selectedFiles.Select(f => Path.Combine(sourceFolder, f)).Where(System.IO.File.Exists)
                        : Directory.GetFiles(sourceFolder);

                    foreach (var filePath in filesToZip)
                    {
                        zipArchive.CreateEntryFromFile(filePath, Path.GetFileName(filePath));
                    }
                }
                context.Response.Close();
            }
            catch { context.Response.Close(); }
        }

        // ================= HELPERS & UPNP (Unchanged Logic) ================= //

        private void ServeMediaFile(HttpListenerContext context, string path)
        {
            string base64Param = path.Substring(6);
            string fullPath = "";

            try { fullPath = Encoding.UTF8.GetString(Convert.FromBase64String(base64Param)); }
            catch { context.Response.StatusCode = 404; context.Response.Close(); return; }

            if (!Config.SharedFolders.Any(f => fullPath.StartsWith(f)) || !System.IO.File.Exists(fullPath))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            FileInfo fileInfo = new FileInfo(fullPath);
            long fileSize = fileInfo.Length;
            string mimeType = GetMimeType(fileInfo.Extension);

            context.Response.ContentType = mimeType;

            // --- ADD THIS LINE TO FIX VLC TITLES ---
            // This tells VLC: "Don't use the URL, use this real filename instead"
            context.Response.AddHeader("Content-Disposition", $"inline; filename=\"{fileInfo.Name}\"");
            // ---------------------------------------

            context.Response.AddHeader("transferMode.dlna.org", "Streaming");
            context.Response.AddHeader("contentFeatures.dlna.org", "DLNA.ORG_OP=01;DLNA.ORG_FLAGS=01700000000000000000000000000000");
            context.Response.AddHeader("Accept-Ranges", "bytes");

            // ... (Keep the rest of the method exactly the same) ...
            string rangeHeader = context.Request.Headers["Range"];
            long start = 0, end = fileSize - 1;

            if (rangeHeader != null)
            {
                context.Response.StatusCode = 206;
                string[] ranges = rangeHeader.Replace("bytes=", "").Split('-');
                start = long.Parse(ranges[0]);
                if (ranges.Length > 1 && !string.IsNullOrEmpty(ranges[1])) end = long.Parse(ranges[1]);
            }

            long contentLength = end - start + 1;
            context.Response.ContentLength64 = contentLength;
            context.Response.AddHeader("Content-Range", $"bytes {start}-{end}/{fileSize}");

            using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.Seek(start, SeekOrigin.Begin);
                byte[] buffer = new byte[64 * 1024];
                int bytesRead;
                long bytesRemaining = contentLength;
                try
                {
                    while (bytesRemaining > 0 && (bytesRead = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, bytesRemaining))) > 0)
                    {
                        context.Response.OutputStream.Write(buffer, 0, bytesRead);
                        bytesRemaining -= bytesRead;
                    }
                }
                catch { }
            }
            context.Response.Close();
            Log($" Served: {fileInfo.Name}");
        }

        private bool IsMediaFile(string filename)
        {
            string[] mediaExtensions = {
        ".mp4", ".mkv", ".avi", ".mov", ".webm",".wmv", // Video
        ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac","mpga", // Audio
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" // Images
    };

            string ext = Path.GetExtension(filename).ToLower();
            return mediaExtensions.Contains(ext);
        }

        private string GetFileIcon(string extension)
        {
            string ext = extension.ToLower();
            if (new[] { ".mp4", ".mkv", ".avi", ".mov", ".webm" }.Contains(ext)) return "🎬";
            if (new[] { ".mp3", ".wav", ".flac", ".ogg", ".m4a" }.Contains(ext)) return "🎵";
            if (new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" }.Contains(ext)) return "🖼️";
            if (new[] { ".pdf", ".doc", ".docx", ".txt" }.Contains(ext)) return "📄";
            if (new[] { ".zip", ".rar", ".7z" }.Contains(ext)) return "📦";
            if (new[] { ".exe", ".msi", ".apk" }.Contains(ext)) return "💾";
            return "📃";
        }

        private string GetMimeType(string extension)
        {
            return extension.ToLower() switch
            {
                ".mp4" => "video/mp4",
                ".mkv" => "video/x-matroska",
                ".avi" => "video/x-msvideo",
                ".mov" => "video/quicktime",
                ".webm" => "video/webm",
                ".mp3" => "audio/mpeg",
                ".wav" => "audio/wav",
                ".flac" => "audio/flac",
                ".ogg" => "audio/ogg",
                ".m4a" => "audio/mp4",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                ".pdf" => "application/pdf",
                ".txt" => "text/plain",
                ".zip" => "application/zip",
                ".html" => "text/html",
                _ => "application/octet-stream"
            };
        }

        private string FormatSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1) { order++; len = len / 1024; }
            return $"{len:0.##} {sizes[order]}";
        }

        // ================= UPNP HANDLERS ================= //

        private void HandleSoapBrowse(HttpListenerContext context)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int matchCount = 0;

            try
            {
                string requestBody;
                using (var reader = new StreamReader(context.Request.InputStream)) requestBody = reader.ReadToEnd();

                string objectId = "0";
                if (requestBody.Contains("<ObjectID>"))
                {
                    int start = requestBody.IndexOf("<ObjectID>") + 10;
                    int end = requestBody.IndexOf("</ObjectID>");
                    objectId = requestBody.Substring(start, end - start);
                }

                bool isRecursive = requestBody.Contains("BrowseRecursive");
                Log($" Browse: ID '{objectId}'");

                StringBuilder didl = new StringBuilder();
                didl.Append("&lt;DIDL-Lite xmlns=&quot;urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/&quot; xmlns:dc=&quot;http://purl.org/dc/elements/1.1/&quot; xmlns:upnp=&quot;urn:schemas-upnp-org:metadata-1-0/upnp/&quot; xmlns:dlna=&quot;urn:schemas-dlna-org:metadata-1-0/&quot;&gt;");

                if (objectId == "0")
                {
                    // === VIRTUAL ROOT: Loop through your SharedFolders List ===
                    foreach (var folderPath in Config.SharedFolders)
                    {
                        if (!Directory.Exists(folderPath)) continue;

                        var dirInfo = new DirectoryInfo(folderPath);
                        // ID is the Base64 of the FULL PATH
                        string id = Convert.ToBase64String(Encoding.UTF8.GetBytes(folderPath));
                        string safeTitle = System.Security.SecurityElement.Escape(dirInfo.Name);

                        didl.Append($"&lt;container id=&quot;{id}&quot; parentID=&quot;0&quot; restricted=&quot;1&quot; searchable=&quot;0&quot;&gt;");
                        didl.Append($"&lt;dc:title&gt;{safeTitle}&lt;/dc:title&gt;");
                        didl.Append($"&lt;upnp:class&gt;object.container.storageFolder&lt;/upnp:class&gt;");
                        didl.Append($"&lt;/container&gt;");
                        matchCount++;
                    }
                }
                else
                {
                    // === PHYSICAL FOLDER: Decode the ID to get the real path ===
                    string currentPath = "";
                    try { byte[] data = Convert.FromBase64String(objectId); currentPath = Encoding.UTF8.GetString(data); } catch { }

                    // Security: Only allow if it starts with one of your shared paths
                    bool isAllowed = Config.SharedFolders.Any(allowed => currentPath.StartsWith(allowed));

                    if (Directory.Exists(currentPath) && isAllowed)
                    {
                        if (isRecursive)
                        {
                            var allFiles = GetAllFilesRecursive(currentPath);
                            foreach (var file in allFiles)
                            {
                                if (!IsMediaFile(file)) continue;
                                AppendFileXml(didl, file, objectId, ref matchCount);
                            }
                        }
                        else
                        {
                            foreach (var dir in Directory.GetDirectories(currentPath))
                            {
                                var dirInfo = new DirectoryInfo(dir);
                                if ((dirInfo.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden) continue;

                                string id = Convert.ToBase64String(Encoding.UTF8.GetBytes(dir));
                                string safeTitle = System.Security.SecurityElement.Escape(System.Security.SecurityElement.Escape(dirInfo.Name));

                                didl.Append($"&lt;container id=&quot;{id}&quot; parentID=&quot;{objectId}&quot; restricted=&quot;1&quot; searchable=&quot;0&quot;&gt;");
                                didl.Append($"&lt;dc:title&gt;{safeTitle}&lt;/dc:title&gt;");
                                didl.Append($"&lt;upnp:class&gt;object.container.storageFolder&lt;/upnp:class&gt;");
                                didl.Append($"&lt;/container&gt;");
                                matchCount++;
                            }

                            foreach (var file in Directory.GetFiles(currentPath))
                            {
                                if (!IsMediaFile(file)) continue;
                                AppendFileXml(didl, file, objectId, ref matchCount);
                            }
                        }
                    }
                }

                didl.Append("&lt;/DIDL-Lite&gt;");

                string soap = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"" s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
    <s:Body>
        <u:BrowseResponse xmlns:u=""urn:schemas-upnp-org:service:ContentDirectory:1"">
            <Result>{didl}</Result>
            <NumberReturned>{matchCount}</NumberReturned>
            <TotalMatches>{matchCount}</TotalMatches>
            <UpdateID>1</UpdateID>
        </u:BrowseResponse>
    </s:Body>
</s:Envelope>";

                byte[] bytes = Encoding.UTF8.GetBytes(soap);
                context.Response.ContentType = "text/xml; charset=\"utf-8\"";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                context.Response.Close();
            }
            catch (Exception ex)
            {
                Log($" BROWSE ERROR: {ex.Message}");
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
        }

        // === HELPER METHODS (Paste these inside MainWindow class) ===

        private List<string> GetAllFilesRecursive(string path)
        {
            var files = new List<string>();
            try
            {
                files.AddRange(Directory.GetFiles(path));
                foreach (var directory in Directory.GetDirectories(path))
                {
                    files.AddRange(GetAllFilesRecursive(directory));
                }
            }
            catch { /* Ignore permission errors */ }
            return files;
        }

        private void AppendFileXml(StringBuilder didl, string fullPath, string parentId, ref int matchCount)
        {
            try
            {
                var info = new FileInfo(fullPath);

                // Encode the FULL PATH in the URL. 
                // We use Base64 to make it safe for URLs (slash/space issues).
                string pathEncoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(fullPath));

                // Note the new URL structure: /file/{Base64Path}
                string url = $"http://{_localIp}:{Config.Port}/file/{pathEncoded}";
                string thumbUrl = $"http://{_localIp}:{Config.Port}/thumb/{pathEncoded}";

                string mime = GetMimeType(info.Extension);
                string id = pathEncoded; // ID is also the encoded path

                string rawTitle = Path.GetFileNameWithoutExtension(info.Name);
                string safeTitle = System.Security.SecurityElement.Escape(System.Security.SecurityElement.Escape(rawTitle));

                string upnpClass = "object.item";
                if (mime.StartsWith("video")) upnpClass = "object.item.videoItem";
                else if (mime.StartsWith("audio")) upnpClass = "object.item.audioItem.musicTrack";
                else if (mime.StartsWith("image")) upnpClass = "object.item.imageItem.photo";

                didl.Append($"&lt;item id=&quot;{id}&quot; parentID=&quot;{parentId}&quot; restricted=&quot;1&quot;&gt;");
                didl.Append($"&lt;dc:title&gt;{safeTitle}&lt;/dc:title&gt;");
                didl.Append($"&lt;upnp:class&gt;{upnpClass}&lt;/upnp:class&gt;");

                if (upnpClass.Contains("video") || upnpClass.Contains("audio") || upnpClass.Contains("image"))
                {
                    didl.Append($"&lt;upnp:albumArtURI&gt;{thumbUrl}&lt;/upnp:albumArtURI&gt;");
                }

                string dlnaFeatures = "DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000";
                didl.Append($"&lt;res protocolInfo=&quot;http-get:*:{mime}:{dlnaFeatures}&quot; size=&quot;{info.Length}&quot;&gt;{url}&lt;/res&gt;");

                didl.Append("&lt;/item&gt;");
                matchCount++;
            }
            catch { }
        }







        private void ServeDeviceDescription(HttpListenerContext context)
        {
            string xml = $@"<?xml version=""1.0""?><root xmlns=""urn:schemas-upnp-org:device-1-0""><specVersion><major>1</major><minor>0</minor></specVersion><device><deviceType>urn:schemas-upnp-org:device:MediaServer:1</deviceType><friendlyName>Local Stream PC ({Environment.MachineName})</friendlyName><manufacturer>LocalStream</manufacturer><modelName>Windows Server</modelName><UDN>{UDN}</UDN><serviceList><service><serviceType>urn:schemas-upnp-org:service:ContentDirectory:1</serviceType><serviceId>urn:upnp-org:serviceId:ContentDirectory</serviceId><controlURL>/control/ContentDirectory</controlURL><eventSubURL>/events/ContentDirectory</eventSubURL><SCPDURL>/scpd/ContentDirectory.xml</SCPDURL></service></serviceList></device></root>";
            ReplyXml(context, xml);
        }

        private void ServeServiceDescription(HttpListenerContext context)
        {
            string xml = @"<?xml version=""1.0"" encoding=""utf-8""?><scpd xmlns=""urn:schemas-upnp-org:service-1-0""><specVersion><major>1</major><minor>0</minor></specVersion><actionList><action><name>Browse</name><argumentList><argument><name>ObjectID</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_ObjectID</relatedStateVariable></argument><argument><name>BrowseFlag</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_BrowseFlag</relatedStateVariable></argument><argument><name>Filter</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_Filter</relatedStateVariable></argument><argument><name>StartingIndex</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_Index</relatedStateVariable></argument><argument><name>RequestedCount</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_Count</relatedStateVariable></argument><argument><name>SortCriteria</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_SortCriteria</relatedStateVariable></argument><argument><name>Result</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Result</relatedStateVariable></argument><argument><name>NumberReturned</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Count</relatedStateVariable></argument><argument><name>TotalMatches</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Count</relatedStateVariable></argument><argument><name>UpdateID</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_UpdateID</relatedStateVariable></argument></argumentList></action></actionList><serviceStateTable><stateVariable sendEvents=""no""><name>A_ARG_TYPE_ObjectID</name><dataType>string</dataType></stateVariable><stateVariable sendEvents=""no""><name>A_ARG_TYPE_Result</name><dataType>string</dataType></stateVariable><stateVariable sendEvents=""no""><name>A_ARG_TYPE_BrowseFlag</name><dataType>string</dataType></stateVariable><stateVariable sendEvents=""no""><name>A_ARG_TYPE_Filter</name><dataType>string</dataType></stateVariable><stateVariable sendEvents=""no""><name>A_ARG_TYPE_SortCriteria</name><dataType>string</dataType></stateVariable><stateVariable sendEvents=""no""><name>A_ARG_TYPE_Index</name><dataType>ui4</dataType></stateVariable><stateVariable sendEvents=""no""><name>A_ARG_TYPE_Count</name><dataType>ui4</dataType></stateVariable><stateVariable sendEvents=""no""><name>A_ARG_TYPE_UpdateID</name><dataType>ui4</dataType></stateVariable></serviceStateTable></scpd>";
            ReplyXml(context, xml);
        }

        private void ReplyXml(HttpListenerContext context, string xml)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(xml);
            context.Response.ContentType = "text/xml";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.Close();
        }

        private async Task StartSsdpServer(CancellationToken token)
        {
            try
            {
                using (var client = new UdpClient())
                {
                    var localEp = new IPEndPoint(IPAddress.Any, 1900);
                    client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    client.ExclusiveAddressUse = false;

                    try
                    {
                        client.Client.Bind(localEp);

                        // CRITICAL FIX: Join the multicast group on EVERY active network adapter
                        foreach (var item in _localIps)
                        {
                            try { client.JoinMulticastGroup(IPAddress.Parse("239.255.255.250"), IPAddress.Parse(item.Ip)); }
                            catch { }
                        }

                        Log("SSDP Active (Broadcasting on LAN & Wi-Fi)");
                    }
                    catch (Exception bindEx)
                    {
                        Log($"SSDP Bind Error: {bindEx.Message}");
                        return;
                    }

                    while (!token.IsCancellationRequested)
                    {
                        var result = await client.ReceiveAsync();
                        string request = Encoding.UTF8.GetString(result.Buffer);

                        if (request.StartsWith("M-SEARCH"))
                        {
                            // Multi-Homed Reply: Send a distinct UPnP reply for EVERY active IP address. 
                            // The receiving device (TV, Phone) will automatically connect to the IP that matches its subnet!
                            foreach (var item in _localIps)
                            {
                                string response = $"HTTP/1.1 200 OK\r\n" +
                                                  $"CACHE-CONTROL: max-age=1800\r\n" +
                                                  $"EXT:\r\n" +
                                                 $"LOCATION: http://{item.Ip}:{Config.Port}/description.xml\r\n" +
                                                  $"SERVER: Windows UPnP/1.1 LocalStream/1.1\r\n" +
                                                  $"ST: urn:schemas-upnp-org:device:MediaServer:1\r\n" +
                                                  $"USN: uuid:{_serverUuid}::urn:schemas-upnp-org:device:MediaServer:1\r\n\r\n";

                                byte[] data = Encoding.UTF8.GetBytes(response);
                                await client.SendAsync(data, data.Length, result.RemoteEndPoint);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"SSDP Fail: {ex.Message}");
            }
        }

        private async Task ScanForDevices()
        {
            // FIX 1: Find the correct local IP to bind to
            IPEndPoint localEndPoint = new IPEndPoint(IPAddress.Parse(GetLocalIpAddress()), 0);

            using (var client = new UdpClient(localEndPoint)) // Bind here
            {
                // FIX 2: Set TTL (Time To Live) to ensuring packet survives the router hop
                client.Ttl = 4;

                // Send M-SEARCH
                string req = "M-SEARCH * HTTP/1.1\r\n" +
                             "HOST: 239.255.255.250:1900\r\n" +
                             "MAN: \"ssdp:discover\"\r\n" +
                             "MX: 3\r\n" +
                             "ST: ssdp:all\r\n\r\n";

                byte[] data = Encoding.UTF8.GetBytes(req);
                var endpoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);

                try
                {
                    // Try sending multiple times
                    for (int i = 0; i < 3; i++)
                    {
                        await client.SendAsync(data, data.Length, endpoint);
                        await Task.Delay(100);
                    }
                }
                catch (SocketException ex)
                {
                    Log($" Discovery Network Error: {ex.Message}. Check firewall.");
                    return;
                }

                var timeout = TimeSpan.FromSeconds(4);
                var startTime = DateTime.Now;

                while (DateTime.Now - startTime < timeout)
                {
                    try
                    {
                        if (client.Available > 0)
                        {
                            var result = await client.ReceiveAsync();
                            string msg = Encoding.UTF8.GetString(result.Buffer);

                            // Process full metadata
                            _ = ProcessDiscoveryResponse(msg, result.RemoteEndPoint.Address.ToString());
                        }
                        else await Task.Delay(100);
                    }
                    catch { break; } // Stop loop on error
                }
            }
        }
        private async Task ProcessDiscoveryResponse(string msg, string ip)
        {
            try
            {
                // Extract Location Header
                string location = msg.Split('\n')
                    .FirstOrDefault(x => x.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
                    ?.Substring(9).Trim();

                if (string.IsNullOrEmpty(location)) return;

                // Check if we already have this IP (Basic de-duplication)
                bool exists = false;

                // FIX 1: Use Dispatcher.UIThread.Invoke
                Dispatcher.UIThread.Invoke(() => exists = DiscoveredDevices.Any(d => d.IpAddress == ip));

                if (exists) return;

                // FETCH AND PARSE XML DESCRIPTION
                using (var httpClient = new HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromSeconds(3);
                    var xmlStr = await httpClient.GetStringAsync(location);
                    var doc = XDocument.Parse(xmlStr);
                    var ns = doc.Root.GetDefaultNamespace();

                    string friendlyName = doc.Descendants(ns + "friendlyName").FirstOrDefault()?.Value ?? "Unknown";
                    string manufacturer = doc.Descendants(ns + "manufacturer").FirstOrDefault()?.Value ?? "";

                    // Find ContentDirectory service
                    string contentDirUrl = "";
                    var service = doc.Descendants(ns + "service")
                        .FirstOrDefault(s => s.Element(ns + "serviceType")?.Value.Contains("ContentDirectory") == true);

                    if (service != null)
                    {
                        contentDirUrl = service.Element(ns + "controlURL")?.Value;
                    }

                    // Calculate Base URL
                    Uri locUri = new Uri(location);
                    string baseUrl = $"{locUri.Scheme}://{locUri.Authority}";

                    // FIX 2: Use Dispatcher.UIThread.Invoke
                    Dispatcher.UIThread.Invoke(() =>
                    {
                        // Double check duplication before adding
                        if (!DiscoveredDevices.Any(d => d.FriendlyName == friendlyName))
                        {
                            DiscoveredDevices.Add(new UpnpDevice
                            {
                                FriendlyName = friendlyName,
                                IpAddress = ip,
                                ServiceType = manufacturer,
                                ContentDirectoryUrl = contentDirUrl,
                                BaseUrl = baseUrl
                            });
                        }
                    });
                }
            }
            catch (Exception)
            {
                // Ignore errors
            }
        }



        private bool IsAdministrator()
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }

        
    }

    public class UpnpDevice
    {
        public string FriendlyName { get; set; }
        public string IpAddress { get; set; }
        public string ServiceType { get; set; }
        public string ContentDirectoryUrl { get; set; }
        public string BaseUrl { get; set; }
    }


    public class RemoteItem : INotifyPropertyChanged
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public bool IsFolder { get; set; }
        public string Url { get; set; }
        public string TypeIcon { get; set; }
        public string Details { get; set; }
        public string ThumbnailUrl { get; set; }

        public long Size { get; set; }



        private double _watchProgress = 0;
        public double WatchProgress
        {
            get => _watchProgress;
            set { _watchProgress = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasProgress)); }
        }
        public bool HasProgress => _watchProgress > 0;

        private Avalonia.Media.Imaging.Bitmap? _thumbnailBitmap;
        public Avalonia.Media.Imaging.Bitmap? ThumbnailBitmap
        {
            get => _thumbnailBitmap;
            set
            {
                _thumbnailBitmap = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsIconVisible));
                OnPropertyChanged(nameof(IsImageVisible));
            }
        }
        public bool IsIconVisible => _thumbnailBitmap == null;
        public bool IsImageVisible => _thumbnailBitmap != null;














        public async Task LoadThumbnailAsync()
        {
            if (string.IsNullOrEmpty(ThumbnailUrl)) return;

            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                {
                    var response = await client.GetAsync(ThumbnailUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        var data = await response.Content.ReadAsByteArrayAsync();

                        // CRITICAL FIX: Decode the image safely on the UI thread 
                        // so the MemoryStream doesn't dispose too early.
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            using (var stream = new MemoryStream(data))
                            {
                                // DecodeToWidth saves a massive amount of RAM!
                                ThumbnailBitmap = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 200);
                            }
                        });
                    }
                }
            }
            catch
            {
                // Silently fail and keep the default SVG icon if the image is broken/unreachable
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }


    public class ServerUrlItem
    {
        public string NetworkName { get; set; }
        public string Url { get; set; }
    }

    public static class Icons
    {
        public const string Folder = "M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z";
        public const string Video = "M23 7l-7 5 7 5V7z M1 5h14v14H1z"; // Video Camera
        public const string Audio = "M9 18V5l12-2v13 M6 18a3 3 0 1 1-6 0 3 3 0 0 1 6 0zm12-2a3 3 0 1 1-6 0 3 3 0 0 1 6 0z"; // Music Note
        public const string Image = "M21 19V5a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2z M8.5 8.5a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3z M21 15l-5-5L5 21"; // Picture
        public const string File = "M13 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9z M13 2v7h7"; // Document
    }


    public class BreadcrumbItem : INotifyPropertyChanged
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public bool IsNotFirst { get; set; }
        public string FontWeight => IsLast ? "Bold" : "Normal";

        private bool _isLast;
        public bool IsLast
        {
            get => _isLast;
            set { _isLast = value; OnPropertyChanged(); OnPropertyChanged(nameof(FontWeight)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }




}
