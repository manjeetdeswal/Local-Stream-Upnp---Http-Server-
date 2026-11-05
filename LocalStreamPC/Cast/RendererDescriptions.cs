using System;
using System.Net;
using LocalStreamPC.Services;
using static LocalStreamPC.Cast.UpnpXml;

namespace LocalStreamPC.Cast
{
    /// <summary>The XML documents that describe the cast receiver (a UPnP MediaRenderer:1).</summary>
    public static class RendererDescriptions
    {
        public const string DeviceType = "urn:schemas-upnp-org:device:MediaRenderer:1";
        public const string AvTransport = "urn:schemas-upnp-org:service:AVTransport:1";
        public const string RenderingControl = "urn:schemas-upnp-org:service:RenderingControl:1";
        public const string ConnectionManager = "urn:schemas-upnp-org:service:ConnectionManager:1";

        public static string FriendlyName => $"LocalStream Player ({Environment.MachineName})";

        /// <summary>What we tell senders we can play. VLC handles nearly anything, so the list ends with a wildcard.</summary>
        public const string SinkProtocolInfo =
            "http-get:*:video/mp4:*,http-get:*:video/x-matroska:*,http-get:*:video/x-mkv:*,http-get:*:video/webm:*," +
            "http-get:*:video/quicktime:*,http-get:*:video/x-msvideo:*,http-get:*:video/avi:*,http-get:*:video/x-ms-wmv:*," +
            "http-get:*:video/mpeg:*,http-get:*:video/mp2t:*,http-get:*:video/vnd.dlna.mpeg-tts:*,http-get:*:video/3gpp:*," +
            "http-get:*:video/x-flv:*,http-get:*:application/vnd.apple.mpegurl:*,http-get:*:application/x-mpegURL:*," +
            "http-get:*:application/dash+xml:*," +
            "http-get:*:audio/mpeg:*,http-get:*:audio/mp3:*,http-get:*:audio/mp4:*,http-get:*:audio/x-m4a:*,http-get:*:audio/aac:*," +
            "http-get:*:audio/flac:*,http-get:*:audio/x-flac:*,http-get:*:audio/wav:*,http-get:*:audio/x-wav:*,http-get:*:audio/L16:*," +
            "http-get:*:audio/ogg:*,http-get:*:audio/opus:*,http-get:*:audio/x-ms-wma:*," +
            "http-get:*:image/jpeg:*,http-get:*:image/png:*,http-get:*:image/gif:*,http-get:*:image/bmp:*,http-get:*:image/webp:*," +
            "http-get:*:*:*";

        public static string DeviceDescription(string uuid) => $@"<?xml version=""1.0"" encoding=""utf-8""?>
<root xmlns=""urn:schemas-upnp-org:device-1-0"" xmlns:dlna=""urn:schemas-dlna-org:device-1-0"">
  <specVersion><major>1</major><minor>0</minor></specVersion>
  <device>
    <deviceType>{DeviceType}</deviceType>
    <friendlyName>{WebUtility.HtmlEncode(FriendlyName)}</friendlyName>
    <manufacturer>LocalStream</manufacturer>
    <modelName>LocalStream Player</modelName>
    <modelDescription>Cast receiver for LocalStream PC</modelDescription>
    <modelNumber>{UpdateService.CurrentVersionText}</modelNumber>
    <UDN>uuid:{uuid}</UDN>
    <dlna:X_DLNADOC>DMR-1.50</dlna:X_DLNADOC>
    <serviceList>
      <service>
        <serviceType>{AvTransport}</serviceType>
        <serviceId>urn:upnp-org:serviceId:AVTransport</serviceId>
        <SCPDURL>/scpd/AVTransport.xml</SCPDURL>
        <controlURL>/control/AVTransport</controlURL>
        <eventSubURL>/event/AVTransport</eventSubURL>
      </service>
      <service>
        <serviceType>{RenderingControl}</serviceType>
        <serviceId>urn:upnp-org:serviceId:RenderingControl</serviceId>
        <SCPDURL>/scpd/RenderingControl.xml</SCPDURL>
        <controlURL>/control/RenderingControl</controlURL>
        <eventSubURL>/event/RenderingControl</eventSubURL>
      </service>
      <service>
        <serviceType>{ConnectionManager}</serviceType>
        <serviceId>urn:upnp-org:serviceId:ConnectionManager</serviceId>
        <SCPDURL>/scpd/ConnectionManager.xml</SCPDURL>
        <controlURL>/control/ConnectionManager</controlURL>
        <eventSubURL>/event/ConnectionManager</eventSubURL>
      </service>
    </serviceList>
  </device>
</root>";

