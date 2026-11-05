using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LocalStreamPC.Services;

namespace LocalStreamPC.Server
{
    public enum EntryKind { Folder, Video, Audio, Image, Other }

    public sealed record LibraryEntry(string Root, string Path, string Name, long Size, DateTime Added, EntryKind Kind);

    public sealed record TrackTags(string Title, string Artist, string Album, uint Track, uint Disc, uint Year);

    /// <summary>
    /// A list of every file in the shared folders, refreshed in the background, for searching
    /// across all folders, "Recently added" and the music section (artist / album from tags).
    /// </summary>
    public sealed class LibraryIndex : IDisposable
    {
        private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);
        // Media files are always indexed (up to a very large number); folders and other files have a
        // budget shared fairly between the shared folders, so one huge drive (like a whole "E:\") can't
        // crowd the others out.
        private const int MaxMedia = 1_500_000;
        private static readonly int MaxOther = int.TryParse(Environment.GetEnvironmentVariable("LOCALSTREAM_INDEX_MAX_OTHER"), out int m) && m > 0 ? m : 400_000;

        private readonly ConfigService _config;
        private readonly AppLog _log;
        private readonly SemaphoreSlim _scanGate = new(1, 1);   // live updates
        private readonly object _startLock = new();
        private volatile IReadOnlyList<LibraryEntry> _entries = Array.Empty<LibraryEntry>();
        private volatile string _scannedRoots = "\0";
        private DateTime _scannedAt = DateTime.MinValue;
        private Task? _background;

        // Music tags, cached on disk so they are read only once per file version.
        private readonly ConcurrentDictionary<string, CachedTags> _tags = new(StringComparer.OrdinalIgnoreCase);
        private bool _tagsLoaded;
        private int _tagJobs;

        private sealed record CachedTags(long Size, DateTime Modified, TrackTags Tags);

        // Photo dates (EXIF "date taken"), also cached on disk.
        private readonly ConcurrentDictionary<string, CachedDate> _dates = new(StringComparer.OrdinalIgnoreCase);
        private bool _datesLoaded;
        private int _dateJobs;
        private volatile bool _scanning;
        private sealed record CachedDate(long Size, DateTime Modified, DateTime Taken);

