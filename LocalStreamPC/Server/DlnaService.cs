using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using LocalStreamPC.Services;
using Microsoft.AspNetCore.Http;

namespace LocalStreamPC.Server
{
    /// <summary>
    /// UPnP MediaServer / ContentDirectory: what TVs, VLC and the LocalStream apps talk to.
    /// Object ids and /file/ URLs keep the old format (base64 of the full path) so existing
    /// clients and saved history keep working - but every id is now checked with PathGuard.
    /// </summary>
    public sealed class DlnaService
    {
        private const string DlnaFeatures = "DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000";
        private readonly ConfigService _config;
        private readonly AppLog _log;

        public DlnaService(ConfigService config, AppLog log)
        {
            _config = config;
            _log = log;
        }

        public static string EncodeId(string path) => Convert.ToBase64String(Encoding.UTF8.GetBytes(path));

        public static bool TryDecodeId(string? id, out string path)
        {
            path = "";
            if (string.IsNullOrEmpty(id)) return false;
            id = Uri.UnescapeDataString(id).Trim();
            // Tolerate URL-safe base64 and missing padding from picky clients.
            id = id.Replace('-', '+').Replace('_', '/');
            switch (id.Length % 4) { case 2: id += "=="; break; case 3: id += "="; break; }
            var buffer = new byte[id.Length];
            if (!Convert.TryFromBase64String(id, buffer, out int written)) return false;
            path = Encoding.UTF8.GetString(buffer, 0, written);
            return true;
        }

        public string DeviceDescription() => $@"<?xml version=""1.0""?>
<root xmlns=""urn:schemas-upnp-org:device-1-0"" xmlns:dlna=""urn:schemas-dlna-org:device-1-0"">
  <specVersion><major>1</major><minor>0</minor></specVersion>
  <device>
    <deviceType>urn:schemas-upnp-org:device:MediaServer:1</deviceType>
    <friendlyName>Local Stream PC ({WebUtility.HtmlEncode(Environment.MachineName)})</friendlyName>
    <manufacturer>LocalStream</manufacturer>
    <modelName>LocalStream PC Server</modelName>
    <modelNumber>{UpdateService.CurrentVersionText}</modelNumber>
    <UDN>uuid:{_config.Current.ServerUuid}</UDN>
    <dlna:X_DLNADOC xmlns:dlna=""urn:schemas-dlna-org:device-1-0"">DMS-1.50</dlna:X_DLNADOC>
    <serviceList>
      <service>
        <serviceType>urn:schemas-upnp-org:service:ContentDirectory:1</serviceType>
        <serviceId>urn:upnp-org:serviceId:ContentDirectory</serviceId>
        <controlURL>/control/ContentDirectory</controlURL>
        <eventSubURL>/events/ContentDirectory</eventSubURL>
        <SCPDURL>/scpd/ContentDirectory.xml</SCPDURL>
      </service>
      <service>
        <serviceType>urn:schemas-upnp-org:service:ConnectionManager:1</serviceType>
        <serviceId>urn:upnp-org:serviceId:ConnectionManager</serviceId>
        <controlURL>/control/ConnectionManager</controlURL>
        <eventSubURL>/events/ConnectionManager</eventSubURL>
        <SCPDURL>/scpd/ConnectionManager.xml</SCPDURL>
      </service>
    </serviceList>
  </device>
</root>";

        public const string ContentDirectoryScpd = @"<?xml version=""1.0"" encoding=""utf-8""?><scpd xmlns=""urn:schemas-upnp-org:service-1-0""><specVersion><major>1</major><minor>0</minor></specVersion><actionList><action><name>Browse</name><argumentList><argument><name>ObjectID</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_ObjectID</relatedStateVariable></argument><argument><name>BrowseFlag</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_BrowseFlag</relatedStateVariable></argument><argument><name>Filter</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_Filter</relatedStateVariable></argument><argument><name>StartingIndex</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_Index</relatedStateVariable></argument><argument><name>RequestedCount</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_Count</relatedStateVariable></argument><argument><name>SortCriteria</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_SortCriteria</relatedStateVariable></argument><argument><name>Result</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Result</relatedStateVariable></argument><argument><name>NumberReturned</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Count</relatedStateVariable></argument><argument><name>TotalMatches</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Count</relatedStateVariable></argument><argument><name>UpdateID</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_UpdateID</relatedStateVariable></argument></argumentList></action></actionList><serviceStateTable><stateVariable sendEvents=""no""><name>A_ARG_TYPE_ObjectID</name><dataType>string</dataType></stateVariable><stateVariable sendEvents=""no""><name>A_ARG_TYPE_Result</name><dataType>string</dataType></stateVariable><stateVariable sendEvents=""no""><name>A_ARG_TYPE_BrowseFlag</name><dataType>string</dataType></stateVariable><stateVariable sendEvents=""no""><name>A_ARG_TYPE_Filter</name><dataType>string</dataType></stateVariable><stateVariable sendEvents=""no""><name>A_ARG_TYPE_SortCriteria</name><dataType>string</dataType></stateVariable><stateVariable sendEvents=""no""><name>A_ARG_TYPE_Index</name><dataType>ui4</dataType></stateVariable><stateVariable sendEvents=""no""><name>A_ARG_TYPE_Count</name><dataType>ui4</dataType></stateVariable><stateVariable sendEvents=""no""><name>A_ARG_TYPE_UpdateID</name><dataType>ui4</dataType></stateVariable></serviceStateTable></scpd>";

