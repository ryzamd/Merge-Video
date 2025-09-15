using Spectre.Console;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace MergeVideo
{
    // Dùng Spectre.Console cho UI, giữ tên lớp cũ để không phải đổi callsite.
    public sealed class ConsoleProgressBar : IDisposable
    {
        // ===== Shim instance API (no-op) để tương thích nếu có chỗ còn 'new ConsoleProgressBar' =====
        public ConsoleProgressBar(string label, int width = 40) { /* no-op */ }
        public void Report(double pct01) { /* no-op */ }
        public void Done() { /* no-op */ }
        public void Dispose() { /* no-op */ }

        // ======== Spectre helpers ========
        static string E(string s) => Markup.Escape(s ?? string.Empty);

        public static void WriteHeader(string header)
            => AnsiConsole.MarkupLine($"[bold]{E(header)}[/]");

        public static void WriteStep(string text)
            => AnsiConsole.MarkupLine($"-> {E(text)}");

        // 1 thanh tổng theo đếm item (Normalize all)
        public static Task RunSingleBarByCountAsync(
            string label,
            int totalCount,
            Func<Func<int, Task>, Task> work,
            ConcurrentQueue<string>? logs = null)
        {
            return AnsiConsole.Progress()
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(new ProgressColumn[]
                {
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                })
                .StartAsync(async ctx =>
                {
                    var safe = string.IsNullOrWhiteSpace(label) ? " " : label; // Spectre cấm rỗng
                    var task = ctx.AddTask(E(safe), maxValue: totalCount);

                    async Task Report(int done)
                    {
                        task.Value = Math.Clamp(done, 0, totalCount);
                        // mỗi lần cập nhật tiến độ, xả log nếu có
                        if (logs != null)
                        {
                            while (logs.TryDequeue(out var line))
                                AnsiConsole.WriteLine(line);
                        }
                        await Task.Yield();
                    }

                    // chạy công việc song song
                    var workTask = work(Report);

                    // vòng bơm log trong khi chờ work kết thúc
                    while (!workTask.IsCompleted)
                    {
                        if (logs != null)
                        {
                            while (logs.TryDequeue(out var line))
                                AnsiConsole.WriteLine(line);
                        }
                        await Task.WhenAny(workTask, Task.Delay(50));
                    }
                    await workTask;

                    // xả nốt log còn lại
                    if (logs != null)
                    {
                        while (logs.TryDequeue(out var line))
                            AnsiConsole.WriteLine(line);
                    }

                    task.Value = task.MaxValue;
                });
        }

        // Mỗi output một progress bar
        public static async Task RunPerOutputBarsAsync<T>(
            string groupHeader,
            IEnumerable<T> outputs,
            Func<T, string> title,                        // tên file hiển thị
            Func<T, Func<double, Task>, Task> runner)     // runner(item, onPercent 0..100)
        {
            if (!string.IsNullOrWhiteSpace(groupHeader))
                WriteHeader(groupHeader);

            foreach (var item in outputs)
            {
                WriteStep($"Creating {title(item)}");

                await AnsiConsole.Progress()
                    .AutoClear(false)
                    .HideCompleted(false)
                    .Columns(new ProgressColumn[]
                    {
                        new TaskDescriptionColumn(),
                        new ProgressBarColumn(),
                        new PercentageColumn(),
                    })
                    .StartAsync(async ctx =>
                    {
                        var t = ctx.AddTask($"[dim]{E(title(item))}[/]", maxValue: 100);
                        async Task OnPercent(double p)
                        {
                            t.Value = Math.Clamp(p, 0, 100);
                            await Task.Yield();
                        }
                        await runner(item, OnPercent);
                        t.Value = t.MaxValue;
                    });
            }
        }

        // Chạy ffmpeg với -progress pipe:1/2 và gọi onPercent(0..100)
        public static async Task RunFfmpegWithProgressAsync(
            string ffmpegPath,
            string ffmpegArgs,            // KHÔNG kèm -progress/-nostats
            double totalDurationSec,
            string workingDir,
            Func<double, Task> onPercent) // 0..100
        {
            if (string.IsNullOrWhiteSpace(ffmpegPath))
                ffmpegPath = "ffmpeg";

            // ép output progress ra stdout
            var args = $"-hide_banner -y -nostdin -nostats -progress pipe:1 {ffmpegArgs}";

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDir) ? Environment.CurrentDirectory : workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,   // progress
                RedirectStandardError = true,    // để debug
                CreateNoWindow = true,
            };

            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            p.OutputDataReceived += async (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;

                // out_time_ms=123456789
                if (e.Data.StartsWith("out_time_ms=", StringComparison.OrdinalIgnoreCase))
                {
                    if (long.TryParse(e.Data.AsSpan("out_time_ms=".Length), out var us) && totalDurationSec > 0)
                    {
                        var sec = us / 1_000_000.0;
                        var pct = Math.Min(100.0, (sec / totalDurationSec) * 100.0);
                        await onPercent(pct);
                    }
                }
                // out_time=HH:MM:SS.mmm
                else if (e.Data.StartsWith("out_time=", StringComparison.OrdinalIgnoreCase))
                {
                    var s = e.Data["out_time=".Length..].Trim();
                    if (TimeSpan.TryParse(s, out var ts) && totalDurationSec > 0)
                    {
                        var pct = Math.Min(100.0, (ts.TotalSeconds / totalDurationSec) * 100.0);
                        await onPercent(pct);
                    }
                }
            };

            p.ErrorDataReceived += (_, __) => { /* optional: log stderr */ };
            p.Exited += (_, __) => tcs.TrySetResult(p.ExitCode);

            if (!p.Start())
                throw new InvalidOperationException("Cannot start ffmpeg.");

            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            var code = await tcs.Task.ConfigureAwait(false);
            if (code != 0)
                throw new InvalidOperationException($"ffmpeg exited with code {code}");
        }
    }
}
