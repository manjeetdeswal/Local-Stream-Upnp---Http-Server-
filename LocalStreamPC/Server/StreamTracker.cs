using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace LocalStreamPC.Server
{
    /// <summary>One person/device playing one file (many range requests are folded into one entry).</summary>
    public sealed record StreamInfo(string Key, string Who, string Device, string FileName, bool Converting,
                                    DateTime Started, long Bytes, double BytesPerSecond, bool Active);

    /// <summary>
    /// Counts what is being streamed right now, for the dashboard's "Streaming now" card and to
    /// keep the PC awake. Players make lots of short range requests, so a stream counts as
    /// running until nothing was sent for <see cref="IdleAfter"/>.
    /// </summary>
    public sealed class StreamTracker
    {
        private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(25);
        private static readonly TimeSpan BlockAfterStop = TimeSpan.FromMinutes(2);
        private readonly object _lock = new();
        private readonly Dictionary<string, Entry> _entries = new();
        private readonly Dictionary<string, DateTime> _blocked = new(); // "ip|file" stopped from the dashboard
        private readonly Func<int> _speedLimitMbps;
        private readonly Timer _pruneTimer;

        public event Action? Changed;

        /// <summary>A stream ended (nothing sent for a while): for the history.</summary>
        public event Action<StreamInfo>? Finished;

        /// <param name="speedLimitMbps">Per-stream limit in Mbit/s, 0 for none (read for every chunk, so changes apply at once).</param>
        public StreamTracker(Func<int>? speedLimitMbps = null)
        {
            _speedLimitMbps = speedLimitMbps ?? (() => 0);
            _pruneTimer = new Timer(_ => { lock (_lock) Prune(); }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        private sealed class Entry
        {
            public required string Key, Who, Device, FileName, Ip;
            public bool Converting;
            public DateTime Started, LastActivity;
            public long Bytes;
            public int OpenRequests;
            public readonly Queue<(DateTime Time, long Bytes)> Samples = new();
            public readonly HashSet<HttpContext> Requests = new();
            // Speed limit (shared by all requests of this stream)
            public DateTime BudgetStart = DateTime.UtcNow;
            public long BudgetBytes;
        }

        /// <summary>True when this client's stream of this file was stopped from the dashboard a moment ago.</summary>
        public bool IsBlocked(HttpContext ctx, string fileName)
        {
            string key = ClientIp(ctx) + "|" + fileName;
            lock (_lock)
            {
                if (!_blocked.TryGetValue(key, out var until)) return false;
                if (until > DateTime.UtcNow) return true;
                _blocked.Remove(key);
                return false;
            }
        }

        /// <summary>Ends a stream now (the player gets disconnected and can't reconnect for 2 minutes).</summary>
        public bool Stop(string key)
        {
            List<HttpContext> requests;
            lock (_lock)
            {
                if (!_entries.TryGetValue(key, out var e)) return false;
                requests = e.Requests.ToList();
                _blocked[e.Ip + "|" + e.FileName] = DateTime.UtcNow + BlockAfterStop;
                e.OpenRequests = 0;
                e.LastActivity = DateTime.MinValue;
            }
            foreach (var r in requests) { try { r.Abort(); } catch { } }
            lock (_lock) Prune();
            Changed?.Invoke();
            return true;
        }

        private static string ClientIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.IsIPv4MappedToIPv6 == true
            ? ctx.Connection.RemoteIpAddress.MapToIPv4().ToString()
            : ctx.Connection.RemoteIpAddress?.ToString() ?? "?";

        /// <summary>True while anything is being streamed (open request or data sent recently).</summary>
        public bool IsBusy
        {
            get { lock (_lock) { Prune(); return _entries.Count > 0; } }
        }

        public IReadOnlyList<StreamInfo> Snapshot()
        {
            lock (_lock)
            {
                Prune();
                var now = DateTime.UtcNow;
                return _entries.Values.OrderBy(e => e.Started).Select(e =>
                {
                    while (e.Samples.Count > 0 && now - e.Samples.Peek().Time > TimeSpan.FromSeconds(6)) e.Samples.Dequeue();
                    double rate = e.Samples.Count == 0 ? 0 : e.Samples.Sum(s => s.Bytes) / 6.0;
                    return new StreamInfo(e.Key, e.Who, e.Device, e.FileName, e.Converting, e.Started, e.Bytes, rate, e.OpenRequests > 0);
                }).ToList();
            }
        }

        /// <summary>Wraps the response so every byte sent is counted. Dispose the result when the request ends.</summary>
        public IDisposable Begin(HttpContext ctx, string fileName, bool converting, string? userName)
        {
            string ip = ClientIp(ctx);
            string device = DescribeDevice(ctx.Request.Headers.UserAgent.ToString(), ip);
            string key = $"{ip}|{fileName}|{converting}";

            Entry entry;
            lock (_lock)
            {
                if (!_entries.TryGetValue(key, out entry!))
                {
                    entry = new Entry { Key = key, Who = userName ?? "", Device = device, FileName = fileName, Ip = ip, Converting = converting, Started = DateTime.UtcNow };
                    _entries[key] = entry;
                }
                if (userName != null) entry.Who = userName;
                entry.OpenRequests++;
                entry.LastActivity = DateTime.UtcNow;
                entry.Requests.Add(ctx);
            }
            Changed?.Invoke();

            var counting = new CountingStream(ctx.Response.Body, n =>
            {
                TimeSpan wait = TimeSpan.Zero;
                lock (_lock)
                {
                    entry.Bytes += n;
                    entry.LastActivity = DateTime.UtcNow;
                    entry.Samples.Enqueue((entry.LastActivity, n));
                    if (entry.Samples.Count > 4000) entry.Samples.Dequeue();

                    // Speed limit: if more was sent than the limit allows since the budget window began, wait.
                    int mbps = _speedLimitMbps();
                    if (mbps > 0)
                    {
                        var now = DateTime.UtcNow;
                        if (now - entry.BudgetStart > TimeSpan.FromSeconds(10)) { entry.BudgetStart = now; entry.BudgetBytes = 0; }
                        entry.BudgetBytes += n;
                        double allowedSeconds = entry.BudgetBytes / (mbps * 125_000.0);
                        wait = entry.BudgetStart + TimeSpan.FromSeconds(allowedSeconds) - now;
                    }
                }
                return wait;
            });
            ctx.Response.Body = counting;
            return new Ender(() =>
            {
                lock (_lock) { entry.OpenRequests--; entry.LastActivity = DateTime.UtcNow; entry.Requests.Remove(ctx); }
                Changed?.Invoke();
            });
        }

        private void Prune()
        {
            var now = DateTime.UtcNow;
            foreach (var e in _entries.Values.Where(e => e.OpenRequests <= 0 && now - e.LastActivity > IdleAfter).ToList())
            {
                _entries.Remove(e.Key);
                var info = new StreamInfo(e.Key, e.Who, e.Device, e.FileName, e.Converting, e.Started, e.Bytes, 0, false);
                ThreadPool.QueueUserWorkItem(_ => Finished?.Invoke(info));
            }
        }

        /// <summary>A readable name for the client from its User-Agent ("Chrome on Android", "Samsung TV"...).</summary>
        public static string DescribeDevice(string ua, string ip)
        {
            string u = ua.ToLowerInvariant();
            string? what =
                u.Contains("tizen") || u.Contains("samsung") ? "Samsung TV" :
                u.Contains("webos") || u.Contains("lg") && u.Contains("tv") ? "LG TV" :
                u.Contains("bravia") || u.Contains("sony") ? "Sony TV" :
                u.Contains("roku") ? "Roku" :
                u.Contains("aft") && u.Contains("amazon") ? "Fire TV" :
                u.Contains("xbox") ? "Xbox" :
                u.Contains("playstation") ? "PlayStation" :
                u.Contains("vlc") || u.Contains("libvlc") ? "VLC" :
                u.Contains("kodi") ? "Kodi" :
                u.Contains("bubbleupnp") ? "BubbleUPnP" :
                u.Contains("localstream") ? "LocalStream app" :
                u.Contains("dlna") || u.Contains("upnp") ? "Media player" :
                null;
            if (what == null && u.Length > 0)
            {
                string browser = u.Contains("edg/") ? "Edge" : u.Contains("firefox") ? "Firefox" : u.Contains("chrome") ? "Chrome" : u.Contains("safari") ? "Safari" : "Browser";
                string os = u.Contains("android") ? "Android" : u.Contains("iphone") || u.Contains("ipad") ? "iOS" : u.Contains("windows") ? "Windows" : u.Contains("mac os") ? "Mac" : u.Contains("linux") ? "Linux" : "";
                what = os.Length > 0 ? $"{browser} on {os}" : browser;
            }
            return $"{what ?? "Device"} ({ip})";
        }

        private sealed class Ender : IDisposable
        {
            private Action? _onEnd;
            public Ender(Action onEnd) => _onEnd = onEnd;
            public void Dispose() => Interlocked.Exchange(ref _onEnd, null)?.Invoke();
        }

        /// <summary>Response body wrapper that reports how many bytes were written.</summary>
        private sealed class CountingStream : Stream
        {
            private readonly Stream _inner;
            private readonly Func<long, TimeSpan> _onWrite; // returns how long to wait (speed limit)
            public CountingStream(Stream inner, Func<long, TimeSpan> onWrite) { _inner = inner; _onWrite = onWrite; }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => _inner.Flush();
            public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count)
            {
                _inner.Write(buffer, offset, count);
                var wait = _onWrite(count);
                if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            }
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            {
                // Large writes are split so the speed limit stays smooth.
                const int Slice = 64 * 1024;
                for (int i = 0; i < buffer.Length; i += Slice)
                {
                    var part = buffer.Slice(i, Math.Min(Slice, buffer.Length - i));
                    await _inner.WriteAsync(part, ct);
                    var wait = _onWrite(part.Length);
                    if (wait > TimeSpan.Zero) await Task.Delay(wait > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : wait, ct);
                }
            }
        }
    }
}