        public const string ConnectionManagerScpd = @"<?xml version=""1.0"" encoding=""utf-8""?><scpd xmlns=""urn:schemas-upnp-org:service-1-0""><specVersion><major>1</major><minor>0</minor></specVersion><actionList><action><name>GetProtocolInfo</name><argumentList><argument><name>Source</name><direction>out</direction><relatedStateVariable>SourceProtocolInfo</relatedStateVariable></argument><argument><name>Sink</name><direction>out</direction><relatedStateVariable>SinkProtocolInfo</relatedStateVariable></argument></argumentList></action><action><name>GetCurrentConnectionIDs</name><argumentList><argument><name>ConnectionIDs</name><direction>out</direction><relatedStateVariable>CurrentConnectionIDs</relatedStateVariable></argument></argumentList></action><action><name>GetCurrentConnectionInfo</name><argumentList><argument><name>ConnectionID</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_ConnectionID</relatedStateVariable></argument><argument><name>RcsID</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_RcsID</relatedStateVariable></argument><argument><name>AVTransportID</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_AVTransportID</relatedStateVariable></argument><argument><name>ProtocolInfo</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_ProtocolInfo</relatedStateVariable></argument><argument><name>PeerConnectionManager</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_ConnectionManager</relatedStateVariable></argument><argument><name>PeerConnectionID</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_ConnectionID</relatedStateVariable></argument><argument><name>Direction</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Direction</relatedStateVariable></argument><argument><name>Status</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_ConnectionStatus</relatedStateVariable></argument></argumentList></action></actionList><serviceStateTable><stateVariable sendEvents=""yes""><name>SourceProtocolInfo</name><dataType>string</dataType></stateVariable><stateVariable sendEvents=""yes""><name>SinkProtocolInfo</name><dataType>string</dataType></stateVariable><stateVariable sendEvents=""yes""><name>CurrentConnectionIDs</name><dataType>string</dataType></stateVariable><stateVariable sendEvents=""no""><name>A_ARG_TYPE_ConnectionStatus</name><dataType>string</dataType></stateVariable><stateVariable sendEvents=""no""><name>A_ARG_TYPE_ConnectionManager</name><dataType>string</dataType></stateVariable><stateVariable sendEvents=""no""><name>A_ARG_TYPE_Direction</name><dataType>string</dataType></stateVariable><stateVariable sendEvents=""no""><name>A_ARG_TYPE_ProtocolInfo</name><dataType>string</dataType></stateVariable><stateVariable sendEvents=""no""><name>A_ARG_TYPE_ConnectionID</name><dataType>i4</dataType></stateVariable><stateVariable sendEvents=""no""><name>A_ARG_TYPE_AVTransportID</name><dataType>i4</dataType></stateVariable><stateVariable sendEvents=""no""><name>A_ARG_TYPE_RcsID</name><dataType>i4</dataType></stateVariable></serviceStateTable></scpd>";

        /// <summary>Handles POST /control/ContentDirectory (the SOAP Browse action).</summary>
        public async Task HandleBrowseAsync(HttpContext ctx, IReadOnlyList<string> roots)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string body;
            using (var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8))
                body = await reader.ReadToEndAsync(ctx.RequestAborted);

