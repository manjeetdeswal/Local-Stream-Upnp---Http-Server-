using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using LocalStreamPC.Models;
using LocalStreamPC.Server;
using LocalStreamPC.Services;

namespace LocalStreamPC.ViewModels
{
    public sealed class StreamRow
    {
        public required string Key { get; init; }
        public required string Title { get; init; }
        public required string Detail { get; init; }
        public required bool Converting { get; init; }
        public string Badge => Converting ? "Converting" : "Direct";
        public string BadgeColor => Converting ? "#F59E0B" : "#10B981";
    }

    /// <summary>The dashboard's "Streaming now" card: who is playing what, how fast.</summary>
    public sealed class StreamsViewModel : ObservableObject
    {
        private readonly StreamTracker _tracker;
        private readonly DispatcherTimer _timer;
        private string _lastShown = "";

        public StreamsViewModel(StreamTracker tracker)
        {
            _tracker = tracker;
            _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Refresh());
            _timer.Start();
        }

        public ObservableCollection<StreamRow> Streams { get; } = new();

        private bool _hasStreams;
        public bool HasStreams { get => _hasStreams; private set { if (SetField(ref _hasStreams, value)) OnPropertyChanged(nameof(EmptyText)); } }
        public string EmptyText => "Nothing is streaming right now.";

        private string _header = "Streaming now";
        public string Header { get => _header; private set => SetField(ref _header, value); }

        /// <summary>Cuts a stream off (that device can't restart it for two minutes).</summary>
        public void Stop(StreamRow row)
        {
            if (_tracker.Stop(row.Key)) { _lastShown = ""; Refresh(); }
        }

        private void Refresh()
        {
            var list = _tracker.Snapshot();
            var rows = list.Select(s =>
            {
                string who = string.IsNullOrEmpty(s.Who) ? s.Device : $"{s.Who} · {s.Device}";
                string speed = s.BytesPerSecond > 0 ? $"{MediaTypes.FormatSize((long)s.BytesPerSecond)}/s" : (s.Active ? "buffering" : "idle");
                var elapsed = DateTime.UtcNow - s.Started;
                string time = elapsed.TotalMinutes < 1 ? "just started" : elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours} h {elapsed.Minutes} min" : $"{(int)elapsed.TotalMinutes} min";
                return new StreamRow { Key = s.Key, Title = s.FileName, Detail = $"{who} · {speed} · {time} · {MediaTypes.FormatSize(s.Bytes)} sent", Converting = s.Converting };
            }).ToList();

