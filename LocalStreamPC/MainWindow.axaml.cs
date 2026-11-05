using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using LocalStreamPC.Models;
using LocalStreamPC.Services;
using LocalStreamPC.ViewModels;

namespace LocalStreamPC
{
    /// <summary>
    /// View only: forwards clicks to <see cref="MainViewModel"/> and handles purely visual things
    /// (theme, dialogs, clipboard, folder picker, tray). All logic lives in the ViewModel and services.
    /// </summary>
    public partial class MainWindow : Window
    {
        private static readonly double[] GridWidths = { 140, 180, 240, 320, 450 };
        private readonly MainViewModel _vm;
        private UpdateInfo? _update;
        private bool _exitRequested;
        private bool _initialising = true;

        public static readonly StyledProperty<double> GridItemWidthProperty =
            AvaloniaProperty.Register<MainWindow, double>(nameof(GridItemWidth), 180.0);
        public double GridItemWidth
        {
            get => GetValue(GridItemWidthProperty);
            set { SetValue(GridItemWidthProperty, value); GridItemHeight = value / 1.777; } // keep 16:9
        }

        public static readonly StyledProperty<double> GridItemHeightProperty =
            AvaloniaProperty.Register<MainWindow, double>(nameof(GridItemHeight), 101.0);
        public double GridItemHeight
        {
            get => GetValue(GridItemHeightProperty);
            set => SetValue(GridItemHeightProperty, value);
        }

        /// <summary>For the XAML designer/previewer only.</summary>
        public MainWindow() : this(App.CreateViewModel()) { }

        public MainWindow(MainViewModel vm)
        {
            _vm = vm;
            DataContext = vm;
            InitializeComponent();

            LoadSettingsIntoForm();
            CmbGridSize.SelectedIndex = Math.Clamp(vm.Config.GridSizeIndex, 0, GridWidths.Length - 1);
            GridItemWidth = GridWidths[CmbGridSize.SelectedIndex];
            ApplyTheme(vm.Config.IsDarkMode);
            CreateTrayIcon();

            if (Environment.GetCommandLineArgs().Contains("-minimized") && vm.Config.MinimizeToTray)
            {
                WindowState = WindowState.Minimized;
                ShowInTaskbar = false;
            }

            // Seek when the user lets go of the slider, not on every pixel while dragging.
            SliderCastSeek.AddHandler(PointerPressedEvent, (_, _) => _vm.Cast.IsUserSeeking = true, RoutingStrategies.Tunnel, handledEventsToo: true);
            SliderCastSeek.AddHandler(PointerReleasedEvent, async (_, _) =>
            {
                await _vm.Cast.SeekAsync(SliderCastSeek.Value);
                _vm.Cast.IsUserSeeking = false;
            }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

            ApplyTabOrder();
            vm.TabOrderChanged += ApplyTabOrder;
            GoToTab(vm.StartTab);

            BtnConfirmYes.Click += (_, _) => FinishConfirm(true);
            BtnConfirmNo.Click += (_, _) => FinishConfirm(false);
            TxtVersion.Text = "v" + UpdateService.CurrentVersionText;
            Closing += Window_Closing;
            Loaded += MainWindow_Loaded;
            _initialising = false;
        }

        private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Minimized && !ShowInTaskbar) Hide();

            if (_vm.Config.AutoStartServer && _vm.SharedFolders.Count > 0)
            {
                _vm.Log("Auto-starting server...");
                await ToggleServerAsync();
            }
            if (_vm.Config.CastReceiverEnabled) await _vm.Cast.SetReceivingAsync(true);
            await CheckForUpdatesAsync(isStartup: true);
        }

        // ================================================================ navigation

