using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace LocalStreamPC.Services
{
    /// <summary>
    /// Keeps the PC from going to sleep while something is streaming, converting or casting, and
    /// lets it sleep normally again afterwards. The screen may still turn off, except while a cast
    /// video is playing here.
    ///
    /// Windows: SetThreadExecutionState from one dedicated thread. Linux: systemd-inhibit.
    /// macOS: caffeinate.
    /// </summary>
    public sealed class SleepGuard : IDisposable
    {
        public enum Level { None, System, Display }

        private readonly AppLog _log;
        private readonly Func<Level> _wanted;
        private readonly Timer _timer;
        private readonly BlockingCollection<Level> _windowsQueue = new();
        private Thread? _windowsThread;
        private Process? _inhibitor;
        private Level _current = Level.None;

        /// <param name="wanted">Asked every few seconds what level is needed right now.</param>
        public SleepGuard(AppLog log, Func<Level> wanted)
        {
            _log = log;
            _wanted = wanted;
            _timer = new Timer(_ => Evaluate(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
        }

        public Level Current => _current;

        /// <summary>Re-checks now (e.g. right after a cast started).</summary>
        public void Evaluate()
        {
            Level want;
            try { want = _wanted(); } catch { want = Level.None; }
            lock (this)
            {
                if (want == _current) return;
                var before = _current;
                _current = want;
                try { Apply(want); }
                catch (Exception ex) { _log.Write($"Keep-awake: {ex.Message}"); }
                if (before == Level.None) _log.Write("Keeping the PC awake while media is playing.");
                else if (want == Level.None) _log.Write("Nothing is playing: the PC may sleep again.");
            }
        }

        private void Apply(Level level)
        {
            if (OperatingSystem.IsWindows())
            {
                if (_windowsThread == null)
                {
                    _windowsThread = new Thread(WindowsLoop) { IsBackground = true, Name = "LocalStream keep-awake" };
                    _windowsThread.Start();
                }
                _windowsQueue.Add(level);
                return;
            }

            StopInhibitor();
            if (level == Level.None) return;
            if (OperatingSystem.IsLinux())
            {
                string what = level == Level.Display ? "idle:sleep" : "sleep";
                _inhibitor = TryStart("systemd-inhibit", "--what=" + what, "--who=LocalStream", "--why=Media is playing", "--mode=block", "sleep", "infinity");
            }
            else if (OperatingSystem.IsMacOS())
            {
                _inhibitor = level == Level.Display ? TryStart("caffeinate", "-i", "-d") : TryStart("caffeinate", "-i");
            }
        }

        // SetThreadExecutionState(ES_CONTINUOUS ...) holds while the calling thread lives, and must be
        // cleared from that same thread, so one thread owns it.
        private void WindowsLoop()
        {
            foreach (var level in _windowsQueue.GetConsumingEnumerable())
            {
                uint flags = EsContinuous;
                if (level != Level.None) flags |= EsSystemRequired;
                if (level == Level.Display) flags |= EsDisplayRequired;
                SetThreadExecutionState(flags);
            }
            SetThreadExecutionState(EsContinuous);
        }

        private Process? TryStart(string exe, params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                return Process.Start(psi);
            }
            catch
            {
                return null; // tool not installed: nothing we can do
            }
        }

        private void StopInhibitor()
        {
            try { if (_inhibitor is { HasExited: false }) _inhibitor.Kill(true); } catch { }
            _inhibitor?.Dispose();
            _inhibitor = null;
        }

        public void Dispose()
        {
            _timer.Dispose();
            if (OperatingSystem.IsWindows()) { _windowsQueue.Add(Level.None); _windowsQueue.CompleteAdding(); }
            StopInhibitor();
        }

        private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001, EsDisplayRequired = 0x00000002;

        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint esFlags);
    }
}
