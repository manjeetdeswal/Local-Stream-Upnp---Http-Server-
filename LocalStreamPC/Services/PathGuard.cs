using System;
using System.Collections.Generic;
using System.IO;

namespace LocalStreamPC.Services
{
    /// <summary>
    /// The single place that decides whether a path a client sent us is inside a shared folder.
    /// Every server endpoint that touches the disk goes through here.
    ///
    /// It fixes three problems in the old checks:
    ///  * paths were compared before normalising, so "D:\Movies\..\..\Windows" passed;
    ///  * a plain prefix match let "D:\Movies" also allow "D:\MoviesPrivate";
    ///  * some checks were case-sensitive on Windows and some weren't.
    /// </summary>
    public static class PathGuard
    {
        private static readonly StringComparison Comparison =
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        /// <summary>
        /// Resolves <paramref name="path"/> to a full, normalised path and checks it is one of the
        /// shared roots or inside one.
        /// </summary>
        /// <param name="allowRoot">False for operations that must never target a shared root itself
        /// (deleting it, moving it).</param>
        public static bool TryResolve(string? path, IReadOnlyList<string> roots, out string fullPath, bool allowRoot = true)
        {
            fullPath = "";
            if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0) return false;

            try
            {
                // Relative paths would resolve against the working directory - never accept them.
                if (!Path.IsPathFullyQualified(path)) return false;
                string candidate = Normalize(path);

                foreach (var root in roots)
                {
                    if (string.IsNullOrWhiteSpace(root)) continue;
                    string normRoot = Normalize(root);

                    if (string.Equals(candidate, normRoot, Comparison))
                    {
                        if (!allowRoot) return false;
                        fullPath = candidate;
                        return true;
                    }

                    string prefix = EndsWithSeparator(normRoot) ? normRoot : normRoot + Path.DirectorySeparatorChar;
                    if (candidate.StartsWith(prefix, Comparison))
                    {
                        fullPath = candidate;
                        return true;
                    }
                }
            }
            catch
            {
                // Invalid characters, too long, etc.
            }
            return false;
        }

        /// <summary>True if the path is exactly one of the shared roots.</summary>
        public static bool IsRoot(string fullPath, IReadOnlyList<string> roots)
        {
            foreach (var root in roots)
            {
                try { if (string.Equals(Normalize(root), Normalize(fullPath), Comparison)) return true; }
                catch { }
            }
            return false;
        }

        /// <summary>The shared root that contains <paramref name="fullPath"/>, or null.</summary>
        public static string? FindRoot(string fullPath, IReadOnlyList<string> roots)
        {
            string? best = null;
            foreach (var root in roots)
            {
                if (TryResolve(fullPath, new[] { root }, out _) && (best == null || root.Length > best.Length))
                    best = Normalize(root);
            }
            return best;
        }

        /// <summary>
        /// Turns a client-supplied file name into a safe single file name: strips any directory part
        /// (both / and \, whatever OS we are on) and rejects ".", ".." and names with invalid characters.
        /// </summary>
        public static string? SanitizeFileName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            name = name.Trim().Trim('"');
            int cut = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
            if (cut >= 0) name = name[(cut + 1)..];
            name = name.Trim();

            if (name.Length == 0 || name == "." || name == "..") return null;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            // Characters Windows forbids even when running elsewhere, so files stay portable.
            if (name.IndexOfAny(new[] { ':', '*', '?', '"', '<', '>', '|' }) >= 0) return null;
            if (name.EndsWith('.') || name.EndsWith(' ')) name = name.TrimEnd('.', ' ');
            return name.Length == 0 ? null : name;
        }

        private static string Normalize(string path)
        {
            string full = Path.GetFullPath(path);
            // Keeps "D:\" and "/" intact, trims "D:\Movies\" to "D:\Movies".
            return Path.TrimEndingDirectorySeparator(full);
        }

        private static bool EndsWithSeparator(string path)
            => path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar);
    }
}
