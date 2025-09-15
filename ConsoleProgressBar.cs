using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace MergeVideo
{
    sealed class ConsoleProgressBar : IDisposable
    {
        // Global state management for all progress bars
        private static readonly object GlobalLock = new();
        private static readonly Dictionary<ConsoleProgressBar, BarState> ActiveBars = new();
        private static int BaseRow = -1;
        private static Timer? GlobalTimer;
        private static int MaxRowUsed = -1;

        // Individual bar state
        private class BarState
        {
            public int Row { get; set; }
            public double Progress { get; set; }
            public string Label { get; set; }
            public bool IsDisposed { get; set; }
            public DateTime LastUpdate { get; set; }

            public BarState(string label, int row)
            {
                Label = label;
                Row = row;
                Progress = 0;
                IsDisposed = false;
                LastUpdate = DateTime.Now;
            }
        }

        private readonly BarState _state;
        private readonly int _width;
        private bool _disposed;

        public ConsoleProgressBar(string label, int width = 40)
        {
            Label = label ?? "Progress";
            _width = Math.Max(10, Math.Min(width, 60)); // Clamp width

            lock (GlobalLock)
            {
                // Initialize base row on first bar
                if (BaseRow < 0)
                {
                    BaseRow = Console.CursorTop;
                    Console.WriteLine(); // Reserve space
                }

                // Find next available row
                int row = 0;
                while (ActiveBars.Values.Any(b => b.Row == row && !b.IsDisposed))
                    row++;

                _state = new BarState(label!, row);
                ActiveBars[this] = _state;
                MaxRowUsed = Math.Max(MaxRowUsed, row);

                // Ensure we have enough space
                EnsureConsoleSpace(row);

                // Start global timer if not running
                if (GlobalTimer == null)
                {
                    GlobalTimer = new Timer(_ => UpdateAllBars(), null, 0, 100);
                }
            }
        }

        public string Label { get; }

        public void Report(double pct)
        {
            if (_disposed) return;

            pct = Math.Clamp(pct, 0, 1);
            lock (GlobalLock)
            {
                if (_state != null)
                {
                    _state.Progress = pct;
                    _state.LastUpdate = DateTime.Now;
                }
            }
        }

        public void Done()
        {
            if (_disposed) return;
            Report(1.0);
            Thread.Sleep(100); // Give timer a chance to render final state
            Dispose();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (GlobalLock)
            {
                if (_state != null)
                {
                    _state.IsDisposed = true;

                    // Clear the line
                    try
                    {
                        var (currentLeft, currentTop) = Console.GetCursorPosition();
                        int targetRow = BaseRow + _state.Row;

                        if (targetRow < Console.BufferHeight)
                        {
                            Console.SetCursorPosition(0, targetRow);
                            Console.Write(new string(' ', Console.BufferWidth - 1));
                            Console.SetCursorPosition(currentLeft, currentTop);
                        }
                    }
                    catch { /* Ignore console errors */ }
                }

                ActiveBars.Remove(this);

                // Stop timer if no active bars
                if (!ActiveBars.Any(kvp => !kvp.Value.IsDisposed))
                {
                    GlobalTimer?.Dispose();
                    GlobalTimer = null;
                }
            }
        }

        private static void EnsureConsoleSpace(int row)
        {
            try
            {
                int neededRow = BaseRow + row + 1;
                while (Console.CursorTop < neededRow)
                {
                    Console.WriteLine();
                }
            }
            catch { /* Ignore console errors */ }
        }

        private static void UpdateAllBars()
        {
            lock (GlobalLock)
            {
                if (BaseRow < 0) return;

                var (savedLeft, savedTop) = (0, 0);
                try
                {
                    (savedLeft, savedTop) = Console.GetCursorPosition();
                }
                catch { return; }

                foreach (var kvp in ActiveBars.ToList())
                {
                    var bar = kvp.Key;
                    var state = kvp.Value;

                    if (state.IsDisposed) continue;

                    try
                    {
                        RenderBar(state, bar._width);
                    }
                    catch { /* Ignore render errors */ }
                }

                try
                {
                    Console.SetCursorPosition(savedLeft, savedTop);
                }
                catch { /* Ignore positioning errors */ }
            }
        }

        private static void RenderBar(BarState state, int barWidth)
        {
            int targetRow = BaseRow + state.Row;
            if (targetRow >= Console.BufferHeight) return;

            // Build progress bar string
            int filled = (int)Math.Round(state.Progress * barWidth);
            string bar = new string('█', filled) + new string('░', barWidth - filled);
            int percent = (int)Math.Round(state.Progress * 100);

            // Truncate label if needed
            int maxLabelLen = Console.BufferWidth - barWidth - 10; // Reserve space for bar and percentage
            string label = state.Label;
            if (label.Length > maxLabelLen && maxLabelLen > 3)
                label = label.Substring(0, maxLabelLen - 3) + "...";

            string line = $"{label} [{bar}] {percent,3}%";

            // Ensure line fits console width
            if (line.Length >= Console.BufferWidth)
                line = line.Substring(0, Console.BufferWidth - 1);

            // Pad to clear any previous content
            line = line.PadRight(Math.Min(Console.BufferWidth - 1, 120));

            Console.SetCursorPosition(0, targetRow);
            Console.Write(line);
        }

        public static void ResetRegion()
        {
            lock (GlobalLock)
            {
                // Clear all active bars
                foreach (var bar in ActiveBars.Keys.ToList())
                {
                    bar.Dispose();
                }

                ActiveBars.Clear();
                BaseRow = -1;
                MaxRowUsed = -1;

                GlobalTimer?.Dispose();
                GlobalTimer = null;

                // Move cursor to next line
                try
                {
                    Console.WriteLine();
                }
                catch { /* Ignore */ }
            }
        }

        // Static runner for FFmpeg with progress tracking
        public static int RunProcessWithProgress(
            ProcessStartInfo psi,
            Func<string, double?> tryParseProgress,
            string logsRootDir,
            string label,
            Action<string>? onLine,
            ConsoleProgressBar? bar)
        {
            psi.UseShellExecute = false;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            psi.CreateNoWindow = true;

            var outputBuffer = new StringBuilder(64 * 1024);
            var errorBuffer = new StringBuilder(64 * 1024);
            object bufferLock = new();

            bool ownsBar = false;
            if (bar == null)
            {
                bar = new ConsoleProgressBar(label);
                ownsBar = true;
            }

            try
            {
                using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    lock (bufferLock) { outputBuffer.AppendLine(e.Data); }
                    onLine?.Invoke(e.Data);
                };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    lock (bufferLock) { errorBuffer.AppendLine(e.Data); }

                    var progress = tryParseProgress?.Invoke(e.Data);
                    if (progress.HasValue)
                    {
                        bar.Report(progress.Value);
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();

                bar.Done();

                // Log errors if process failed
                if (process.ExitCode != 0 && !string.IsNullOrEmpty(logsRootDir))
                {
                    try
                    {
                        var logsDir = System.IO.Path.Combine(logsRootDir, "logs");
                        System.IO.Directory.CreateDirectory(logsDir);

                        var safeName = Regex.Replace(label ?? "process", @"[^A-Za-z0-9_.-]+", "_");
                        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff");
                        var logPath = System.IO.Path.Combine(logsDir, $"error-{safeName}-{timestamp}.log");

                        string logContent;
                        lock (bufferLock)
                        {
                            logContent = $"Exit Code: {process.ExitCode}\n\n" +
                                        $"=== STDOUT ===\n{outputBuffer}\n\n" +
                                        $"=== STDERR ===\n{errorBuffer}";
                        }

                        System.IO.File.WriteAllText(logPath, logContent, new UTF8Encoding(false));
                    }
                    catch { /* Best effort logging */ }
                }

                return process.ExitCode;
            }
            finally
            {
                if (ownsBar)
                    bar.Dispose();
            }
        }
    }
}