            // Only touch the UI when something visible changed.
            string signature = string.Join("\n", rows.Select(r => r.Key + r.Title + r.Detail + r.Converting));
            if (signature == _lastShown) return;
            _lastShown = signature;
            Streams.Clear();
            foreach (var r in rows) Streams.Add(r);
            HasStreams = rows.Count > 0;
            Header = rows.Count switch { 0 => "Streaming now", 1 => "Streaming now · 1 stream", _ => $"Streaming now · {rows.Count} streams" };
        }
    }

    public sealed class TvSessionRow : ObservableObject
    {
        public required string DeviceId { get; init; }
        public required string DeviceName { get; init; }
        public required string Title { get; init; }
        public required string StateText { get; init; }
        public required bool IsPlaying { get; init; }
        public required double Position { get; init; }
        public required double Duration { get; init; }
        public required int Volume { get; init; }
        public required bool CanSeek { get; init; }
        public string PlayPauseIcon => IsPlaying ? CastViewModel.PauseIcon : CastViewModel.PlayIcon;
        public string TimeText => Duration > 0 ? $"{Format(Position)} / {Format(Duration)}" : Format(Position);
        public bool HasVolume => Volume >= 0;
        private static string Format(double s) { var t = TimeSpan.FromSeconds(Math.Max(0, s)); return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss"); }
    }

    /// <summary>"Playing on TV": media this app sent to TVs (from the browser or the dashboard), with a small remote.</summary>
    public sealed class TvViewModel : ObservableObject
    {
        private readonly TvCastService _tv;
        private readonly AppLog _log;
        private bool _queued;

        public TvViewModel(TvCastService tv, AppLog log)
        {
            _tv = tv;
            _log = log;
            _tv.Changed += () =>
            {
                if (_queued) return;
                _queued = true;
                Dispatcher.UIThread.Post(() => { _queued = false; Refresh(); }, DispatcherPriority.Background);
            };
        }

        public ObservableCollection<TvSessionRow> Sessions { get; } = new();
        public ObservableCollection<RendererDevice> Devices { get; } = new();

        private bool _hasSessions;
        public bool HasSessions { get => _hasSessions; private set => SetField(ref _hasSessions, value); }

        private bool _isScanning;
        public bool IsScanning { get => _isScanning; private set { if (SetField(ref _isScanning, value)) OnPropertyChanged(nameof(PickerStatus)); } }

        private bool _isPickerOpen;
        public bool IsPickerOpen { get => _isPickerOpen; private set => SetField(ref _isPickerOpen, value); }

        private string _pickerTitle = "";
        public string PickerTitle { get => _pickerTitle; private set => SetField(ref _pickerTitle, value); }

        public string PickerStatus => IsScanning ? "Looking for TVs and speakers…" : Devices.Count == 0 ? "No TVs found. Make sure the TV is on and on the same network, then search again." : "";

        private RemoteItem? _pending;

        private void Refresh()
        {
            Sessions.Clear();
            foreach (var s in _tv.Statuses)
            {
                Sessions.Add(new TvSessionRow
                {
                    DeviceId = s.DeviceId,
                    DeviceName = s.DeviceName,
                    Title = s.Title,
                    StateText = s.State switch
                    {
                        "PLAYING" => "Playing", "PAUSED_PLAYBACK" => "Paused", "TRANSITIONING" => "Loading…",
                        "STOPPED" => "Stopped", "NO_MEDIA_PRESENT" => "Stopped", _ => s.State,
                    } + (s.Converted ? " · converting" : "") + (s.StartedBy != null ? $" · by {s.StartedBy}" : ""),
                    IsPlaying = s.State is "PLAYING" or "TRANSITIONING",
                    Position = s.Position,
                    Duration = s.Duration,
                    Volume = s.Volume,
                    CanSeek = s.Duration > 0 && !s.IsImage,
                });
            }
            HasSessions = Sessions.Count > 0;
        }

        public async Task ControlAsync(string deviceId, string action, double value = 0)
        {
            try { await _tv.ControlAsync(deviceId, action, value); }
            catch (Exception ex) { _log.Write($"TV: {ex.Message}"); }
            Refresh();
        }

        // ------------------------------------------------------------ "Play on TV…" for items in the Servers browser

        public async Task OpenPickerAsync(RemoteItem item)
        {
            _pending = item;
            PickerTitle = $"Play \"{item.Title}\" on…";
            IsPickerOpen = true;
            await ScanAsync(force: false);
        }

        public async Task ScanAsync(bool force)
        {
            if (IsScanning) return;
            IsScanning = true;
            try
            {
                var found = await _tv.Discovery.ScanAsync(force);
                Devices.Clear();
                foreach (var d in found) Devices.Add(d);
            }
            catch (Exception ex) { _log.Write($"TV search failed: {ex.Message}"); }
            finally { IsScanning = false; OnPropertyChanged(nameof(PickerStatus)); }
        }

        public void ClosePicker()
        {
            IsPickerOpen = false;
            _pending = null;
        }

        public async Task PlayOnAsync(RendererDevice device)
        {
            var item = _pending;
            ClosePicker();
            if (item == null || string.IsNullOrEmpty(item.Url)) return;
            string ext = System.IO.Path.GetExtension(new Uri(item.Url).AbsolutePath);
            string? name = Cast.CastMediaInfo.FileNameFromUrl(item.Url);
            if (name != null) ext = System.IO.Path.GetExtension(name);
            string mime = MediaTypes.GetMimeType(ext);
            if (mime == "application/octet-stream") mime = item.TypeIcon == Icons.Audio ? "audio/mpeg" : item.TypeIcon == Icons.Image ? "image/jpeg" : "video/mp4";
            string upnpClass = mime.StartsWith("audio") ? "object.item.audioItem.musicTrack" : mime.StartsWith("image") ? "object.item.imageItem.photo" : "object.item.videoItem";
            try
            {
                await _tv.PlayAsync(device, new TvMedia(item.Url, item.Title, mime, upnpClass, item.Size, 0, item.ThumbnailUrl, null, false), "this PC");
            }
            catch (Exception ex)
            {
                _log.Write($"Play on {device.Name} failed: {ex.Message}");
            }
            Refresh();
        }
    }

    public sealed class ShareRow
    {
        public required string Token { get; init; }
        public required string Name { get; init; }
        public required string Detail { get; init; }
        public required string Url { get; init; }
    }

    /// <summary>The dashboard's "Share links" card: links made from the web page, with Stop buttons.</summary>
    public sealed class SharesViewModel : ObservableObject
    {
        private readonly ConfigService _config;
        private readonly Func<int> _port;
        private readonly DispatcherTimer _timer;
        private string _signature = "";

        public SharesViewModel(ConfigService config, Func<int> port)
        {
            _config = config;
            _port = port;
            _timer = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Background, (_, _) => Refresh());
            _timer.Start();
            Refresh();
        }

        public ObservableCollection<ShareRow> Shares { get; } = new();

        private bool _hasShares;
        public bool HasShares { get => _hasShares; private set => SetField(ref _hasShares, value); }

        public void Refresh()
        {
            var list = _config.ActiveShares;
            string sig = string.Join("|", list.Select(s => s.Token + s.Opens + s.Expires.Ticks));
            if (sig == _signature) return;
            _signature = sig;
            Shares.Clear();
            string host = NetworkHelper.GetPrimaryIPv4();
            foreach (var s in list.OrderBy(s => s.Expires))
            {
                var left = s.Expires - DateTime.UtcNow;
                string expires = left.TotalHours < 1 ? $"{Math.Max(1, (int)left.TotalMinutes)} min left" : left.TotalDays < 2 ? $"{(int)left.TotalHours} h left" : $"{(int)left.TotalDays} days left";
                Shares.Add(new ShareRow
                {
                    Token = s.Token,
                    Name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(s.Path)) is { Length: > 0 } n ? n : s.Path,
                    Detail = $"{(s.IsFolder ? "Folder" : "File")} · by {s.CreatedBy} · {expires} · opened {s.Opens} time{(s.Opens == 1 ? "" : "s")}",
                    Url = $"http://{host}:{_port()}/s/{s.Token}",
                });
            }
            HasShares = Shares.Count > 0;
        }

        public void Revoke(ShareRow row)
        {
            _config.RemoveShare(row.Token);
            Refresh();
        }
    }

    public sealed class HistoryRow
    {
        public required string When { get; init; }
        public required string Day { get; init; }
        public required string Who { get; init; }
        public required string Action { get; init; }
        public required string Item { get; init; }
        public required string Detail { get; init; }
        public required string Icon { get; init; }
        public required string Color { get; init; }
        public bool HasDetail => Detail.Length > 0;
        public string Line2 => Detail.Length > 0 ? $"{Who}  ·  {Detail}" : Who;
    }

    /// <summary>The History page: who watched, uploaded, deleted or shared what, and when.</summary>
    public sealed class HistoryViewModel : ObservableObject
    {
        private readonly ActivityLog _log;
        private bool _queued;

        public HistoryViewModel(ActivityLog log)
        {
            _log = log;
            _log.Changed += () =>
            {
                if (_queued || !IsActive) return;
                _queued = true;
                Dispatcher.UIThread.Post(() => { _queued = false; Refresh(); }, DispatcherPriority.Background);
            };
        }

        public ObservableCollection<HistoryRow> Rows { get; } = new();

        /// <summary>Only refreshed while the page is shown.</summary>
        public bool IsActive { get; set; }

        private string _filter = "";
        public string Filter { get => _filter; set { if (SetField(ref _filter, value)) Refresh(); } }

        private bool _isEmpty = true;
        public bool IsEmpty { get => _isEmpty; private set => SetField(ref _isEmpty, value); }

        private string _summary = "";
        public string Summary { get => _summary; private set => SetField(ref _summary, value); }

        public void Refresh()
        {
            var all = _log.Recent(3000);
            string f = Filter.Trim();
            var shown = all.Where(e => f.Length == 0 || e.Who.Contains(f, StringComparison.CurrentCultureIgnoreCase) || e.Item.Contains(f, StringComparison.CurrentCultureIgnoreCase)
                                       || e.Action.Contains(f, StringComparison.CurrentCultureIgnoreCase) || e.Device.Contains(f, StringComparison.CurrentCultureIgnoreCase))
                           .OrderByDescending(e => e.Time).Take(500).ToList();
            Rows.Clear();
            var today = DateTime.Now.Date;
            foreach (var e in shown)
            {
                var local = e.Time.ToLocalTime();
                string day = local.Date == today ? "Today" : local.Date == today.AddDays(-1) ? "Yesterday" : local.ToString("dddd d MMMM yyyy");
                var (icon, color) = IconFor(e.Action);
                Rows.Add(new HistoryRow
                {
                    When = local.ToString("t"), Day = day, Who = string.IsNullOrEmpty(e.Who) ? e.Device : $"{e.Who} · {e.Device}",
                    Action = e.Action, Item = e.Item, Detail = e.Detail ?? "", Icon = icon, Color = color,
                });
            }
            IsEmpty = Rows.Count == 0;
            int watched = all.Count(e => e.Action is "Watched" or "Listened to" && e.Time > DateTime.UtcNow.AddDays(-7));
            Summary = all.Count == 0 ? "Nothing has happened yet." : $"{all.Count:N0} events kept · {watched} watched or listened to this week";
        }

        private static (string Icon, string Color) IconFor(string action) => action switch
        {
            "Watched" or "Viewed" => ("M5 3l14 9-14 9V3z", "#3B82F6"),
            "Listened to" => ("M9 18V5l12-2v13 M6 21a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M18 19a3 3 0 1 0 0-6 3 3 0 0 0 0 6z", "#8B5CF6"),
            "Uploaded" or "Added subtitles" => ("M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4 M17 8l-5-5-5 5 M12 3v12", "#10B981"),
            "Downloaded" or "Downloaded zip" => ("M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4 M7 10l5 5 5-5 M12 15V3", "#0EA5E9"),
            "Deleted" => ("M3 6h18 M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6 M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2", "#EF4444"),
            "Signed in" or "Signed out" => ("M15 3h4a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2h-4 M10 17l5-5-5-5 M15 12H3", "#64748B"),
            "Created share link" or "Opened share link" => ("M18 8a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M6 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M18 22a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M8.6 13.5l6.8 4 M15.4 6.5l-6.8 4", "#F59E0B"),
            _ when action.StartsWith("Played on") => ("M2 7a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2z M8 22h8 M12 19v3", "#F59E0B"),
            _ => ("M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20z M12 6v6l4 2", "#64748B"),
        };

        public void Clear()
        {
            _log.Clear();
            Refresh();
        }
    }

    public sealed class LibraryRootRow
    {
        public required string Path { get; init; }
        public required string Detail { get; init; }
        public required bool Ok { get; init; }
        public string DetailColor => Ok ? "#94A3B8" : "#EF4444";
    }

    /// <summary>The dashboard's "Library" card: what was found in each shared folder, rescan, folders left out.</summary>
    public sealed class LibraryViewModel : ObservableObject
    {
        private readonly LibraryIndex _library;
        private readonly ConfigService _config;
        private readonly AppLog _log;
        private bool _queued;

        public LibraryViewModel(LibraryIndex library, ConfigService config, AppLog log)
        {
            _library = library;
            _config = config;
            _log = log;
            _library.StatsChanged += () =>
            {
                if (_queued) return;
                _queued = true;
                Dispatcher.UIThread.Post(() => { _queued = false; Refresh(); }, DispatcherPriority.Background);
            };
            Refresh();
        }

        public ObservableCollection<LibraryRootRow> Roots { get; } = new();
        public ObservableCollection<string> Excluded { get; } = new();

        private string _summary = "";
        public string Summary { get => _summary; private set => SetField(ref _summary, value); }

        private bool _isScanning;
        public bool IsScanning { get => _isScanning; private set { if (SetField(ref _isScanning, value)) OnPropertyChanged(nameof(RescanText)); } }
        public string RescanText => IsScanning ? "Reading…" : "Rescan now";

        public bool SkipBuildFolders
        {
            get => _config.Current.SkipBuildFolders;
            set
            {
                if (_config.Current.SkipBuildFolders == value) return;
                _config.Current.SkipBuildFolders = value;
                _config.Save();
                OnPropertyChanged();
                _ = _library.RescanAsync();
            }
        }

        public bool FindCoversOnline
        {
            get => _config.Current.FindCoversOnline;
            set
            {
                if (_config.Current.FindCoversOnline == value) return;
                _config.Current.FindCoversOnline = value;
                _config.Save();
                OnPropertyChanged();
            }
        }

        public bool HasExcluded => Excluded.Count > 0;

        public void Refresh()
        {
            var stats = _library.Stats;
            Roots.Clear();
            foreach (var st in stats)
            {
                string detail = st.Scanning && st.Scanned == DateTime.MinValue ? "Reading…"
                    : !st.Reachable ? (st.Problem?.Replace("Library: ", "") ?? "Can't be opened.")
                    : $"{Count(st.Songs, "song")} · {Count(st.Videos, "video")} · {Count(st.Photos, "photo")}" + (st.Scanning ? " · reading again…" : "");
                Roots.Add(new LibraryRootRow { Path = st.Root, Detail = detail, Ok = st.Reachable });
            }
            int songs = stats.Sum(x => x.Songs), videos = stats.Sum(x => x.Videos), photos = stats.Sum(x => x.Photos);
            Summary = stats.Count == 0 ? "Add a shared folder to build your library." : $"{Count(songs, "song")}, {Count(videos, "video")} and {Count(photos, "photo")} in {Count(stats.Count, "folder")}";
            IsScanning = _library.IsScanning;
            Excluded.Clear();
            foreach (var e in _config.ExcludedFolders) Excluded.Add(e);
            OnPropertyChanged(nameof(HasExcluded));
        }

        private static string Count(int n, string word) => $"{n:N0} {word}{(n == 1 ? "" : "s")}";

        public void Rescan()
        {
            IsScanning = true;
            _ = _library.RescanAsync();
        }

        /// <returns>An error to show, or null.</returns>
        public string? Exclude(string folder)
        {
            folder = System.IO.Path.TrimEndingDirectorySeparator(folder.Trim());
            if (!_config.SharedRoots.Any(r => folder.StartsWith(System.IO.Path.TrimEndingDirectorySeparator(r) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                return "Pick a folder inside one of your shared folders (to stop sharing a whole folder, remove it from the list above).";
            if (!_config.Current.ExcludedFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) _config.Current.ExcludedFolders.Add(folder);
            _config.Save();
            _log.Write($"Library: leaving out {folder}.");
            Refresh();
            _ = _library.RescanAsync();
            return null;
        }

        public void Include(string folder)
        {
            _config.Current.ExcludedFolders.RemoveAll(f => System.IO.Path.TrimEndingDirectorySeparator(f).Equals(folder, StringComparison.OrdinalIgnoreCase));
            _config.Save();
            Refresh();
            _ = _library.RescanAsync();
        }
    }
}