            var req = ParseBrowseRequest(body);
            string baseUrl = $"http://{HostForUrls(ctx)}:{_config.Current.Port}";
            var entries = new List<Action<XmlWriter>>();
            if (req.Flag == "BrowseMetadata")
            {
                var single = MetadataEntry(req.ObjectId, roots, baseUrl);
                if (single != null) entries.Add(single);
            }
            else if (req.ObjectId == "0")
            {
                foreach (var root in roots.Where(Directory.Exists))
                {
                    string title = new DirectoryInfo(root).Name;
                    if (string.IsNullOrEmpty(title)) title = root;
                    string id = EncodeId(root);
                    entries.Add(w => WriteContainer(w, id, "0", title));
                }
            }
            else if (TryDecodeId(req.ObjectId, out string rawPath) &&
                     PathGuard.TryResolve(rawPath, roots, out string folder) && Directory.Exists(folder))
            {
                string parentId = req.ObjectId;
                if (req.Flag == "BrowseRecursive")
                {
                    foreach (var file in EnumerateFilesRecursive(folder).Where(MediaTypes.IsMediaFile))
                        entries.Add(w => WriteItem(w, new FileInfo(file), parentId, baseUrl));
                }
                else
                {
                    var (folders, files) = FolderReader.Read(folder);
                    foreach (var dir in folders)
                    {
                        if ((dir.Attributes & FileAttributes.Hidden) != 0) continue; // attributes come from the scan
                        string id = EncodeId(dir.FullName);
                        entries.Add(w => WriteContainer(w, id, parentId, dir.Name));
                    }
                    foreach (var file in files.Where(f => MediaTypes.IsMediaFile(f.Name)))
                        entries.Add(w => WriteItem(w, file, parentId, baseUrl));
                }
            }
            else
            {
                _log.Write($"Browse refused for id outside shared folders.");
            }

            int total = entries.Count;
            var page = (req.RequestedCount > 0 ? entries.Skip(req.StartingIndex).Take(req.RequestedCount) : entries.Skip(req.StartingIndex)).ToList();

            string didl = BuildDidl(page);
            string soap = BuildSoapResponse(didl, page.Count, total);

