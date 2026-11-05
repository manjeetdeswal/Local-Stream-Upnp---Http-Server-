using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using LocalStreamPC.Models;

namespace LocalStreamPC.Services
{
    public sealed record DidlContainer(string Id, string Title, string? ChildCount, string? ThumbnailUrl);
    public sealed record DidlItem(string Id, string Title, string Url, string UpnpClass, string? ThumbnailUrl, long Size);
    public sealed record DidlResult(List<DidlContainer> Containers, List<DidlItem> Items)
    {
        public static DidlResult Empty => new(new List<DidlContainer>(), new List<DidlItem>());
    }

    /// <summary>Talks to a remote UPnP/DLNA ContentDirectory (another LocalStream server, a NAS, a phone...).</summary>
    public sealed class UpnpBrowseClient
    {
        private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
        private readonly string _baseUrl;
        private readonly string _controlUrl;

        public UpnpBrowseClient(string baseUrl, string controlUrl)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _controlUrl = controlUrl;
        }

        /// <param name="recursive">"BrowseRecursive" is a LocalStream extension; other servers ignore it.</param>
        public async Task<DidlResult> BrowseAsync(string objectId, bool recursive = false, int requestedCount = 0,
                                                  TimeSpan? timeout = null, CancellationToken ct = default)
        {
            string url = _controlUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? _controlUrl
                : _baseUrl + "/" + _controlUrl.TrimStart('/');

            // The id comes from the remote server, so it must be escaped before going into XML.
            string soap = $@"<?xml version=""1.0""?>
<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"" s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
  <s:Body>
    <u:Browse xmlns:u=""urn:schemas-upnp-org:service:ContentDirectory:1"">
      <ObjectID>{SecurityElement.Escape(objectId)}</ObjectID>
      <BrowseFlag>{(recursive ? "BrowseRecursive" : "BrowseDirectChildren")}</BrowseFlag>
      <Filter>*</Filter>
      <StartingIndex>0</StartingIndex>
      <RequestedCount>{requestedCount}</RequestedCount>
      <SortCriteria></SortCriteria>
    </u:Browse>
  </s:Body>
</s:Envelope>";

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));

            using var content = new StringContent(soap, Encoding.UTF8, "text/xml");
            content.Headers.Add("SOAPAction", "\"urn:schemas-upnp-org:service:ContentDirectory:1#Browse\"");
            using var response = await Http.PostAsync(url, content, cts.Token);
            if (!response.IsSuccessStatusCode) return DidlResult.Empty;

            string body = await response.Content.ReadAsStringAsync(cts.Token);
            var resultNode = XDocument.Parse(body).Descendants().FirstOrDefault(n => n.Name.LocalName == "Result");
            return resultNode == null ? DidlResult.Empty : ParseDidl(resultNode.Value);
        }

        private DidlResult ParseDidl(string didlXml)
        {
            var result = DidlResult.Empty;
            if (string.IsNullOrWhiteSpace(didlXml)) return result;
            var doc = XDocument.Parse(didlXml);

            static string? Child(XElement e, string name) => e.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value;

            foreach (var c in doc.Descendants().Where(e => e.Name.LocalName == "container"))
            {
                result.Containers.Add(new DidlContainer(
                    c.Attribute("id")?.Value ?? "",
                    Child(c, "title") ?? "Folder",
                    c.Attribute("childCount")?.Value,
                    AbsoluteUrl(Child(c, "albumArtURI"))));
            }

            foreach (var i in doc.Descendants().Where(e => e.Name.LocalName == "item"))
            {
                var res = i.Elements().FirstOrDefault(e => e.Name.LocalName == "res");
                long.TryParse(res?.Attribute("size")?.Value, out long size);
                result.Items.Add(new DidlItem(
                    i.Attribute("id")?.Value ?? "",
                    Child(i, "title") ?? "File",
                    res?.Value ?? "",
                    Child(i, "class") ?? "",
                    AbsoluteUrl(Child(i, "albumArtURI")),
                    size));
            }
            return result;
        }

        private string? AbsoluteUrl(string? url)
        {
            if (string.IsNullOrEmpty(url) || url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return url;
            return url.StartsWith('/') ? _baseUrl + url : _baseUrl + "/" + url;
        }
    }

    /// <summary>Finds media servers on the LAN with an SSDP M-SEARCH.</summary>
    public sealed class DeviceDiscovery
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
        private readonly AppLog _log;

        public DeviceDiscovery(AppLog log) => _log = log;

        public async Task ScanAsync(Action<UpnpDevice> onFound, CancellationToken ct = default)
        {
            var seenLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new List<Task>();
            var gate = new object();

            const string request = "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 3\r\nST: ssdp:all\r\n\r\n";
            byte[] data = Encoding.UTF8.GetBytes(request);
            var multicast = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);

            // Bound to the main adapter so the multicast leaves on the right network.
            using var lan = new UdpClient(new IPEndPoint(IPAddress.Parse(NetworkHelper.GetPrimaryIPv4()), 0)) { Ttl = 4 };
            // A second, loopback socket asks this PC directly: finds a server running here even
            // when a firewall or the network blocks multicast loopback.
            using var local = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

            using var listenCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            listenCts.CancelAfter(TimeSpan.FromSeconds(6));

            async Task ListenAsync(UdpClient socket)
            {
                try
                {
                    while (!listenCts.IsCancellationRequested)
                    {
                        var packet = await socket.ReceiveAsync(listenCts.Token);
                        string msg = Encoding.UTF8.GetString(packet.Buffer);
                        string? location = msg.Split('\n')
                            .FirstOrDefault(l => l.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))?[9..].Trim();
                        if (string.IsNullOrEmpty(location)) continue;
                        lock (gate)
                        {
                            if (!seenLocations.Add(location)) continue;
                            pending.Add(FetchDescriptionAsync(location, packet.RemoteEndPoint.Address.ToString(), onFound));
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (SocketException) { }
            }

            var listeners = Task.WhenAll(ListenAsync(lan), ListenAsync(local));
            try
            {
                try { await local.SendAsync(data, data.Length, new IPEndPoint(IPAddress.Loopback, 1900)); } catch (SocketException) { }
                for (int i = 0; i < 5; i++)
                {
                    await lan.SendAsync(data, data.Length, multicast);
                    await Task.Delay(400, ct);
                }
            }
            catch (SocketException ex)
            {
                _log.Write($"Discovery network error: {ex.Message}. Check your firewall.");
            }

            await listeners;
            Task[] fetches;
            lock (gate) fetches = pending.ToArray();
            await Task.WhenAll(fetches);
        }

        private static async Task FetchDescriptionAsync(string location, string ip, Action<UpnpDevice> onFound)
        {
            try
            {
                var doc = XDocument.Parse(await Http.GetStringAsync(location));
                var ns = doc.Root!.GetDefaultNamespace();

                var service = doc.Descendants(ns + "service")
                    .FirstOrDefault(s => s.Element(ns + "serviceType")?.Value.Contains("ContentDirectory") == true);
                string controlUrl = service?.Element(ns + "controlURL")?.Value ?? "";
                if (string.IsNullOrEmpty(controlUrl)) return; // not a media server

                var uri = new Uri(location);
                onFound(new UpnpDevice
                {
                    FriendlyName = doc.Descendants(ns + "friendlyName").FirstOrDefault()?.Value ?? "Unknown",
                    IpAddress = ip,
                    ServiceType = doc.Descendants(ns + "manufacturer").FirstOrDefault()?.Value ?? "",
                    ContentDirectoryUrl = controlUrl,
                    BaseUrl = $"{uri.Scheme}://{uri.Authority}",
                });
            }
            catch
            {
                // Device didn't answer or isn't UPnP - skip it.
            }
        }
    }
}
