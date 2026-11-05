using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace LocalStreamPC.Cast
{
    /// <summary>A SOAP action call: action name and its arguments.</summary>
    public sealed record SoapRequest(string Action, Dictionary<string, string> Args)
    {
        public string Arg(string name) => Args.TryGetValue(name, out var v) ? v : "";
    }

    /// <summary>A UPnP error to return as a SOAP fault (e.g. 701 "Transition not available").</summary>
    public sealed class UpnpException : Exception
    {
        public int Code { get; }
        public UpnpException(int code, string message) : base(message) => Code = code;
    }

    public static class UpnpXml
    {
        private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";

        public static SoapRequest ParseSoap(string body)
        {
            XDocument doc;
            try { doc = XDocument.Parse(body); }
            catch { throw new UpnpException(401, "Invalid Action"); }

            var action = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Body")?.Elements().FirstOrDefault()
                         ?? throw new UpnpException(401, "Invalid Action");
            var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in action.Elements()) args[e.Name.LocalName] = e.Value;
            return new SoapRequest(action.Name.LocalName, args);
        }

        public static string Response(string serviceType, string action, IEnumerable<(string Name, string Value)> outArgs)
        {
            XNamespace u = serviceType;
            var body = new XElement(u + (action + "Response"), new XAttribute(XNamespace.Xmlns + "u", u),
                outArgs.Select(a => new XElement(a.Name, a.Value)));
            return Envelope(body);
        }

        public static string Fault(int code, string description)
        {
            XNamespace c = "urn:schemas-upnp-org:control-1-0";
            var fault = new XElement(Soap + "Fault",
                new XElement("faultcode", "s:Client"),
                new XElement("faultstring", "UPnPError"),
                new XElement("detail",
                    new XElement(c + "UPnPError", new XAttribute("xmlns", c.NamespaceName),
                        new XElement(c + "errorCode", code),
                        new XElement(c + "errorDescription", description))));
            return Envelope(fault);
        }

        private static string Envelope(XElement content)
        {
            var env = new XElement(Soap + "Envelope",
                new XAttribute(XNamespace.Xmlns + "s", Soap),
                new XAttribute(Soap + "encodingStyle", "http://schemas.xmlsoap.org/soap/encoding/"),
                new XElement(Soap + "Body", content));
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + env.ToString(SaveOptions.DisableFormatting);
        }

        /// <summary>UPnP time "H+:MM:SS" (e.g. "01:02:03").</summary>
        public static string FormatTime(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        }

        /// <summary>Parses "H+:MM:SS[.F+]", "MM:SS" or plain seconds.</summary>
        public static bool TryParseTime(string? s, out TimeSpan time)
        {
            time = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            var parts = s.Split(':');
            try
            {
                double seconds = parts.Length switch
                {
                    1 => double.Parse(parts[0], CultureInfo.InvariantCulture),
                    2 => int.Parse(parts[0]) * 60 + double.Parse(parts[1], CultureInfo.InvariantCulture),
                    3 => int.Parse(parts[0]) * 3600 + int.Parse(parts[1]) * 60 + double.Parse(parts[2], CultureInfo.InvariantCulture),
                    _ => -1,
                };
                if (seconds < 0) return false;
                time = TimeSpan.FromSeconds(seconds);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Title and kind from the DIDL-Lite metadata a control point sends with a URI.</summary>
        public static (string? Title, string? UpnpClass) ParseDidlMetadata(string? didl)
        {
            if (string.IsNullOrWhiteSpace(didl)) return (null, null);
            try
            {
                var doc = XDocument.Parse(didl);
                string? title = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value;
                string? cls = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "class")?.Value;
                return (string.IsNullOrWhiteSpace(title) ? null : title.Trim(), cls);
            }
            catch { return (null, null); }
        }

        /// <summary>The LastChange XML that GENA events carry (already a string; the caller escapes it).</summary>
        public static string LastChange(string eventNamespace, IEnumerable<XElement> values)
        {
            XNamespace ns = eventNamespace;
            var evt = new XElement(ns + "Event",
                new XElement(ns + "InstanceID", new XAttribute("val", 0),
                    values.Select(v => new XElement(ns + v.Name.LocalName, v.Attributes()))));
            return evt.ToString(SaveOptions.DisableFormatting);
        }

        public static string PropertySet(IEnumerable<(string Name, string Value)> properties)
        {
            XNamespace e = "urn:schemas-upnp-org:event-1-0";
            var set = new XElement(e + "propertyset", new XAttribute(XNamespace.Xmlns + "e", e),
                properties.Select(p => new XElement(e + "property", new XElement(p.Name, p.Value))));
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + set.ToString(SaveOptions.DisableFormatting);
        }

        // ------------------------------------------------------------------ SCPD builder

        public sealed record Arg(string Name, bool Out, string StateVariable);
        public sealed record Action(string Name, params Arg[] Args);
        public sealed record StateVar(string Name, string DataType, bool SendEvents = false, string[]? Allowed = null, (int Min, int Max, int Step)? Range = null);

        public static Arg In(string name, string variable) => new(name, false, variable);
        public static Arg Out(string name, string variable) => new(name, true, variable);

        public static string Scpd(IEnumerable<Action> actions, IEnumerable<StateVar> variables)
        {
            XNamespace ns = "urn:schemas-upnp-org:service-1-0";
            var doc = new XElement(ns + "scpd",
                new XElement(ns + "specVersion", new XElement(ns + "major", 1), new XElement(ns + "minor", 0)),
                new XElement(ns + "actionList", actions.Select(a =>
                    new XElement(ns + "action",
                        new XElement(ns + "name", a.Name),
                        a.Args.Length == 0 ? null : new XElement(ns + "argumentList", a.Args.Select(arg =>
                            new XElement(ns + "argument",
                                new XElement(ns + "name", arg.Name),
                                new XElement(ns + "direction", arg.Out ? "out" : "in"),
                                new XElement(ns + "relatedStateVariable", arg.StateVariable))))))),
                new XElement(ns + "serviceStateTable", variables.Select(v =>
                    new XElement(ns + "stateVariable", new XAttribute("sendEvents", v.SendEvents ? "yes" : "no"),
                        new XElement(ns + "name", v.Name),
                        new XElement(ns + "dataType", v.DataType),
                        v.Allowed == null ? null : new XElement(ns + "allowedValueList", v.Allowed.Select(x => new XElement(ns + "allowedValue", x))),
                        v.Range == null ? null : new XElement(ns + "allowedValueRange",
                            new XElement(ns + "minimum", v.Range.Value.Min),
                            new XElement(ns + "maximum", v.Range.Value.Max),
                            new XElement(ns + "step", v.Range.Value.Step))))));
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + doc.ToString(SaveOptions.DisableFormatting);
        }
    }
}