        private void Nav_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is RadioButton { Tag: string tag }) GoToTab(tag);
        }

        /// <summary>Opens a sidebar tab and ticks its button.</summary>
        private void GoToTab(string tag)
        {
            foreach (var b in NavPanel.Children.OfType<RadioButton>())
                b.IsChecked = (string?)b.Tag == tag;
            switch (tag)
            {
                case "TabLibrary": _vm.OpenLibrary(); break;
                case "TabSettings": _vm.CurrentTab = AppTab.Settings; LoadSettingsIntoForm(); break;
                case "TabHistory": _vm.CurrentTab = AppTab.History; break;
                default: _vm.CurrentTab = AppTab.Server; break;
            }
        }

        /// <summary>Puts the sidebar buttons in the order chosen in Settings.</summary>
        private void ApplyTabOrder()
        {
            var buttons = NavPanel.Children.OfType<RadioButton>().Where(b => b.Tag is string).ToDictionary(b => (string)b.Tag!);
            var order = _vm.TabOrder.Where(buttons.ContainsKey).ToList();
            if (order.Count == NavPanel.Children.Count && order.Select((t, i) => (string?)NavPanel.Children[i].Tag == t).All(x => x)) return;
            NavPanel.Children.Clear();
            foreach (var tag in order) NavPanel.Children.Add(buttons[tag]);
        }

        private void BtnTabUp_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string tag }) _vm.MoveTab(tag, -1);
        }

        private void BtnTabDown_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string tag }) _vm.MoveTab(tag, +1);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (e.InitialPressMouseButton == MouseButton.XButton1) // mouse "back" button
            {
                _vm.GoBack();
                e.Handled = true;
            }
        }

        // ================================================================ shared folders + server

        private async void BtnAddFolder_Click(object? sender, RoutedEventArgs e)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Add Shared Folder",
                AllowMultiple = true,
            });
            var paths = folders
                .Select(f => f.TryGetLocalPath())
                .Where(p => !string.IsNullOrEmpty(p))
                .Cast<string>();
            _vm.AddSharedFolders(paths);
        }

        private void BtnRemoveFolder_Click(object? sender, RoutedEventArgs e)
        {
            if (ListSharedFolders.SelectedItem is string path) _vm.RemoveSharedFolder(path);
        }

        private async void BtnToggleServer_Click(object? sender, RoutedEventArgs e) => await ToggleServerAsync();

        private async Task ToggleServerAsync()
        {
            BtnToggleServer.IsEnabled = false;
            try
            {
                if (_vm.IsServerRunning) await _vm.StopServerAsync();
                else await _vm.StartServerAsync();
            }
            finally
            {
                BtnToggleServer.IsEnabled = true;
                BtnToggleServer.Content = _vm.IsServerRunning ? "STOP SERVER" : "START SERVER";
                BtnToggleServer.Background = _vm.IsServerRunning ? Brushes.Crimson : Brush.Parse("#10B981");
            }
        }

        private async void BtnCopySpecificIp_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string url } btn || Clipboard == null) return;
            await Clipboard.SetTextAsync(url);
            var old = btn.Content;
            btn.Content = "Copied!";
            btn.Background = Brushes.ForestGreen;
            await Task.Delay(2000);
            btn.Content = old;
            btn.Background = Brush.Parse("#3B82F6");
        }

        private async void BtnCopyLogs_Click(object? sender, RoutedEventArgs e)
        {
            if (Clipboard != null && !string.IsNullOrEmpty(_vm.LogText))
            {
                await Clipboard.SetTextAsync(_vm.LogText);
                _vm.Log("Logs copied to clipboard.");
            }
        }

        // ================================================================ cast receiver

        private async void SwCastReceiver_Click(object? sender, RoutedEventArgs e)
        {
            bool want = SwCastReceiver.IsChecked == true;
            bool on = await _vm.Cast.SetReceivingAsync(want);
            SwCastReceiver.SetCurrentValue(ToggleButton.IsCheckedProperty, on); // snap back if it failed
        }

        private async void BtnCastPlayPause_Click(object? sender, RoutedEventArgs e) => await _vm.Cast.PlayPauseAsync();
        private async void BtnCastStop_Click(object? sender, RoutedEventArgs e) => await _vm.Cast.StopAsync();
        private async void BtnCastNext_Click(object? sender, RoutedEventArgs e) => await _vm.Cast.NextAsync();
        private async void BtnCastPrevious_Click(object? sender, RoutedEventArgs e) => await _vm.Cast.PreviousAsync();
        private async void BtnCastFullscreen_Click(object? sender, RoutedEventArgs e) => await _vm.Cast.ToggleFullscreenAsync();

        // ================================================================ discovery + remote browsing

        private async void BtnScan_Click(object? sender, RoutedEventArgs e) => await ScanAsync();

        private async Task ScanAsync()
        {
            BtnScan.Content = "Scanning...";
            BtnScan.IsEnabled = false;
            await _vm.ScanAsync();
            BtnScan.Content = "🔄 Refresh Network";
            BtnScan.IsEnabled = true;
        }

        private void BtnConnectServer_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: UpnpDevice device }) _vm.Connect(device);
        }

        private void ListDevices_DoubleTapped(object? sender, TappedEventArgs e)
        {
            if (ListDevices.SelectedItem is UpnpDevice device) _vm.Connect(device);
        }

        private void ListRemoteFiles_DoubleTapped2(object? sender, TappedEventArgs e)
        {
            if (sender is ListBox { SelectedItem: RemoteItem item }) _vm.OpenItem(item);
        }

        private void BtnBackToLibrary_Click(object? sender, RoutedEventArgs e) => _vm.OpenLibrary();

        private async void BtnStartFromList_Click(object? sender, RoutedEventArgs e)
        {
            if (_vm.SharedFolders.Count == 0) { GoToTab("TabServer"); return; }
            await ToggleServerAsync();
        }

        /// <summary>The server button above the library: this PC, the other servers found, "All servers…".</summary>
        private void BtnSwitchServer_Click(object? sender, RoutedEventArgs e)
        {
            var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            foreach (var d in _vm.SwitchableServers)
            {
                var device = d;
                bool current = d.IsThisPc ? _vm.BrowsingName == "This PC" : _vm.BrowsingName == d.FriendlyName;
                var item = new MenuItem { Header = (current ? "✓  " : "     ") + (d.IsThisPc ? "This PC (my library)" : d.FriendlyName) };
                item.Click += (_, _) => { if (!current) _vm.Connect(device); };
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            var all = new MenuItem { Header = "     All servers on the network…" };
            all.Click += (_, _) => _vm.ShowServerList();
            menu.Items.Add(all);
            menu.ShowAt(BtnSwitchServer);
            if (_vm.DiscoveredDevices.All(x => x.IsThisPc) && !_vm.IsScanning) _ = _vm.ScanAsync(); // fill the menu for next time
        }

        private void BtnBreadcrumb_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string id }) _vm.NavigateToBreadcrumb(id);
        }

        private void BtnSeeAllHistory_Click(object? sender, RoutedEventArgs e) => _vm.ShowAllHistory();
        private void BtnSeeAllRandom_Click(object? sender, RoutedEventArgs e) => _vm.ShowAllRecommendations();
        private async void BtnPlayAll_Click(object? sender, RoutedEventArgs e) => await _vm.PlayCurrentFolderAsync();

        private void CtxMenuPlay_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { DataContext: RemoteItem item }) _vm.Play(new[] { item });
        }

        private async void CtxMenuPlayOnTv_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { DataContext: RemoteItem item }) await _vm.Tv.OpenPickerAsync(item);
        }

        // ================================================================ Play on TV

        private async void BtnPickTv_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: RendererDevice device }) await _vm.Tv.PlayOnAsync(device);
        }

        private async void BtnTvRescan_Click(object? sender, RoutedEventArgs e) => await _vm.Tv.ScanAsync(force: true);
        private void BtnTvPickerClose_Click(object? sender, RoutedEventArgs e) => _vm.Tv.ClosePicker();

        private async void BtnTvPlayPause_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: TvSessionRow row }) await _vm.Tv.ControlAsync(row.DeviceId, row.IsPlaying ? "pause" : "play");
        }
        // ---------------------------------------------------------------- streams, share links, history

        private void BtnStopStream_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: StreamRow row }) _vm.Streams.Stop(row);
        }

        private async void BtnCopyShare_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: ShareRow row } btn || Clipboard == null) return;
            await Clipboard.SetTextAsync(row.Url);
            btn.Content = "Copied!";
            await Task.Delay(1500);
            btn.Content = "Copy link";
        }

        private async void BtnRevokeShare_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: ShareRow row }) return;
            if (await ConfirmAsync($"Stop the share link for \"{row.Name}\"? People who have it won't be able to open it any more.", "Stop link"))
                _vm.Shares.Revoke(row);
        }

        private async void BtnClearHistory_Click(object? sender, RoutedEventArgs e)
        {
            if (await ConfirmAsync("Clear the whole history? This can't be undone.", "Clear")) _vm.History.Clear();
        }

        private void BtnRescanLibrary_Click(object? sender, RoutedEventArgs e) => _vm.Library.Rescan();

        private async void BtnExcludeFolder_Click(object? sender, RoutedEventArgs e)
        {
            var start = _vm.SharedFolders.FirstOrDefault() is string first ? await StorageProvider.TryGetFolderFromPathAsync(first) : null;
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder to leave out of the library", AllowMultiple = true, SuggestedStartLocation = start });
            string? error = null;
            foreach (var f in folders)
                if (f.TryGetLocalPath() is string path) error = _vm.Library.Exclude(path) ?? error;
            TxtLibraryError.Text = error ?? "";
            TxtLibraryError.IsVisible = error != null;
        }

        private void BtnIncludeFolder_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string path }) _vm.Library.Include(path);
        }

        private void BtnOpenTailscale_Click(object? sender, RoutedEventArgs e) => OpenInBrowser("https://tailscale.com/download");
        private void BtnOpenTmdb_Click(object? sender, RoutedEventArgs e) => OpenInBrowser("https://www.themoviedb.org/settings/api");
        private void BtnOpenOsub_Click(object? sender, RoutedEventArgs e) => OpenInBrowser("https://www.opensubtitles.com/en/consumers");

        private async void BtnBrowseCert_Click(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose the HTTPS certificate",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Certificates") { Patterns = new[] { "*.pfx", "*.p12", "*.crt", "*.pem", "*.cer" } } },
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is string path) TxtCertPath.Text = path;
        }

        private async void BtnTvStop_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: TvSessionRow row }) await _vm.Tv.ControlAsync(row.DeviceId, "stop");
        }
        private async void BtnTvBack_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: TvSessionRow row }) await _vm.Tv.ControlAsync(row.DeviceId, "seek", Math.Max(0, row.Position - 30));
        }
        private async void BtnTvForward_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: TvSessionRow row }) await _vm.Tv.ControlAsync(row.DeviceId, "seek", Math.Min(row.Duration - 5, row.Position + 30));
        }

        private TaskCompletionSource<bool>? _confirm;

        /// <summary>A yes/no question shown over the window.</summary>
        private Task<bool> ConfirmAsync(string message, string yesText)
        {
            _confirm?.TrySetResult(false);
            var confirm = _confirm = new TaskCompletionSource<bool>();
            TxtConfirmMessage.Text = message;
            BtnConfirmYes.Content = yesText;
            ConfirmOverlay.IsVisible = true;
            return confirm.Task;
        }

        private void FinishConfirm(bool result)
        {
            ConfirmOverlay.IsVisible = false;
            _confirm?.TrySetResult(result);
            _confirm = null;
        }

        private void CtxMenuRemoveHistory_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { DataContext: RemoteItem item }) _vm.RemoveFromHistory(item);
        }

        private void CmbGridSize_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (CmbGridSize == null || CmbGridSize.SelectedIndex < 0) return;
            GridItemWidth = GridWidths[Math.Min(CmbGridSize.SelectedIndex, GridWidths.Length - 1)];
            if (!_initialising) _vm.SaveGridSize(CmbGridSize.SelectedIndex);
        }

        // ================================================================ settings

        private void LoadSettingsIntoForm()
        {
            var c = _vm.Config;
            TxtPort.Text = c.Port.ToString();
            ChkRunAtStartup.IsChecked = c.RunAtStartup;
            ChkMinimizeToTray.IsChecked = c.MinimizeToTray;
            ChkAutoStartServer.IsChecked = c.AutoStartServer;
            ChkEnableAuth.IsChecked = c.EnableAuth;
            ChkAllowDeletion.IsChecked = c.AllowDeletion;
            ChkAllowDlna.IsChecked = c.AllowDlnaWithoutLogin;
            ChkCastFullscreen.IsChecked = c.CastFullscreen;
            ChkBuiltInPlayer.IsChecked = c.UseBuiltInPlayer;
            TxtCastPort.Text = c.CastPort.ToString();
            TxtVlcPath.Text = c.VlcPath ?? "";
            ChkKeepAwake.IsChecked = c.KeepAwakeWhileStreaming;
            ChkCastResume.IsChecked = c.CastResume;
            TxtSlideshowSeconds.Text = c.SlideshowSeconds.ToString();
            ChkHardwareEncoding.IsChecked = c.UseHardwareEncoding;
            TxtSpeedLimit.Text = c.StreamSpeedLimitMbps.ToString();
            ChkEnableHttps.IsChecked = c.EnableHttps;
            TxtHttpsPort.Text = c.HttpsPort.ToString();
            TxtCertPath.Text = c.HttpsCertPath ?? "";
            TxtKeyPath.Text = c.HttpsKeyPath ?? "";
            TxtCertPassword.Text = c.HttpsCertPassword ?? "";
            TxtTmdbKey.Text = c.TmdbApiKey ?? "";
            TxtOsubKey.Text = c.OpenSubtitlesApiKey ?? "";
            TxtSubLanguages.Text = c.SubtitleLanguages;
            CmbSubScale.SelectedIndex = c.CastSubtitleScale switch { <= 80 => 0, <= 115 => 1, <= 160 => 2, _ => 3 };
            string? tailscale = NetworkHelper.GetTailscaleAddress();
            TxtTailscaleState.Text = tailscale != null ? $"Tailscale is connected on this PC: http://{tailscale}:{c.Port}/" : "";
            TxtTailscaleState.IsVisible = tailscale != null;
            _ = _vm.RefreshEncoderTextAsync();
            TxtSettingsError.IsVisible = false;
            _vm.Accounts.CancelEdit();
            _vm.Accounts.Reload();
        }

        private void BtnSaveSettings_Click(object? sender, RoutedEventArgs e)
        {
            string? error = _vm.SaveSettings(new SettingsInput(
                TxtPort.Text ?? "",
                ChkRunAtStartup.IsChecked == true,
                ChkMinimizeToTray.IsChecked == true,
                ChkAutoStartServer.IsChecked == true,
                ChkEnableAuth.IsChecked == true,
                ChkAllowDeletion.IsChecked == true,
                ChkAllowDlna.IsChecked == true,
                TxtCastPort.Text ?? "",
                ChkCastFullscreen.IsChecked == true,
                TxtVlcPath.Text ?? "",
                ChkBuiltInPlayer.IsChecked == true,
                ChkKeepAwake.IsChecked == true,
                ChkCastResume.IsChecked == true,
                TxtSlideshowSeconds.Text ?? "")
            {
                UseHardwareEncoding = ChkHardwareEncoding.IsChecked == true,
                SpeedLimitMbps = TxtSpeedLimit.Text ?? "0",
                EnableHttps = ChkEnableHttps.IsChecked == true,
                HttpsPort = TxtHttpsPort.Text ?? "",
                HttpsCertPath = TxtCertPath.Text ?? "",
                HttpsKeyPath = TxtKeyPath.Text ?? "",
                HttpsCertPassword = TxtCertPassword.Text ?? "",
                TmdbApiKey = TxtTmdbKey.Text ?? "",
                OpenSubtitlesApiKey = TxtOsubKey.Text ?? "",
                SubtitleLanguages = TxtSubLanguages.Text ?? "",
                CastSubtitleScale = CmbSubScale.SelectedItem is ComboBoxItem { Tag: string scale } && int.TryParse(scale, out int sc) ? sc : 100,
            });

            TxtSettingsError.Text = error ?? "";
            TxtSettingsError.IsVisible = error != null;
        }

        // ---------------------------------------------------------------- web accounts

        private void BtnAddAccount_Click(object? sender, RoutedEventArgs e) => _vm.Accounts.BeginAdd();
        private void BtnEditAccount_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string name }) _vm.Accounts.BeginEdit(name);
        }
        private void BtnCancelAccount_Click(object? sender, RoutedEventArgs e) => _vm.Accounts.CancelEdit();
        private void BtnSaveAccount_Click(object? sender, RoutedEventArgs e) => _vm.Accounts.SaveEdit();
        private void BtnToggleAccountPwd_Click(object? sender, RoutedEventArgs e) => TogglePasswordVisibility(TxtAccountPassword, sender);

        private async void BtnRemoveAccount_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string name }) return;
            if (!await ConfirmAsync($"Remove the account \"{name}\"? It won't be able to log in any more.", "Remove")) return;
            string? error = _vm.Accounts.Remove(name);
            if (error != null) { TxtSettingsError.Text = error; TxtSettingsError.IsVisible = true; }
        }

        private static void TogglePasswordVisibility(TextBox box, object? sender)
        {
            if (sender is not Button { Content: TextBlock icon }) return;
            bool hidden = box.PasswordChar == '*';
            box.PasswordChar = hidden ? '\0' : '*';
            icon.Text = hidden ? "🙈" : "👁️";
        }

        // ================================================================ theme

        private void BtnToggleTheme_Click(object? sender, RoutedEventArgs e)
        {
            bool dark = !_vm.Config.IsDarkMode;
            _vm.SetDarkMode(dark);
            ApplyTheme(dark);
        }

        private void ApplyTheme(bool isDark)
        {
            RequestedThemeVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;
            ToolTip.SetTip(BtnToggleTheme, isDark ? "Switch to Light Mode" : "Switch to Dark Mode");
            BtnToggleTheme.Background = Brush.Parse(isDark ? "#374151" : "#E5E7EB");
            ThemeIconPath.Data = Geometry.Parse(isDark
                ? "M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z"
                : "M12 1v2M12 21v2M4.22 4.22l1.42 1.42M18.36 18.36l1.42 1.42M1 12h2M21 12h2M4.22 19.78l1.42-1.42M18.36 5.64l1.42-1.42M12 17a5 5 0 1 0 0-10 5 5 0 0 0 0 10z");
            ThemeIconPath.Stroke = isDark ? Brushes.White : Brushes.Black;
        }

        // ================================================================ updates

        private async void BtnCheckUpdate_Click(object? sender, RoutedEventArgs e)
        {
            BtnCheckUpdate.Content = "⏳";
            BtnCheckUpdate.IsEnabled = false;
            await CheckForUpdatesAsync(isStartup: false);
        }

        private async Task CheckForUpdatesAsync(bool isStartup)
        {
            try
            {
                var info = await _vm.CheckForUpdatesAsync();
                if (info.IsUpdateAvailable)
                {
                    _update = info;
                    _vm.Log($"Update available: {info.LatestVersion} (you have {info.CurrentVersion}).");
                    bool skipped = isStartup && info.LatestVersion == _vm.Config.SkippedUpdate;
                    bool oneClick = UpdateService.CanInstall(info);
                    TxtUpdateMessage.Text = $"Version {info.LatestVersion} of LocalStream is available. You have {info.CurrentVersion}." +
                        (oneClick ? "\n\nInstalling takes a few seconds: LocalStream closes, updates and starts again." : "\n\nDownload it from the release page.");
                    TxtUpdateNotes.Text = info.Notes?.Trim() ?? "";
                    UpdateNotesScroller.IsVisible = !string.IsNullOrWhiteSpace(info.Notes);
                    BtnDownloadUpdate.Content = oneClick ? "Install update" : "Open release page";
                    UpdateProgressPanel.IsVisible = false;
                    UpdateButtons.IsVisible = true;
                    if (!skipped) UpdateDialogOverlay.IsVisible = true;
                    BtnCheckUpdate.Content = "⭐";
                    ToolTip.SetTip(BtnCheckUpdate, "Update Available!");
                    BtnCheckUpdate.Background = Brushes.ForestGreen;
                }
                else if (!isStartup)
                {
                    _vm.Log($"You are running the latest version ({info.CurrentVersion}).");
                    BtnCheckUpdate.Content = "✔️";
                    ToolTip.SetTip(BtnCheckUpdate, "Up to date");
                }
            }
            catch (Exception ex)
            {
                _vm.Log($"Update check failed: {ex.Message}");
                if (!isStartup)
                {
                    BtnCheckUpdate.Content = "❌";
                    ToolTip.SetTip(BtnCheckUpdate, "Check Failed");
                }
            }

            if (!isStartup)
            {
                await Task.Delay(4000);
                if (BtnCheckUpdate.Content?.ToString() != "⭐")
                {
                    BtnCheckUpdate.Content = new Avalonia.Controls.Shapes.Path
                    {
                        Classes = { "icon" },
                        Data = Geometry.Parse("M21 2v6h-6 M3 12a9 9 0 0 1 15-6.7L21 8 M3 22v-6h6 M21 12a9 9 0 0 1-15 6.7L3 16"),
                        Width = 20, Height = 20, Stroke = Brushes.White,
                    };
                    ToolTip.SetTip(BtnCheckUpdate, "Check for Updates");
                }
                BtnCheckUpdate.IsEnabled = true;
            }
        }

        private void BtnCloseUpdateDialog_Click(object? sender, RoutedEventArgs e) => UpdateDialogOverlay.IsVisible = false;

        private void BtnSkipUpdate_Click(object? sender, RoutedEventArgs e)
        {
            if (_update != null) _vm.SkipUpdate(_update.LatestVersion);
            UpdateDialogOverlay.IsVisible = false;
        }

        private async void BtnDownloadUpdate_Click(object? sender, RoutedEventArgs e)
        {
            var info = _update;
            if (info == null) return;
            if (!UpdateService.CanInstall(info))
            {
                OpenInBrowser(info.ReleaseUrl);
                UpdateDialogOverlay.IsVisible = false;
                return;
            }

            UpdateButtons.IsVisible = false;
            UpdateProgressPanel.IsVisible = true;
            TxtUpdateProgress.Text = "Downloading…";
            try
            {
                var progress = new Progress<double>(p =>
                {
                    UpdateProgress.Value = p;
                    TxtUpdateProgress.Text = $"Downloading… {p:P0}";
                });
                string installer = await _vm.DownloadUpdateAsync(info, progress);
                TxtUpdateProgress.Text = "Installing. LocalStream will start again in a moment.";
                _vm.Log($"Installing LocalStream {info.LatestVersion}…");
                UpdateService.RunInstaller(installer);
                await Task.Delay(500);
                _exitRequested = true;
                Close(); // the installer waits for us to close
            }
            catch (Exception ex)
            {
                _vm.Log($"Update failed: {ex.Message}");
                TxtUpdateProgress.Text = $"The update couldn't be installed ({ex.Message}). You can download it from the release page instead.";
                BtnDownloadUpdate.Content = "Open release page";
                _update = info with { InstallerUrl = null };
                UpdateButtons.IsVisible = true;
            }
        }

        private void OpenInBrowser(string url)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true }); }
            catch (Exception ex) { _vm.Log($"Failed to open the browser: {ex.Message}"); }
        }

        // ================================================================ tray + closing

        private NativeMenuItem? _trayNowPlaying, _trayPlayPause, _trayNext, _trayStop, _trayTvStop;
        private TrayIcon? _tray;

        private void CreateTrayIcon()
        {
            try
            {
                var open = new NativeMenuItem("Open Dashboard");
                open.Click += (_, _) => ShowFromTray();
                var exit = new NativeMenuItem("Exit Server");
                exit.Click += (_, _) => { _exitRequested = true; Close(); };

                // Controls for whatever is being cast to this PC (and TVs this app plays on).
                _trayNowPlaying = new NativeMenuItem("") { IsEnabled = false, IsVisible = false };
                _trayPlayPause = new NativeMenuItem("Pause") { IsVisible = false };
                _trayPlayPause.Click += async (_, _) => await _vm.Cast.PlayPauseAsync();
                _trayNext = new NativeMenuItem("Next") { IsVisible = false };
                _trayNext.Click += async (_, _) => await _vm.Cast.NextAsync();
                _trayStop = new NativeMenuItem("Stop") { IsVisible = false };
                _trayStop.Click += async (_, _) => await _vm.Cast.StopAsync();
                _trayTvStop = new NativeMenuItem("Stop playing on TVs") { IsVisible = false };
                _trayTvStop.Click += async (_, _) => { foreach (var row in _vm.Tv.Sessions.ToList()) await _vm.Tv.ControlAsync(row.DeviceId, "stop"); };

                _tray = new TrayIcon
                {
                    Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://LocalStreamPC/Assets/app-icon.ico"))),
                    ToolTipText = "LocalStream Server",
                    IsVisible = true,
                    Menu = new NativeMenu { _trayNowPlaying, _trayPlayPause, _trayNext, _trayStop, _trayTvStop, new NativeMenuItemSeparator(), open, exit },
                };
                _tray.Clicked += (_, _) => ShowFromTray();
                TrayIcon.SetIcons(Application.Current!, new TrayIcons { _tray });

                _vm.Cast.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(CastViewModel.HasMedia) or nameof(CastViewModel.IsPlaying) or nameof(CastViewModel.Title) or nameof(CastViewModel.CanNext))
                        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateTrayMenu);
                };
                _vm.Tv.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TvViewModel.HasSessions)) Avalonia.Threading.Dispatcher.UIThread.Post(UpdateTrayMenu); };
            }
            catch (Exception ex)
            {
                _vm.Log("Error creating tray icon: " + ex.Message);
            }
        }

        private void UpdateTrayMenu()
        {
            if (_trayNowPlaying == null) return;
            var c = _vm.Cast;
            bool media = c.HasMedia;
            string title = c.Title.Length > 48 ? c.Title[..47] + "…" : c.Title;
            _trayNowPlaying.Header = media ? (c.IsPlaying ? "▶ " : "❚❚ ") + title : "";
            _trayNowPlaying.IsVisible = media;
            _trayPlayPause!.Header = c.IsPlaying ? "Pause" : "Play";
            _trayPlayPause.IsVisible = media;
            _trayNext!.IsVisible = media && c.CanNext;
            _trayStop!.IsVisible = media;
            _trayTvStop!.IsVisible = _vm.Tv.HasSessions;
            if (_tray != null) _tray.ToolTipText = media ? $"LocalStream · {(c.IsPlaying ? "playing" : "paused")}: {title}" : "LocalStream Server";
        }

        private void ShowFromTray()
        {
            ShowInTaskbar = true;
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private bool _shutdownDone;

        private async void Window_Closing(object? sender, WindowClosingEventArgs e)
        {
            if (!_exitRequested && _vm.Config.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            if (_shutdownDone) return;

            // Stop the server cleanly (sends SSDP byebye, closes connections), then close for real.
            e.Cancel = true;
            await _vm.ShutdownAsync();
            _shutdownDone = true;
            _exitRequested = true;
            Close();
        }
    }
}
