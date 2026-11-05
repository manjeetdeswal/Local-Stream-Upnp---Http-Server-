using System;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace LocalStreamPC.Models
{
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }

    public class UpnpDevice
    {
        public string FriendlyName { get; set; } = "";
        public string IpAddress { get; set; } = "";
        public string ServiceType { get; set; } = "";
        public string ContentDirectoryUrl { get; set; } = "";
        public string BaseUrl { get; set; } = "";
        /// <summary>This PC's own LocalStream server.</summary>
        public bool IsThisPc { get; set; }
    }

    /// <summary>A row in Settings → "Sidebar order".</summary>
    public class TabOrderRow
    {
        public string Tag { get; set; } = "";
        public string Name { get; set; } = "";
        public string Position { get; set; } = "";
        public bool CanMoveUp { get; set; }
        public bool CanMoveDown { get; set; }
    }

    public class ServerUrlItem
    {
        public string NetworkName { get; set; } = "";
        public string Url { get; set; } = "";
        public Bitmap? QrBitmap { get; set; }
    }

    public class BreadcrumbItem : ObservableObject
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public bool IsNotFirst { get; set; }
        public string FontWeight => IsLast ? "Bold" : "Normal";

        private bool _isLast;
        public bool IsLast
        {
            get => _isLast;
            set { if (SetField(ref _isLast, value)) OnPropertyChanged(nameof(FontWeight)); }
        }
    }

    public class RemoteItem : ObservableObject
    {
        private static readonly HttpClient ThumbClient = new() { Timeout = TimeSpan.FromSeconds(15) };
        // A folder can have hundreds of items: fetch a few covers at a time instead of all at once.
        private static readonly System.Threading.SemaphoreSlim ThumbSlots = new(6);

        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public bool IsFolder { get; set; }
        public string Url { get; set; } = "";
        /// <summary>SVG path data shown when there is no thumbnail (see <see cref="Icons"/>).</summary>
        public string TypeIcon { get; set; } = Icons.File;
        public string Details { get; set; } = "";
        public string? ThumbnailUrl { get; set; }
        public long Size { get; set; }

        private double _watchProgress;
        public double WatchProgress
        {
            get => _watchProgress;
            set { if (SetField(ref _watchProgress, value)) OnPropertyChanged(nameof(HasProgress)); }
        }
        public bool HasProgress => _watchProgress > 0;

        private Bitmap? _thumbnailBitmap;
        public Bitmap? ThumbnailBitmap
        {
            get => _thumbnailBitmap;
            set
            {
                if (!SetField(ref _thumbnailBitmap, value)) return;
                OnPropertyChanged(nameof(IsIconVisible));
                OnPropertyChanged(nameof(IsImageVisible));
            }
        }
        public bool IsIconVisible => _thumbnailBitmap == null;
        public bool IsImageVisible => _thumbnailBitmap != null;

        public async Task LoadThumbnailAsync()
        {
            if (string.IsNullOrEmpty(ThumbnailUrl) || ThumbnailBitmap != null) return;
            await ThumbSlots.WaitAsync();
            try
            {
                using var response = await ThumbClient.GetAsync(ThumbnailUrl);
                if (!response.IsSuccessStatusCode) return;
                var data = await response.Content.ReadAsByteArrayAsync();

                // Decode on a background thread; only hand the finished bitmap to the UI thread.
                var bitmap = await Task.Run(() =>
                {
                    using var stream = new MemoryStream(data);
                    return Bitmap.DecodeToWidth(stream, 200);
                });
                await Dispatcher.UIThread.InvokeAsync(() => ThumbnailBitmap = bitmap);
            }
            catch
            {
                // Broken or unreachable image: keep the default icon.
            }
            finally
            {
                ThumbSlots.Release();
            }
        }
    }

    public static class Icons
    {
        public const string Folder = "M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z";
        public const string Video = "M23 7l-7 5 7 5V7z M1 5h14v14H1z";
        public const string Audio = "M9 18V5l12-2v13 M6 18a3 3 0 1 1-6 0 3 3 0 0 1 6 0zm12-2a3 3 0 1 1-6 0 3 3 0 0 1 6 0z";
        public const string Image = "M21 19V5a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2z M8.5 8.5a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3z M21 15l-5-5L5 21";
        public const string File = "M13 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9z M13 2v7h7";
    }
}
