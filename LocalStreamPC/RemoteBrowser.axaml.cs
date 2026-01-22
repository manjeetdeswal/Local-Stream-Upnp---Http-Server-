using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LocalStreamPC
{
    public partial class RemoteBrowser : Window
    {
        private string _controlUrl;
        private string _baseUrl;
        private Stack<string> _history = new Stack<string>();
        private string _currentContainer = "0";
        private bool _isGridView = false;

        public ObservableCollection<RemoteItem> Items { get; set; } = new ObservableCollection<RemoteItem>();

        public RemoteBrowser(string controlUrl, string baseUrl, string serverName)
        {
            InitializeComponent();
            _controlUrl = controlUrl;
            _baseUrl = baseUrl;

            var txtName = this.FindControl<TextBlock>("TxtServerName");
            if (txtName != null) txtName.Text = serverName;

            var listList = this.FindControl<ListBox>("ListRemoteFiles_List");
            var listGrid = this.FindControl<ListBox>("ListRemoteFiles_Grid");

            if (listList != null) listList.ItemsSource = Items;
            if (listGrid != null) listGrid.ItemsSource = Items;

            Loaded += (s, e) => LoadFolder("0");
        }

        private async void LoadFolder(string containerId)
        {
            var loading = this.FindControl<ProgressBar>("LoadingBar");
            var btnBack = this.FindControl<Button>("BtnBack");

            if (loading != null) loading.IsVisible = true;
            this.Title = "⏳ Requesting...";
            Items.Clear();

            try
            {
                string fullUrl = _baseUrl.TrimEnd('/') + "/" + _controlUrl.TrimStart('/');

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

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("LocalStream/1.0");
                    client.Timeout = TimeSpan.FromSeconds(10);
                    var content = new StringContent(soap, Encoding.UTF8, "text/xml");
                    content.Headers.Add("SOAPAction", "\"urn:schemas-upnp-org:service:ContentDirectory:1#Browse\"");

                    var response = await client.PostAsync(fullUrl, content);
                    var responseString = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        try
                        {
                            var soapDoc = XDocument.Parse(responseString);
                            var resultNode = soapDoc.Descendants().FirstOrDefault(n => n.Name.LocalName == "Result");
                            if (resultNode != null) ParseDidl(resultNode.Value);
                        }
                        catch (Exception ex) { this.Title = $"❌ XML Error: {ex.Message}"; }
                    }
                }
                _currentContainer = containerId;
                if (btnBack != null) btnBack.IsEnabled = _history.Count > 0;
            }
            catch (Exception ex) { this.Title = $"❌ Network Error: {ex.Message}"; }
            finally { if (loading != null) loading.IsVisible = false; }
        }

        private void ParseDidl(string xml)
        {
            try
            {
                var doc = XDocument.Parse(xml);
                var containers = doc.Descendants().Where(e => e.Name.LocalName == "container").ToList();
                var items = doc.Descendants().Where(e => e.Name.LocalName == "item").ToList();

                Dispatcher.UIThread.Invoke(() =>
                {
                    this.Title = $"📂 {containers.Count} Folders, {items.Count} Files";

                    foreach (var c in containers)
                    {
                        try
                        {
                            string title = c.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value ?? "Folder";
                            string countStr = c.Attribute("childCount")?.Value;
                            string details = string.IsNullOrEmpty(countStr) ? "Folder" : $"Folder ({countStr} items)";

                            Items.Add(new RemoteItem { Id = c.Attribute("id")?.Value, Title = title, IsFolder = true, TypeIcon = "📁", Details = details });
                        }
                        catch { }
                    }

                    foreach (var i in items)
                    {
                        try
                        {
                            string title = i.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value ?? "File";
                            var res = i.Elements().FirstOrDefault(e => e.Name.LocalName == "res");
                            var upnpClass = i.Elements().FirstOrDefault(e => e.Name.LocalName == "class")?.Value ?? "";
                            string thumbUrl = i.Elements().FirstOrDefault(e => e.Name.LocalName == "albumArtURI")?.Value;

                            string icon = "📄";
                            if (upnpClass.Contains("video")) icon = "🎬";
                            else if (upnpClass.Contains("audio") || upnpClass.Contains("music")) icon = "🎵";
                            else if (upnpClass.Contains("image")) icon = "🖼️";

                            var newItem = new RemoteItem
                            {
                                Id = i.Attribute("id")?.Value,
                                Title = title,
                                IsFolder = false,
                                Url = res?.Value,
                                TypeIcon = icon,
                                Details = "Media File",
                                ThumbnailUrl = thumbUrl
                            };
                            if (!string.IsNullOrEmpty(thumbUrl)) _ = newItem.LoadThumbnailAsync();
                            Items.Add(newItem);
                        }
                        catch { }
                    }
                });
            }
            catch { }
        }

        // ==========================================
        //  RECURSIVE PLAYLIST LOGIC (Shared)
        // ==========================================

        // CHANGE: Returns List<RemoteItem> instead of List<string> so we keep the Titles
        private async Task<List<RemoteItem>> FetchRecursiveUrlsAsync(string containerId)
        {
            var itemsList = new List<RemoteItem>();
            try
            {
                string fullUrl = _baseUrl.TrimEnd('/') + "/" + _controlUrl.TrimStart('/');
                string soap = $@"<?xml version=""1.0""?>
<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"" s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
  <s:Body>
    <u:Browse xmlns:u=""urn:schemas-upnp-org:service:ContentDirectory:1"">
      <ObjectID>{containerId}</ObjectID>
      <BrowseFlag>BrowseRecursive</BrowseFlag> <Filter>*</Filter>
      <StartingIndex>0</StartingIndex>
      <RequestedCount>0</RequestedCount>
      <SortCriteria></SortCriteria>
    </u:Browse>
  </s:Body>
</s:Envelope>";

                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
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

                                if (res != null && !string.IsNullOrEmpty(res.Value))
                                {
                                    itemsList.Add(new RemoteItem
                                    {
                                        Url = res.Value,
                                        Title = title
                                    });
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                this.Title = $"❌ Fetch Error: {ex.Message}";
            }
            return itemsList;
        }

        private async void BtnPlayAll_Click(object sender, RoutedEventArgs e)
        {
            var loading = this.FindControl<ProgressBar>("LoadingBar");
            if (loading != null) loading.IsVisible = true;
            this.Title = "⏳ Fetching all files...";

            // Now returns RemoteItem objects with titles
            var items = await FetchRecursiveUrlsAsync(_currentContainer);

            if (items.Count > 0)
            {
                this.Title = $"▶ Playing {items.Count} files...";
                PlayUrlsInVlc(items);
            }
            else
            {
                this.Title = "⚠️ No files found.";
            }

            if (loading != null) loading.IsVisible = false;
        }

        private async void CtxPlaySelected_Click(object sender, RoutedEventArgs e)
        {
            var list = _isGridView
                ? this.FindControl<ListBox>("ListRemoteFiles_Grid")
                : this.FindControl<ListBox>("ListRemoteFiles_List");

            if (list?.SelectedItems == null || list.SelectedItems.Count == 0) return;

            var loading = this.FindControl<ProgressBar>("LoadingBar");
            if (loading != null) loading.IsVisible = true;
            this.Title = "⏳ Gathering selection...";

            var finalItems = new List<RemoteItem>();

            foreach (RemoteItem item in list.SelectedItems)
            {
                if (item.IsFolder)
                {
                    var folderItems = await FetchRecursiveUrlsAsync(item.Id);
                    finalItems.AddRange(folderItems);
                }
                else if (!string.IsNullOrEmpty(item.Url))
                {
                    finalItems.Add(item);
                }
            }

            if (finalItems.Count > 0)
            {
                this.Title = $"▶ Playing {finalItems.Count} items...";
                PlayUrlsInVlc(finalItems);
            }
            else
            {
                this.Title = "⚠️ No playable files in selection.";
            }

            if (loading != null) loading.IsVisible = false;
        }

        // Change the method signature to accept an optional 'title'
        // CHANGE: Now accepts List<RemoteItem> to access titles
        private void PlayUrlsInVlc(List<RemoteItem> items)
        {
            if (items == null || items.Count == 0) return;

            // 1. Generate M3U Playlist Content
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("#EXTM3U");

            foreach (var item in items)
            {
                // #EXTINF:-1,Movie Title
                // http://url...
                sb.AppendLine($"#EXTINF:-1,{item.Title}");
                sb.AppendLine(item.Url);
            }

            // 2. Save to Temp File
            string tempPlaylist = Path.Combine(Path.GetTempPath(), $"LocalStream_{DateTime.Now.Ticks}.m3u");
            File.WriteAllText(tempPlaylist, sb.ToString());

            // 3. Find VLC
            string vlcPath = "vlc";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string[] paths = {
            @"C:\Program Files\VideoLAN\VLC\vlc.exe",
            @"C:\Program Files (x86)\VideoLAN\VLC\vlc.exe"
        };
                foreach (var p in paths) if (File.Exists(p)) { vlcPath = p; break; }

                // Registry check (optional but good)
                if (vlcPath == "vlc")
                {
                    try
                    {
                        using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\VideoLAN\VLC"))
                            if (key?.GetValue("InstallDir") is string dir)
                                if (File.Exists(Path.Combine(dir, "vlc.exe"))) vlcPath = Path.Combine(dir, "vlc.exe");
                    }
                    catch { }
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                vlcPath = "/Applications/VLC.app/Contents/MacOS/VLC";
            }

            // 4. Launch VLC with the Playlist
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = vlcPath,
                    Arguments = $"\"{tempPlaylist}\"", // Pass the M3U file path
                    UseShellExecute = false
                });
            }
            catch (Exception ex)
            {
                this.Title = $"❌ VLC Error: {ex.Message}";
            }
        }

        // ==========================================

        private void BtnToggleView_Click(object sender, RoutedEventArgs e)
        {
            _isGridView = !_isGridView;
            var listList = this.FindControl<ListBox>("ListRemoteFiles_List");
            var listGrid = this.FindControl<ListBox>("ListRemoteFiles_Grid");
            if (listList != null) listList.IsVisible = !_isGridView;
            if (listGrid != null) listGrid.IsVisible = _isGridView;
        }

        private void ListRemoteFiles_DoubleTapped(object sender, Avalonia.Input.TappedEventArgs e)
        {
            var list = sender as ListBox;
            if (list != null && list.SelectedItem is RemoteItem item)
            {
                if (item.IsFolder)
                {
                    _history.Push(_currentContainer);
                    LoadFolder(item.Id);
                }
                else
                {
                    // Create a single-item list
                    PlayUrlsInVlc(new List<RemoteItem> { item });
                }
            }
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            if (_history.Count > 0) LoadFolder(_history.Pop());
        }
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

        private Bitmap? _thumbnailBitmap;
        public Bitmap? ThumbnailBitmap
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
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(5); // Don't wait too long
                    var response = await client.GetAsync(ThumbnailUrl);

                    if (response.IsSuccessStatusCode)
                    {
                        var data = await response.Content.ReadAsByteArrayAsync();
                        using (var stream = new MemoryStream(data))
                        {
                            var bitmap = new Bitmap(stream);
                            Dispatcher.UIThread.Invoke(() => ThumbnailBitmap = bitmap);
                        }
                    }
                }
            }
            catch
            {
                // If it fails, we just keep the default icon. No crash.
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}