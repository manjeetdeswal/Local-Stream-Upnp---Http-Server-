using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LocalStreamPC.Services;

namespace LocalStreamPC.Server
{
    /// <summary>What SSDP announces: one UPnP root device.</summary>
    public sealed record SsdpDevice(string DeviceType, Func<string> Uuid, Func<int> Port, string DescriptionPath, string[] ServiceTypes);

    /// <summary>
    /// SSDP: announces devices on the LAN (NOTIFY alive/byebye) and answers M-SEARCH so TVs, VLC,
    /// phones and casting apps can find them. One shared socket serves every registered device
    /// (the media server and the cast receiver) - two sockets on port 1900 would each only get
    /// some of the searches.
    /// </summary>
    public sealed class SsdpService
    {
        private static readonly IPEndPoint Multicast = new(IPAddress.Parse("239.255.255.250"), 1900);

        private readonly AppLog _log;
        private readonly object _lock = new();
        private readonly List<SsdpDevice> _devices = new();
        private IReadOnlyList<(string Ip, string Name)> _localIps = Array.Empty<(string, string)>();
        private UdpClient? _client;
        private CancellationTokenSource? _cts;
        private Task? _loops;

        public SsdpService(AppLog log) => _log = log;

        /// <summary>Starts announcing <paramref name="device"/>. Dispose the result to send byebye and stop.</summary>
        public async Task<IAsyncDisposable> RegisterAsync(SsdpDevice device, IReadOnlyList<(string Ip, string Name)> localIps)
        {
            bool open;
            lock (_lock)
            {
                _devices.Add(device);
                _localIps = localIps;
                open = _client != null || TryOpenSocket();
            }
            if (open) await SendNotifyAsync(device, "ssdp:alive");
            return new Registration(this, device);
        }

