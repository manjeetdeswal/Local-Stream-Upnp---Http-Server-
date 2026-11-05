using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using LocalStreamPC.Services;
using Microsoft.AspNetCore.Http;

namespace LocalStreamPC.Server
{
    public sealed record ActivityEntry(DateTime Time, string Who, string Device, string Action, string Item, string? Detail);

    /// <summary>
    /// Who watched, downloaded, uploaded or changed what, and when (the dashboard's History page).
    /// Kept on disk (the last few thousand entries).
    /// </summary>
    public sealed class ActivityLog
    {
        private const int MaxEntries = 3000;
        private readonly object _lock = new();
        private readonly List<ActivityEntry> _entries = new();
        private readonly Timer _saveTimer;
        private bool _dirty;

        public event Action? Changed;

        public ActivityLog()
        {
            try
            {
                if (File.Exists(FilePath))
                    _entries.AddRange(JsonSerializer.Deserialize<List<ActivityEntry>>(File.ReadAllText(FilePath)) ?? new());
            }
            catch { }
            _saveTimer = new Timer(_ => Save(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        }

        private static string FilePath => Path.Combine(AppPaths.DataDir, "activity.json");

        public void Add(string? who, string action, string item, HttpContext? ctx, string? detail = null)
        {
            string device = ctx == null ? "" : StreamTracker.DescribeDevice(ctx.Request.Headers.UserAgent.ToString(),
                ctx.Connection.RemoteIpAddress is { } ip ? (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString()) : "?");
            Add(new ActivityEntry(DateTime.UtcNow, who ?? "", device, action, item, detail));
        }

        public void Add(ActivityEntry entry)
        {
            lock (_lock)
            {
                _entries.Add(entry);
                if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
                _dirty = true;
            }
            Changed?.Invoke();
        }

        /// <summary>Newest first.</summary>
        public IReadOnlyList<ActivityEntry> Recent(int max = 500)
        {
            lock (_lock) return _entries.AsEnumerable().Reverse().Take(max).ToList();
        }

        public void Clear()
        {
            lock (_lock) { _entries.Clear(); _dirty = true; }
            Save();
            Changed?.Invoke();
        }

        public void Save()
        {
            string? json = null;
            lock (_lock)
            {
                if (!_dirty) return;
                _dirty = false;
                json = JsonSerializer.Serialize(_entries);
            }
            try
            {
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch { lock (_lock) _dirty = true; }
        }
    }
}
