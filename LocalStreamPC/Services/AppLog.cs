using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace LocalStreamPC.Services
{
    /// <summary>
    /// Thread-safe, size-limited log. The old code appended to a TextBox string forever,
    /// which slowed the UI down and grew memory on a long-running server.
    /// </summary>
    public sealed class AppLog
    {
        private const int MaxLines = 500;
        private readonly LinkedList<string> _lines = new();
        private readonly object _lock = new();

        /// <summary>Set LOCALSTREAM_LOG=1 to also print the log to the terminal (troubleshooting).</summary>
        private static readonly bool MirrorToConsole = Environment.GetEnvironmentVariable("LOCALSTREAM_LOG") == "1";

        /// <summary>Raised (on any thread) after a line is added.</summary>
        public event Action? Changed;

        public void Write(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message.Trim()}";
            lock (_lock)
            {
                _lines.AddLast(line);
                while (_lines.Count > MaxLines) _lines.RemoveFirst();
            }
            Debug.WriteLine(line);
            if (MirrorToConsole) Console.Error.WriteLine(line);
            Changed?.Invoke();
        }

        public string GetText()
        {
            lock (_lock) return string.Join(Environment.NewLine, _lines);
        }
    }
}