            ctx.Response.ContentType = "text/xml; charset=\"utf-8\"";
            await ctx.Response.WriteAsync(soap, Encoding.UTF8);
            _log.Write($"Browse: {DescribeId(req.ObjectId)} -> {page.Count}/{total} items ({sw.ElapsedMilliseconds} ms)");
        }

        private Action<XmlWriter>? MetadataEntry(string objectId, IReadOnlyList<string> roots, string baseUrl)
        {
            if (objectId == "0")
                return w => WriteContainer(w, "0", "-1", "Local Stream PC");
            if (!TryDecodeId(objectId, out string raw) || !PathGuard.TryResolve(raw, roots, out string full)) return null;

            string parentId = PathGuard.IsRoot(full, roots) ? "0" : EncodeId(Path.GetDirectoryName(full) ?? "");
            if (Directory.Exists(full))
            {
                string title = new DirectoryInfo(full).Name;
                return w => WriteContainer(w, objectId, parentId, string.IsNullOrEmpty(title) ? full : title);
            }
            if (File.Exists(full) && MediaTypes.IsMediaFile(full))
                return w => WriteItem(w, new FileInfo(full), parentId, baseUrl);
            return null;
        }

        private static void WriteContainer(XmlWriter w, string id, string parentId, string title)
        {
            w.WriteStartElement("container", DidlNs);
            w.WriteAttributeString("id", id);
            w.WriteAttributeString("parentID", parentId);
            w.WriteAttributeString("restricted", "1");
            w.WriteAttributeString("searchable", "0");
            w.WriteElementString("title", DcNs, title);
            w.WriteElementString("class", UpnpNs, "object.container.storageFolder");
            w.WriteEndElement();
        }

        private static void WriteItem(XmlWriter w, FileInfo info, string parentId, string baseUrl)
        {
            string fullPath = info.FullName;
            try { if (!info.Exists) return; }
            catch { return; }

            string id = EncodeId(fullPath);
            string mime = MediaTypes.GetMimeType(info.Extension);
            string upnpClass = mime.StartsWith("video") ? "object.item.videoItem"
                             : mime.StartsWith("audio") ? "object.item.audioItem.musicTrack"
                             : mime.StartsWith("image") ? "object.item.imageItem.photo"
                             : "object.item";

            w.WriteStartElement("item", DidlNs);
            w.WriteAttributeString("id", id);
            w.WriteAttributeString("parentID", parentId);
            w.WriteAttributeString("restricted", "1");
            w.WriteElementString("title", DcNs, Path.GetFileNameWithoutExtension(info.Name));
            w.WriteElementString("class", UpnpNs, upnpClass);
            w.WriteElementString("albumArtURI", UpnpNs, $"{baseUrl}/thumb/{id}");

            w.WriteStartElement("res", DidlNs);
            w.WriteAttributeString("protocolInfo", $"http-get:*:{mime}:{DlnaFeatures}");
            w.WriteAttributeString("size", info.Length.ToString());
            w.WriteString($"{baseUrl}/file/{id}");
            w.WriteEndElement();

            if (mime.StartsWith("video"))
            {
                string? srt = SubtitleMatcher.FindBestMatch(fullPath);
                if (srt != null)
                {
                    w.WriteStartElement("res", DidlNs);
                    w.WriteAttributeString("protocolInfo", "http-get:*:text/srt:*");
                    w.WriteString($"{baseUrl}/file/{EncodeId(srt)}");
                    w.WriteEndElement();
                }
            }
            w.WriteEndElement();
        }

        private const string DidlNs = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
        private const string DcNs = "http://purl.org/dc/elements/1.1/";
        private const string UpnpNs = "urn:schemas-upnp-org:metadata-1-0/upnp/";

        private static string BuildDidl(IEnumerable<Action<XmlWriter>> entries)
        {
            var sb = new StringBuilder();
            using (var w = XmlWriter.Create(sb, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = false }))
            {
                w.WriteStartElement("DIDL-Lite", DidlNs);
                w.WriteAttributeString("xmlns", "dc", null, DcNs);
                w.WriteAttributeString("xmlns", "upnp", null, UpnpNs);
                w.WriteAttributeString("xmlns", "dlna", null, "urn:schemas-dlna-org:metadata-1-0/");
                foreach (var write in entries)
                {
                    try { write(w); } catch { /* skip unreadable entries */ }
                }
                w.WriteEndElement();
            }
            return sb.ToString();
        }

        private static string BuildSoapResponse(string didl, int returned, int total)
        {
            XNamespace s = "http://schemas.xmlsoap.org/soap/envelope/";
            XNamespace u = "urn:schemas-upnp-org:service:ContentDirectory:1";
            var doc = new XDocument(new XDeclaration("1.0", "utf-8", null),
                new XElement(s + "Envelope",
                    new XAttribute(XNamespace.Xmlns + "s", s),
                    new XAttribute(s + "encodingStyle", "http://schemas.xmlsoap.org/soap/encoding/"),
                    new XElement(s + "Body",
                        new XElement(u + "BrowseResponse",
                            new XAttribute(XNamespace.Xmlns + "u", u),
                            new XElement("Result", didl), // XElement escapes the DIDL text exactly once
                            new XElement("NumberReturned", returned),
                            new XElement("TotalMatches", total),
                            new XElement("UpdateID", 1)))));
            return doc.Declaration + "\n" + doc.Root!.ToString(SaveOptions.DisableFormatting);
        }

        private sealed record BrowseRequest(string ObjectId, string Flag, int StartingIndex, int RequestedCount);

        private static BrowseRequest ParseBrowseRequest(string body)
        {
            string Get(string name)
            {
                try
                {
                    return XDocument.Parse(body).Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";
                }
                catch
                {
                    // Some clients send slightly broken XML; fall back to a plain text search.
                    int start = body.IndexOf($"<{name}>", StringComparison.Ordinal);
                    int end = body.IndexOf($"</{name}>", StringComparison.Ordinal);
                    return start >= 0 && end > start ? WebUtility.HtmlDecode(body[(start + name.Length + 2)..end]) : "";
                }
            }

            string id = Get("ObjectID").Trim();
            string flag = Get("BrowseFlag").Trim();
            int.TryParse(Get("StartingIndex"), out int startIndex);
            int.TryParse(Get("RequestedCount"), out int count);
            return new BrowseRequest(string.IsNullOrEmpty(id) ? "0" : id,
                                     string.IsNullOrEmpty(flag) ? "BrowseDirectChildren" : flag,
                                     Math.Max(0, startIndex), Math.Max(0, count));
        }

        /// <summary>
        /// The address the client actually reached us on, so the URLs we hand out are routable
        /// for that client even on a PC with several network adapters.
        /// </summary>
        public static string HostForUrls(HttpContext ctx)
        {
            var local = ctx.Connection.LocalIpAddress;
            if (local != null)
            {
                if (local.IsIPv4MappedToIPv6) local = local.MapToIPv4();
                if (local.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.Any.Equals(local))
                    return local.ToString();
                if (local.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                    return $"[{local}]";
            }
            return ctx.Request.Host.Host;
        }

        private static IEnumerable<string> EnumerateFilesRecursive(string path)
        {
            foreach (var f in SafeEnumerate(() => Directory.EnumerateFiles(path))) yield return f;
            foreach (var d in SafeEnumerate(() => Directory.EnumerateDirectories(path)))
                foreach (var f in EnumerateFilesRecursive(d)) yield return f;
        }

        private static List<string> SafeEnumerate(Func<IEnumerable<string>> enumerate)
        {
            try { return enumerate().ToList(); }
            catch { return new List<string>(); } // permission denied etc.
        }

        private static string DescribeId(string id)
            => id == "0" ? "root" : TryDecodeId(id, out var p) ? Path.GetFileName(p.TrimEnd('\\', '/')) : "?";
    }
}
