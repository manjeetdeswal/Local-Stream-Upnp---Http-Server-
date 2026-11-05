using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace LocalStreamPC.Services
{
    /// <summary>A TV, speaker or PC on the network that media can be sent to (a DLNA/UPnP MediaRenderer).</summary>
    public sealed record RendererDevice(string Id, string Name, string Manufacturer, string Model, string Ip,
                                        string AvTransportUrl, string? RenderingControlUrl, bool IsThisPc,
                                        bool IsCast = false, int CastPort = 8009)
    {
        /// <summary>"Chromecast" or "DLNA", shown next to the name.</summary>
        public string Kind => IsCast ? "Chromecast" : "DLNA";
        public string Description => string.Join(" · ", new[] { Manufacturer, Model }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
    }

    /// <summary>What to send to a TV.</summary>
    public sealed record TvMedia(string Url, string Title, string Mime, string UpnpClass, long Size,
                                 double Duration, string? ThumbnailUrl, string? SubtitleUrl, bool Converted);

    public sealed record TvStatus(string DeviceId, string DeviceName, string Title, string State,
                                  double Position, double Duration, int Volume, bool Converted, string? StartedBy, bool IsImage);

    /// <summary>Finds MediaRenderers with SSDP (M-SEARCH on every network adapter).</summary>
    public sealed class RendererDiscovery
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };
        private readonly ConcurrentDictionary<string, (RendererDevice Device, DateTime Seen)> _devices = new();
        private readonly Func<string> _ownRendererUuid;
        private readonly SemaphoreSlim _scanGate = new(1, 1);
        private DateTime _lastScan = DateTime.MinValue;

        public RendererDiscovery(Func<string> ownRendererUuid) => _ownRendererUuid = ownRendererUuid;

        public IReadOnlyList<RendererDevice> Known =>
            _devices.Values.Where(d => DateTime.UtcNow - d.Seen < TimeSpan.FromMinutes(3)) // switched-off TVs drop out
                    .Select(d => d.Device).OrderBy(d => d.IsThisPc).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        public RendererDevice? Find(string id) => _devices.TryGetValue(id, out var d) ? d.Device : null;

        /// <summary>Searches the network (about 3 seconds) unless a search ran very recently.</summary>
        public async Task<IReadOnlyList<RendererDevice>> ScanAsync(bool force = false, CancellationToken ct = default)
        {
            await _scanGate.WaitAsync(ct);
            try
            {
                if (!force && DateTime.UtcNow - _lastScan < TimeSpan.FromSeconds(20) && !_devices.IsEmpty) return Known;
                var found = new ConcurrentDictionary<string, byte>();
                var fetches = new ConcurrentBag<Task>();
                const string st = "urn:schemas-upnp-org:device:MediaRenderer:1";
                byte[] request = Encoding.ASCII.GetBytes($"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: {st}\r\n\r\n");
                var multicast = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);

                var sockets = new List<UdpClient>();
                foreach (var (ip, _) in NetworkHelper.GetActiveIPv4Addresses())
                {
                    try { sockets.Add(new UdpClient(new IPEndPoint(IPAddress.Parse(ip), 0)) { Ttl = 4 }); } catch { }
                }
                try { sockets.Add(new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))); } catch { } // finds this PC's own receiver

                using var listen = CancellationTokenSource.CreateLinkedTokenSource(ct);
                listen.CancelAfter(TimeSpan.FromSeconds(3.5));
                // Chromecasts and Google TVs announce themselves with mDNS instead.
                var casts = CastDiscovery.ScanAsync(TimeSpan.FromSeconds(3), ct);
                var listeners = sockets.Select(s => Task.Run(async () =>
                {
                    try
                    {
                        while (!listen.IsCancellationRequested)
                        {
                            var packet = await s.ReceiveAsync(listen.Token);
                            string msg = Encoding.UTF8.GetString(packet.Buffer);
                            string? location = Header(msg, "LOCATION");
                            if (location == null || !found.TryAdd(location, 0)) continue;
                            fetches.Add(FetchAsync(location, packet.RemoteEndPoint.Address));
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (SocketException) { }
                    catch (ObjectDisposedException) { }
                })).ToList();

                try
                {
                    for (int i = 0; i < 3; i++)
                    {
                        foreach (var s in sockets)
                        {
                            try
                            {
                                var target = ((IPEndPoint)s.Client.LocalEndPoint!).Address.Equals(IPAddress.Loopback) ? new IPEndPoint(IPAddress.Loopback, 1900) : multicast;
                                await s.SendAsync(request, request.Length, target);
                            }
                            catch (SocketException) { }
                        }
                        await Task.Delay(500, ct);
                    }
                    await Task.WhenAll(listeners);
                    await Task.WhenAll(fetches.ToArray());
                    try
                    {
                        foreach (var cast in await casts)
                        {
                            // A Google TV can also answer DLNA; the Cast entry plays far more formats.
                            _devices[cast.Id] = (cast, DateTime.UtcNow);
                        }
                    }
                    catch { }
                }
                finally
                {
                    foreach (var s in sockets) s.Dispose();
                }
                _lastScan = DateTime.UtcNow;
                return Known;
            }
            finally { _scanGate.Release(); }
        }

        private async Task FetchAsync(string location, IPAddress from)
        {
            try
            {
                var doc = XDocument.Parse(await Http.GetStringAsync(location));
                XNamespace ns = doc.Root!.GetDefaultNamespace();
                var device = doc.Descendants(ns + "device").FirstOrDefault(d => d.Element(ns + "deviceType")?.Value.Contains("MediaRenderer") == true)
                             ?? doc.Descendants(ns + "device").FirstOrDefault();
                if (device == null) return;

                string? Service(string type) => device.Descendants(ns + "service")
                    .FirstOrDefault(s => s.Element(ns + "serviceType")?.Value.Contains(type) == true)?.Element(ns + "controlURL")?.Value;
                string? avt = Service(":AVTransport:");
                if (string.IsNullOrEmpty(avt)) return; // can't play anything

                var baseUri = Uri.TryCreate(doc.Root.Element(ns + "URLBase")?.Value, UriKind.Absolute, out var b) ? b : new Uri(location);
                string udn = device.Element(ns + "UDN")?.Value.Trim() ?? location;
                string name = device.Element(ns + "friendlyName")?.Value.Trim() ?? "TV";
                bool isThisPc = udn.Contains(_ownRendererUuid(), StringComparison.OrdinalIgnoreCase);
                string ip = from.IsIPv4MappedToIPv6 ? from.MapToIPv4().ToString() : from.ToString();
                // A device seen on the network and on this PC's loopback is the same one: keep the network address.
                if (IPAddress.IsLoopback(from) && _devices.TryGetValue(udn, out var seen) && !IPAddress.IsLoopback(IPAddress.Parse(seen.Device.Ip))) ip = seen.Device.Ip;
                string? rcs = Service(":RenderingControl:");

                var renderer = new RendererDevice(udn, isThisPc ? name + " (this PC)" : name,
                    device.Element(ns + "manufacturer")?.Value.Trim() ?? "", device.Element(ns + "modelName")?.Value.Trim() ?? "",
                    ip, new Uri(baseUri, avt).AbsoluteUri, rcs == null ? null : new Uri(baseUri, rcs).AbsoluteUri, isThisPc);
                _devices[udn] = (renderer, DateTime.UtcNow);
            }
            catch
            {
                // Not answering or not UPnP: skip.
            }
        }

        private static string? Header(string msg, string name)
            => msg.Split('\n').FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..].Trim();
    }

    /// <summary>SOAP calls to one MediaRenderer's AVTransport and RenderingControl services.</summary>
    public static class RendererClient
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
        private const string Avt = "urn:schemas-upnp-org:service:AVTransport:1";
        private const string Rcs = "urn:schemas-upnp-org:service:RenderingControl:1";

        public static Task SetUriAsync(RendererDevice d, string url, string metadata, CancellationToken ct = default)
            => CallAsync(d.AvTransportUrl, Avt, "SetAVTransportURI", ct, ("InstanceID", "0"), ("CurrentURI", url), ("CurrentURIMetaData", metadata));
        public static Task PlayAsync(RendererDevice d, CancellationToken ct = default) => CallAsync(d.AvTransportUrl, Avt, "Play", ct, ("InstanceID", "0"), ("Speed", "1"));
        public static Task PauseAsync(RendererDevice d, CancellationToken ct = default) => CallAsync(d.AvTransportUrl, Avt, "Pause", ct, ("InstanceID", "0"));
        public static Task StopAsync(RendererDevice d, CancellationToken ct = default) => CallAsync(d.AvTransportUrl, Avt, "Stop", ct, ("InstanceID", "0"));
        public static Task SeekAsync(RendererDevice d, TimeSpan t, CancellationToken ct = default)
            => CallAsync(d.AvTransportUrl, Avt, "Seek", ct, ("InstanceID", "0"), ("Unit", "REL_TIME"), ("Target", FormatTime(t)));

        public static async Task<(string State, double Position, double Duration)> GetStatusAsync(RendererDevice d, CancellationToken ct = default)
        {
            var info = await CallAsync(d.AvTransportUrl, Avt, "GetTransportInfo", ct, ("InstanceID", "0"));
            var pos = await CallAsync(d.AvTransportUrl, Avt, "GetPositionInfo", ct, ("InstanceID", "0"));
            return (info.GetValueOrDefault("CurrentTransportState") ?? "UNKNOWN",
                    ParseTime(pos.GetValueOrDefault("RelTime")), ParseTime(pos.GetValueOrDefault("TrackDuration")));
        }

        public static async Task<int?> GetVolumeAsync(RendererDevice d, CancellationToken ct = default)
        {
            if (d.RenderingControlUrl == null) return null;
            var r = await CallAsync(d.RenderingControlUrl, Rcs, "GetVolume", ct, ("InstanceID", "0"), ("Channel", "Master"));
            return int.TryParse(r.GetValueOrDefault("CurrentVolume"), out int v) ? v : null;
        }

        public static Task SetVolumeAsync(RendererDevice d, int volume, CancellationToken ct = default)
            => d.RenderingControlUrl == null ? Task.CompletedTask
             : CallAsync(d.RenderingControlUrl, Rcs, "SetVolume", ct, ("InstanceID", "0"), ("Channel", "Master"), ("DesiredVolume", Math.Clamp(volume, 0, 100).ToString()));

        /// <summary>DIDL-Lite metadata most TVs want before they'll play a URL (title, type, subtitles).</summary>
        public static string BuildMetadata(TvMedia m)
        {
            XNamespace didl = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
            XNamespace dc = "http://purl.org/dc/elements/1.1/";
            XNamespace upnp = "urn:schemas-upnp-org:metadata-1-0/upnp/";
            XNamespace dlna = "urn:schemas-dlna-org:metadata-1-0/";
            XNamespace sec = "http://www.sec.co.kr/";
            string flags = m.Converted
                ? "DLNA.ORG_OP=00;DLNA.ORG_CI=1;DLNA.ORG_FLAGS=01700000000000000000000000000000"
                : "DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000";
            var res = new XElement(didl + "res", new XAttribute("protocolInfo", $"http-get:*:{m.Mime}:{flags}"), m.Url);
            if (m.Size > 0 && !m.Converted) res.Add(new XAttribute("size", m.Size));
            if (m.Duration > 0) res.Add(new XAttribute("duration", FormatTime(TimeSpan.FromSeconds(m.Duration)) + ".000"));

            var item = new XElement(didl + "item", new XAttribute("id", "1"), new XAttribute("parentID", "0"), new XAttribute("restricted", "1"),
                new XElement(dc + "title", m.Title),
                new XElement(upnp + "class", m.UpnpClass));
            if (m.ThumbnailUrl != null) item.Add(new XElement(upnp + "albumArtURI", m.ThumbnailUrl));
            item.Add(res);
            if (m.SubtitleUrl != null)
            {
                item.Add(new XElement(didl + "res", new XAttribute("protocolInfo", "http-get:*:text/srt:*"), m.SubtitleUrl));
                item.Add(new XElement(sec + "CaptionInfoEx", new XAttribute(sec + "type", "srt"), m.SubtitleUrl)); // Samsung
            }
            var root = new XElement(didl + "DIDL-Lite",
                new XAttribute(XNamespace.Xmlns + "dc", dc), new XAttribute(XNamespace.Xmlns + "upnp", upnp),
                new XAttribute(XNamespace.Xmlns + "dlna", dlna), new XAttribute(XNamespace.Xmlns + "sec", sec), item);
            return root.ToString(SaveOptions.DisableFormatting);
        }

        private static async Task<Dictionary<string, string>> CallAsync(string url, string service, string action, CancellationToken ct, params (string Name, string Value)[] args)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>");
            sb.Append($"<u:{action} xmlns:u=\"{service}\">");
            foreach (var (n, v) in args) sb.Append($"<{n}>{SecurityElement.Escape(v)}</{n}>");
            sb.Append($"</u:{action}></s:Body></s:Envelope>");

            using var content = new StringContent(sb.ToString(), Encoding.UTF8, "text/xml");
            content.Headers.ContentType!.CharSet = "utf-8";
            content.Headers.Add("SOAPAction", $"\"{service}#{action}\"");
            using var response = await Http.PostAsync(url, content, ct);
            string body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                string? code = null, desc = null;
                try
                {
                    var x = XDocument.Parse(body);
                    code = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorCode")?.Value;
                    desc = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorDescription")?.Value;
                }
                catch { }
                throw new InvalidOperationException($"{action} was refused by the device ({code ?? ((int)response.StatusCode).ToString()}{(desc != null ? ": " + desc : "")}).");
            }
            var result = new Dictionary<string, string>();
            try
            {
                var x = XDocument.Parse(body);
                var resp = x.Descendants().FirstOrDefault(e => e.Name.LocalName == action + "Response");
                if (resp != null) foreach (var e in resp.Elements()) result[e.Name.LocalName] = e.Value;
            }
            catch { }
            return result;
        }

        public static string FormatTime(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";

        public static double ParseTime(string? s)
        {
            if (string.IsNullOrWhiteSpace(s) || s.StartsWith("NOT_IMPLEMENTED")) return 0;
            var parts = s.Trim().Split(':');
            try
            {
                double total = 0;
                foreach (var p in parts) total = total * 60 + double.Parse(p, CultureInfo.InvariantCulture);
                return total;
            }
            catch { return 0; }
        }
    }

    /// <summary>
    /// "Play on TV": sends media to renderers and keeps track of what each one is playing, for the
    /// web remote and the dashboard. Converted streams can't be seeked by the TV itself, so seeking
    /// one restarts the conversion at the new position.
    /// </summary>
    public sealed class TvCastService : IDisposable
    {
        public sealed class Session
        {
            public required RendererDevice Device { get; init; }
            public required TvMedia Media { get; set; }
            public Func<double, TvMedia>? Reseek { get; set; } // for converted streams: media starting at a position
            public double Offset { get; set; }                 // where the current converted stream starts
            public string? StartedBy { get; init; }
            public string State { get; set; } = "TRANSITIONING";
            public double Position { get; set; }
            public double Duration { get; set; }
            public int Volume { get; set; } = -1;
            public DateTime Updated { get; set; } = DateTime.UtcNow;
            public DateTime StoppedSince { get; set; } = DateTime.MaxValue;
            public int Failures { get; set; }
            public CastClient? Cast { get; set; }              // Chromecast connection (null for DLNA)
        }

        private readonly AppLog _log;
        private readonly ConcurrentDictionary<string, Session> _sessions = new();
        private readonly Timer _poll;
        private int _polling;

        public RendererDiscovery Discovery { get; }

        public event Action? Changed;

        public TvCastService(AppLog log, RendererDiscovery discovery)
        {
            _log = log;
            Discovery = discovery;
            _poll = new Timer(_ => _ = PollAsync(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }

        public bool IsPlayingSomething => _sessions.Values.Any(s => s.State is "PLAYING" or "TRANSITIONING");

        public IReadOnlyList<TvStatus> Statuses => _sessions.Values.Select(ToStatus).ToList();

        public TvStatus? StatusOf(string deviceId) => _sessions.TryGetValue(deviceId, out var s) ? ToStatus(s) : null;

        private static TvStatus ToStatus(Session s) => new(s.Device.Id, s.Device.Name, s.Media.Title, s.State,
            s.Offset + s.Position, s.Duration > 0 ? s.Duration : s.Media.Duration, s.Volume, s.Media.Converted, s.StartedBy,
            s.Media.UpnpClass.Contains("image"));

        public async Task PlayAsync(RendererDevice device, TvMedia media, string? startedBy, double startAt = 0, Func<double, TvMedia>? reseek = null)
        {
            // A converted stream can't seek: start the conversion at the resume point instead.
            if (device.IsCast) { await PlayOnCastAsync(device, media, startedBy, startAt); return; }
            double offset = 0;
            if (media.Converted && startAt > 1 && reseek != null) { media = reseek(startAt); offset = startAt; startAt = 0; }

            try { await RendererClient.StopAsync(device); } catch { } // many TVs refuse a new URI while playing
            await RendererClient.SetUriAsync(device, media.Url, RendererClient.BuildMetadata(media));
            await RendererClient.PlayAsync(device);
            var session = new Session { Device = device, Media = media, StartedBy = startedBy, Reseek = reseek, Offset = offset };
            _sessions[device.Id] = session;
            _log.Write($"Playing \"{media.Title}\" on {device.Name}{(media.Converted ? " (converting)" : "")}.");
            Changed?.Invoke();

            if (startAt > 1) _ = SeekWhenPlayingAsync(session, startAt);
        }

        private async Task PlayOnCastAsync(RendererDevice device, TvMedia media, string? startedBy, double startAt)
        {
            if (_sessions.TryRemove(device.Id, out var old) && old.Cast != null) await old.Cast.DisposeAsync();
            var cast = new CastClient();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await cast.ConnectAsync(device.Ip, device.CastPort, cts.Token);
                string kind = media.UpnpClass.Contains("video") ? "video" : media.UpnpClass.Contains("audio") ? "audio" : media.UpnpClass.Contains("image") ? "image" : "video";
                await cast.LoadAsync(media.Url, media.Mime, media.Title, media.ThumbnailUrl, media.SubtitleUrl, startAt, kind, cts.Token);
            }
            catch
            {
                await cast.DisposeAsync();
                throw;
            }
            _sessions[device.Id] = new Session { Device = device, Media = media, StartedBy = startedBy, Cast = cast, State = "TRANSITIONING" };
            _log.Write($"Casting \"{media.Title}\" to {device.Name}{(media.Converted ? " (converting)" : "")}.");
            Changed?.Invoke();
        }

        private static string CastState(string? state) => state switch
        {
            "PLAYING" => "PLAYING",
            "PAUSED" => "PAUSED_PLAYBACK",
            "BUFFERING" => "TRANSITIONING",
            "IDLE" => "STOPPED",
            _ => "TRANSITIONING",
        };

        private async Task CastControlAsync(Session s, string action, double value)
        {
            var c = s.Cast!;
            switch (action)
            {
                case "play": await c.PlayAsync(); s.State = "PLAYING"; break;
                case "pause": await c.PauseAsync(); s.State = "PAUSED_PLAYBACK"; break;
                case "stop":
                    _sessions.TryRemove(s.Device.Id, out _);
                    await c.QuitAsync();
                    await c.DisposeAsync();
                    break;
                case "seek": await c.SeekAsync(Math.Max(0, value)); s.Position = value; break;
                case "volume": await c.SetVolumeAsync((int)value); s.Volume = (int)value; break;
                default: throw new ArgumentException("Unknown action");
            }
            Changed?.Invoke();
        }

        /// <summary>Stops every TV session (tray menu, app exit).</summary>
        public async Task StopAllAsync()
        {
            foreach (var id in _sessions.Keys.ToList())
                try { await ControlAsync(id, "stop"); } catch { }
        }

        private async Task SeekWhenPlayingAsync(Session session, double seconds)
        {
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(500);
                try
                {
                    var (state, _, _) = await RendererClient.GetStatusAsync(session.Device);
                    if (state == "PLAYING") { await RendererClient.SeekAsync(session.Device, TimeSpan.FromSeconds(seconds)); return; }
                }
                catch { }
            }
        }

        public async Task ControlAsync(string deviceId, string action, double value = 0)
        {
            if (!_sessions.TryGetValue(deviceId, out var s)) throw new InvalidOperationException("Nothing is playing on that device.");
            if (s.Cast != null) { await CastControlAsync(s, action, value); return; }
            var d = s.Device;
            switch (action)
            {
                case "play": await RendererClient.PlayAsync(d); s.State = "PLAYING"; break;
                case "pause": await RendererClient.PauseAsync(d); s.State = "PAUSED_PLAYBACK"; break;
                case "stop":
                    try { await RendererClient.StopAsync(d); } catch { }
                    _sessions.TryRemove(deviceId, out _);
                    break;
                case "seek":
                    if (s.Media.Converted && s.Reseek != null)
                    {
                        var media = s.Reseek(value);
                        try { await RendererClient.StopAsync(d); } catch { }
                        await RendererClient.SetUriAsync(d, media.Url, RendererClient.BuildMetadata(media));
                        await RendererClient.PlayAsync(d);
                        s.Media = media;
                        s.Offset = value;
                        s.Position = 0;
                    }
                    else
                    {
                        await RendererClient.SeekAsync(d, TimeSpan.FromSeconds(Math.Max(0, value)));
                        s.Position = value;
                    }
                    break;
                case "volume":
                    await RendererClient.SetVolumeAsync(d, (int)value);
                    s.Volume = (int)value;
                    break;
                default: throw new ArgumentException("Unknown action");
            }
            Changed?.Invoke();
        }

        private async Task PollAsync()
        {
            if (_sessions.IsEmpty || Interlocked.Exchange(ref _polling, 1) == 1) return;
            try
            {
                foreach (var s in _sessions.Values.ToList())
                {
                    if (s.Cast != null) { await PollCastAsync(s); continue; }
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                        var (state, pos, dur) = await RendererClient.GetStatusAsync(s.Device, cts.Token);
                        s.State = state;
                        s.Position = pos;
                        if (dur > 0) s.Duration = s.Media.Converted ? Math.Max(s.Media.Duration, dur) : dur;
                        if (s.Volume < 0 || DateTime.UtcNow.Second % 10 == 0) s.Volume = await RendererClient.GetVolumeAsync(s.Device, cts.Token) ?? s.Volume;
                        s.Failures = 0;
                        s.Updated = DateTime.UtcNow;
                        if (state is "STOPPED" or "NO_MEDIA_PRESENT")
                        {
                            if (s.StoppedSince == DateTime.MaxValue) s.StoppedSince = DateTime.UtcNow;
                            else if (DateTime.UtcNow - s.StoppedSince > TimeSpan.FromSeconds(30)) _sessions.TryRemove(s.Device.Id, out _);
                        }
                        else s.StoppedSince = DateTime.MaxValue;
                    }
                    catch
                    {
                        if (++s.Failures >= 10) _sessions.TryRemove(s.Device.Id, out _); // TV switched off
                    }
                }
                Changed?.Invoke();
            }
            finally { Volatile.Write(ref _polling, 0); }
        }

        private async Task PollCastAsync(Session s)
        {
            var c = s.Cast!;
            try
            {
                if (!c.IsConnected) throw new IOException("closed");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                await c.RefreshAsync(cts.Token);
                s.State = CastState(c.PlayerState);
                // Between status messages the position moves on by itself while playing.
                s.Position = c.CurrentTime + (c.PlayerState == "PLAYING" ? (DateTime.UtcNow - c.StatusTime).TotalSeconds : 0);
                if (c.Duration > 0) s.Duration = c.Duration;
                if (c.Volume >= 0) s.Volume = (int)c.Volume;
                s.Failures = 0;
                s.Updated = DateTime.UtcNow;
                if (s.State == "STOPPED")
                {
                    if (s.StoppedSince == DateTime.MaxValue) s.StoppedSince = DateTime.UtcNow;
                    else if (DateTime.UtcNow - s.StoppedSince > TimeSpan.FromSeconds(30)) await DropAsync(s);
                }
                else s.StoppedSince = DateTime.MaxValue;
            }
            catch
            {
                if (++s.Failures >= 5 || !c.IsConnected) await DropAsync(s);
            }
        }

        private async Task DropAsync(Session s)
        {
            _sessions.TryRemove(s.Device.Id, out _);
            if (s.Cast != null) await s.Cast.DisposeAsync();
        }

        /// <summary>The address of this PC that the device can reach (the adapter the OS would use to talk to it).</summary>
        public static string LocalAddressFor(string deviceIp)
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect(IPAddress.Parse(deviceIp), 9); // no packet is sent for UDP connect
                return ((IPEndPoint)socket.LocalEndPoint!).Address.ToString();
            }
            catch { return NetworkHelper.GetPrimaryIPv4(); }
        }

        public void Dispose()
        {
            _poll.Dispose();
            foreach (var s in _sessions.Values) if (s.Cast != null) _ = s.Cast.DisposeAsync().AsTask();
        }
    }
}