        // Live updates: the shared folders are watched, so new files show up right away.
        private readonly List<FileSystemWatcher> _watchers = new();
        private string _watchedRoots = "\0";
        private readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);
        private readonly Timer _applyTimer;
        private long _version;

        public LibraryIndex(ConfigService config, AppLog log)
        {
            _config = config;
            _log = log;
            _applyTimer = new Timer(_ => ApplyChanges(), null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>Goes up whenever something in a shared folder changes (the web page polls it to refresh).</summary>
        public long Version => Interlocked.Read(ref _version);

        /// <summary>Raised (on a worker thread) after files were added, removed or renamed.</summary>
        public event Action? Changed;

        private static string DateCacheFile => Path.Combine(AppPaths.DataDir, "photo-dates.json");

        private static string TagCacheFile => Path.Combine(AppPaths.DataDir, "music-tags-v3.json"); // v3: names cleaned of site tags, legacy encodings fixed

        /// <summary>
        /// The current list. When the shared folders changed, waits a few seconds for the scan and then
        /// returns what is ready (the list keeps filling in, folder by folder); later calls refresh in the background.
        /// </summary>
        public async Task<IReadOnlyList<LibraryEntry>> GetAsync(CancellationToken ct = default)
        {
            string roots = string.Join("|", _config.SharedRoots);
            if (roots != _scannedRoots)
            {
                var scan = StartScan();
                await Task.WhenAny(scan, Task.Delay(TimeSpan.FromSeconds(5), ct));
            }
            else if (DateTime.UtcNow - _scannedAt > MaxAge) StartScan();
            return _entries;
        }

        private Task StartScan()
        {
            lock (_startLock)
            {
                if (_background == null || _background.IsCompleted) _background = Task.Run(ScanAsync);
                return _background;
            }
        }

        /// <summary>Call when files were added or removed through the web UI.</summary>
        public void Invalidate() => _scannedAt = DateTime.MinValue;

        /// <summary>What the last scan found in one shared folder (for the dashboard's Library card).</summary>
        public sealed record RootStat(string Root, int Videos, int Songs, int Photos, int Others, bool Reachable, string? Problem, DateTime Scanned, bool Scanning);

        private readonly ConcurrentDictionary<string, RootStat> _stats = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Per shared folder, in the order they are shared.</summary>
        public IReadOnlyList<RootStat> Stats => _config.SharedRoots.Select(r => _stats.TryGetValue(r, out var st) ? st
            : new RootStat(r, 0, 0, 0, 0, true, null, DateTime.MinValue, true)).ToList();

        /// <summary>Raised when a scan starts or a folder finishes (worker thread).</summary>
        public event Action? StatsChanged;

        /// <summary>True while a shared folder is still being read.</summary>
        public bool IsScanning => _scanning;

        public bool MusicTagsComplete => Volatile.Read(ref _tagJobs) == 0 && !_scanning;

        /// <summary>Every shared folder is read at the same time (they are often different drives), and each
        /// one appears in the list as soon as it is done.</summary>
        private async Task ScanAsync()
        {
            var rootsList = _config.SharedRoots.ToArray();
            string roots = string.Join("|", rootsList);
            if (roots == _scannedRoots && DateTime.UtcNow - _scannedAt < TimeSpan.FromSeconds(30)) return;
            bool rootsChanged = roots != _scannedRoots;
            _scanning = true;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                // Folders no longer shared disappear at once; the others keep their old list until rescanned.
                var perRoot = new ConcurrentDictionary<string, List<LibraryEntry>>(StringComparer.OrdinalIgnoreCase);
                foreach (var g in _entries.GroupBy(e => e.Root, StringComparer.OrdinalIgnoreCase))
                    if (rootsList.Contains(g.Key, StringComparer.OrdinalIgnoreCase)) perRoot[g.Key] = g.ToList();
                _scannedRoots = roots;
                Publish(rootsList, perRoot);
                Watch(rootsList);

                int mediaCount = 0;
                int otherBudget = MaxOther / Math.Max(1, rootsList.Length);
                foreach (var r in rootsList)
                    _stats[r] = (_stats.TryGetValue(r, out var old) ? old : new RootStat(r, 0, 0, 0, 0, true, null, DateTime.MinValue, true)) with { Scanning = true };
                StatsChanged?.Invoke();
                await Task.WhenAll(rootsList.Select(root => Task.Run(() =>
                {
                    var rootWatch = System.Diagnostics.Stopwatch.StartNew();
                    var list = ScanRoot(root, otherBudget, ref mediaCount, out string? problem);
                    if (problem != null) _log.Write(problem);
                    _stats[root] = list == null
                        ? new RootStat(root, 0, 0, 0, 0, false, problem, DateTime.Now, false)
                        : new RootStat(root, list.Count(e => e.Kind == EntryKind.Video), list.Count(e => e.Kind == EntryKind.Audio), list.Count(e => e.Kind == EntryKind.Image),
                                       list.Count(e => e.Kind == EntryKind.Other), true, problem, DateTime.Now, false);
                    StatsChanged?.Invoke();
                    if (list != null)
                    {
                        perRoot[root] = list;
                        Publish(rootsList, perRoot);
                        int media = list.Count(e => e.Kind is EntryKind.Video or EntryKind.Audio or EntryKind.Image);
                        if (rootsChanged || list.Count > 2000)
                            _log.Write($"Library: {root} - {media:N0} media files ({list.Count(e => e.Kind == EntryKind.Video):N0} videos, {list.Count(e => e.Kind == EntryKind.Audio):N0} songs, {list.Count(e => e.Kind == EntryKind.Image):N0} photos) in {rootWatch.Elapsed.TotalSeconds:0.0} s.");
                        StartTagJobs(list);
                    }
                    else perRoot.TryRemove(root, out _);
                })));
                Publish(rootsList, perRoot);
                _scannedAt = DateTime.UtcNow;
                if (_entries.Count > 2000 && rootsList.Length > 1) _log.Write($"Library: all {rootsList.Length} shared folders read ({_entries.Count:N0} items, {sw.Elapsed.TotalSeconds:0.0} s).");
            }
            catch (Exception ex) { _log.Write($"Library: scan failed ({ex.Message})."); }
            finally
            {
                _scanning = false;
                foreach (var (k, v) in _stats) if (v.Scanning) _stats[k] = v with { Scanning = false };
                foreach (var k in _stats.Keys.Where(k => !_config.SharedRoots.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList()) _stats.TryRemove(k, out _);
                StatsChanged?.Invoke();
            }
        }

        private void Publish(string[] roots, ConcurrentDictionary<string, List<LibraryEntry>> perRoot)
        {
            lock (perRoot)
            {
                // A folder shared twice (as "F:\" and "F:\songs") is listed once.
                _entries = roots.SelectMany(r => perRoot.TryGetValue(r, out var l) ? l : new List<LibraryEntry>())
                                .DistinctBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
                Interlocked.Increment(ref _version);
            }
        }

        private void StartTagJobs(IReadOnlyList<LibraryEntry> list)
        {
            Interlocked.Increment(ref _tagJobs);
            Interlocked.Increment(ref _dateJobs);
            _ = Task.Run(() => ReadMusicTags(list));
            _ = Task.Run(() => ReadPhotoDates(list));
        }

        /// <returns>The entries of one shared folder, or null when it can't be read at all.</returns>
        private List<LibraryEntry>? ScanRoot(string root, int otherBudget, ref int mediaCount, out string? problem)
        {
            problem = null;
            bool exists;
            try { exists = Directory.Exists(root); } catch { exists = false; }
            if (!exists)
            {
                bool network = root.StartsWith(@"\\") || (root.Length >= 2 && root[1] == ':' && IsNetworkDrive(root));
                problem = $"Library: {root} can't be opened, so it is left out." + (network || root.Length >= 2 && root[1] == ':' && !DriveExists(root)
                    ? " If it is a network or USB drive, make sure it is connected. (Network drives mapped in Windows are not visible to programs started \"as administrator\"; use the \\\\server\\share address instead.)" : "");
                return null;
            }
            var result = new List<LibraryEntry>();
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            };
            int others = 0, skippedFolders = 0;
            bool otherCapped = false, mediaCapped = false;
            // Walked by hand (not one recursive enumeration) so excluded folders are never even opened.
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(root));
            while (pending.Count > 0 && !mediaCapped)
            {
                var dir = pending.Pop();
                IEnumerable<FileSystemInfo> children;
                try { children = dir.EnumerateFileSystemInfos("*", options).ToList(); }
                catch { continue; } // unreadable folder
                foreach (var info in children)
                {
                    try
                    {
                        if (info is DirectoryInfo sub)
                        {
                            if (_config.IsExcludedFolder(sub.FullName, sub.Name)) { skippedFolders++; continue; }
                            pending.Push(sub);
                        }
                        if (EntryFor(root, info) is not { } e) continue; // (copying a file keeps its write time, so "added" is the later of the two)
                        if (e.Kind is EntryKind.Video or EntryKind.Audio or EntryKind.Image)
                        {
                            if (Interlocked.Increment(ref mediaCount) > MaxMedia) { problem = $"Library: more than {MaxMedia:N0} media files; the rest of {root} is left out."; mediaCapped = true; break; }
                            result.Add(e);
                        }
                        else if (others < otherBudget) { result.Add(e); others++; }
                        else otherCapped = true; // keep going: media further down still counts
                    }
                    catch { /* vanished while scanning */ }
                }
            }
            if (otherCapped && problem == null)
                problem = $"Library: {root} has a very large number of folders and other files; every media file is listed, but searching for other files there may miss some.";
            if (skippedFolders > 0) _log.Write($"Library: {root} - left out {skippedFolders:N0} program/excluded folder{(skippedFolders == 1 ? "" : "s")}.");
            return result;
        }

        /// <summary>True when the path is inside a folder the library leaves out.</summary>
        private bool IsInExcludedFolder(string root, string path)
        {
            string rootFull = Path.TrimEndingDirectorySeparator(root);
            for (var cur = new DirectoryInfo(path); cur != null && !Path.TrimEndingDirectorySeparator(cur.FullName).Equals(rootFull, StringComparison.OrdinalIgnoreCase); cur = cur.Parent)
                if (_config.IsExcludedFolder(cur.FullName, cur.Name)) return true;
            return false;
        }

        /// <summary>Rescan everything now (after the excluded folders changed, or from the dashboard).</summary>
        public Task RescanAsync()
        {
            _scannedRoots = "\0"; // treat like changed folders: read everything again
            return StartScan();
        }

        private static bool DriveExists(string root)
        {
            try { return new DriveInfo(root[..2]).IsReady; } catch { return false; }
        }

        private static bool IsNetworkDrive(string root)
        {
            try { return new DriveInfo(root[..2]).DriveType == DriveType.Network; } catch { return false; }
        }

        private static LibraryEntry? EntryFor(string root, FileSystemInfo info)
        {
            if (info is DirectoryInfo d) return new LibraryEntry(root, d.FullName, d.Name, 0, d.CreationTimeUtc, EntryKind.Folder);
            if (info is FileInfo f && !f.Name.EndsWith(".uploading", StringComparison.OrdinalIgnoreCase))
            {
                var added = f.CreationTimeUtc > f.LastWriteTimeUtc ? f.CreationTimeUtc : f.LastWriteTimeUtc;
                return new LibraryEntry(root, f.FullName, f.Name, f.Length, added, KindOf(f.Name));
            }
            return null;
        }

        // ------------------------------------------------------------ live updates

        private void Watch(string[] roots)
        {
            string key = string.Join("|", roots);
            lock (_watchers)
            {
                if (key == _watchedRoots) return;
                _watchedRoots = key;
                foreach (var w in _watchers) w.Dispose();
                _watchers.Clear();
                foreach (var root in roots)
                {
                    if (!Directory.Exists(root)) continue;
                    try
                    {
                        var w = new FileSystemWatcher(root)
                        {
                            IncludeSubdirectories = true,
                            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                            InternalBufferSize = 64 * 1024,
                        };
                        w.Created += (_, e) => Queue(e.FullPath);
                        w.Deleted += (_, e) => Queue(e.FullPath);
                        w.Changed += (_, e) => Queue(e.FullPath);
                        w.Renamed += (_, e) => { Queue(e.OldFullPath); Queue(e.FullPath); };
                        w.Error += (_, _) => { Invalidate(); StartScan(); }; // too many changes at once: rescan
                        w.EnableRaisingEvents = true;
                        _watchers.Add(w);
                    }
                    catch (Exception ex) { _log.Write($"Library: can't watch {root} for new files ({ex.Message})."); }
                }
            }
        }

        private void Queue(string path)
        {
            lock (_changed) _changed.Add(path);
            _applyTimer.Change(1200, Timeout.Infinite); // wait for a burst (a copy of many files) to settle
        }

        private void ApplyChanges()
        {
            string[] paths;
            lock (_changed) { paths = _changed.ToArray(); _changed.Clear(); }
            if (paths.Length == 0) return;
            if (paths.Length > 5000) { Invalidate(); StartScan(); return; }
            try
            {
                _scanGate.Wait();
                try
                {
                    var roots = _config.SharedRoots.ToArray();
                    var byPath = _entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
                    bool any = false;
                    var added = new List<LibraryEntry>();
                    foreach (var path in paths)
                    {
                        string? root = roots.FirstOrDefault(r => path.StartsWith(r.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                        if (root == null) continue;
                        // Gone (or replaced): drop it and, for a folder, everything inside.
                        string prefix = path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        foreach (var k in byPath.Keys.Where(k => k.Equals(path, StringComparison.OrdinalIgnoreCase) || k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                        { byPath.Remove(k); any = true; }
                        if (IsHiddenPath(root, path) || IsInExcludedFolder(root, path)) continue;
                        if (File.Exists(path))
                        {
                            if (EntryFor(root, new FileInfo(path)) is { } e) { byPath[e.Path] = e; added.Add(e); any = true; }
                        }
                        else if (Directory.Exists(path))
                        {
                            var dir = new DirectoryInfo(path);
                            if (EntryFor(root, dir) is { } d) { byPath[d.Path] = d; any = true; }
                            try
                            {
                                foreach (var info in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint }))
                                    if (!IsInExcludedFolder(root, info is DirectoryInfo ? info.FullName : Path.GetDirectoryName(info.FullName)!) && EntryFor(root, info) is { } e) { byPath[e.Path] = e; added.Add(e); }
                            }
                            catch { }
                        }
                    }
                    if (!any) return;
                    _entries = byPath.Values.ToList();
                    Interlocked.Increment(ref _version);
                    if (added.Count > 0)
                    {
                        StartTagJobs(added);
                    }
                }
                finally { _scanGate.Release(); }
                Changed?.Invoke();
            }
            catch (Exception ex) { _log.Write($"Library: update failed ({ex.Message})."); }
        }

        /// <summary>Hidden/system files and folders are left out of the index, as in a full scan.</summary>
        private static bool IsHiddenPath(string root, string path)
        {
            try
            {
                var p = new DirectoryInfo(path);
                string rootFull = Path.TrimEndingDirectorySeparator(root);
                for (var cur = p; cur != null && !Path.TrimEndingDirectorySeparator(cur.FullName).Equals(rootFull, StringComparison.OrdinalIgnoreCase); cur = cur.Parent)
                {
                    if (!cur.Exists && !File.Exists(cur.FullName)) continue;
                    var attrs = File.GetAttributes(cur.FullName);
                    if ((attrs & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) return true;
                }
            }
            catch { }
            return false;
        }

        public void Dispose()
        {
            lock (_watchers) { foreach (var w in _watchers) w.Dispose(); _watchers.Clear(); }
            _applyTimer.Dispose();
        }

        public static EntryKind KindOf(string name)
        {
            string ext = Path.GetExtension(name);
            return MediaTypes.Video.Contains(ext) ? EntryKind.Video
                 : MediaTypes.Audio.Contains(ext) ? EntryKind.Audio
                 : MediaTypes.Image.Contains(ext) ? EntryKind.Image
                 : EntryKind.Other;
        }

        // ------------------------------------------------------------ queries

        /// <summary>Entries whose name contains every word of the query, best matches first.</summary>
        public static List<LibraryEntry> Search(IEnumerable<LibraryEntry> entries, string query, int max, out bool truncated)
        {
            string q = Normalize(query);
            var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            truncated = false;
            if (words.Length == 0) return new List<LibraryEntry>();

            var matches = new List<(LibraryEntry Entry, int Score)>();
            foreach (var e in entries)
            {
                string name = Normalize(e.Name);
                if (!words.All(name.Contains)) continue;
                int score = name.StartsWith(q) ? 0 : name.Contains(q) ? 1 : 2;
                if (e.Kind == EntryKind.Other) score += 3; // media first
                matches.Add((e, score));
                if (matches.Count > 5000) { truncated = true; break; }
            }
            var sorted = matches.OrderBy(m => m.Score).ThenBy(m => m.Entry.Name.Length).ThenBy(m => m.Entry.Name, StringComparer.CurrentCultureIgnoreCase)
                                .Select(m => m.Entry).ToList();
            if (sorted.Count > max) { truncated = true; sorted = sorted.Take(max).ToList(); }
            return sorted;
        }

        private static string Normalize(string s)
        {
            var chars = s.ToLowerInvariant().Select(c => c is '.' or '_' or '-' or '(' or ')' or '[' or ']' ? ' ' : c).ToArray();
            return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        public static List<LibraryEntry> RecentlyAdded(IEnumerable<LibraryEntry> entries, int max)
            => entries.Where(e => e.Kind is EntryKind.Video or EntryKind.Audio or EntryKind.Image)
                      .OrderByDescending(e => e.Added).Take(max).ToList();

        public TrackTags TagsForPath(string path)
            => _tags.TryGetValue(path, out var t) ? t.Tags : FromFileName(path, null, null, null, 0, 0, 0);

        public TrackTags TagsFor(LibraryEntry e)
            => _tags.TryGetValue(e.Path, out var t) ? t.Tags : FromFileName(e.Path, null, null, null, 0, 0, 0);

        // ------------------------------------------------------------ photo dates

        public bool PhotoDatesComplete => Volatile.Read(ref _dateJobs) == 0 && !_scanning;

        /// <summary>When the photo was taken (EXIF), else when the file was made.</summary>
        public DateTime DateTakenFor(LibraryEntry e)
            => _dates.TryGetValue(e.Path, out var d) ? d.Taken : e.Added.ToLocalTime();

        private void ReadPhotoDates(IReadOnlyList<LibraryEntry> entries)
        {
            try
            {
                LoadDateCache();
                int read = 0;
                foreach (var e in entries)
                {
                    if (e.Kind != EntryKind.Image) continue;
                    FileInfo f;
                    try { f = new FileInfo(e.Path); if (!f.Exists) continue; } catch { continue; }
                    if (_dates.TryGetValue(e.Path, out var cached) && cached.Size == f.Length && cached.Modified == f.LastWriteTimeUtc) continue;
                    _dates[e.Path] = new CachedDate(f.Length, f.LastWriteTimeUtc, ReadDateTaken(f) ?? e.Added.ToLocalTime());
                    if (++read % 500 == 0) SaveDateCache();
                }
                if (read > 0) SaveDateCache();
            }
            catch (Exception ex) { _log.Write($"Library: could not read photo dates ({ex.Message})."); }
            finally { Interlocked.Decrement(ref _dateJobs); }
        }

        internal static DateTime? ReadDateTaken(FileInfo f)
        {
            try
            {
                using var file = TagLib.File.Create(f.FullName);
                if (file is TagLib.Image.File image && image.ImageTag?.DateTime is DateTime taken && taken.Year > 1900) return taken;
            }
            catch { }
            return null;
        }

        private void LoadDateCache()
        {
            if (_datesLoaded) return;
            _datesLoaded = true;
            try
            {
                if (!File.Exists(DateCacheFile)) return;
                var data = JsonSerializer.Deserialize<Dictionary<string, CachedDate>>(File.ReadAllText(DateCacheFile));
                if (data != null) foreach (var (k, v) in data) _dates[k] = v;
            }
            catch { }
        }

        private void SaveDateCache()
        {
            lock (_dates)
            {
                try
                {
                    string tmp = DateCacheFile + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(_dates.ToDictionary(k => k.Key, v => v.Value)));
                    File.Move(tmp, DateCacheFile, overwrite: true);
                }
                catch { }
            }
        }

        // ------------------------------------------------------------ music tags

        private void ReadMusicTags(IReadOnlyList<LibraryEntry> entries)
        {
            try
            {
                LoadTagCache();
                int read = 0;
                foreach (var e in entries)
                {
                    if (e.Kind != EntryKind.Audio) continue;
                    FileInfo f;
                    try { f = new FileInfo(e.Path); if (!f.Exists) continue; } catch { continue; }
                    if (_tags.TryGetValue(e.Path, out var cached) && cached.Size == f.Length && cached.Modified == f.LastWriteTimeUtc) continue;
                    _tags[e.Path] = new CachedTags(f.Length, f.LastWriteTimeUtc, ReadTags(f));
                    if (++read % 500 == 0) SaveTagCache();
                }
                if (read > 0) SaveTagCache();
            }
            catch (Exception ex) { _log.Write($"Library: could not read music tags ({ex.Message})."); }
            finally { Interlocked.Decrement(ref _tagJobs); }
        }

        private static TrackTags ReadTags(FileInfo f)
        {
            try
            {
                using var file = TagLib.File.Create(f.FullName, TagLib.ReadStyle.None);
                var t = file.Tag;
                string? artist = MusicNames.Clean(t.FirstAlbumArtist) ?? MusicNames.Clean(t.FirstPerformer);
                return FromFileName(f.FullName, MusicNames.Clean(t.Title), artist, MusicNames.Clean(t.Album), t.Track, t.Disc, t.Year);
            }
            catch
            {
                return FromFileName(f.FullName, null, null, null, 0, 0, 0);
            }
        }

        /// <summary>Fills what the tags don't say from the file and folder names ("02 - Artist - Song.mp3").</summary>
        private static TrackTags FromFileName(string path, string? title, string? artist, string? album, uint track, uint disc, uint year)
        {
            var (fileArtist, fileTitle, fileTrack) = MusicNames.FromFile(Path.GetFileNameWithoutExtension(path));
            string folder = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
            return new TrackTags(title ?? fileTitle, artist ?? fileArtist ?? "Unknown artist",
                                 album ?? MusicNames.Clean(folder) ?? "Unknown album", track > 0 ? track : fileTrack, disc, year);
        }



        private void LoadTagCache()
        {
            if (_tagsLoaded) return;
            _tagsLoaded = true;
            try
            {
                if (!File.Exists(TagCacheFile)) return;
                var data = JsonSerializer.Deserialize<Dictionary<string, CachedTags>>(File.ReadAllText(TagCacheFile));
                if (data != null) foreach (var (k, v) in data) _tags[k] = v;
            }
            catch { /* rebuilt on the fly */ }
        }

        private void SaveTagCache()
        {
            lock (_tags)
            {
                try
                {
                    string tmp = TagCacheFile + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(_tags.ToDictionary(k => k.Key, v => v.Value)));
                    File.Move(tmp, TagCacheFile, overwrite: true);
                }
                catch { }
            }
        }
    }

    /// <summary>Where each person stopped watching each file ("Continue watching" and resume).</summary>
    public sealed class WatchHistory
    {
        public sealed record Point(double Position, double Duration, DateTime Updated);

        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Point>> _byUser = new();
        private readonly Timer _saveTimer;
        private int _dirty;

        public WatchHistory()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Point>>>(File.ReadAllText(FilePath));
                    if (data != null)
                        foreach (var (user, points) in data)
                            _byUser[user] = new ConcurrentDictionary<string, Point>(points, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch { }
            _saveTimer = new Timer(_ => SaveIfDirty(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }

        private static string FilePath => Path.Combine(AppPaths.DataDir, "watch-history.json");

        public void Report(string user, string path, double position, double duration)
        {
            var points = _byUser.GetOrAdd(user, _ => new ConcurrentDictionary<string, Point>(StringComparer.OrdinalIgnoreCase));
            bool finished = IsFinished(position, duration);
            if (finished || position < 5) points.TryRemove(path, out _);
            else points[path] = new Point(position, duration, DateTime.UtcNow);
            if (points.Count > 300)
                foreach (var old in points.OrderBy(p => p.Value.Updated).Take(points.Count - 300).ToList()) points.TryRemove(old.Key, out _);
            Interlocked.Exchange(ref _dirty, 1);
        }

        /// <summary>Finished: the last 5 %, or (for anything longer than 5 minutes) the end credits.</summary>
        public static bool IsFinished(double position, double duration)
            => duration > 0 && (position > duration * 0.95 || (duration > 300 && duration - position < 45));

        public Point? Get(string user, string path)
            => _byUser.TryGetValue(user, out var p) && p.TryGetValue(path, out var point) ? point : null;

        public IEnumerable<(string Path, Point Point)> Recent(string user)
            => _byUser.TryGetValue(user, out var p)
                ? p.OrderByDescending(x => x.Value.Updated).Where(x => DateTime.UtcNow - x.Value.Updated < TimeSpan.FromDays(90)).Select(x => (x.Key, x.Value))
                : Enumerable.Empty<(string, Point)>();

        public void Remove(string user, string path)
        {
            if (_byUser.TryGetValue(user, out var p) && p.TryRemove(path, out _)) Interlocked.Exchange(ref _dirty, 1);
        }

        private void SaveIfDirty()
        {
            if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
            try
            {
                var data = _byUser.ToDictionary(u => u.Key, u => u.Value.ToDictionary(p => p.Key, p => p.Value));
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(data));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch { Interlocked.Exchange(ref _dirty, 1); }
        }

        public void Flush() => SaveIfDirty();
    }

    /// <summary>
    /// Cleans song names: download sites stamp their name into tags and file names
    /// ("Kangna_SpotiDost", "www.350c.com", "Song (DJPunjab.Com)"), and some tools write themselves as the artist.
    /// </summary>
    public static class MusicNames
    {
        private static readonly System.Text.RegularExpressions.Regex Site = new(
            @"[\(\[\{]?\s*(?:https?://)?(?:www\.)?[a-z0-9-]+(?:\.[a-z0-9-]+)*\.(?:com|net|org|in|pk|info|co|me|io|cc|to|xyz|club|site|online|fm|biz|ws|us|uk|ru|tv|live|link|top|pro|mobi)\b\s*[\)\]\}]?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex Sites = new(
            @"[\(\[\{]?\s*\b(spoti ?dost|pagal ?world|pagal ?free|mr[- ]?jatt|dj ?punjab|songs ?pk|dj ?maza|wap ?king|mp3 ?mad|raag ?fm|downloadming|web ?music|mass ?tamilan|star ?musiq|dj ?johal|risky ?jatt|jatt ?jugadu|mp3 ?skull|sound ?cloud|ytmp3|y2mate|savefrom|\d{2,3} ?kbps)\b\s*[\)\]\}]?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex Tools = new(
            @"^(adobe systems( incorporated)?|adobe|microsoft( corporation)?|apple( computer| inc\.?)?|sony creative software|lame|lavf.*|audacity|itunes|windows media player|fl studio|nero|vlc|ffmpeg|encoded by.*)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        // "002 Kangna", "15 - Song", "3. Song" are track numbers; "7 Rings" and "99 Problems" are titles.
        private static readonly System.Text.RegularExpressions.Regex LeadingTrack = new(@"^\s*(?:cd\s?\d\s?)?(?:(0\d{1,2})\s+|(\d{1,3})\s*[-.)]\s*)(?=\S)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        static MusicNames()
        {
            try { System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); } catch { }
        }

        /// <summary>
        /// Old tags are often in a Chinese, Cyrillic or other legacy code page but read as Latin-1, which
        /// shows as "ÐÔ¼òµ¥". Such text is turned back into bytes and decoded properly; null when it
        /// can't be repaired (the file name is used instead).
        /// </summary>
        public static string? FixEncoding(string? value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            if (value.Any(c => c > 0xFF)) return value; // real Unicode already
            int high = value.Count(c => c >= 0x80);
            if (high == 0) return value;
            int letters = value.Count(char.IsLetter);
            // A few accents ("Beyoncé", "Sigur Rós") are fine; mostly accented letters are not.
            if (high < 3 || high < letters * 0.3) return value;
            byte[] bytes = System.Text.Encoding.Latin1.GetBytes(value);
            foreach (var name in new[] { "utf-8", "gb18030", "big5", "shift_jis", "windows-1251", "euc-kr" })
            {
                try
                {
                    var enc = System.Text.Encoding.GetEncoding(name, System.Text.EncoderFallback.ExceptionFallback, System.Text.DecoderFallback.ExceptionFallback);
                    string decoded = enc.GetString(bytes);
                    if (decoded.Any(char.IsControl)) continue;
                    // The right code page gives real letters of one script, not more Latin-1 noise.
                    if (decoded.Count(c => c >= 0x80 && c <= 0xFF) > 0 && name != "windows-1251") continue;
                    return decoded;
                }
                catch { }
            }
            return null;
        }

        /// <summary>The name without site stamps, or null when nothing real is left.</summary>
        public static string? Clean(string? value)
        {
            value = FixEncoding(value);
            if (string.IsNullOrWhiteSpace(value)) return null;
            string v = value.Replace('\0', ' ').Trim();
            v = Site.Replace(v, " ");
            v = Sites.Replace(v, " ");
            v = System.Text.RegularExpressions.Regex.Replace(v, @"[\(\[\{]\s*[\)\]\}]", " "); // "()" left behind
            v = System.Text.RegularExpressions.Regex.Replace(v, @"\s+", " ").Trim(' ', '-', '_', '|', '~', '.', ',');
            if (v.Length == 0 || Tools.IsMatch(v) || v.All(c => char.IsDigit(c) || char.IsPunctuation(c) || c == ' ')) return null;
            return Cast.CastMediaInfo.RealName(v);
        }

        /// <summary>"002_Kangna_SpotiDost" → (null, "Kangna", 2); "03 - Arijit Singh - Tum Hi Ho" → ("Arijit Singh", "Tum Hi Ho", 3).</summary>
        public static (string? Artist, string Title, uint Track) FromFile(string name)
        {
            string s = name.Replace('_', ' ');
            uint track = 0;
            var m = LeadingTrack.Match(s);
            if (m.Success && uint.TryParse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, out uint n) && s.Length > m.Length) { track = n; s = s[m.Length..]; }
            s = Clean(s) ?? name;
            var parts = s.Split(" - ", 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && Clean(parts[0]) is { } artist && Clean(parts[1]) is { } title) return (artist, title, track);
            return (null, s, track);
        }
    }
}