        public static readonly string AvTransportScpd = Scpd(
            new[]
            {
                new UpnpXml.Action("SetAVTransportURI", In("InstanceID", "A_ARG_TYPE_InstanceID"), In("CurrentURI", "AVTransportURI"), In("CurrentURIMetaData", "AVTransportURIMetaData")),
                new UpnpXml.Action("SetNextAVTransportURI", In("InstanceID", "A_ARG_TYPE_InstanceID"), In("NextURI", "NextAVTransportURI"), In("NextURIMetaData", "NextAVTransportURIMetaData")),
                new UpnpXml.Action("GetMediaInfo", In("InstanceID", "A_ARG_TYPE_InstanceID"), Out("NrTracks", "NumberOfTracks"), Out("MediaDuration", "CurrentMediaDuration"),
                    Out("CurrentURI", "AVTransportURI"), Out("CurrentURIMetaData", "AVTransportURIMetaData"), Out("NextURI", "NextAVTransportURI"), Out("NextURIMetaData", "NextAVTransportURIMetaData"),
                    Out("PlayMedium", "PlaybackStorageMedium"), Out("RecordMedium", "RecordStorageMedium"), Out("WriteStatus", "RecordMediumWriteStatus")),
                new UpnpXml.Action("GetTransportInfo", In("InstanceID", "A_ARG_TYPE_InstanceID"), Out("CurrentTransportState", "TransportState"), Out("CurrentTransportStatus", "TransportStatus"), Out("CurrentSpeed", "TransportPlaySpeed")),
                new UpnpXml.Action("GetPositionInfo", In("InstanceID", "A_ARG_TYPE_InstanceID"), Out("Track", "CurrentTrack"), Out("TrackDuration", "CurrentTrackDuration"),
                    Out("TrackMetaData", "CurrentTrackMetaData"), Out("TrackURI", "CurrentTrackURI"), Out("RelTime", "RelativeTimePosition"), Out("AbsTime", "AbsoluteTimePosition"),
                    Out("RelCount", "RelativeCounterPosition"), Out("AbsCount", "AbsoluteCounterPosition")),
                new UpnpXml.Action("GetDeviceCapabilities", In("InstanceID", "A_ARG_TYPE_InstanceID"), Out("PlayMedia", "PossiblePlaybackStorageMedia"), Out("RecMedia", "PossibleRecordStorageMedia"), Out("RecQualityModes", "PossibleRecordQualityModes")),
                new UpnpXml.Action("GetTransportSettings", In("InstanceID", "A_ARG_TYPE_InstanceID"), Out("PlayMode", "CurrentPlayMode"), Out("RecQualityMode", "CurrentRecordQualityMode")),
                new UpnpXml.Action("GetCurrentTransportActions", In("InstanceID", "A_ARG_TYPE_InstanceID"), Out("Actions", "CurrentTransportActions")),
                new UpnpXml.Action("Stop", In("InstanceID", "A_ARG_TYPE_InstanceID")),
                new UpnpXml.Action("Play", In("InstanceID", "A_ARG_TYPE_InstanceID"), In("Speed", "TransportPlaySpeed")),
                new UpnpXml.Action("Pause", In("InstanceID", "A_ARG_TYPE_InstanceID")),
                new UpnpXml.Action("Seek", In("InstanceID", "A_ARG_TYPE_InstanceID"), In("Unit", "A_ARG_TYPE_SeekMode"), In("Target", "A_ARG_TYPE_SeekTarget")),
                new UpnpXml.Action("Next", In("InstanceID", "A_ARG_TYPE_InstanceID")),
                new UpnpXml.Action("Previous", In("InstanceID", "A_ARG_TYPE_InstanceID")),
            },
            new[]
            {
                new StateVar("TransportState", "string", false, new[] { "STOPPED", "PLAYING", "PAUSED_PLAYBACK", "TRANSITIONING", "NO_MEDIA_PRESENT" }),
                new StateVar("TransportStatus", "string", false, new[] { "OK", "ERROR_OCCURRED" }),
                new StateVar("PlaybackStorageMedium", "string", false, new[] { "NONE", "NETWORK" }),
                new StateVar("RecordStorageMedium", "string", false, new[] { "NOT_IMPLEMENTED" }),
                new StateVar("PossiblePlaybackStorageMedia", "string"),
                new StateVar("PossibleRecordStorageMedia", "string"),
                new StateVar("CurrentPlayMode", "string", false, new[] { "NORMAL" }),
                new StateVar("TransportPlaySpeed", "string", false, new[] { "1" }),
                new StateVar("RecordMediumWriteStatus", "string", false, new[] { "NOT_IMPLEMENTED" }),
                new StateVar("CurrentRecordQualityMode", "string", false, new[] { "NOT_IMPLEMENTED" }),
                new StateVar("PossibleRecordQualityModes", "string"),
                new StateVar("NumberOfTracks", "ui4", false, null, (0, 1, 1)),
                new StateVar("CurrentTrack", "ui4", false, null, (0, 1, 1)),
                new StateVar("CurrentTrackDuration", "string"),
                new StateVar("CurrentMediaDuration", "string"),
                new StateVar("CurrentTrackMetaData", "string"),
                new StateVar("CurrentTrackURI", "string"),
                new StateVar("AVTransportURI", "string"),
                new StateVar("AVTransportURIMetaData", "string"),
                new StateVar("NextAVTransportURI", "string"),
                new StateVar("NextAVTransportURIMetaData", "string"),
                new StateVar("RelativeTimePosition", "string"),
                new StateVar("AbsoluteTimePosition", "string"),
                new StateVar("RelativeCounterPosition", "i4"),
                new StateVar("AbsoluteCounterPosition", "i4"),
                new StateVar("CurrentTransportActions", "string"),
                new StateVar("LastChange", "string", true),
                new StateVar("A_ARG_TYPE_SeekMode", "string", false, new[] { "REL_TIME", "ABS_TIME", "TRACK_NR" }),
                new StateVar("A_ARG_TYPE_SeekTarget", "string"),
                new StateVar("A_ARG_TYPE_InstanceID", "ui4"),
            });

