using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LocalStreamPC.Services
{
    /// <summary>
    /// Lists a folder in one pass. Entries come back with size, dates and attributes already filled
    /// in from the directory scan, instead of a separate disk lookup per file (which is what made
    /// big folders slow, especially on network drives).
    /// </summary>
    public static class FolderReader
    {
        private static readonly EnumerationOptions Options = new() { IgnoreInaccessible = true, AttributesToSkip = 0 };

        public static (List<DirectoryInfo> Folders, List<FileInfo> Files) Read(string folder, bool skipHidden = false)
        {
            var folders = new List<DirectoryInfo>();
            var files = new List<FileInfo>();
            try
            {
                foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", Options))
                {
                    if (skipHidden && (entry.Attributes & FileAttributes.Hidden) != 0) continue;
                    if (entry is DirectoryInfo d) folders.Add(d);
                    else if (entry is FileInfo f) files.Add(f);
                }
            }
            catch
            {
                // Permission denied, folder removed, etc.: show what we have.
            }
            folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return (folders, files);
        }
    }
}
