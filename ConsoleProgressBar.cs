using System;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MergeVideo
{
    sealed class ConsoleProgressBar : IDisposable
    {
        // ====== Quản lý slot (mỗi bar một slot), vẽ bám "đáy cửa sổ" động ======
        private static readonly object G = new();
        private static int _regionTop = -1;           // hàng đầu vùng progress (cố định)
        private static int _nextOffset = 0;           // next compact row offset
        private static readonly SortedSet<int> _freeOffsets = new(); // offsets reuse
        private static int _printedLines = 0;         // số dòng đã "đăt chỗ"

        private static int AcquireOffset()
        {
            lock (G)
            {
                if (_regionTop < 0) _regionTop = Console.CursorTop; // neo 1 lần
                int off = _freeOffsets.Count > 0 ? _freeOffsets.Min : _nextOffset++;
                if (_freeOffsets.Count > 0) _freeOffsets.Remove(off);

                // đảm bảo đã "in xuống dòng" đủ để có chỗ vẽ
                int need = off + 1;
                while (_printedLines < need)
                {
                    try
                    {
                        int row = Math.Min(_regionTop + _printedLines,
                        Math.Max(0, Console.BufferHeight - 1));
                        Console.SetCursorPosition(0, row);
                        Console.WriteLine();
                    }
                    catch { /* shell resize - bỏ qua */ }

                    _printedLines++;
                }
                return off;
            }
        }

        private static void ReleaseOffset(int off)
        {
            lock (G) { _freeOffsets.Add(off); }
        }

        // ====== Một thanh ======
        private readonly object _lock = new();
        private readonly int _width;
        private readonly Timer _timer;
        private double _targetPct = 0;
        private double _drawnPct = -1;
        private bool _disposed = false;
        private readonly int _offset;
        private int Row => Math.Min(_regionTop + _offset, Math.Max(0, Console.BufferHeight - 1));

        public ConsoleProgressBar(string label, int width = 40)
        {
            Label = label;
            _width = Math.Max(10, width);
            _offset = AcquireOffset();
            _timer = new Timer(_ => TickSafe(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(125));
        }

        public string Label { get; }

        public void Report(double pct)
        {
            if (double.IsNaN(pct) || double.IsInfinity(pct)) pct = 0;
            pct = Math.Clamp(pct, 0, 1);
            lock (_lock) _targetPct = pct;
        }

        private void TickSafe()
        {
            try { Tick(); }
            catch { /* tránh crash nếu console bị resize/đổi buffer giữa chừng */ }
        }

        private void Tick()
        {
            if (_disposed) return;

            double pct;
            lock (_lock) pct = _targetPct;
            if (pct == _drawnPct) return;
            _drawnPct = pct;

            int filled = (int)Math.Round(pct * _width);
            string bar = new string('#', filled) + new string('-', _width - filled);
            int percent = (int)Math.Round(pct * 100);
            string text = $"{Label} [{bar}] {percent,3}%";

            int avail = Math.Max(10, Console.BufferWidth - 1);
            if (text.Length > avail) text = text[..avail];
            text = text.PadRight(avail);

            lock (G)
            {
                var (cx, cy) = Console.GetCursorPosition();
                int row = Row; // hàng cố định
                try
                {
                    Console.SetCursorPosition(0, row);
                    Console.Write(text);
                }
                catch { /* tránh crash nếu shell đang resize */ }
                try { Console.SetCursorPosition(cx, cy); } catch { }
            }
        }

        public void Done()
        {
            if (_disposed) return;
            Report(1);
            TickSafe();
            Dispose();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _timer?.Dispose(); } catch { }
            ReleaseOffset(_offset);
        }

        // ========= Runner FFmpeg: chỉ ghi log khi lỗi =========
        public static int RunProcessWithProgress(
            ProcessStartInfo psi,
            Func<string, double?> tryParseProgress,
            string logsRootDir,
            string label,
            Action<string>? onLine,
            ConsoleProgressBar bar)
        {
            psi.UseShellExecute = false;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            psi.CreateNoWindow = true;

            var buf = new StringBuilder(64 * 1024);
            object gate = new();

            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (gate) buf.AppendLine(e.Data);
                onLine?.Invoke(e.Data);
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (gate) buf.AppendLine(e.Data);
                var v = tryParseProgress?.Invoke(e.Data);
                if (v.HasValue) bar.Report(v.Value);
            };

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            bar.Done();

            if (p.ExitCode != 0)
            {
                try
                {
                    var logsDir = System.IO.Path.Combine(logsRootDir, "logs");
                    System.IO.Directory.CreateDirectory(logsDir);
                    var safe = Regex.Replace(label ?? "ffmpeg", @"[^A-Za-z0-9_.-]+", "_");
                    var logPath = System.IO.Path.Combine(
                        logsDir, $"ffmpeg-error-{DateTime.UtcNow:yyyyMMdd_HHmmssfff}-{safe}.log");
                    System.IO.File.WriteAllText(logPath, buf.ToString(), new UTF8Encoding(false));
                }
                catch { }
            }
            return p.ExitCode;
        }

        // Dùng khi chuyển “pha” (ví dụ sau rename, trước normalize) để dọn vùng cũ
        public static void ResetRegion()
        {
            lock (G)
            {
                _regionTop = -1;
                _nextOffset = 0;
                _printedLines = 0;
                _freeOffsets.Clear();

                try
                {
                    int row = Math.Min(Console.CursorTop + 1,
                    Math.Max(0, Console.BufferHeight - 1));
                    Console.SetCursorPosition(0, row);
                } catch { }
            }
        }
    }
}
