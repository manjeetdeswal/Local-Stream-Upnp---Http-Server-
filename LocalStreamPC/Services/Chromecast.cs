using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace LocalStreamPC.Services
{
    /// <summary>
    /// Finds Chromecasts and Google TVs with mDNS (_googlecast._tcp), like phones do.
    /// </summary>
    public static class CastDiscovery
    {
        private static readonly IPEndPoint MdnsGroup = new(IPAddress.Parse("224.0.0.251"), 5353);

        public static async Task<List<RendererDevice>> ScanAsync(TimeSpan time, CancellationToken ct)
        {
            var found = new Dictionary<string, RendererDevice>();
            byte[] query = BuildQuery("_googlecast._tcp.local");
            var sockets = new List<UdpClient>();
            var ips = NetworkHelper.GetActiveIPv4Addresses();
            foreach (var (ip, _) in ips)
            {
                try { sockets.Add(new UdpClient(new IPEndPoint(IPAddress.Parse(ip), 0))); } catch { }
            }
            // Some devices answer to the mDNS group even when asked for a direct reply: listen there too
            // (shared with the system's own mDNS service, so this may not be possible everywhere).
            UdpClient? group = null;
            try
            {
                group = new UdpClient { ExclusiveAddressUse = false };
                group.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                group.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
                foreach (var (ip, _) in ips) { try { group.JoinMulticastGroup(MdnsGroup.Address, IPAddress.Parse(ip)); } catch { } }
            }
            catch { group?.Dispose(); group = null; }
            var receivers = group == null ? sockets : sockets.Append(group).ToList();
            using var listen = CancellationTokenSource.CreateLinkedTokenSource(ct);
            listen.CancelAfter(time);
            var listeners = receivers.Select(s => Task.Run(async () =>
            {
                try
                {
                    while (!listen.IsCancellationRequested)
                    {
                        var packet = await s.ReceiveAsync(listen.Token);
                        foreach (var d in Parse(packet.Buffer, packet.RemoteEndPoint.Address))
                            lock (found) found[d.Id] = d;
                    }
                }
                catch (OperationCanceledException) { }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            })).ToList();
            try
            {
                for (int i = 0; i < 3 && !listen.IsCancellationRequested; i++)
                {
                    foreach (var s in sockets) { try { await s.SendAsync(query, query.Length, MdnsGroup); } catch (SocketException) { } }
                    await Task.Delay(600, ct);
                }
                await Task.WhenAll(listeners);
            }
            catch (OperationCanceledException) { }
            finally { foreach (var s in sockets) s.Dispose(); group?.Dispose(); }
            lock (found) return found.Values.ToList();
        }

        /// <summary>A DNS question for PTR records, asking for unicast replies (so we get them on our own port).</summary>
        private static byte[] BuildQuery(string name)
        {
            var ms = new MemoryStream();
            ms.Write(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }); // id, flags, 1 question
            foreach (var label in name.Split('.'))
            {
                var b = Encoding.ASCII.GetBytes(label);
                ms.WriteByte((byte)b.Length);
                ms.Write(b);
            }
            ms.WriteByte(0);
            ms.Write(new byte[] { 0, 12, 0x80, 1 }); // PTR, IN with "unicast response" bit
            return ms.ToArray();
        }

        /// <summary>Reads the PTR/SRV/TXT/A records of a reply into devices.</summary>
        internal static IEnumerable<RendererDevice> Parse(byte[] data, IPAddress from)
        {
            var records = new List<(string Name, int Type, byte[] Data, int Offset, int Length)>();
            try
            {
                if (data.Length < 12) yield break;
                int qd = data[4] << 8 | data[5], an = data[6] << 8 | data[7], ns = data[8] << 8 | data[9], ar = data[10] << 8 | data[11];
                int pos = 12;
                for (int i = 0; i < qd; i++) { ReadName(data, ref pos); pos += 4; }
                for (int i = 0; i < an + ns + ar && pos < data.Length; i++)
                {
                    string rname = ReadName(data, ref pos);
                    int type = data[pos] << 8 | data[pos + 1];
                    int len = data[pos + 8] << 8 | data[pos + 9];
                    pos += 10;
                    records.Add((rname, type, data, pos, len));
                    pos += len;
                }
            }
            catch { yield break; }

            var instances = new List<string>();
            foreach (var r in records.Where(r => r.Type == 12 && r.Name.StartsWith("_googlecast._tcp", StringComparison.OrdinalIgnoreCase)))
            {
                int p = r.Offset;
                try { instances.Add(ReadName(r.Data, ref p)); } catch { }
            }
            foreach (var instance in instances.Distinct())
            {
                int port = 8009;
                string? target = null;
                var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in records.Where(r => r.Name.Equals(instance, StringComparison.OrdinalIgnoreCase)))
                {
                    if (r.Type == 33 && r.Length >= 6) // SRV
                    {
                        port = r.Data[r.Offset + 4] << 8 | r.Data[r.Offset + 5];
                        int p = r.Offset + 6;
                        try { target = ReadName(r.Data, ref p); } catch { }
                    }
                    else if (r.Type == 16) // TXT
                    {
                        int p = r.Offset, end = r.Offset + r.Length;
                        while (p < end)
                        {
                            int l = r.Data[p++];
                            string kv = Encoding.UTF8.GetString(r.Data, p, Math.Min(l, end - p));
                            p += l;
                            int eq = kv.IndexOf('=');
                            if (eq > 0) txt[kv[..eq]] = kv[(eq + 1)..];
                        }
                    }
                }
                var a = records.FirstOrDefault(r => r.Type == 1 && r.Length == 4 && target != null && r.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
                string ip = a.Data != null ? new IPAddress(a.Data.AsSpan(a.Offset, 4)).ToString()
                          : from.IsIPv4MappedToIPv6 ? from.MapToIPv4().ToString() : from.ToString();
                string id = "cast:" + (txt.GetValueOrDefault("id") ?? instance);
                string name = txt.GetValueOrDefault("fn") ?? instance.Split('.')[0];
                string model = txt.GetValueOrDefault("md") ?? "Chromecast";
                yield return new RendererDevice(id, name, "Google Cast", model, ip, "", null, false, IsCast: true, CastPort: port);
            }
        }

        private static string ReadName(byte[] data, ref int pos)
        {
            var labels = new List<string>();
            int jumps = 0, p = pos;
            bool jumped = false;
            while (true)
            {
                int len = data[p];
                if (len == 0) { p++; break; }
                if ((len & 0xC0) == 0xC0) // compression pointer
                {
                    int target = (len & 0x3F) << 8 | data[p + 1];
                    if (!jumped) pos = p + 2;
                    jumped = true;
                    p = target;
                    if (++jumps > 20) throw new InvalidDataException("loop");
                    continue;
                }
                labels.Add(Encoding.UTF8.GetString(data, p + 1, len));
                p += len + 1;
            }
            if (!jumped) pos = p;
            return string.Join('.', labels);
        }
    }

    /// <summary>
    /// Talks to one Chromecast: Google's Cast protocol (a TLS connection on port 8009 carrying small
    /// protobuf frames with JSON inside). Launches the built-in "Default Media Receiver" and plays a URL.
    /// </summary>
    public sealed class CastClient : IAsyncDisposable
    {
        private const string DefaultReceiver = "CC1AD845";
        private const string NsConnection = "urn:x-cast:com.google.cast.tp.connection";
        private const string NsHeartbeat = "urn:x-cast:com.google.cast.tp.heartbeat";
        private const string NsReceiver = "urn:x-cast:com.google.cast.receiver";
        private const string NsMedia = "urn:x-cast:com.google.cast.media";
        private const string Sender = "sender-localstream";

        private readonly TcpClient _tcp = new();
        private SslStream? _ssl;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly CancellationTokenSource _cts = new();
        private int _requestId = 1;
        private string? _transportId;
        private readonly Dictionary<int, TaskCompletionSource<JsonObject>> _pending = new();

        public string? PlayerState { get; private set; }
        public double CurrentTime { get; private set; }
        public double Duration { get; private set; }
        public double Volume { get; private set; } = -1;
        public int? MediaSessionId { get; private set; }
        public bool IsConnected => _tcp.Connected && !_cts.IsCancellationRequested;
        public DateTime StatusTime { get; private set; } = DateTime.UtcNow;

        public async Task ConnectAsync(string host, int port, CancellationToken ct)
        {
            await _tcp.ConnectAsync(host, port, ct);
            // Chromecasts use a self-signed device certificate.
            _ssl = new SslStream(_tcp.GetStream(), false, (_, _, _, _) => true);
            await _ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, ct);
            _ = Task.Run(ReadLoopAsync);
            _ = Task.Run(HeartbeatLoopAsync);
            await SendAsync("receiver-0", NsConnection, new JsonObject { ["type"] = "CONNECT" });
        }

        /// <summary>Starts the Default Media Receiver and loads the media.</summary>
        public async Task LoadAsync(string url, string contentType, string title, string? imageUrl, string? subtitleUrl, double startAt, string kind, CancellationToken ct)
        {
            var status = await RequestAsync("receiver-0", NsReceiver, new JsonObject { ["type"] = "LAUNCH", ["appId"] = DefaultReceiver }, ct);
            _transportId = status["status"]?["applications"]?.AsArray()
                .FirstOrDefault(a => a?["appId"]?.GetValue<string>() == DefaultReceiver)?["transportId"]?.GetValue<string>()
                ?? throw new InvalidOperationException("The Chromecast didn't start its media player.");
            await SendAsync(_transportId, NsConnection, new JsonObject { ["type"] = "CONNECT" });

            var metadata = new JsonObject
            {
                ["metadataType"] = kind == "audio" ? 3 : kind == "image" ? 4 : 1, // music track / photo / movie
                ["title"] = title,
            };
            if (imageUrl != null) metadata["images"] = new JsonArray(new JsonObject { ["url"] = imageUrl });
            var media = new JsonObject
            {
                ["contentId"] = url,
                ["contentUrl"] = url,
                ["contentType"] = contentType,
                ["streamType"] = "BUFFERED",
                ["metadata"] = metadata,
            };
            if (contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase))
            {
                // Our HLS pieces are MPEG-TS (the receiver otherwise guesses from the first piece).
                media["hlsSegmentFormat"] = "ts";
                media["hlsVideoSegmentFormat"] = "mpeg2_ts";
            }
            var load = new JsonObject { ["type"] = "LOAD", ["media"] = media, ["autoplay"] = true, ["currentTime"] = startAt };
            if (subtitleUrl != null)
            {
                media["tracks"] = new JsonArray(new JsonObject
                {
                    ["trackId"] = 1, ["type"] = "TEXT", ["trackContentId"] = subtitleUrl, ["trackContentType"] = "text/vtt",
                    ["subtype"] = "SUBTITLES", ["name"] = "Subtitles",
                });
                media["textTrackStyle"] = new JsonObject { ["backgroundColor"] = "#00000099", ["fontScale"] = 1.0 };
                load["activeTrackIds"] = new JsonArray(1);
            }
            var reply = await RequestAsync(_transportId, NsMedia, load, ct);
            if (reply["type"]?.GetValue<string>() is "LOAD_FAILED" or "LOAD_CANCELLED" or "INVALID_REQUEST")
                throw new InvalidOperationException("The Chromecast couldn't play this file" + (kind == "video" ? " (try \"Convert while playing\")." : "."));
        }

        public Task PlayAsync() => MediaCommandAsync("PLAY");
        public Task PauseAsync() => MediaCommandAsync("PAUSE");
        public Task StopAsync() => MediaCommandAsync("STOP");
        public Task SeekAsync(double seconds) => MediaCommandAsync("SEEK", o => o["currentTime"] = seconds);

        public Task SetVolumeAsync(int percent)
            => RequestAsync("receiver-0", NsReceiver, new JsonObject { ["type"] = "SET_VOLUME", ["volume"] = new JsonObject { ["level"] = Math.Clamp(percent, 0, 100) / 100.0 } }, _cts.Token);

        public async Task RefreshAsync(CancellationToken ct)
        {
            if (_transportId == null) return;
            await RequestAsync(_transportId, NsMedia, new JsonObject { ["type"] = "GET_STATUS" }, ct);
            await RequestAsync("receiver-0", NsReceiver, new JsonObject { ["type"] = "GET_STATUS" }, ct);
        }

        /// <summary>Closes the media player on the TV (back to its home screen).</summary>
        public async Task QuitAsync()
        {
            try
            {
                await RequestAsync("receiver-0", NsReceiver, new JsonObject { ["type"] = "STOP" }, new CancellationTokenSource(3000).Token);
            }
            catch { }
        }

        private async Task MediaCommandAsync(string type, Action<JsonObject>? extra = null)
        {
            if (_transportId == null || MediaSessionId == null) throw new InvalidOperationException("Nothing is playing on the Chromecast.");
            var msg = new JsonObject { ["type"] = type, ["mediaSessionId"] = MediaSessionId };
            extra?.Invoke(msg);
            await RequestAsync(_transportId, NsMedia, msg, _cts.Token);
        }

        // ------------------------------------------------------------ protocol

        private async Task<JsonObject> RequestAsync(string destination, string ns, JsonObject message, CancellationToken ct)
        {
            int id = Interlocked.Increment(ref _requestId);
            message["requestId"] = id;
            var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pending) _pending[id] = tcs;
            try
            {
                await SendAsync(destination, ns, message);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using (timeout.Token.Register(() => tcs.TrySetCanceled()))
                    return await tcs.Task;
            }
            catch (TaskCanceledException) { throw new TimeoutException("The Chromecast didn't answer."); }
            finally { lock (_pending) _pending.Remove(id); }
        }

        private async Task SendAsync(string destination, string ns, JsonObject payload)
        {
            var ssl = _ssl ?? throw new InvalidOperationException("Not connected");
            byte[] body = Encode(Sender, destination, ns, payload.ToJsonString());
            var frame = new byte[4 + body.Length];
            frame[0] = (byte)(body.Length >> 24); frame[1] = (byte)(body.Length >> 16); frame[2] = (byte)(body.Length >> 8); frame[3] = (byte)body.Length;
            body.CopyTo(frame, 4);
            await _writeLock.WaitAsync();
            try { await ssl.WriteAsync(frame); await ssl.FlushAsync(); }
            finally { _writeLock.Release(); }
        }

        private async Task ReadLoopAsync()
        {
            var header = new byte[4];
            try
            {
                while (!_cts.IsCancellationRequested && _ssl != null)
                {
                    await _ssl.ReadExactlyAsync(header, _cts.Token);
                    int len = header[0] << 24 | header[1] << 16 | header[2] << 8 | header[3];
                    if (len <= 0 || len > 1 << 20) break;
                    var body = new byte[len];
                    await _ssl.ReadExactlyAsync(body, _cts.Token);
                    var (ns, payload) = Decode(body);
                    if (payload == null) continue;
                    JsonObject? msg;
                    try { msg = JsonNode.Parse(payload) as JsonObject; } catch { continue; }
                    if (msg == null) continue;
                    string type = msg["type"]?.GetValue<string>() ?? "";
                    if (ns == NsHeartbeat && type == "PING") { _ = SendAsync("receiver-0", NsHeartbeat, new JsonObject { ["type"] = "PONG" }); continue; }
                    if (type == "MEDIA_STATUS") ReadMediaStatus(msg);
                    if (type == "RECEIVER_STATUS" && msg["status"]?["volume"]?["level"] is JsonNode level) Volume = Math.Round(level.GetValue<double>() * 100);
                    if (type == "CLOSE") { PlayerState = "IDLE"; MediaSessionId = null; }
                    if (msg["requestId"] is JsonNode rid && rid.GetValue<int>() is int id and > 0)
                    {
                        TaskCompletionSource<JsonObject>? tcs;
                        lock (_pending) _pending.TryGetValue(id, out tcs);
                        tcs?.TrySetResult(msg);
                    }
                }
            }
            catch { }
            _cts.Cancel();
        }

        private void ReadMediaStatus(JsonObject msg)
        {
            var status = msg["status"]?.AsArray().FirstOrDefault();
            if (status == null) { PlayerState = "IDLE"; return; }
            MediaSessionId = status["mediaSessionId"]?.GetValue<int>() ?? MediaSessionId;
            PlayerState = status["playerState"]?.GetValue<string>() ?? PlayerState;
            CurrentTime = status["currentTime"]?.GetValue<double>() ?? CurrentTime;
            if (status["media"]?["duration"] is JsonNode d && d.GetValueKind() == JsonValueKind.Number) Duration = d.GetValue<double>();
            StatusTime = DateTime.UtcNow;
        }

        private async Task HeartbeatLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    await Task.Delay(5000, _cts.Token);
                    await SendAsync("receiver-0", NsHeartbeat, new JsonObject { ["type"] = "PING" });
                }
            }
            catch { }
        }

        // CastMessage protobuf: 1 version(varint) 2 source 3 destination 4 namespace 5 payload_type(varint) 6 payload_utf8
        internal static byte[] Encode(string source, string destination, string ns, string payload)
        {
            var ms = new MemoryStream();
            ms.WriteByte(0x08); ms.WriteByte(0);
            WriteString(ms, 2, source);
            WriteString(ms, 3, destination);
            WriteString(ms, 4, ns);
            ms.WriteByte(0x28); ms.WriteByte(0);
            WriteString(ms, 6, payload);
            return ms.ToArray();
        }

        private static void WriteString(MemoryStream ms, int field, string value)
        {
            byte[] b = Encoding.UTF8.GetBytes(value);
            ms.WriteByte((byte)(field << 3 | 2));
            WriteVarint(ms, (ulong)b.Length);
            ms.Write(b);
        }

        private static void WriteVarint(MemoryStream ms, ulong v)
        {
            while (v >= 0x80) { ms.WriteByte((byte)(v | 0x80)); v >>= 7; }
            ms.WriteByte((byte)v);
        }

        internal static (string? Namespace, string? Payload) Decode(byte[] data)
        {
            string? ns = null, payload = null;
            int pos = 0;
            while (pos < data.Length)
            {
                ulong key = ReadVarint(data, ref pos);
                int field = (int)(key >> 3), wire = (int)(key & 7);
                if (wire == 0) { ReadVarint(data, ref pos); continue; }
                if (wire != 2) break;
                int len = (int)ReadVarint(data, ref pos);
                if (field == 4) ns = Encoding.UTF8.GetString(data, pos, len);
                else if (field == 6) payload = Encoding.UTF8.GetString(data, pos, len);
                pos += len;
            }
            return (ns, payload);
        }

        private static ulong ReadVarint(byte[] data, ref int pos)
        {
            ulong result = 0;
            int shift = 0;
            while (pos < data.Length)
            {
                byte b = data[pos++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { if (_ssl != null) await _ssl.DisposeAsync(); } catch { }
            _tcp.Dispose();
        }
    }
}
