using MergeVideo.Models;
using MergeVideo.Utilities;
using System.Diagnostics;
using System.Text;

namespace MergeVideo
{
    sealed class ConsoleProgressBar : IDisposable
    {
        private readonly object _lock = new();
        private readonly int _width;
        private readonly string _prefix;
        private double _lastPct = -1;
        private string _lastMsg = "";
        private readonly System.Timers.Timer _spinner;
        private readonly char[] _frames = new[] { '|', '/', '-', '\\' };
        private int _fi = 0;
        private bool _active = true;

        public ConsoleProgressBar(string prefix, int width = 40, int fps = 12)
        {
            _width = Math.Max(10, width);
            _prefix = prefix;
            _spinner = new System.Timers.Timer(1000.0 / fps);
            _spinner.Elapsed += (_, __) => { lock (_lock) { if (_active) Redraw(_lastPct, _lastMsg); } };
            _spinner.AutoReset = true;
            _spinner.Start();
        }

        public void Report(double pct, string? message = null)
        {
            lock (_lock)
            {
                if (double.IsNaN(pct) || double.IsInfinity(pct)) pct = 0;
                pct = Math.Clamp(pct, 0, 1);
                _lastPct = pct;
                if (message != null) _lastMsg = message;
                Redraw(pct, _lastMsg);
            }
        }

        private void Redraw(double pct, string msg)
        {
            int filled = (int)Math.Round(pct * _width);
            var bar = new string('#', filled) + new string('-', _width - filled);
            var percent = (int)Math.Round(pct * 100);
            var spin = _frames[_fi++ % _frames.Length];
            var line = $"{_prefix} [{bar}] {percent,3}% {spin} {msg}";
            int avail = Math.Max(10, Console.BufferWidth - 1);
            if (line.Length > avail) line = line.Substring(0, avail);
            Console.SetCursorPosition(0, Console.CursorTop);
            Console.Write(line.PadRight(avail));
        }

        public void Done(string? endMessage = null)
        {
            lock (_lock)
            {
                _active = false;
                _spinner.Stop();
                Redraw(1, endMessage ?? "Done");
                Console.WriteLine();
            }
        }

        public void Dispose() => Done();


        public static int RunProcessWithProgress(
            ProcessStartInfo psi,
            Func<string, double?> tryParseProgress,
            WorkDirs work,
            Action<string>? onLine = null,
            ConsoleProgressBar? bar = null)
        {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;

            // Đảm bảo log nằm đúng thư mục logs
            string logDir = work.LogsDir;
            Utils.EnsureDir(logDir);
            string logPath = Path.Combine(logDir, "ffmpeg-log.txt");
            using var logWriter = new StreamWriter(logPath, append: true, Encoding.UTF8);

            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };

            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                var line = e.Data;
                logWriter.WriteLine(line);
                onLine?.Invoke(line);
                var v = tryParseProgress(line);
                if (v.HasValue) bar?.Report(v.Value);
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                var line = e.Data;
                logWriter.WriteLine(line);
                onLine?.Invoke(line);
                var v = tryParseProgress(line);
                if (v.HasValue) bar?.Report(v.Value);
            };

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            bar?.Done();
            return p.ExitCode;
        }
    }
}
