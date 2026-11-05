using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace LocalStreamPC.Services
{
    /// <summary>Finds the .srt/.vtt that belongs to a video in the same folder.</summary>
    public static class SubtitleMatcher
    {
        private static readonly ConcurrentDictionary<string, (DateTime Time, List<string> Files)> Cache = new();
        private static readonly string[] NoiseTags = { "1080p", "720p", "480p", "4k", "bluray", "webrip", "web-dl", "x264", "x265", "hevc", "brrip", "hdtv", "yify", "yts", "aac", "ac3" };
        private static readonly Regex SxxExx = new(@"s(\d+)e(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex NxNN = new(@"(\d+)x(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex EpNN = new(@"ep?(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Brackets = new(@"\[.*?\]", RegexOptions.Compiled);

        /// <summary>Forget the cached folder listing (after a subtitle was saved there).</summary>
        public static void Forget(string dir) => Cache.TryRemove(dir, out _);

        /// <summary>
        /// Every subtitle file made for this video ("Movie.srt", "Movie.en.srt", "Movie.fr.2.srt"),
        /// with the best match first even when its name differs.
        /// </summary>
        public static List<string> FindAll(string videoPath)
        {
            var list = new List<string>();
            string? best = FindBestMatch(videoPath);
            if (best != null) list.Add(best);
            string? dir = Path.GetDirectoryName(videoPath);
            if (dir != null && Cache.TryGetValue(dir, out var entry))
            {
                string stem = Path.GetFileNameWithoutExtension(videoPath) + ".";
                list.AddRange(entry.Files.Where(f => Path.GetFileName(f).StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            }
            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>"Movie.en.srt" → "en" (or null).</summary>
        public static string? LanguageOf(string videoPath, string subtitlePath)
        {
            string stem = Path.GetFileNameWithoutExtension(videoPath) + ".";
            string name = Path.GetFileNameWithoutExtension(subtitlePath);
            if (!name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) return null;
            string rest = name[stem.Length..].Split('.')[0];
            return rest.Length is >= 2 and <= 3 && rest.All(char.IsLetter) ? rest.ToLowerInvariant() : null;
        }

        public static string? FindBestMatch(string videoPath)
        {
            string? dir = Path.GetDirectoryName(videoPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

            if (!Cache.TryGetValue(dir, out var entry) || (DateTime.Now - entry.Time).TotalSeconds > 10)
            {
                List<string> found;
                try
                {
                    found = Directory.EnumerateFiles(dir)
                        .Where(f => MediaTypes.Subtitle.Contains(Path.GetExtension(f)))
                        .ToList();
                }
                catch { found = new List<string>(); }
                entry = (DateTime.Now, found);
                Cache[dir] = entry;
            }

            var subs = entry.Files;
            if (subs.Count == 0) return null;

            // 1. Exact name match
            string exactSrt = Path.ChangeExtension(videoPath, ".srt");
            string exactVtt = Path.ChangeExtension(videoPath, ".vtt");
            if (subs.Contains(exactSrt)) return exactSrt;
            if (subs.Contains(exactVtt)) return exactVtt;

            // 2. Only one subtitle in the folder: assume it belongs to this video
            if (subs.Count == 1) return subs[0];

            // 3. TV / anime episode codes
            string? videoCode = GetEpisodeCode(Path.GetFileName(videoPath));
            if (videoCode != null)
            {
                foreach (var sub in subs)
                    if (GetEpisodeCode(Path.GetFileName(sub)) == videoCode) return sub;
            }

            // 4. Movies: score by word overlap
            string cleanVideo = CleanMediaName(Path.GetFileNameWithoutExtension(videoPath));
            var videoWords = cleanVideo.Split(' ').Where(w => w.Length > 1).ToList();

            string? best = null;
            int bestScore = 0;
            foreach (var sub in subs)
            {
                string cleanSub = CleanMediaName(Path.GetFileNameWithoutExtension(sub));
                var subWords = cleanSub.Split(' ').ToList();
                int score = videoWords.Count(subWords.Contains);
                if (cleanSub.Contains(cleanVideo) || cleanVideo.Contains(cleanSub)) score += 10;
                if (score > bestScore) { bestScore = score; best = sub; }
            }
            return best;
        }

        private static string? GetEpisodeCode(string fileName)
        {
            var m1 = SxxExx.Match(fileName);
            if (m1.Success) return $"S{int.Parse(m1.Groups[1].Value):D2}E{int.Parse(m1.Groups[2].Value):D2}";
            var m2 = NxNN.Match(fileName);
            if (m2.Success) return $"S{int.Parse(m2.Groups[1].Value):D2}E{int.Parse(m2.Groups[2].Value):D2}";
            var m3 = EpNN.Match(fileName);
            if (m3.Success) return $"E{int.Parse(m3.Groups[1].Value):D2}";
            return null;
        }

        private static string CleanMediaName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            name = name.ToLowerInvariant();
            foreach (var tag in NoiseTags) name = name.Replace(tag, "");
            name = Brackets.Replace(name, " ");
            name = name.Replace("(", " ").Replace(")", " ").Replace(".", " ").Replace("_", " ").Replace("-", " ");
            return string.Join(" ", name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