        private sealed class Registration : IAsyncDisposable
        {
            private readonly SsdpService _owner;
            private readonly SsdpDevice _device;
            private int _disposed;
            public Registration(SsdpService owner, SsdpDevice device) { _owner = owner; _device = device; }
            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) await _owner.UnregisterAsync(_device);
            }
        }

        private async Task UnregisterAsync(SsdpDevice device)
        {
            await SendNotifyAsync(device, "ssdp:byebye");
            Task? loops = null;
            lock (_lock)
            {
                _devices.Remove(device);
                if (_devices.Count == 0 && _client != null)
                {
                    _cts?.Cancel();
                    loops = _loops;
                    _client.Dispose();
                    _client = null;
                }
            }
            if (loops != null) { try { await loops.WaitAsync(TimeSpan.FromSeconds(2)); } catch { } }
        }

        private bool TryOpenSocket()
        {
            try
            {
                var client = new UdpClient();
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                client.ExclusiveAddressUse = false;
                client.Client.Bind(new IPEndPoint(IPAddress.Any, 1900));
                foreach (var (ip, _) in _localIps)
                {
                    try { client.JoinMulticastGroup(Multicast.Address, IPAddress.Parse(ip)); } catch { }
                }
                _client = client;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _loops = Task.WhenAll(Task.Run(() => ReceiveLoopAsync(client, token)), Task.Run(() => HeartbeatAsync(token)));
                _log.Write("SSDP active (announcing on all networks).");
                return true;
            }
            catch (Exception ex)
            {
                _log.Write($"SSDP bind error: {ex.Message}. Other devices may not find this PC automatically.");
                return false;
            }
        }

        private static string ServerHeader => $"{Environment.OSVersion.Platform}/1.0 UPnP/1.1 LocalStream/{UpdateService.CurrentVersionText}";

        private SsdpDevice[] Devices { get { lock (_lock) return _devices.ToArray(); } }
        private IReadOnlyList<(string Ip, string Name)> LocalIps { get { lock (_lock) return _localIps; } }

        private async Task ReceiveLoopAsync(UdpClient client, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                UdpReceiveResult packet;
                try { packet = await client.ReceiveAsync(token); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { continue; } // e.g. ICMP port unreachable on Windows

                string request = Encoding.UTF8.GetString(packet.Buffer);
                if (!request.StartsWith("M-SEARCH", StringComparison.OrdinalIgnoreCase)) continue;
                string st = request.Split('\n')
                    .FirstOrDefault(l => l.StartsWith("ST:", StringComparison.OrdinalIgnoreCase))?[3..].Trim() ?? "";

                // Answer once per matching device, from the address on the client's subnet.
                string ip = BestLocalIpFor(packet.RemoteEndPoint.Address, LocalIps);
                foreach (var device in Devices)
                {
                    string? responseSt = MatchSearchTarget(device, st);
                    if (responseSt == null) continue;
                    string response = "HTTP/1.1 200 OK\r\n" +
                                      "CACHE-CONTROL: max-age=1800\r\n" +
                                      "EXT:\r\n" +
                                      $"LOCATION: http://{ip}:{device.Port()}{device.DescriptionPath}\r\n" +
                                      $"SERVER: {ServerHeader}\r\n" +
                                      $"ST: {responseSt}\r\n" +
                                      $"USN: {Usn(device, responseSt)}\r\n\r\n";
                    byte[] data = Encoding.UTF8.GetBytes(response);
                    try { await client.SendAsync(data, data.Length, packet.RemoteEndPoint); } catch { }
                }
            }
        }

        /// <summary>Which ST to answer with, or null if the search isn't for this device.</summary>
        private static string? MatchSearchTarget(SsdpDevice d, string st)
        {
            if (st.Equals("ssdp:all", StringComparison.OrdinalIgnoreCase)) return d.DeviceType;
            if (st.Equals("upnp:rootdevice", StringComparison.OrdinalIgnoreCase)) return "upnp:rootdevice";
            if (st.Equals(d.DeviceType, StringComparison.OrdinalIgnoreCase)) return d.DeviceType;
            if (st.Equals($"uuid:{d.Uuid()}", StringComparison.OrdinalIgnoreCase)) return $"uuid:{d.Uuid()}";
            return d.ServiceTypes.FirstOrDefault(s => s.Equals(st, StringComparison.OrdinalIgnoreCase));
        }

        private static string Usn(SsdpDevice d, string st)
            => st.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase) ? st : $"uuid:{d.Uuid()}::{st}";

        private async Task HeartbeatAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromMinutes(3), token); } catch (OperationCanceledException) { break; }
                foreach (var device in Devices) await SendNotifyAsync(device, "ssdp:alive");
            }
        }

        private async Task SendNotifyAsync(SsdpDevice device, string nts)
        {
            UdpClient? client;
            lock (_lock) client = _client;
            if (client == null) return;

            // Announce the root device, the device type and each service, as UPnP requires.
            var targets = new List<(string Nt, string Usn)>
            {
                ("upnp:rootdevice", $"uuid:{device.Uuid()}::upnp:rootdevice"),
                ($"uuid:{device.Uuid()}", $"uuid:{device.Uuid()}"),
                (device.DeviceType, $"uuid:{device.Uuid()}::{device.DeviceType}"),
            };
            targets.AddRange(device.ServiceTypes.Select(s => (s, $"uuid:{device.Uuid()}::{s}")));

            foreach (var (ip, _) in LocalIps)
            {
                foreach (var (nt, usn) in targets)
                {
                    string msg = "NOTIFY * HTTP/1.1\r\n" +
                                 "HOST: 239.255.255.250:1900\r\n" +
                                 "CACHE-CONTROL: max-age=1800\r\n" +
                                 $"LOCATION: http://{ip}:{device.Port()}{device.DescriptionPath}\r\n" +
                                 $"NT: {nt}\r\n" +
                                 $"NTS: {nts}\r\n" +
                                 $"SERVER: {ServerHeader}\r\n" +
                                 $"USN: {usn}\r\n\r\n";
                    byte[] bytes = Encoding.UTF8.GetBytes(msg);
                    try { await client.SendAsync(bytes, bytes.Length, Multicast); } catch { }
                }
            }
        }

        private static string BestLocalIpFor(IPAddress client, IReadOnlyList<(string Ip, string Name)> localIps)
        {
            if (client.IsIPv4MappedToIPv6) client = client.MapToIPv4();
            byte[] c = client.GetAddressBytes();
            foreach (var (ip, _) in localIps)
            {
                if (!IPAddress.TryParse(ip, out var local)) continue;
                byte[] l = local.GetAddressBytes();
                if (c.Length == 4 && l.Length == 4 && c[0] == l[0] && c[1] == l[1] && c[2] == l[2]) return ip;
            }
            return localIps.Count > 0 ? localIps[0].Ip : "127.0.0.1";
        }
    }
}
