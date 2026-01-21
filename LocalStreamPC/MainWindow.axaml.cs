using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage; // For Cross-platform folder picking
using Avalonia.Threading; // For UI Thread updates
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
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
        // --- SETTINGS ---
        public static AppConfig Config { get; private set; }
        private string _configPath = "config.json";

        public ObservableCollection<UpnpDevice> DiscoveredDevices { get; set; } = new ObservableCollection<UpnpDevice>();

        public MainWindow()
        {
            InitializeComponent();
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

        // ===========================
        //       UI EVENTS
        // ===========================





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

                    // Log($"🧹 Cache cleaned. Current size: {totalSize / 1024 / 1024} MB");
                }
                catch (Exception ex)
                {
                    Log($"⚠️ Cache Cleanup Error: {ex.Message}");
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
                    Log($"📂 Added: {path}");
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
                Log($"🗑️ Removed: {path}");
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
            Log("Settings saved!");
        }

        private void Nav_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton btn && btn.Tag is string tag)
            {
                // Avalonia uses IsVisible instead of Visibility.Collapsed/Visible
                TabServer.IsVisible = false;
                TabScanner.IsVisible = false;
                TabSettings.IsVisible = false;

                if (tag == "TabServer") TabServer.IsVisible = true;
                if (tag == "TabScanner") TabScanner.IsVisible = true;
                if (tag == "TabSettings") TabSettings.IsVisible = true;
            }
        }

        private void ListDevices_DoubleTapped(object sender, Avalonia.Input.TappedEventArgs e)
        {
            if (ListDevices.SelectedItem is UpnpDevice device)
            {
                if (string.IsNullOrEmpty(device.ContentDirectoryUrl))
                {
                    Log("This device does not support file browsing.");
                    return;
                }

                // UNCOMMENT THIS NOW:
                var browser = new RemoteBrowser(device.ContentDirectoryUrl, device.BaseUrl, device.FriendlyName);
                browser.Show();

                Log($"Opening browser for: {device.FriendlyName}");
            }
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

        // ===========================
        //    SERVER & CONFIG LOGIC
        // ===========================

        public class AppConfig
        {
            public List<string> SharedFolders { get; set; } = new List<string>();
            public int Port { get; set; } = 8080;
            public bool RunAtStartup { get; set; } = false;
            public bool MinimizeToTray { get; set; } = false;
            public bool AutoStartServer { get; set; } = false;
            public bool IsDarkMode { get; set; } = true;
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

            TxtPort.Text = Config.Port.ToString();
            ChkRunAtStartup.IsChecked = Config.RunAtStartup;
            ChkMinimizeToTray.IsChecked = Config.MinimizeToTray;
            ChkAutoStartServer.IsChecked = Config.AutoStartServer;
            ApplyTheme(Config.IsDarkMode);

            // Auto-Start Logic
            if (Config.AutoStartServer && Config.SharedFolders.Count > 0)
            {
                Log("🚀 Auto-starting Server...");
                BtnToggleServer_Click(null, null);
            }
        }



        private void BtnToggleTheme_Click(object sender, RoutedEventArgs e)
        {
            // Toggle the boolean
            Config.IsDarkMode = !Config.IsDarkMode;

            // Apply visual change
            ApplyTheme(Config.IsDarkMode);

            // Save immediately so it persists
            SaveConfig();
        }

        private void ApplyTheme(bool isDark)
        {
            if (isDark)
            {
                RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
                BtnToggleTheme.Content = "🌙 Dark Mode";

                // FIXED: Use 'Brush.Parse' (singular), not 'Brushes.Parse'
                BtnToggleTheme.Background = Avalonia.Media.Brush.Parse("#374151");
                BtnToggleTheme.Foreground = Avalonia.Media.Brushes.White;
            }
            else
            {
                RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
                BtnToggleTheme.Content = "☀️ Light Mode";

                // FIXED: Use 'Brush.Parse' (singular)
                BtnToggleTheme.Background = Avalonia.Media.Brush.Parse("#E5E7EB");
                BtnToggleTheme.Foreground = Avalonia.Media.Brushes.Black;
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
            }
            else
            {
                // Fix: Check List count instead of TxtFolderPath
                if (Config.SharedFolders.Count == 0)
                {
                    Log("⚠️ Please add at least one folder first.");
                    return;
                }

                _localIp = GetLocalIpAddress();
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

        private void SaveConfig()
        {
            // 1. FOLDERS: No need to save manually here. 
            // The Add/Remove buttons already updated the Config.SharedFolders list.

            // 2. OTHER SETTINGS
            if (int.TryParse(TxtPort.Text, out int p)) Config.Port = p;
            Config.RunAtStartup = ChkRunAtStartup.IsChecked == true;
            Config.MinimizeToTray = ChkMinimizeToTray.IsChecked == true;
            Config.AutoStartServer = ChkAutoStartServer.IsChecked == true;

            // 3. WRITE TO FILE
            try
            {
                string json = JsonSerializer.Serialize(Config);
                System.IO.File.WriteAllText(_configPath, json);
            }
            catch (Exception ex) { Log("Error saving settings: " + ex.Message); }

            SetStartup(Config.RunAtStartup);
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
                Log($"** HTTP Server active at http://{_localIp}:{Config.Port}/");
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

            try
            {
                if (rawUrl == "/")
                {
                    string relativePath = context.Request.QueryString["path"] ?? "";
                    ServeWebBrowser(context, relativePath);
                }
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
                    Log($"❌ Thumb 404: File not found {fullPath}");
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
                        Log($"⚠️ Image Resize Failed (Sending Original): {ex.Message}");
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
                        Log($"❌ FFmpeg Error: {ex.Message}");
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
                Log($"🔥 Critical Thumb Error: {ex.Message}");
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

            Log("🛑 Server stopped.");
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

        private void ServeWebBrowser(HttpListenerContext context, string pathParam)
        {
            // 1. Sanitize
            if (pathParam.Contains("..")) pathParam = "";

            // 2. VIRTUAL ROOT: If path is empty, show Shared Folders
            if (string.IsNullOrEmpty(pathParam))
            {
                var sb = new StringBuilder();
                sb.Append("<!DOCTYPE html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width, initial-scale=1'>");
                sb.Append("<title>LocalStream Root</title>");
                // Reuse styles
                sb.Append("<style>body{font-family:'Segoe UI',sans-serif;background:#f0f2f5;padding:20px} .item{background:white;padding:15px;margin-bottom:10px;border-radius:8px;display:flex;align-items:center;gap:15px;cursor:pointer;box-shadow:0 1px 3px rgba(0,0,0,0.1)} .item:hover{background:#fff} .icon{font-size:1.5em} a{text-decoration:none;color:#333;font-weight:600;flex-grow:1}</style>");
                sb.Append("</head><body>");

                sb.Append("<h1>📡 Shared Libraries</h1>");

                foreach (var folder in Config.SharedFolders)
                {
                    if (!Directory.Exists(folder)) continue;
                    var info = new DirectoryInfo(folder);
                    string link = $"/?path={WebUtility.UrlEncode(folder)}";

                    sb.Append($"<div class='item' onclick=\"window.location='{link}'\">");
                    sb.Append("<span class='icon'>💽</span>");
                    sb.Append($"<a href='{link}'>{info.Name}</a>");
                    sb.Append($"<span style='color:#888;font-size:0.8em'>{folder}</span>");
                    sb.Append("</div>");
                }

                if (Config.SharedFolders.Count == 0) sb.Append("<p>No folders shared. Add them in the Server App.</p>");

                sb.Append("</body></html>");

                byte[] b = Encoding.UTF8.GetBytes(sb.ToString());
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.ContentLength64 = b.Length;
                context.Response.OutputStream.Write(b, 0, b.Length);
                context.Response.Close();
                return;
            }

            // 3. NORMAL BROWSING
            string currentPath = pathParam;
            bool isAllowed = Config.SharedFolders.Any(root => currentPath.StartsWith(root, StringComparison.OrdinalIgnoreCase));

            if (!isAllowed || !Directory.Exists(currentPath))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            var html = new StringBuilder();
            html.Append("<!DOCTYPE html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width, initial-scale=1'>");
            html.Append($"<title>{new DirectoryInfo(currentPath).Name}</title>");
            html.Append("<style>");

            // CSS STYLES
            html.Append("body { font-family: 'Segoe UI', sans-serif; background: #f0f2f5; margin: 0; padding: 20px; padding-bottom: 80px; }");
            html.Append(".header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px; flex-wrap: wrap; gap: 10px; }");
            html.Append("h1 { margin: 0; color: #333; font-size: 1.5rem; word-break: break-all; }");
            html.Append(".btn { padding: 8px 15px; border: none; border-radius: 5px; cursor: pointer; font-weight: 500; text-decoration: none; color: white; display: inline-flex; align-items: center; justify-content: center; gap: 5px; }");
            html.Append(".btn-primary { background: #007bff; } .btn-primary:hover { background: #0056b3; }");
            html.Append(".btn-secondary { background: #6c757d; } .btn-secondary:hover { background: #545b62; }");
            html.Append(".btn-success { background: #28a745; } .btn-success:hover { background: #218838; }");
            html.Append(".container { display: flex; flex-direction: column; gap: 10px; }");
            html.Append(".grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(160px, 1fr)); gap: 15px; }");
            html.Append(".item { background: white; border-radius: 8px; box-shadow: 0 1px 3px rgba(0,0,0,0.1); overflow: hidden; transition: transform 0.2s; position: relative; }");
            html.Append(".item:hover { background: #f9f9f9; box-shadow: 0 4px 8px rgba(0,0,0,0.15); }");
            html.Append(".checkbox { z-index: 10; cursor: pointer; }");

            // View Specific Styles
            html.Append(".list .item { display: flex; align-items: center; padding: 10px; gap: 15px; }");
            html.Append(".list .preview-box { display: none !important; }");
            html.Append(".list .icon { font-size: 1.5em; width: 40px; text-align: center; }");
            html.Append(".list .details { flex-grow: 1; display: flex; align-items: center; justify-content: space-between; overflow: hidden; }");
            html.Append(".list .actions { display: flex; gap: 5px; margin-left: 10px; }");

            html.Append(".grid .item { display: flex; flex-direction: column; aspect-ratio: 3/4; }");
            html.Append(".grid .list-only { display: none !important; }");
            html.Append(".grid .checkbox { position: absolute; top: 8px; left: 8px; width: 20px; height: 20px; }");
            html.Append(".grid .preview-box { flex-grow: 1; background: #eee; display: flex; justify-content: center; align-items: center; font-size: 3em; background-size: cover; background-position: center; }");
            html.Append(".grid .details { padding: 10px; display: flex; flex-direction: column; gap: 8px; border-top: 1px solid #eee; }");
            html.Append(".grid .actions { display: flex; gap: 5px; margin-top: auto; }");
            html.Append(".grid .actions .btn { flex: 1; }");

            html.Append(".name { text-decoration: none; color: #333; font-weight: 500; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; display: block; }");
            html.Append(".meta { color: #888; font-size: 0.85em; }");
            html.Append("</style>");

            // JS SCRIPTS
            html.Append("<script>");
            html.Append("function toggleView() { var c = document.getElementById('container'); var isGrid = c.classList.contains('grid'); c.className = isGrid ? 'container list' : 'container grid'; localStorage.setItem('viewMode', isGrid ? 'list' : 'grid'); }");
            html.Append("function updateSelection() { var count = document.querySelectorAll('.file-check:checked').length; var bar = document.getElementById('selectionBar'); bar.style.display = count > 0 ? 'flex' : 'none'; document.getElementById('selCount').innerText = count + ' selected'; }");
            html.Append("function downloadSelected() { var checks = document.querySelectorAll('.file-check:checked'); var files = []; checks.forEach(c => files.push(c.value)); if(files.length===0) return; window.location = '/zip?path=" + WebUtility.UrlEncode(currentPath) + "&files=' + encodeURIComponent(files.join('|')); }");
            html.Append("function downloadAll() { window.location = '/zip?path=" + WebUtility.UrlEncode(currentPath) + "'; }");
            html.Append("function uploadFiles() { document.getElementById('fileInput').click(); }");
            html.Append("function handleUpload(input) { if(input.files.length === 0) return; var formData = new FormData(); for(var i=0; i<input.files.length; i++) formData.append('files', input.files[i]); fetch('/upload?path=" + WebUtility.UrlEncode(currentPath) + "', {method:'POST', body:formData}).then(r => window.location.reload()).catch(e => alert('Upload failed')); }");
            html.Append("window.onload = function() { var mode = localStorage.getItem('viewMode') || 'list'; document.getElementById('container').className = 'container ' + mode; };");
            html.Append("</script></head><body>");

            // HEADER
            string folderName = new DirectoryInfo(currentPath).Name;
            html.Append("<div class='header'>");
            html.Append($"<h1>📂 {folderName}</h1>");
            html.Append("<div style='display:flex; gap:10px'>");
            html.Append("<input type='file' id='fileInput' multiple style='display:none' onchange='handleUpload(this)'>");
            html.Append("<button class='btn btn-success' onclick='uploadFiles()'>⬆️ Upload</button>");
            html.Append("<button class='btn btn-primary' onclick='downloadAll()'>📦 Zip</button>");
            html.Append("<button class='btn btn-secondary' onclick='toggleView()'>👁 View</button>");
            html.Append("</div></div>");

            html.Append("<div id='container' class='container list'>");

            // --- FIX: UP BUTTON (Now matches Folder layout) ---
            string parent = Path.GetDirectoryName(currentPath);
            bool parentIsAllowed = parent != null && Config.SharedFolders.Any(root => parent.StartsWith(root));
            string upLink = parentIsAllowed ? $"/?path={WebUtility.UrlEncode(parent)}" : "/";

            html.Append($"<div class='item' onclick=\"window.location='{upLink}'\">");
            // Standard Structure: Preview Box + List Icon + Details
            html.Append("<div class='preview-box'>⬆️</div>");
            html.Append("<span class='list-only icon'>⬆️</span>");
            html.Append("<div class='details'><div><a class='name'>..</a><span class='meta'>Go Up</span></div></div>");
            html.Append("</div>");
            // --------------------------------------------------

            // SUB-FOLDERS
            foreach (var dir in Directory.GetDirectories(currentPath))
            {
                var info = new DirectoryInfo(dir);
                string link = $"/?path={WebUtility.UrlEncode(dir)}";

                html.Append($"<div class='item' onclick=\"window.location='{link}'\">");
                html.Append("<div class='preview-box'>📁</div><span class='list-only icon'>📁</span>");
                html.Append($"<div class='details'><div><a class='name'>{info.Name}</a><span class='meta'>Folder</span></div></div>");
                html.Append("</div>");
            }

            // FILES
            foreach (var file in Directory.GetFiles(currentPath))
            {
                var info = new FileInfo(file);
                string fullPathB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(file));
                string url = $"/file/{fullPathB64}";
                string thumbUrl = $"/thumb/{fullPathB64}";
                string icon = GetFileIcon(info.Extension);
                bool isMedia = IsMediaFile(info.Name);
                string bgStyle = isMedia ? $"style=\"background-image: url('{thumbUrl}');\"" : "";

                html.Append("<div class='item'>");
                html.Append($"<input type='checkbox' class='checkbox file-check' value='{WebUtility.UrlEncode(info.Name)}' onclick='event.stopPropagation(); updateSelection()'>");
                html.Append($"<div class='preview-box' {bgStyle}>{(!isMedia ? icon : "")}</div>");
                html.Append($"<span class='list-only icon'>{icon}</span>");
                html.Append("<div class='details'>");
                html.Append($"<div style='flex-grow:1; min-width:0;'><a href='{url}' target='_blank' class='name'>{info.Name}</a><span class='meta'>{FormatSize(info.Length)}</span></div>");
                html.Append("<div class='actions'>");
                html.Append($"<a href='{url}' target='_blank' class='btn btn-primary btn-action'>▶</a>");
                html.Append($"<a href='{url}' download class='btn btn-secondary btn-action'>⬇</a>");
                html.Append("</div></div></div>");
            }

            html.Append("</div>"); // End Container

            // Selection Bar
            html.Append("<div id='selectionBar' style='position:fixed; bottom:0; left:0; right:0; background:#333; color:white; padding:15px; display:none; justify-content:space-between; align-items:center; box-shadow:0 -2px 10px rgba(0,0,0,0.2);'>");
            html.Append("<span id='selCount' style='font-weight:bold'>0 selected</span>");
            html.Append("<button class='btn btn-success' onclick='downloadSelected()'>📦 Download Selected</button>");
            html.Append("</div>");

            html.Append("</body></html>");

            byte[] buffer = Encoding.UTF8.GetBytes(html.ToString());
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = buffer.Length;
            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            context.Response.Close();
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
                    Log("⚠️ Upload blocked: Invalid path.");
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                // 3. Security: Ensure the path is actually inside a Shared Folder
                // This prevents people from uploading to C:\Windows or other unshared places
                bool isAllowed = Config.SharedFolders.Any(root => pathParam.StartsWith(root, StringComparison.OrdinalIgnoreCase));

                if (!isAllowed)
                {
                    Log($"⚠️ Upload blocked: '{pathParam}' is not a shared folder.");
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

                        Log($"📥 Upload received ({FormatSize(data.Length)}). Processing...");

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
                                    Log($"✅ Saved: {filename}");
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
                Log($"🔥 Upload Error: {ex.Message}");
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
            Log($"▶️ Served: {fileInfo.Name}");
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
                Log($"📂 Browse: ID '{objectId}'");

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
                Log($"🔥 BROWSE ERROR: {ex.Message}");
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
                    // LINUX FIX: Bind to 0.0.0.0 (Any) instead of specific IP
                    // This allows Linux to pick up multicast packets from all interfaces
                    var localEp = new IPEndPoint(IPAddress.Any, 1900);

                    client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    client.ExclusiveAddressUse = false;

                    try
                    {
                        client.Client.Bind(localEp);

                        // JOIN the group specifying the interface IP
                        client.JoinMulticastGroup(IPAddress.Parse("239.255.255.250"), IPAddress.Parse(_localIp));

                        Log("📡 SSDP Active (Upnp Server Enabled)");
                    }
                    catch (Exception bindEx)
                    {
                        Log($"⚠️ SSDP Bind Error: {bindEx.Message}");
                        return;
                    }

                    // ... (Rest of the Receive Loop is the same) ...
                    while (!token.IsCancellationRequested)
                    {
                        // Paste the existing loop here
                        try
                        {
                            var result = await client.ReceiveAsync(token);
                            string msg = Encoding.UTF8.GetString(result.Buffer);

                            if (msg.Contains("M-SEARCH") && (msg.Contains("MediaServer") || msg.Contains("ssdp:all") || msg.Contains("upnp:rootdevice")))
                            {
                                // ... (Response logic remains the same) ...
                                string response = $"HTTP/1.1 200 OK\r\n" +
                                                  $"CACHE-CONTROL: max-age=1800\r\n" +
                                                  $"DATE: {DateTime.Now:r}\r\n" +
                                                  $"EXT:\r\n" +
                                                  $"LOCATION: http://{_localIp}:{PORT}/description.xml\r\n" +
                                                  $"SERVER: Linux UPnP/1.0\r\n" +
                                                  $"ST: urn:schemas-upnp-org:device:MediaServer:1\r\n" +
                                                  $"USN: {UDN}::urn:schemas-upnp-org:device:MediaServer:1\r\n\r\n";

                                byte[] bytes = Encoding.UTF8.GetBytes(response);
                                await client.SendAsync(bytes, bytes.Length, result.RemoteEndPoint);
                            }
                        }
                        catch (OperationCanceledException) { break; }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"❌ SSDP Critical Fail: {ex.Message}");
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
                    Log($"⚠️ Discovery Network Error: {ex.Message}. Check firewall.");
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
}