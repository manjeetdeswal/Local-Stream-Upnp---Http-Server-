using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LocalStreamPC.Services
{
    public sealed record SubtitleResult(int FileId, string Name, string Language, int Downloads, bool HashMatch);

    /// <summary>
    /// "Find subtitles": searches OpenSubtitles.com (with the user's own free API key) by the video's
    /// fingerprint and name, and saves the chosen one next to the video as "Name.en.srt".
    /// </summary>
    public sealed class OpenSubtitlesClient
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
        private readonly Func<string> _apiKey;
        private readonly string _api = (Environment.GetEnvironmentVariable("LOCALSTREAM_OSUB_URL") ?? "https://api.opensubtitles.com/api/v1").TrimEnd('/');

        public OpenSubtitlesClient(Func<string> apiKey) => _apiKey = apiKey;

        public bool Enabled => !string.IsNullOrWhiteSpace(_apiKey());

        public async Task<List<SubtitleResult>> SearchAsync(string videoPath, string languages, CancellationToken ct)
        {
            string hash = MovieHash(videoPath);
            var name = VideoNameParser.Parse(videoPath);
            var q = new SortedDictionary<string, string>(StringComparer.Ordinal) // the API wants sorted parameters
            {
                ["languages"] = string.Join(',', languages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.ToLowerInvariant())),
                ["moviehash"] = hash,
                ["query"] = name.Title.ToLowerInvariant(),
            };
            if (name.IsEpisode) { q["season_number"] = name.Season.ToString(); q["episode_number"] = name.Episode.ToString(); }
            else if (name.Year > 0) q["year"] = name.Year.ToString();
            string url = _api + "/subtitles?" + string.Join("&", q.Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value)));
            using var req = Request(HttpMethod.Get, url);
            using var res = await Http.SendAsync(req, ct);
            await ThrowIfFailed(res);
            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var list = new List<SubtitleResult>();
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("attributes", out var a)) continue;
                if (!a.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0) continue;
                var f = files[0];
                int fileId = f.TryGetProperty("file_id", out var fid) && fid.TryGetInt32(out int id) ? id : 0;
                if (fileId == 0) continue;
                string release = a.TryGetProperty("release", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
                string fileName = f.TryGetProperty("file_name", out var fn) && fn.ValueKind == JsonValueKind.String ? fn.GetString() ?? "" : "";
                list.Add(new SubtitleResult(fileId, release.Length > 0 ? release : fileName,
                    a.TryGetProperty("language", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() ?? "" : "",
                    a.TryGetProperty("download_count", out var dc) && dc.TryGetInt32(out int n) ? n : 0,
                    a.TryGetProperty("moviehash_match", out var mm) && mm.ValueKind == JsonValueKind.True));
            }
            return list.OrderByDescending(s => s.HashMatch).ThenByDescending(s => s.Downloads).Take(30).ToList();
        }

        /// <summary>Downloads subtitle <paramref name="fileId"/> and saves it next to the video. Returns the saved path.</summary>
        public async Task<string> DownloadAsync(string videoPath, int fileId, string language, CancellationToken ct)
        {
            using var req = Request(HttpMethod.Post, _api + "/download");
            req.Content = new StringContent(JsonSerializer.Serialize(new { file_id = fileId, sub_format = "srt" }), Encoding.UTF8, "application/json"); // with a length (some servers refuse chunked bodies)
            using var res = await Http.SendAsync(req, ct);
            await ThrowIfFailed(res);
            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            string link = doc.RootElement.TryGetProperty("link", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()! : throw new InvalidOperationException("OpenSubtitles didn't give a download link.");
            byte[] data = await Http.GetByteArrayAsync(link, ct);
            if (data.Length == 0 || data.Length > 10 * 1024 * 1024) throw new InvalidOperationException("The subtitle file was empty or too large.");

            string lang = new string((language ?? "").Where(char.IsLetterOrDigit).Take(8).ToArray()).ToLowerInvariant();
            string basePath = Path.Combine(Path.GetDirectoryName(videoPath)!, Path.GetFileNameWithoutExtension(videoPath) + (lang.Length > 0 ? "." + lang : ""));
            string target = basePath + ".srt";
            for (int i = 2; File.Exists(target); i++) target = $"{basePath}.{i}.srt";
            await File.WriteAllBytesAsync(target, data, ct);
            return target;
        }

        private HttpRequestMessage Request(HttpMethod method, string url)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.Add("Api-Key", _apiKey().Trim());
            req.Headers.UserAgent.ParseAdd("LocalStream v" + (typeof(OpenSubtitlesClient).Assembly.GetName().Version?.ToString(3) ?? "1.0"));
            req.Headers.Accept.ParseAdd("application/json");
            return req;
        }

        private static async Task ThrowIfFailed(HttpResponseMessage res)
        {
            if (res.IsSuccessStatusCode) return;
            string detail = "";
            try
            {
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) detail = m.GetString() ?? "";
            }
            catch { }
            throw new InvalidOperationException((int)res.StatusCode switch
            {
                401 or 403 => "OpenSubtitles refused the API key. Check it in Settings.",
                406 or 429 => "Today's OpenSubtitles download limit is reached. Try again tomorrow." + (detail.Length > 0 ? $" ({detail})" : ""),
                _ => $"OpenSubtitles answered {(int)res.StatusCode}{(detail.Length > 0 ? ": " + detail : "")}.",
            });
        }

        /// <summary>The OpenSubtitles fingerprint: file size plus the 64-bit sums of the first and last 64 KB.</summary>
        public static string MovieHash(string path)
        {
            const int chunk = 64 * 1024;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long size = fs.Length;
            ulong hash = (ulong)size;
            var buffer = new byte[chunk];
            void Sum(long offset)
            {
                fs.Position = offset;
                int read = 0, n;
                Array.Clear(buffer);
                while (read < chunk && (n = fs.Read(buffer, read, chunk - read)) > 0) read += n;
                for (int i = 0; i + 8 <= chunk; i += 8) hash = unchecked(hash + BitConverter.ToUInt64(buffer, i));
            }
            Sum(0);
            Sum(Math.Max(0, size - chunk));
            return hash.ToString("x16");
        }
    }
}
