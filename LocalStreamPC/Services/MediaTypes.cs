using System;
using System.Collections.Generic;
using System.IO;

namespace LocalStreamPC.Services
{
    public static class MediaTypes
    {
        public static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".avi", ".mov", ".webm", ".wmv", ".m4v", ".ts", ".mpg", ".mpeg", ".m2ts", ".mts", ".3gp", ".flv", ".vob", ".ogv", ".divx", ".asf" };
        public static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".mpga", ".opus", ".wma", ".aiff", ".aif", ".ape", ".wv", ".mka", ".m4b", ".oga", ".amr", ".ac3", ".dts", ".alac", ".weba", ".mp2" };
        public static readonly HashSet<string> Image = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };
        public static readonly HashSet<string> Subtitle = new(StringComparer.OrdinalIgnoreCase) { ".srt", ".vtt" };

        public static bool IsMediaFile(string fileName)
        {
            string ext = Path.GetExtension(fileName);
            return Video.Contains(ext) || Audio.Contains(ext) || Image.Contains(ext);
        }

        public static string GetMimeType(string extension) => extension.ToLowerInvariant() switch
        {
            ".mp4" or ".m4v" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".avi" => "video/x-msvideo",
            ".mov" => "video/quicktime",
            ".webm" => "video/webm",
            ".wmv" => "video/x-ms-wmv",
            ".ts" or ".m2ts" or ".mts" => "video/mp2t",
            ".mpg" or ".mpeg" or ".vob" => "video/mpeg",
            ".3gp" => "video/3gpp",
            ".flv" => "video/x-flv",
            ".ogv" => "video/ogg",
            ".divx" => "video/x-msvideo",
            ".asf" => "video/x-ms-asf",
            ".wma" => "audio/x-ms-wma",
            ".aiff" or ".aif" => "audio/aiff",
            ".ape" => "audio/x-ape",
            ".wv" => "audio/x-wavpack",
            ".mka" => "audio/x-matroska",
            ".m4b" or ".alac" => "audio/mp4",
            ".oga" => "audio/ogg",
            ".amr" => "audio/amr",
            ".ac3" => "audio/ac3",
            ".dts" => "audio/vnd.dts",
            ".weba" => "audio/webm",
            ".mp2" => "audio/mpeg",
            ".srt" => "text/plain",
            ".vtt" => "text/vtt",
            ".mp3" or ".mpga" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".flac" => "audio/flac",
            ".ogg" => "audio/ogg",
            ".opus" => "audio/opus",
            ".m4a" => "audio/mp4",
            ".aac" => "audio/aac",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".zip" => "application/zip",
            // Served as plain text: an uploaded .html file must never run as a page on our origin.
            ".html" or ".htm" or ".svg" or ".xml" or ".js" => "text/plain",
            _ => "application/octet-stream"
        };

        public static string FormatSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1) { order++; len /= 1024; }
            return $"{len:0.##} {sizes[order]}";
        }
    }
}
