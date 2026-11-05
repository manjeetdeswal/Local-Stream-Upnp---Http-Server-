using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LocalStreamPC.Models
{
    public class PlaybackHistory
    {
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public string? ThumbnailUrl { get; set; }
        public double Progress { get; set; }
        public DateTime LastPlayed { get; set; }
        public string ServerName { get; set; } = "";
    }

    /// <summary>A web login. Admins can change files; everyone else can only browse and play.</summary>
    public class UserAccount
    {
        public string Username { get; set; } = "";
        /// <summary>PBKDF2 hash ("pbkdf2$iterations$salt$hash"). Plain-text passwords are never stored.</summary>
        public string? PasswordHash { get; set; }
        public bool IsAdmin { get; set; }
        /// <summary>True: sees every shared folder (also ones added later). False: only <see cref="Folders"/>.</summary>
        public bool AllFolders { get; set; } = true;
        public List<string> Folders { get; set; } = new();
        /// <summary>May upload files and make folders (but not delete, move or copy). Admins always can.</summary>
        public bool CanUpload { get; set; }
        /// <summary>May create share links. Admins always can.</summary>
        public bool CanShare { get; set; }
    }

    /// <summary>A link that opens one file or folder without an account, until it expires.</summary>
    public class ShareLink
    {
        public string Token { get; set; } = "";
        public string Path { get; set; } = "";
        public bool IsFolder { get; set; }
        public string CreatedBy { get; set; } = "";
        public DateTime Created { get; set; }
        public DateTime Expires { get; set; }
        public int Opens { get; set; }
    }

    /// <summary>Where a cast video was left, so casting it again resumes there.</summary>
    public class ResumePoint
    {
        public string Key { get; set; } = "";
        public double Seconds { get; set; }
        public double Duration { get; set; }
        public DateTime Updated { get; set; }
    }

    public class AppConfig
    {
        public string ServerUuid { get; set; } = Guid.NewGuid().ToString();
        public List<string> SharedFolders { get; set; } = new();
        public int Port { get; set; } = 8082;
        public bool RunAtStartup { get; set; } = false;
        public bool MinimizeToTray { get; set; } = false;
        public bool AutoStartServer { get; set; } = true;
        /// <summary>Sidebar tabs in the order the user chose (tags such as "TabLibrary"). Empty: the default order.</summary>
        public List<string> TabOrder { get; set; } = new();
        /// <summary>Bumped when a new version changes a default for everyone (1: auto-start the server).</summary>
        public int SettingsRevision { get; set; }
        public bool IsDarkMode { get; set; } = true;

        // Secure by default for new installs. Existing configs keep whatever the user chose.
        public bool EnableAuth { get; set; } = true;
        public bool AllowDeletion { get; set; } = false;

        // TVs and DLNA players can't log in. When this is on they may stream shared media
        // without a password, but only from private (LAN) addresses.
        public bool AllowDlnaWithoutLogin { get; set; } = true;

        /// <summary>Web logins (v1.3+). Older configs had exactly one admin and one viewer; they are moved here.</summary>
        public List<UserAccount> Users { get; set; } = new();

        // Legacy single admin/viewer logins (v1.0-1.2). Read once for migration into Users, then cleared.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? AdminUsername { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ViewerUsername { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? AdminPasswordHash { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ViewerPasswordHash { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? AdminPassword { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ViewerPassword { get; set; }

        // HTTPS (in addition to http, which TVs need). Without a certificate file a self-signed one is made.
        public bool EnableHttps { get; set; } = false;
        public int HttpsPort { get; set; } = 8443;
        /// <summary>Optional .pfx, or .crt/.pem (with <see cref="HttpsKeyPath"/>), e.g. from "tailscale cert".</summary>
        public string? HttpsCertPath { get; set; }
        public string? HttpsKeyPath { get; set; }
        public string? HttpsCertPassword { get; set; }

        public List<ShareLink> Shares { get; set; } = new();

        /// <summary>Speed limit for each stream in Mbit/s (0 = no limit).</summary>
        public int StreamSpeedLimitMbps { get; set; } = 0;

        /// <summary>Convert with the graphics card (NVIDIA / Intel / AMD) when one works.</summary>
        public bool UseHardwareEncoding { get; set; } = true;

        /// <summary>themoviedb.org API key (v3) or read access token, for posters and descriptions.</summary>
        public string? TmdbApiKey { get; set; }
        /// <summary>opensubtitles.com API key, for "Find subtitles".</summary>
        public string? OpenSubtitlesApiKey { get; set; }
        /// <summary>Preferred subtitle languages, e.g. "en" or "hi,en".</summary>
        public string SubtitleLanguages { get; set; } = "en";
        /// <summary>Subtitle size in the cast player, percent.</summary>
        public int CastSubtitleScale { get; set; } = 100;

        /// <summary>Folders inside the shared folders that the library (Music, Movies, Photos, search) leaves out.</summary>
        public List<string> ExcludedFolders { get; set; } = new();
        /// <summary>Leave out programming project folders (bin, obj, build, node_modules, .git…).</summary>
        public bool SkipBuildFolders { get; set; } = true;
        /// <summary>Look up missing album covers on the Cover Art Archive (MusicBrainz).</summary>
        public bool FindCoversOnline { get; set; } = true;

        /// <summary>Stops the PC from going to sleep while something is streaming, converting or casting.</summary>
        public bool KeepAwakeWhileStreaming { get; set; } = true;

        /// <summary>Secret for signed media links handed to TVs ("Play on TV"), so they work without a login.</summary>
        public string? MediaLinkSecret { get; set; }

        // Cast receiver: other devices can cast media to this PC (DLNA/UPnP MediaRenderer).
        public bool CastReceiverEnabled { get; set; } = false; // remembered between launches
        public bool CastFullscreen { get; set; } = true;
        /// <summary>Play casts in LocalStream's own player window (LibVLC). Off = external VLC window.</summary>
        public bool UseBuiltInPlayer { get; set; } = true;
        public int CastPort { get; set; } = 8083;
        public string RendererUuid { get; set; } = Guid.NewGuid().ToString();
        /// <summary>Optional full path to vlc(.exe) when it isn't installed in the usual place.</summary>
        public string? VlcPath { get; set; }
        /// <summary>Continue cast videos where they were left.</summary>
        public bool CastResume { get; set; } = true;
        public List<ResumePoint> CastResumePoints { get; set; } = new();
        /// <summary>Seconds each photo stays up in a cast slideshow.</summary>
        public int SlideshowSeconds { get; set; } = 6;

        public int GridSizeIndex { get; set; } = 2;
        public bool IsListView { get; set; } = false;

        public List<PlaybackHistory> History { get; set; } = new();

        /// <summary>Last update version the user chose to skip.</summary>
        public string? SkippedUpdate { get; set; }
    }
}