        public static readonly string RenderingControlScpd = Scpd(
            new[]
            {
                new UpnpXml.Action("ListPresets", In("InstanceID", "A_ARG_TYPE_InstanceID"), Out("CurrentPresetNameList", "PresetNameList")),
                new UpnpXml.Action("SelectPreset", In("InstanceID", "A_ARG_TYPE_InstanceID"), In("PresetName", "A_ARG_TYPE_PresetName")),
                new UpnpXml.Action("GetMute", In("InstanceID", "A_ARG_TYPE_InstanceID"), In("Channel", "A_ARG_TYPE_Channel"), Out("CurrentMute", "Mute")),
                new UpnpXml.Action("SetMute", In("InstanceID", "A_ARG_TYPE_InstanceID"), In("Channel", "A_ARG_TYPE_Channel"), In("DesiredMute", "Mute")),
                new UpnpXml.Action("GetVolume", In("InstanceID", "A_ARG_TYPE_InstanceID"), In("Channel", "A_ARG_TYPE_Channel"), Out("CurrentVolume", "Volume")),
                new UpnpXml.Action("SetVolume", In("InstanceID", "A_ARG_TYPE_InstanceID"), In("Channel", "A_ARG_TYPE_Channel"), In("DesiredVolume", "Volume")),
            },
            new[]
            {
                new StateVar("PresetNameList", "string"),
                new StateVar("LastChange", "string", true),
                new StateVar("Mute", "boolean"),
                new StateVar("Volume", "ui2", false, null, (0, 100, 1)),
                new StateVar("A_ARG_TYPE_Channel", "string", false, new[] { "Master" }),
                new StateVar("A_ARG_TYPE_InstanceID", "ui4"),
                new StateVar("A_ARG_TYPE_PresetName", "string", false, new[] { "FactoryDefaults" }),
            });

        public static readonly string ConnectionManagerScpd = Scpd(
            new[]
            {
                new UpnpXml.Action("GetProtocolInfo", Out("Source", "SourceProtocolInfo"), Out("Sink", "SinkProtocolInfo")),
                new UpnpXml.Action("GetCurrentConnectionIDs", Out("ConnectionIDs", "CurrentConnectionIDs")),
                new UpnpXml.Action("GetCurrentConnectionInfo", In("ConnectionID", "A_ARG_TYPE_ConnectionID"), Out("RcsID", "A_ARG_TYPE_RcsID"), Out("AVTransportID", "A_ARG_TYPE_AVTransportID"),
                    Out("ProtocolInfo", "A_ARG_TYPE_ProtocolInfo"), Out("PeerConnectionManager", "A_ARG_TYPE_ConnectionManager"), Out("PeerConnectionID", "A_ARG_TYPE_ConnectionID"),
                    Out("Direction", "A_ARG_TYPE_Direction"), Out("Status", "A_ARG_TYPE_ConnectionStatus")),
            },
            new[]
            {
                new StateVar("SourceProtocolInfo", "string", true),
                new StateVar("SinkProtocolInfo", "string", true),
                new StateVar("CurrentConnectionIDs", "string", true),
                new StateVar("A_ARG_TYPE_ConnectionStatus", "string", false, new[] { "OK", "ContentFormatMismatch", "InsufficientBandwidth", "UnreliableChannel", "Unknown" }),
                new StateVar("A_ARG_TYPE_ConnectionManager", "string"),
                new StateVar("A_ARG_TYPE_Direction", "string", false, new[] { "Input", "Output" }),
                new StateVar("A_ARG_TYPE_ProtocolInfo", "string"),
                new StateVar("A_ARG_TYPE_ConnectionID", "i4"),
                new StateVar("A_ARG_TYPE_AVTransportID", "i4"),
                new StateVar("A_ARG_TYPE_RcsID", "i4"),
            });
    }
}
