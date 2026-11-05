using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LocalStreamPC.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace LocalStreamPC.Server
{
    /// <summary>
    /// The browser UI's endpoints. Every path a client sends is resolved with <see cref="PathGuard"/>
    /// before anything touches the disk.
    /// </summary>
    public sealed class WebApi
    {
        private readonly ConfigService _config;
        private readonly AppLog _log;
        private readonly ThumbnailService _thumbs;
        private readonly ServerServices _services;
        private readonly SemaphoreSlim _sizeSlots = new(2);
        private readonly ConcurrentDictionary<string, (long Size, DateTime Time)> _sizeCache = new(StringComparer.OrdinalIgnoreCase);

        public WebApi(ConfigService config, AppLog log, ThumbnailService thumbs, ServerServices services)
        {
            _config = config;
            _log = log;
            _thumbs = thumbs;
            _services = services;
        }

        // ---------------------------------------------------------------- listing

        /// <summary>GET /api/list?path= : contents of a folder (or the list of libraries when path is empty).</summary>
        public async Task ListAsync(HttpContext ctx, Caller caller)
        {
            string? path = ctx.Request.Query["path"];
            bool isAdmin = caller.IsAdmin;
            var roots = caller.Roots;
            var role = caller.Role;

            if (string.IsNullOrEmpty(path))
            {
                var libraries = roots.Where(Directory.Exists).Select(root => (object)new
                {
                    name = DisplayName(root),
                    path = root,
                    type = "library",
                    size = (long?)null,
                    file = (string?)null,
                    thumb = (string?)null,
                }).ToList();
                // A share link to a single file: the file itself is the "library".
                foreach (var f in roots.Where(File.Exists))
                {
                    string fid = DlnaService.EncodeId(f);
                    libraries.Add(new
                    {
                        name = Path.GetFileName(f), path = f, type = "file", size = (long?)new FileInfo(f).Length,
                        file = $"/file/{fid}", thumb = MediaTypes.IsMediaFile(f) ? $"/thumb/{fid}" : null,
                        kind = LibraryIndex.KindOf(f).ToString().ToLowerInvariant(),
                    });
                }

                await ctx.Response.WriteAsJsonAsync(new
                {
                    role = role.ToString(),
                    user = caller.UserName,
                    canDelete = isAdmin && _config.Current.AllowDeletion,
                    canConvert = FfmpegTools.CanConvert,
                    isRoot = true,
                    path = "",
                    name = caller.IsGuest ? "Shared with you" : "Shared Libraries",
                    guest = caller.IsGuest,
                    canUpload = caller.CanUpload,
                    canShare = caller.CanShare && !caller.IsGuest,
                    parent = (string?)null,
                    crumbs = Array.Empty<object>(),
                    items = libraries,
                });
                return;
            }

            if (!PathGuard.TryResolve(path, roots, out string folder) || !Directory.Exists(folder))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            string root = PathGuard.FindRoot(folder, roots) ?? folder;
            var crumbs = new List<object> { new { name = DisplayName(root), path = root } };
            string relative = folder.Length > root.Length ? folder[root.Length..].Trim(Path.DirectorySeparatorChar) : "";
            string build = root;
            if (relative.Length > 0)
            {
                foreach (var part in relative.Split(Path.DirectorySeparatorChar))
                {
                    build = Path.Combine(build, part);
                    crumbs.Add(new { name = part, path = build });
                }
            }

            string? parent = PathGuard.IsRoot(folder, roots) ? null : Path.GetDirectoryName(folder);

            var items = new List<object>();
            var (folders, files) = FolderReader.Read(folder);
            foreach (var dir in folders)
            {
                items.Add(new { name = dir.Name, path = dir.FullName, type = "folder", size = (long?)null, file = (string?)null, thumb = (string?)null });
            }
            foreach (var file in files)
            {
                long size;
                try { size = file.Length; } catch { continue; } // filled in by the directory scan
                string id = DlnaService.EncodeId(file.FullName);
                items.Add(new
                {
                    name = file.Name,
                    path = file.FullName,
                    type = "file",
                    size = (long?)size,
                    file = $"/file/{id}",
                    thumb = MediaTypes.IsMediaFile(file.Name) ? $"/thumb/{id}" : null,
                    kind = LibraryIndex.KindOf(file.Name).ToString().ToLowerInvariant(),
                });
            }

            await ctx.Response.WriteAsJsonAsync(new
            {
                role = role.ToString(),
                user = caller.UserName,
                canDelete = isAdmin && _config.Current.AllowDeletion,
                canConvert = FfmpegTools.CanConvert,
                isRoot = false,
                guest = caller.IsGuest,
                canUpload = caller.CanUpload,
                canShare = caller.CanShare && !caller.IsGuest,
                path = folder,
                name = DisplayName(folder),
                parent,
                crumbs,
                items,
            });
        }

        /// <summary>
        /// GET /api/size?path= : folder size. Separate from the listing (and cached) because
        /// walking a big folder tree is slow; the old page computed every folder's size before
        /// it could show anything.
        /// </summary>
        public async Task SizeAsync(HttpContext ctx, Caller caller)
        {
            var roots = caller.Roots;
            if (!PathGuard.TryResolve(ctx.Request.Query["path"], roots, out string folder) || !Directory.Exists(folder))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (!_sizeCache.TryGetValue(folder, out var cached) || DateTime.UtcNow - cached.Time > TimeSpan.FromMinutes(2))
            {
                await _sizeSlots.WaitAsync(ctx.RequestAborted);
                try
                {
                    var token = ctx.RequestAborted;
                    long size = await Task.Run(() => GetDirectorySize(folder, token), token);
                    cached = (size, DateTime.UtcNow);
                    _sizeCache[folder] = cached;
                }
                finally { _sizeSlots.Release(); }
            }
            await ctx.Response.WriteAsJsonAsync(new { size = cached.Size });
        }

        // ---------------------------------------------------------------- file operations (admin)

        /// <summary>POST /delete?path=</summary>
        public Task DeleteAsync(HttpContext ctx, Caller caller)
        {
            var roots = caller.Roots;
            if (!_config.Current.AllowDeletion)
            {
                _log.Write("Delete blocked: 'Allow Admin to Delete' is off in Settings.");
                return Status(ctx, StatusCodes.Status403Forbidden);
            }
            // allowRoot:false - a shared folder itself can never be deleted from the browser.
            if (!PathGuard.TryResolve(ctx.Request.Query["path"], roots, out string target, allowRoot: false))
            {
                _log.Write("Delete blocked: path is outside your shared folders.");
                return Status(ctx, StatusCodes.Status403Forbidden);
            }

            try
            {
                if (File.Exists(target)) File.Delete(target);
                else if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                else return Status(ctx, StatusCodes.Status404NotFound);
                _log.Write($"Deleted: {Path.GetFileName(target)}");
                _services.Activity.Add(caller.DisplayName, "Deleted", Path.GetFileName(target), ctx);
                _services.Library.Invalidate();
                return Status(ctx, StatusCodes.Status200OK);
            }
            catch (Exception ex)
            {
                _log.Write($"Delete error: {ex.Message}");
                return Status(ctx, StatusCodes.Status500InternalServerError);
            }
        }

        /// <summary>POST /mkdir?path=&amp;name=</summary>
        public Task CreateFolderAsync(HttpContext ctx, Caller caller)
        {
            var roots = caller.Roots;
            string? name = PathGuard.SanitizeFileName(ctx.Request.Query["name"]);
            if (name == null || !PathGuard.TryResolve(ctx.Request.Query["path"], roots, out string parent) || !Directory.Exists(parent))
                return Status(ctx, StatusCodes.Status403Forbidden);

            string newFolder = Path.Combine(parent, name);
            if (!PathGuard.TryResolve(newFolder, roots, out newFolder) || Directory.Exists(newFolder) || File.Exists(newFolder))
                return Status(ctx, StatusCodes.Status409Conflict);

            try
            {
                Directory.CreateDirectory(newFolder);
                _log.Write($"Created folder: {name}");
                _services.Activity.Add(caller.DisplayName, "Created folder", name, ctx);
                _services.Library.Invalidate();
                return Status(ctx, StatusCodes.Status200OK);
            }
            catch (Exception ex)
            {
                _log.Write($"Create folder error: {ex.Message}");
                return Status(ctx, StatusCodes.Status500InternalServerError);
            }
        }

        /// <summary>POST /move or /copy ?src=&amp;dest= (dest is the target folder)</summary>
        public Task MoveOrCopyAsync(HttpContext ctx, Caller caller, bool isCopy)
        {
            var roots = caller.Roots;
            if (!PathGuard.TryResolve(ctx.Request.Query["src"], roots, out string src, allowRoot: isCopy) ||
                !PathGuard.TryResolve(ctx.Request.Query["dest"], roots, out string destFolder) ||
                !Directory.Exists(destFolder))
            {
                _log.Write("Move/copy blocked: path outside shared folders.");
                return Status(ctx, StatusCodes.Status403Forbidden);
            }

            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(src));
            string dest = UniquePath(Path.Combine(destFolder, name), isCopy ? "_Copy" : "");
            if (!PathGuard.TryResolve(dest, roots, out dest)) return Status(ctx, StatusCodes.Status403Forbidden);

            // Copying or moving a folder into itself would recurse forever.
            if (Directory.Exists(src) && IsSameOrInside(dest, src))
                return Status(ctx, StatusCodes.Status400BadRequest);

            try
            {
                if (File.Exists(src))
                {
                    if (isCopy) File.Copy(src, dest); else File.Move(src, dest);
                }
                else if (Directory.Exists(src))
                {
                    if (isCopy) CopyDirectory(src, dest); else Directory.Move(src, dest);
                }
                else return Status(ctx, StatusCodes.Status404NotFound);

                _log.Write($"{(isCopy ? "Copied" : "Moved")}: {name}");
                _services.Activity.Add(caller.DisplayName, isCopy ? "Copied" : "Moved", name, ctx, "to " + Path.GetFileName(destFolder));
                _services.Library.Invalidate();
                return Status(ctx, StatusCodes.Status200OK);
            }
            catch (Exception ex)
            {
                _log.Write($"Move/copy error: {ex.Message}");
                return Status(ctx, StatusCodes.Status500InternalServerError);
            }
        }

        /// <summary>
        /// POST /upload?path= (multipart/form-data, any number of files).
        /// Streams each file straight to disk instead of buffering the whole request in memory
        /// (which failed above 2 GB), and saves exactly the file's bytes (the old boundary
        /// arithmetic left stray bytes at the end of every file).
        /// </summary>
        public async Task UploadAsync(HttpContext ctx, Caller caller)
        {
            var roots = caller.Roots;
            if (!PathGuard.TryResolve(ctx.Request.Query["path"], roots, out string folder) || !Directory.Exists(folder))
            {
                _log.Write("Upload blocked: target is not inside a shared folder.");
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (!MediaTypeHeaderValue.TryParse(ctx.Request.ContentType, out var contentType) ||
                !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                return;
            }
            string boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value ?? "";
            if (boundary.Length == 0 || boundary.Length > 200)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            // Uploads may be big videos: lift Kestrel's 30 MB default for this request only.
            var sizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = null;

            var reader = new MultipartReader(boundary, ctx.Request.Body);
            var saved = new List<string>();
            MultipartSection? section;
            try
            {
                while ((section = await reader.ReadNextSectionAsync(ctx.RequestAborted)) != null)
                {
                    if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                        !disposition.IsFileDisposition())
                        continue;

                    string? rawName = disposition.FileNameStar.HasValue ? disposition.FileNameStar.Value : disposition.FileName.Value;
                    // Never trust the client's file name: keep only the last path segment.
                    string name = PathGuard.SanitizeFileName(HeaderUtilities.RemoveQuotes(rawName).Value) ?? $"Upload_{DateTime.Now.Ticks}.bin";
                    string target = UniquePath(Path.Combine(folder, name), "");
                    if (!PathGuard.TryResolve(target, roots, out target) || !string.Equals(Path.GetDirectoryName(target), folder, StringComparison.OrdinalIgnoreCase))
                    {
                        _log.Write($"Upload blocked for file name '{rawName}'.");
                        continue;
                    }

                    // Write to a temp name first so a cancelled upload never leaves a half file
                    // under the real name.
                    string partial = target + ".uploading";
                    try
                    {
                        await using (var fs = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                            await section.Body.CopyToAsync(fs, 1 << 20, ctx.RequestAborted);
                        File.Move(partial, target);
                        saved.Add(Path.GetFileName(target));
                        _log.Write($"Saved upload: {Path.GetFileName(target)} ({MediaTypes.FormatSize(new FileInfo(target).Length)})");
                    }
                    catch
                    {
                        try { File.Delete(partial); } catch { }
                        throw;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _log.Write("Upload cancelled by the client.");
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or BadHttpRequestException)
            {
                _log.Write($"Upload error: {ex.Message}");
                ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
            }

            if (saved.Count > 0)
            {
                _services.Library.Invalidate();
                _services.Activity.Add(caller.DisplayName, "Uploaded", saved.Count == 1 ? saved[0] : $"{saved.Count} files", ctx, "to " + Path.GetFileName(folder));
            }
            await ctx.Response.WriteAsJsonAsync(new { saved });
        }

        // ---------------------------------------------------------------- downloads

        /// <summary>
        /// GET /zip?path=&amp;files=a|b|c : zips the chosen files (or all files) of one folder.
        /// File names must be plain names in that folder - no paths, no "..".
        /// </summary>
        public async Task ZipAsync(HttpContext ctx, Caller caller)
        {
            var roots = caller.Roots;
            if (!PathGuard.TryResolve(ctx.Request.Query["path"], roots, out string folder) || !Directory.Exists(folder))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            string? filesParam = ctx.Request.Query["files"];
            List<string> files;
            if (string.IsNullOrEmpty(filesParam))
            {
                files = Safe(() => Directory.EnumerateFiles(folder)).ToList();
            }
            else
            {
                files = new List<string>();
                foreach (var raw in filesParam.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    string? name = PathGuard.SanitizeFileName(raw);
                    if (name == null || name != raw.Trim()) continue; // contained a path: reject
                    string full = Path.Combine(folder, name);
                    if (PathGuard.TryResolve(full, roots, out full) && File.Exists(full)) files.Add(full);
                }
            }

            string zipName = $"{DisplayName(folder)}.zip";
            _services.Activity.Add(caller.DisplayName, "Downloaded", zipName, ctx, $"{files.Count} files as zip");
            ctx.Response.ContentType = "application/zip";
            ctx.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileName = AsciiFallback(zipName),
                FileNameStar = zipName,
            }.ToString();

            // ZipArchive writes synchronously; allow it for this response only and keep the
            // blocking work off Kestrel's request threads.
            var bodyControl = ctx.Features.Get<IHttpBodyControlFeature>();
            if (bodyControl != null) bodyControl.AllowSynchronousIO = true;

            await Task.Run(() =>
            {
                try
                {
                    using var zip = new ZipArchive(ctx.Response.Body, ZipArchiveMode.Create, leaveOpen: true);
                    foreach (var file in files)
                    {
                        ctx.RequestAborted.ThrowIfCancellationRequested();
                        try { zip.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Fastest); }
                        catch (IOException ex) when (!ctx.RequestAborted.IsCancellationRequested)
                        {
                            _log.Write($"Zip: skipped {Path.GetFileName(file)} ({ex.Message})");
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) when (ctx.RequestAborted.IsCancellationRequested || ex is IOException)
                {
                    // Client went away mid-download.
                }
            });
        }

        /// <summary>
        /// GET/HEAD /file/{base64 path}. Byte ranges (including "bytes=-500" and open-ended ranges,
        /// which crashed the old server) are handled by ASP.NET Core's file result.
        /// </summary>
        public async Task ServeFileAsync(HttpContext ctx, string id, Caller caller)
        {
            var roots = caller.Roots;
            if (!DlnaService.TryDecodeId(id, out string raw) || !PathGuard.TryResolve(raw, roots, out string full) || !File.Exists(full))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var info = new FileInfo(full);
            string mime = MediaTypes.GetMimeType(info.Extension);
            var headers = ctx.Response.Headers;
            headers["transferMode.dlna.org"] = "Streaming";
            headers["contentFeatures.dlna.org"] = "DLNA.ORG_OP=01;DLNA.ORG_FLAGS=01700000000000000000000000000000";
            headers.XContentTypeOptions = "nosniff";
            headers.ContentDisposition = new ContentDispositionHeaderValue("inline")
            {
                FileName = AsciiFallback(info.Name),
                FileNameStar = info.Name,
            }.ToString();

            if (_services.Streams.IsBlocked(ctx, info.Name))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden; // stopped from the dashboard
                return;
            }
            if (ctx.Request.Query["dl"] == "1" && !HttpMethods.IsHead(ctx.Request.Method) && ctx.Request.Headers.Range.Count == 0)
                _services.Activity.Add(caller.DisplayName, "Downloaded", info.Name, ctx, MediaTypes.FormatSize(info.Length));

            // Count what's streamed ("Streaming now", keep-awake). Small files like subtitles don't count.
            using var tracking = MediaTypes.IsMediaFile(info.Name) && !HttpMethods.IsHead(ctx.Request.Method)
                ? _services.Streams.Begin(ctx, info.Name, converting: false, caller.DisplayName) : null;
            try
            {
                await Results.File(full, mime, enableRangeProcessing: true, lastModified: info.LastWriteTimeUtc).ExecuteAsync(ctx);
            }
            catch (Exception ex) when (ctx.RequestAborted.IsCancellationRequested || ex is IOException)
            {
                // Player paused, seeked or closed the connection. Normal.
            }
        }

        /// <summary>GET /thumb/{base64 path}</summary>
        public async Task ServeThumbnailAsync(HttpContext ctx, string id, Caller caller)
        {
            var roots = caller.Roots;
            if (!DlnaService.TryDecodeId(id, out string raw) || !PathGuard.TryResolve(raw, roots, out string full) || !File.Exists(full))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var thumb = await _thumbs.GetAsync(full, ctx.RequestAborted);
            if (thumb == null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound; // client shows its default icon
                return;
            }
            ctx.Response.ContentType = thumb.ContentType;
            ctx.Response.Headers.CacheControl = "private, max-age=86400";
            ctx.Response.ContentLength = thumb.Data.Length;
            await ctx.Response.Body.WriteAsync(thumb.Data, ctx.RequestAborted);
        }

        // ---------------------------------------------------------------- helpers

        private static Task Status(HttpContext ctx, int status)
        {
            ctx.Response.StatusCode = status;
            return Task.CompletedTask;
        }

        private static string DisplayName(string path)
        {
            string name = new DirectoryInfo(path).Name;
            return string.IsNullOrEmpty(name) ? path : name;
        }

        private static string AsciiFallback(string name)
        {
            var chars = name.Select(c => c < 32 || c > 126 || c == '"' || c == '\\' ? '_' : c).ToArray();
            return "\"" + new string(chars) + "\"";
        }

        /// <summary>"file.mp4" -> "file_Copy_638...mp4" when the name is taken.</summary>
        private static string UniquePath(string path, string tag)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path) ?? "";
            string stem = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            return Path.Combine(dir, $"{stem}{tag}_{DateTime.Now.Ticks}{ext}");
        }

        private static bool IsSameOrInside(string candidate, string folder)
            => PathGuard.TryResolve(candidate, new[] { folder }, out _);

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            foreach (var dir in Directory.GetDirectories(source))
                CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }

        private static long GetDirectorySize(string folder, CancellationToken token)
        {
            try
            {
                // A whole drive shared ("E:\"): ask the file system instead of walking every file.
                var drive = DriveInfo.GetDrives().FirstOrDefault(d =>
                    string.Equals(Path.TrimEndingDirectorySeparator(d.RootDirectory.FullName), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase));
                if (drive is { IsReady: true }) return drive.TotalSize - drive.TotalFreeSpace;
            }
            catch { }

            long total = 0;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            try
            {
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
                {
                    token.ThrowIfCancellationRequested();
                    total += file.Length;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            return total;
        }

        private static IEnumerable<string> Safe(Func<IEnumerable<string>> enumerate)
        {
            try { return enumerate().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(); }
            catch { return Array.Empty<string>(); }
        }
    }
}
