using System;
using System.IO;
using System.Text;

namespace LocalStreamPC.Services
{
    /// <summary>Run-at-login for Windows (registry Run key) and Linux (XDG autostart).</summary>
    public sealed class StartupService
    {
        private const string AppName = "LocalStreamPC";
        private readonly AppLog _log;

        public StartupService(AppLog log) => _log = log;

        public void SetStartup(bool enable)
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                    if (key == null) return;
                    if (enable)
                    {
                        key.SetValue(AppName, $"\"{exe}\" -minimized");
                        _log.Write("Added to Windows startup.");
                    }
                    else
                    {
                        key.DeleteValue(AppName, false);
                        _log.Write("Removed from Windows startup.");
                    }
                }
                else if (OperatingSystem.IsLinux())
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "autostart");
                    Directory.CreateDirectory(dir);
                    string desktopFile = Path.Combine(dir, "localstream.desktop");

                    if (enable)
                    {
                        string content = "[Desktop Entry]\nType=Application\nName=LocalStream\n" +
                                         $"Exec={QuoteDesktopExecArg(exe)} -minimized\nTerminal=false\n";
                        File.WriteAllText(desktopFile, content);
                        _log.Write("Added to Linux autostart.");
                    }
                    else if (File.Exists(desktopFile))
                    {
                        File.Delete(desktopFile);
                        _log.Write("Removed from Linux autostart.");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Write("Startup error: " + ex.Message);
            }
        }

        /// <summary>
        /// Quotes a path for the Exec= key of a .desktop file (freedesktop spec): wrap in double
        /// quotes and backslash-escape " ` $ and \. Without this a path with spaces breaks.
        /// </summary>
        public static string QuoteDesktopExecArg(string path)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in path)
            {
                if (c is '"' or '`' or '$' or '\\') sb.Append('\\');
                sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
