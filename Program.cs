// VideoSubtitleConcat - .NET 8 Console (with console progress bars) - UPDATED
// - VTT parser: supports mm:ss.mmm or hh:mm:ss.mmm and comma/dot milliseconds
// - Mixed SRT/VTT: auto unify by converting to target before merge
// - Missing subtitles: CSV + OnMissingSubtitle mode (Skip/WarnOnly/CreateEmptyFile)
// - Interactive console mode when no args: loop Y/N
// NOTE: EPPlus license init kept. No MKV muxing of subtitles (explicitly excluded).

using OfficeOpenXml; // EPPlus
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MergeVideo
{
    internal static class Program
    {
        // ====== New runtime options ======
        internal enum OnMissingSubtitleMode { Skip, WarnOnly, CreateEmptyFile }
        internal sealed class RuntimeOptions
        {
            public OnMissingSubtitleMode OnMissingSubtitle { get; set; } = OnMissingSubtitleMode.Skip;
            public bool StrictMapping { get; set; } = false;       // if true -> fail fast when missing
            public string? TargetFormatForced { get; set; } = null; // ".srt" | ".vtt" | null (=auto by majority)
        }

        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            // EPPlus 8+: set License before using ExcelPackage
            ExcelPackage.License.SetNonCommercialPersonal("Your Name");

            try
            {
                if (args.Length == 0)
                {
                    // ===== Interactive console mode =====
                    while (true)
                    {
                        Console.Write("Root path (leave empty to exit): ");
                        var inp = Console.ReadLine();
                        if (string.IsNullOrWhiteSpace(inp)) return 0;
                        var parentFolder = Path.GetFullPath(inp);
                        if (!Directory.Exists(parentFolder))
                        {
                            Console.WriteLine("  ! Folder not found. Try again.\n");
                            continue;
                        }

                        var opts = new RuntimeOptions();
                        Console.Write("OnMissingSubtitle [S]kip/[W]arnOnly/[C]reateEmptyFile (default S): ");
                        var m = (Console.ReadLine() ?? "").Trim().ToUpperInvariant();
                        if (m == "W") opts.OnMissingSubtitle = OnMissingSubtitleMode.WarnOnly;
                        else if (m == "C") opts.OnMissingSubtitle = OnMissingSubtitleMode.CreateEmptyFile;
                        else opts.OnMissingSubtitle = OnMissingSubtitleMode.Skip;

                        // optional strict toggle
                        Console.Write("Strict mapping? stop when missing (y/N): ");
                        var sm = (Console.ReadLine() ?? "").Trim().ToUpperInvariant();
                        opts.StrictMapping = sm == "Y";

                        // optional forced target format
                        Console.Write("Force target subtitle format [.srt/.vtt/empty=auto]: ");
                        var tf = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
                        if (tf == ".srt" || tf == "srt") opts.TargetFormatForced = ".srt";
                        else if (tf == ".vtt" || tf == "vtt") opts.TargetFormatForced = ".vtt";
                        else opts.TargetFormatForced = null;

                        try
                        {
                            var exit = RunOneJob(parentFolder, opts);
                            Console.WriteLine($"Job finished with exit code {exit}.");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("FATAL: " + ex);
                        }

                        Console.Write("Run another job? (Y/N): ");
                        var ans = (Console.ReadLine() ?? "").Trim().ToUpperInvariant();
                        if (ans != "Y") break;
                    }
                    return 0;
                }
                else
                {
                    // ===== Non-interactive (legacy) =====
                    var parentFolder = Path.GetFullPath(args[0]);
                    if (!Directory.Exists(parentFolder))
                    {
                        Console.WriteLine($"Parent folder not found: {parentFolder}");
                        return 2;
                    }
                    return RunOneJob(parentFolder, new RuntimeOptions());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL: " + ex);
                return 99;
            }
        }

        private static int RunOneJob(string parentFolder, RuntimeOptions opts)
        {
            var cfg = Config.DefaultWithToolPaths();
            var work = WorkDirs.Prepare(parentFolder);
            var logger = new ErrorLogger(work.ReportDir);
            var excel = new ExcelLoggers(work.LogsDir);

            // Persist parent path (idempotent help)
            File.WriteAllText(Path.Combine(work.PathDir, "parent_folder.txt"), parentFolder, new UTF8Encoding(false));

            // 1) SCAN + RENAME
            Console.WriteLine("[1/5] Scanning sub-folders & renaming files...");
            var renameState = new RenameState();
            var renamer = new Renamer(cfg, work, logger, excel, opts);
            renamer.ScanAndRenameIfNeeded(parentFolder, renameState);

            // 2) CONCAT VIDEOS (audio-safe)
            var parentName = Path.GetFileName(parentFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var finalMkv = Path.Combine(work.Root, SanitizeFileName(parentName) + ".mkv");
            Console.WriteLine("[2/5] Concatenating videos (audio-safe) -> " + finalMkv);

            // Lấy danh sách clip gốc trong work/videos theo đúng thứ tự số
            var inputs = Directory.EnumerateFiles(work.VideosDir)
                .Where(IsVideo)
                .OrderBy(n => n, new NumericNameComparer())
                .ToList();
            if (inputs.Count == 0)
            {
                logger.Error("No videos found in work/videos to concatenate.");
                return 2;
            }

            // Dùng class mới: copy video, encode audio -> AAC (fix lỗi mất tiếng khi codec audio khác nhau)
            var opt = new AudioSafeConcatManager.Options(
                workDir: work.Root,
                outputFileName: SanitizeFileName(parentName) + ".mkv",
                ffmpegPath: cfg.FfmpegPath,     // hoặc null nếu có trong PATH
                ffprobePath: cfg.FfprobePath,   // nếu có field; nếu không, bỏ qua -> "ffprobe" từ PATH
                segmentTimeSeconds: null,       // giữ BigMkvSplitter ở bước 4
                audioCodec: "aac",
                audioBitrateKbps: 192,
                audioChannels: 2,
                audioSampleRate: 48000,
                selectiveNormalize: true,       // CHỈ encode khi cần
                maxDegreeOfParallelism: 0       // 0 = auto theo CPU
            );
            var manager = new AudioSafeConcatManager(opt, s => Console.WriteLine(s));
            manager.RunAsync(inputs).GetAwaiter().GetResult();

            var normDir = Path.Combine(work.Root, "norm");
            var normList = Directory.Exists(normDir)
                ? Directory.EnumerateFiles(normDir, "*.norm.mkv")
                    .OrderBy(n => n, new NumericNameComparer())
                    .Select(Path.GetFullPath)
                    .ToList()
                : inputs.Select(Path.GetFullPath).ToList(); // fallback nếu norm chưa có (hiếm)

            var manifest = Path.Combine(work.LogsDir, "concat_sources.txt");
            File.WriteAllLines(manifest, normList, new UTF8Encoding(false));

            // Giữ nguyên ghi timeline như trước (phục vụ kiểm tra/ghi log)
            TimelineHelper.WriteConcatTimelineWithOriginalNames(
                work.LogsDir,
                work.VideosDir,
                IsVideo,
                path => GetVideoDurationSeconds(cfg, path)
            );

            // 3) SUBTITLE MERGE (now supports auto-unify mixed SRT/VTT)
            Console.WriteLine("[3/5] Building single subtitle file...");
            var subMerger = new SubtitleMerger(cfg, work, logger, opts);
            subMerger.MergeIfNeeded(finalMkv, parentName);

            // 4) SPLIT IF >12h
            Console.WriteLine("[4/5] Checking duration & splitting if > 12h...");
            var splitter = new BigMkvSplitter(cfg, work, logger);
            splitter.SplitIfNeeded(finalMkv, parentName);

            // 5) DONE
            Console.WriteLine("[5/5] Done. Outputs in: " + work.Root);
            Console.WriteLine(" - Video: " + finalMkv + (File.Exists(finalMkv) ? " (exists)" : ""));
            var outSub = subMerger.GetOutputSubtitlePath(parentName);
            if (outSub != null && File.Exists(outSub)) Console.WriteLine(" - Subtitle: " + outSub);

            excel.FlushAndSave();
            return 0;
        }

        // ---------------- Configuration & Constants ----------------
        private static readonly string[] VideoExt = new[] { ".mp4", ".mkv", ".m4v", ".webm", ".mov", ".avi", ".wmv", ".ts" };
        private static readonly string[] SubExt = new[] { ".srt", ".vtt" /* extendable: .ass/.ssa requires different parser */ };
        private const double SplitThresholdHours = 12.0; // YouTube 12h limit
        private const double SplitChunkHours = 11.0;      // create parts of ~11h

        // ---------------- Utils ----------------
        internal static bool IsVideo(string path)
            => VideoExt.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        internal static bool IsSubtitle(string path)
            => SubExt.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        internal static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
            return cleaned.Trim().TrimEnd('.')
                          .Replace("​", string.Empty)
                          .Replace("﻿", string.Empty);
        }

        internal static string Quote(string path)
            => (path.IndexOfAny(new[] { ' ', '(', ')', '&', '\'', '[', ']', '{', '}', ';' }) >= 0)
               ? '"' + path + '"' : path;

        internal static int NumericPrefixOrDefault(string name)
        {
            var m = Regex.Match(name, @"^\s*(?<n>\d+)");
            if (m.Success && int.TryParse(m.Groups["n"].Value, out int n)) return n;
            return int.MaxValue; // non-number-leading names go to end
        }

        internal static IEnumerable<string> GetSubDirsSorted(string parent)
            => Directory.EnumerateDirectories(parent)
                .OrderBy(dir => NumericPrefixOrDefault(Path.GetFileName(dir)))
                .ThenBy(dir => Path.GetFileName(dir), StringComparer.CurrentCultureIgnoreCase);

        internal static string GetFfprobeDurationSeconds(Config cfg, string mediaPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = cfg.FfprobePath,
                Arguments = $"-v error -show_entries format=duration -of default=nk=1:nw=1 {Quote(mediaPath)}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new Exception($"ffprobe failed for {mediaPath}: {err}");
            return output.Trim();
        }

        internal static double GetVideoDurationSeconds(Config cfg, string mediaPath)
        {
            var s = GetFfprobeDurationSeconds(cfg, mediaPath);
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return d;
            if (double.TryParse(s, out d)) return d;
            throw new Exception($"Unable to parse duration: '{s}' for file {mediaPath}");
        }

        internal static void EnsureDir(string dir)
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        internal static string WithExt(string pathNoExt, string extWithDot)
            => pathNoExt + extWithDot;

        internal static string ZeroPad(int index, int width)
            => index.ToString("D" + width);

        // ================= Console progress helpers =================
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
        }

        static int RunProcessWithProgress(
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
            EnsureDir(logDir);
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


        // ---------------- WorkDirs ----------------
        internal class WorkDirs
        {
            public string Root { get; init; } = default!;
            public string VideosDir { get; init; } = default!;
            public string SubsDir { get; init; } = default!;
            public string PathDir { get; init; } = default!;
            public string LogsDir { get; init; } = default!;
            public string ReportDir { get; init; } = default!;
            public string FilesDir { get; init; } = default!;

            public static WorkDirs Prepare(string parent)
            {
                var workRoot = Path.Combine(parent, "work");
                EnsureDir(workRoot);
                var wd = new WorkDirs
                {
                    Root = workRoot,
                    VideosDir = Path.Combine(workRoot, "videos"),
                    SubsDir = Path.Combine(workRoot, "subtitles"),
                    PathDir = Path.Combine(workRoot, "path"),
                    LogsDir = Path.Combine(workRoot, "logs"),
                    ReportDir = Path.Combine(workRoot, "report"),
                    FilesDir = Path.Combine(workRoot, "files")
                };
                EnsureDir(wd.VideosDir);
                EnsureDir(wd.SubsDir);
                EnsureDir(wd.PathDir);
                EnsureDir(wd.LogsDir);
                EnsureDir(wd.ReportDir);
                EnsureDir(wd.FilesDir);
                return wd;
            }
        }

        // ---------------- Config ----------------
        internal class Config
        {
            public string FfmpegPath { get; init; } = @"C:\ffmpeg\bin\ffmpeg.exe";
            public string FfprobePath { get; init; } = @"C:\ffmpeg\bin\ffprobe.exe";
            public string MkvMergePath { get; init; } = @"C:\Program Files\MKVToolNix\mkvmerge.exe";

            public static Config DefaultWithToolPaths() => new Config();
        }

        // ---------------- Error Logger ----------------
        internal class ErrorLogger
        {
            private readonly string _file;
            private readonly object _lock = new object();
            public ErrorLogger(string reportDir)
            {
                _file = Path.Combine(reportDir, "errors.txt");
            }
            public void Warn(string message)
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] WARN: {message}";
                Console.WriteLine(line);
                lock (_lock)
                {
                    File.AppendAllText(_file, line + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            public void Error(string message)
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR: {message}";
                Console.WriteLine(line);
                lock (_lock)
                {
                    File.AppendAllText(_file, line + Environment.NewLine, new UTF8Encoding(false));
                }
            }
        }

        // ---------------- Excel Loggers ----------------
        internal class ExcelLoggers
        {
            private readonly string _logsDir;
            private readonly ExcelPackage _pkgVideo;
            private readonly ExcelWorksheet _wsVideo;
            private readonly ExcelPackage _pkgSub;
            private readonly ExcelWorksheet _wsSub;
            private int _rowV = 1, _rowS = 1; // headers

            public ExcelLoggers(string logsDir)
            {
                _logsDir = logsDir;
                _pkgVideo = new ExcelPackage();
                _wsVideo = _pkgVideo.Workbook.Worksheets.Add("rename-video");
                _wsVideo.Cells[_rowV, 1].Value = "STT";
                _wsVideo.Cells[_rowV, 2].Value = "Key";
                _wsVideo.Cells[_rowV, 3].Value = "Original";
                _wsVideo.Cells[_rowV, 4].Value = "Renamed";
                _rowV++;

                _pkgSub = new ExcelPackage();
                _wsSub = _pkgSub.Workbook.Worksheets.Add("rename-subtitle");
                _wsSub.Cells[_rowS, 1].Value = "STT";
                _wsSub.Cells[_rowS, 2].Value = "Key";
                _wsSub.Cells[_rowS, 3].Value = "Original";
                _wsSub.Cells[_rowS, 4].Value = "Renamed";
                _rowS++;
            }

            public void LogVideo(int stt, int key, string original, string renamed)
            {
                _wsVideo.Cells[_rowV, 1].Value = stt;
                _wsVideo.Cells[_rowV, 2].Value = key;
                _wsVideo.Cells[_rowV, 3].Value = original;
                _wsVideo.Cells[_rowV, 4].Value = renamed;
                _rowV++;
            }

            public void LogSubtitle(int stt, int key, string original, string renamed)
            {
                _wsSub.Cells[_rowS, 1].Value = stt;
                _wsSub.Cells[_rowS, 2].Value = key;
                _wsSub.Cells[_rowS, 3].Value = original;
                _wsSub.Cells[_rowS, 4].Value = renamed;
                _rowS++;
            }

            public void FlushAndSave()
            {
                var videoXlsx = Path.Combine(_logsDir, "rename-video.xlsx");
                var subXlsx = Path.Combine(_logsDir, "rename-subtitle.xlsx");

                // Có dữ liệu mới nếu lớn hơn dòng header (row = 2)
                bool hasVideoRows = _rowV > 2;
                bool hasSubRows = _rowS > 2;

                if (hasVideoRows || !File.Exists(videoXlsx))
                    _pkgVideo.SaveAs(new FileInfo(videoXlsx)); // ghi mới/ghi đè khi có data mới hoặc file chưa tồn tại

                if (hasSubRows || !File.Exists(subXlsx))
                    _pkgSub.SaveAs(new FileInfo(subXlsx));
            }

        }

        // ---------------- RenameState ----------------
        internal class RenameState
        {
            public int GlobalIndex = 0;
            public int PadWidth = 3;
            public int SubFolderKey = 0;
            public bool RenameCompleted = false;
        }

        // ---------------- Renamer (UPDATED: missing-subtitles CSV + CreateEmptyFile) ----------------
        internal class Renamer
        {
            private readonly Config _cfg;
            private readonly WorkDirs _work;
            private readonly ErrorLogger _log;
            private readonly ExcelLoggers _xlsx;
            private readonly RuntimeOptions _opts;

            private readonly List<string[]> _missingRows = new(); // Video, Duration(s), Index, Note

            public Renamer(Config cfg, WorkDirs work, ErrorLogger log, ExcelLoggers xlsx, RuntimeOptions opts)
            { _cfg = cfg; _work = work; _log = log; _xlsx = xlsx; _opts = opts; }

            public void ScanAndRenameIfNeeded(string parentFolder, RenameState state)
            {
                var existingRenamed = Directory.EnumerateFiles(_work.VideosDir)
                    .Select(Path.GetFileName)
                    .Where(n => Regex.IsMatch(n!, @"^\d+\..+"))
                    .Any();
                if (existingRenamed)
                {
                    Console.WriteLine("  - Detected existing renamed videos in work/videos, skipping rename stage.");
                    state.RenameCompleted = true;
                    return;
                }

                // First pass: count videos
                int totalVideos = 0;
                foreach (var sub in GetSubDirsSorted(parentFolder))
                    foreach (var f in Directory.EnumerateFiles(sub, "*", SearchOption.TopDirectoryOnly))
                        if (IsVideo(f)) totalVideos++;

                state.PadWidth = totalVideos >= 1000 ? 4 : 3;
                using var bar = new ConsoleProgressBar("Rename");
                int processed = 0;

                state.GlobalIndex = 0; state.SubFolderKey = 0;
                foreach (var sub in GetSubDirsSorted(parentFolder))
                {
                    state.SubFolderKey++;
                    var subName = Path.GetFileName(sub);
                    Console.WriteLine($"  - Processing sub-folder [{state.SubFolderKey}] {subName}");

                    var filesDest = Path.Combine(_work.FilesDir, SanitizeFileName(subName));
                    EnsureDir(filesDest);

                    var files = Directory.EnumerateFiles(sub, "*", SearchOption.TopDirectoryOnly).ToList();

                    var videos = files.Where(IsVideo)
                        .OrderBy(p => NumericPrefixOrDefault(Path.GetFileName(p)!))
                        .ThenBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase)
                        .ToList();
                    var subs = files.Where(IsSubtitle)
                        .OrderBy(p => NumericPrefixOrDefault(Path.GetFileName(p)!))
                        .ThenBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase)
                        .ToList();

                    var subMap = BuildSubtitleMap(videos, subs);

                    foreach (var v in videos)
                    {
                        state.GlobalIndex++;
                        var newStem = ZeroPad(state.GlobalIndex, state.PadWidth);

                        // Video
                        var vExt = Path.GetExtension(v);
                        var vNew = Path.Combine(_work.VideosDir, newStem + vExt.ToLowerInvariant());
                        File.Copy(v, vNew, overwrite: true);
                        _xlsx.LogVideo(state.GlobalIndex, state.SubFolderKey, Path.GetFileName(v)!, Path.GetFileName(vNew)!);

                        // Subtitle (if present)
                        if (subMap.TryGetValue(v, out var sPath) && !string.IsNullOrEmpty(sPath) && File.Exists(sPath))
                        {
                            var sExt = Path.GetExtension(sPath);
                            var sNew = Path.Combine(_work.SubsDir, newStem + sExt.ToLowerInvariant());
                            File.Copy(sPath, sNew, overwrite: true);
                            _xlsx.LogSubtitle(state.GlobalIndex, state.SubFolderKey, Path.GetFileName(sPath)!, Path.GetFileName(sNew)!);
                        }
                        else
                        {
                            // Missing subtitle
                            var sNewDefault = Path.Combine(_work.SubsDir, newStem + ".srt");
                            _xlsx.LogSubtitle(state.GlobalIndex, state.SubFolderKey, "(missing)", Path.GetFileName(sNewDefault)!);
                            _log.Warn($"Subtitle missing for video '{Path.GetFileName(v)}' -> expected index {newStem}");

                            // record CSV
                            double dur = 0;
                            try { dur = GetVideoDurationSeconds(_cfg, v); } catch { }
                            _missingRows.Add(new[] { Path.GetFileName(v)!, dur.ToString("0.###", CultureInfo.InvariantCulture), newStem, "missing" });

                            if (_opts.StrictMapping)
                                throw new Exception($"StrictMapping enabled and subtitle is missing for {Path.GetFileName(v)}");

                            if (_opts.OnMissingSubtitle == OnMissingSubtitleMode.CreateEmptyFile)
                            {
                                // Create minimal sidecar .srt (empty file)
                                try { File.WriteAllText(sNewDefault, string.Empty, new UTF8Encoding(false)); }
                                catch (Exception ex) { _log.Warn($"Failed to create empty subtitle '{sNewDefault}': {ex.Message}"); }
                            }
                        }

                        processed++;
                        if (totalVideos > 0)
                            bar.Report((double)processed / totalVideos, $"{newStem} {Path.GetFileName(v)}");
                    }

                    foreach (var other in files.Where(f => !IsVideo(f) && !IsSubtitle(f)))
                    {
                        try
                        {
                            var dest = Path.Combine(filesDest, Path.GetFileName(other)!);
                            File.Copy(other, dest, overwrite: true);
                        }
                        catch (Exception ex)
                        {
                            _log.Warn($"Failed to copy extra file '{other}' -> {ex.Message}");
                        }
                    }
                }

                // write missing-subtitles.csv if any
                if (_missingRows.Count > 0)
                {
                    var csv = Path.Combine(_work.ReportDir, "missing-subtitles.csv");
                    var sb = new StringBuilder();
                    sb.AppendLine("Video,Duration(s),Index,Note");
                    foreach (var r in _missingRows) sb.AppendLine(string.Join(",", r.Select(x => x.Replace(",", " "))));
                    File.WriteAllText(csv, sb.ToString(), new UTF8Encoding(false));
                }

                bar.Done("Renamed all");
                state.RenameCompleted = true;
            }

            private Dictionary<string, string> BuildSubtitleMap(List<string> videos, List<string> subs)
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var subsByNum = new Dictionary<int, List<string>>();
                foreach (var s in subs)
                {
                    var num = NumericPrefixOrDefault(Path.GetFileName(s)!);
                    if (!subsByNum.TryGetValue(num, out var list)) { list = new List<string>(); subsByNum[num] = list; }
                    list.Add(s);
                }

                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in videos)
                {
                    var vn = NumericPrefixOrDefault(Path.GetFileName(v)!);
                    if (subsByNum.TryGetValue(vn, out var cand))
                    {
                        var s = cand.FirstOrDefault(x => !used.Contains(x));
                        if (s != null) { map[v] = s; used.Add(s); continue; }
                    }
                }

                for (int i = 0; i < videos.Count; i++)
                {
                    if (map.ContainsKey(videos[i])) continue;
                    if (i < subs.Count)
                    {
                        var s = subs[i];
                        if (!used.Contains(s)) { map[videos[i]] = s; used.Add(s); }
                    }
                }

                return map;
            }
        }

        // ---------------- Video Concatenator (unchanged) ----------------
        internal class VideoConcatenator
        {
            private readonly Config _cfg; private readonly WorkDirs _work; private readonly ErrorLogger _log;
            public VideoConcatenator(Config cfg, WorkDirs work, ErrorLogger log) { _cfg = cfg; _work = work; _log = log; }

            public void ConcatIfNeeded(string finalMkv)
            {
                if (File.Exists(finalMkv))
                {
                    Console.WriteLine("  - Final MKV already exists, skipping concat.");
                    return;
                }

                var videoFiles = Directory.EnumerateFiles(_work.VideosDir)
                    .Where(IsVideo)
                    .OrderBy(n => n, new NumericNameComparer())
                    .ToList();

                if (videoFiles.Count == 0)
                {
                    _log.Error("No videos found in work/videos to concatenate.");
                    return;
                }

                // Pre-normalize …
                var normDir = Path.Combine(_work.Root, "videos_norm");
                EnsureDir(normDir);
                using (var barN = new ConsoleProgressBar("Normalize inputs"))
                {
                    int done = 0;
                    for (int i = 0; i < videoFiles.Count; i++)
                    {
                        var src = videoFiles[i];
                        var dst = Path.Combine(normDir, Path.GetFileNameWithoutExtension(src) + ".norm.mkv");
                        if (!File.Exists(dst))
                        {
                            var psiN = new ProcessStartInfo
                            {
                                FileName = _cfg.FfmpegPath,
                                Arguments = $"-hide_banner -y -i {Quote(src)} -map 0:v:0 -map 0:a:0? -sn -dn -c copy -fflags +genpts -avoid_negative_ts make_zero -max_interleave_delta 0 {Quote(dst)}"
                            };
                            int exitN = RunProcessWithProgress(psiN, _ => (double)done / Math.Max(1, videoFiles.Count), _work, bar: null);
                            if (exitN != 0) { _log.Warn($"normalize failed for '{src}', will use original"); dst = src; }
                        }
                        videoFiles[i] = dst;
                        done++; barN.Report((double)done / videoFiles.Count, Path.GetFileName(src)!);
                    }

                    var manifest = Path.Combine(_work.LogsDir, "concat_sources.txt");
                    File.WriteAllLines(manifest, videoFiles.Select(Path.GetFullPath), new UTF8Encoding(false));

                    barN.Done("Inputs ready");
                }

                var listPath = Path.Combine(_work.Root, "videos.txt");
                using (var sw = new StreamWriter(listPath, false, new UTF8Encoding(false)))
                    foreach (var v in videoFiles) sw.WriteLine($"file '{v.Replace("'", "'\''")}'");

                double totalDurSec = 0;
                foreach (var v in videoFiles)
                {
                    try { totalDurSec += GetVideoDurationSeconds(_cfg, v); }
                    catch (Exception ex) { _log.Warn($"ffprobe failed on '{v}': {ex.Message}"); }
                }
                if (totalDurSec <= 0) totalDurSec = 1;

                using var bar = new ConsoleProgressBar("FFmpeg concat");
                var psi = new ProcessStartInfo
                {
                    FileName = _cfg.FfmpegPath,
                    Arguments = $"-hide_banner -f concat -safe 0 -i {Quote(listPath)} -map 0:v -map 0:a? -c copy -fflags +genpts -avoid_negative_ts make_zero -progress pipe:1 -nostats {Quote(finalMkv)}"
                };

                double totalUs = totalDurSec * 1_000_000.0;
                double prevPct = 0;
                double? TryParseFfmpeg(string line)
                {
                    double? pct = null;
                    if (line.StartsWith("out_time_us=") && double.TryParse(line[12..], NumberStyles.Any, CultureInfo.InvariantCulture, out var us))
                        pct = Math.Clamp(us / totalUs, 0, 1);
                    else if (line.StartsWith("out_time_ms=") && double.TryParse(line[12..], NumberStyles.Any, CultureInfo.InvariantCulture, out var ms))
                        pct = Math.Clamp(ms / totalUs, 0, 1);
                    else if (line.StartsWith("out_time=") && TimeSpan.TryParseExact(line[9..], new[] { @"hh\:mm\:ss\.ffffff", @"hh\:mm\:ss\.fff" }, CultureInfo.InvariantCulture, out var ts))
                        pct = Math.Clamp(ts.TotalSeconds / totalDurSec, 0, 1);
                    if (line.StartsWith("progress=end")) pct = 1.0;
                    if (pct.HasValue && pct.Value < prevPct) pct = prevPct;
                    if (pct.HasValue) prevPct = pct.Value;
                    return pct;
                }

                var exit = RunProcessWithProgress(psi, TryParseFfmpeg, _work, bar: bar);
                if (exit != 0)
                {
                    _log.Error("ffmpeg concat failed");
                    throw new Exception("ffmpeg concat failed");
                }

                try
                {
                    var outDur = GetVideoDurationSeconds(_cfg, finalMkv);
                    var diff = Math.Abs(outDur - totalDurSec);
                    if (diff > 60)
                        _log.Warn($@"Duration mismatch after concat. Inputs sum = {TimeSpan.FromSeconds(totalDurSec):hh\:mm\:ss}, output = {TimeSpan.FromSeconds(outDur):hh\:mm\:ss}, diff = {TimeSpan.FromSeconds(diff):hh\:mm\:ss}.");
                }
                catch (Exception ex)
                {
                    _log.Warn($"Failed to ffprobe output for sanity check: {ex.Message}");
                }
            }
        }

        // ---------------- Subtitle Merger (UPDATED: VTT parser + mixed-format unify) ----------------
        internal class SubtitleMerger
        {
            private readonly Config _cfg; private readonly WorkDirs _work; private readonly ErrorLogger _log;
            private readonly RuntimeOptions _opts;

            public SubtitleMerger(Config cfg, WorkDirs work, ErrorLogger log, RuntimeOptions opts)
            { _cfg = cfg; _work = work; _log = log; _opts = opts; }

            public string? GetOutputSubtitlePath(string parentName)
            {
                var srt = Path.Combine(_work.Root, SanitizeFileName(parentName) + ".srt");
                var vtt = Path.Combine(_work.Root, SanitizeFileName(parentName) + ".vtt");
                if (File.Exists(srt)) return srt;
                if (File.Exists(vtt)) return vtt;
                return null;
            }

            public void MergeIfNeeded(string finalMkv, string parentName)
            {
                var subs = Directory.EnumerateFiles(_work.SubsDir)
                    .Where(IsSubtitle)
                    .OrderBy(n => n, new NumericNameComparer())
                    .ToList();

                if (subs.Count == 0)
                {
                    Console.WriteLine("  - No subtitles found, skipping subtitle merge.");
                    return;
                }

                string targetExt = ".srt";

                var outPath = Path.Combine(_work.Root, SanitizeFileName(parentName) + targetExt);
                if (File.Exists(outPath))
                {
                    Console.WriteLine("  - Final subtitle already exists, skipping.");
                    return;
                }

                // Mixed-format unify: convert inputs to target
                if (!subs.All(s => Path.GetExtension(s).Equals(targetExt, StringComparison.OrdinalIgnoreCase)))
                {
                    var convDir = Path.Combine(_work.Root, "subs_conv");
                    EnsureDir(convDir);
                    var convList = new List<string>(subs.Count);
                    using var barC = new ConsoleProgressBar("Convert subs");
                    for (int i = 0; i < subs.Count; i++)
                    {
                        var src = subs[i];
                        var dst = Path.Combine(convDir, Path.GetFileNameWithoutExtension(src) + targetExt);
                        if (!File.Exists(dst))
                        {
                            if (!ConvertSubtitle(src, dst, targetExt))
                            {
                                _log.Warn($"Failed to convert '{Path.GetFileName(src)}' to {targetExt}; will skip this subtitle.");
                                continue;
                            }
                        }
                        convList.Add(dst);
                        barC.Report((double)(i + 1) / subs.Count, Path.GetFileName(src)!);
                    }
                    barC.Done("Converted");
                    subs = convList.OrderBy(n => n, new NumericNameComparer()).ToList();
                }

                // ✅ ĐỌC MANIFEST (nếu có), fallback về work/videos nếu không tìm thấy
                var manifestPath = Path.Combine(_work.LogsDir, "concat_sources.txt");
                List<string> segments;
                if (File.Exists(manifestPath))
                {
                    segments = File.ReadAllLines(manifestPath, Encoding.UTF8)
                                   .Where(File.Exists)
                                   .ToList();
                }
                else
                {
                    segments = Directory.EnumerateFiles(_work.VideosDir)
                               .Where(IsVideo)
                               .OrderBy(n => n, new NumericNameComparer())
                               .ToList();
                }

                if (segments.Count != subs.Count)
                {
                    _log.Warn($"Subtitle count ({subs.Count}) != video count ({segments.Count}). We'll align best-effort by index.");
                }

                // ✅ offsetsMs dựa trên file trong manifest/segments
                var offsetsMs = new List<long>();
                long cumMs = 0;
                for (int i = 0; i < segments.Count; i++)
                {
                    offsetsMs.Add(cumMs);
                    try
                    {
                        var durSec = GetVideoDurationSeconds(_cfg, segments[i]);
                        cumMs += (long)Math.Round(durSec * 1000.0);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"ffprobe failed on '{segments[i]}': {ex.Message}. Using 0 offset increment.");
                    }
                }

                // (Tuỳ chọn, KHÁ KHUYÊN DÙNG) Sanity check tổng thời lượng offsets vs file MKV cuối
                try
                {
                    var totalVidSec = GetVideoDurationSeconds(_cfg, finalMkv);
                    var diff = Math.Abs((cumMs / 1000.0) - totalVidSec);
                    if (diff > 3.0)
                        _log.Warn($"[Sanity] Subtitle offsets sum {cumMs / 1000.0:F3}s != video {totalVidSec:F3}s (diff {diff:F3}s).");
                }
                catch { /* ignore */ }


                using var bar = new ConsoleProgressBar("Merge subs");
                if (targetExt.Equals(".srt", StringComparison.OrdinalIgnoreCase))
                    MergeSrt(subs, offsetsMs, outPath, bar);
                else
                    MergeVtt(subs, offsetsMs, outPath, bar);
                bar.Done("Subtitles merged");
            }

            private bool ConvertSubtitle(string srcPath, string dstPath, string targetExt)
            {
                var srcExt = Path.GetExtension(srcPath).ToLowerInvariant();
                try
                {
                    if (targetExt.Equals(".srt", StringComparison.OrdinalIgnoreCase))
                    {
                        // VTT -> SRT
                        if (srcExt == ".srt")
                        {
                            File.Copy(srcPath, dstPath, overwrite: true);
                            return true;
                        }
                        var lines = File.ReadAllLines(srcPath, Encoding.UTF8).ToList();
                        if (lines.Count > 0 && lines[0].Trim().Equals("WEBVTT", StringComparison.OrdinalIgnoreCase))
                        { lines.RemoveAt(0); while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0])) lines.RemoveAt(0); }
                        using var sw = new StreamWriter(dstPath, false, new UTF8Encoding(false));
                        int idx = 0, glob = 0;
                        while (idx < lines.Count)
                        {
                            string? cueId = null;
                            if (idx + 1 < lines.Count && IsVttTimelineLine(lines[idx + 1])) { cueId = lines[idx].Trim(); idx++; }
                            if (idx >= lines.Count) break;
                            if (!IsVttTimelineLine(lines[idx])) { idx++; continue; }
                            var tl = lines[idx++];
                            if (!TryParseVttTimeline(tl, out var startMs, out var endMs, out _)) continue;
                            var text = new List<string>();
                            while (idx < lines.Count && !string.IsNullOrWhiteSpace(lines[idx])) text.Add(lines[idx++]);
                            if (idx < lines.Count && string.IsNullOrWhiteSpace(lines[idx])) idx++;

                            glob++;
                            sw.WriteLine(glob.ToString());
                            sw.WriteLine(FormatSrtTimeline(startMs) + " --> " + FormatSrtTimeline(endMs));
                            foreach (var t in text) sw.WriteLine(t);
                            sw.WriteLine();
                        }
                        return true;
                    }
                    else
                    {
                        // SRT -> VTT
                        if (srcExt == ".vtt")
                        {
                            File.Copy(srcPath, dstPath, overwrite: true);
                            return true;
                        }
                        var lines = File.ReadAllLines(srcPath, Encoding.UTF8);
                        using var sw = new StreamWriter(dstPath, false, new UTF8Encoding(false));
                        sw.WriteLine("WEBVTT"); sw.WriteLine();
                        int idx = 0;
                        while (idx < lines.Length)
                        {
                            while (idx < lines.Length && string.IsNullOrWhiteSpace(lines[idx])) idx++;
                            if (idx >= lines.Length) break;
                            var maybeIndex = lines[idx++]; // ignore index
                            if (idx >= lines.Length) break;
                            var tl = lines[idx++];
                            if (!TryParseSrtTimeline(tl, out var startMs, out var endMs)) continue;
                            var text = new List<string>();
                            while (idx < lines.Length && !string.IsNullOrWhiteSpace(lines[idx])) text.Add(lines[idx++]);
                            if (idx < lines.Length && string.IsNullOrWhiteSpace(lines[idx])) idx++;
                            sw.WriteLine(FormatVttTimeline(startMs, "") + " --> " + FormatVttTimeline(endMs, ""));
                            foreach (var t in text) sw.WriteLine(t);
                            sw.WriteLine();
                        }
                        return true;
                    }
                }
                catch
                {
                    return false;
                }
            }

            private void MergeSrt(List<string> subFiles, List<long> offsetsMs, string outPath, ConsoleProgressBar bar)
            {
                using var sw = new StreamWriter(outPath, false, new UTF8Encoding(false));
                int globalIndex = 0;
                for (int i = 0; i < subFiles.Count; i++)
                {
                    var f = subFiles[i];
                    var offset = i < offsetsMs.Count ? offsetsMs[i] : offsetsMs.LastOrDefault();
                    var lines = File.ReadAllLines(f, Encoding.UTF8);
                    int pos = 0;
                    while (pos < lines.Length)
                    {
                        while (pos < lines.Length && string.IsNullOrWhiteSpace(lines[pos])) pos++;
                        if (pos >= lines.Length) break;
                        var _ = lines[pos++]; // index line (ignored)
                        if (pos >= lines.Length) break;
                        var timelineLine = lines[pos++];
                        if (!TryParseSrtTimeline(timelineLine, out var startMs, out var endMs))
                        {
                            bool found = false;
                            for (; pos < lines.Length; pos++)
                            {
                                if (TryParseSrtTimeline(lines[pos], out startMs, out endMs)) { pos++; found = true; break; }
                            }
                            if (!found) continue;
                        }

                        var textBuilder = new StringBuilder();
                        while (pos < lines.Length && !string.IsNullOrWhiteSpace(lines[pos]))
                        { textBuilder.AppendLine(lines[pos]); pos++; }
                        if (pos < lines.Length && string.IsNullOrWhiteSpace(lines[pos])) pos++;

                        globalIndex++;
                        var newStart = startMs + offset;
                        var newEnd = endMs + offset;
                        sw.WriteLine(globalIndex.ToString());
                        sw.WriteLine(FormatSrtTimeline(newStart) + " --> " + FormatSrtTimeline(newEnd));
                        sw.Write(textBuilder.ToString());
                        sw.WriteLine();
                    }

                    bar.Report((double)(i + 1) / subFiles.Count, Path.GetFileName(f));
                }
            }

            private void MergeVtt(List<string> subFiles, List<long> offsetsMs, string outPath, ConsoleProgressBar bar)
            {
                using var sw = new StreamWriter(outPath, false, new UTF8Encoding(false));
                sw.WriteLine("WEBVTT");
                sw.WriteLine();
                for (int i = 0; i < subFiles.Count; i++)
                {
                    var f = subFiles[i];
                    var offset = i < offsetsMs.Count ? offsetsMs[i] : offsetsMs.LastOrDefault();
                    var lines = File.ReadAllLines(f, Encoding.UTF8).ToList();
                    if (lines.Count > 0 && lines[0].Trim().Equals("WEBVTT", StringComparison.OrdinalIgnoreCase))
                    {
                        lines.RemoveAt(0); while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0])) lines.RemoveAt(0);
                    }
                    int pos = 0;
                    while (pos < lines.Count)
                    {
                        string? cueId = null;
                        if (pos + 1 < lines.Count && IsVttTimelineLine(lines[pos + 1])) { cueId = lines[pos].Trim(); pos++; }
                        if (pos >= lines.Count) break;
                        if (!IsVttTimelineLine(lines[pos])) { pos++; continue; }

                        var tl = lines[pos++];
                        if (!TryParseVttTimeline(tl, out var startMs, out var endMs, out var tailAttrs)) continue;

                        var textBuilder = new StringBuilder();
                        while (pos < lines.Count && !string.IsNullOrWhiteSpace(lines[pos])) { textBuilder.AppendLine(lines[pos]); pos++; }
                        if (pos < lines.Count && string.IsNullOrWhiteSpace(lines[pos])) pos++;

                        var newStart = startMs + offset;
                        var newEnd = endMs + offset;
                        if (!string.IsNullOrEmpty(cueId)) sw.WriteLine(cueId);
                        sw.WriteLine(FormatVttTimeline(newStart, tailAttrs) + " --> " + FormatVttTimeline(newEnd, tailAttrs));
                        sw.Write(textBuilder.ToString());
                        sw.WriteLine();
                    }

                    bar.Report((double)(i + 1) / subFiles.Count, Path.GetFileName(f));
                }
            }

            // ---- Timeline helpers (UPDATED VTT regex) ----
            private static bool TryParseSrtTimeline(string line, out long startMs, out long endMs)
            {
                var m = Regex.Match(line.Trim(), @"^(?<A>\d{2}:\d{2}:\d{2}[,\.]\d{3})\s*-->\s*(?<B>\d{2}:\d{2}:\d{2}[,\.]\d{3})");
                if (!m.Success) { startMs = endMs = 0; return false; }
                startMs = ParseHmsMs(m.Groups["A"].Value);
                endMs = ParseHmsMs(m.Groups["B"].Value);
                return true;
            }

            private static string FormatSrtTimeline(long ms)
            {
                var t = TimeSpan.FromMilliseconds(ms);
                return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00},{3:000}", (int)t.TotalHours, t.Minutes, t.Seconds, t.Milliseconds);
            }

            private static long ParseHmsMs(string hms)
            {
                hms = hms.Replace('.', ',');
                var m = Regex.Match(hms, @"^(?<H>\d{2}):(?<M>\d{2}):(?<S>\d{2}),(?<MS>\d{3})$");
                if (!m.Success) return 0;
                int H = int.Parse(m.Groups["H"].Value);
                int M = int.Parse(m.Groups["M"].Value);
                int S = int.Parse(m.Groups["S"].Value);
                int MS = int.Parse(m.Groups["MS"].Value);
                return (int)(H * 3600000L + M * 60000L + S * 1000L + MS);
            }

            private static bool IsVttTimelineLine(string line)
                => Regex.IsMatch(line.Trim(), @"^(?:\d{2}:)?\d{2}:\d{2}[,\.]\d{3}\s*-->\s*(?:\d{2}:)?\d{2}:\d{2}[,\.]\d{3}");

            private static bool TryParseVttTimeline(string line, out long startMs, out long endMs, out string tailAttrs)
            {
                startMs = endMs = 0; tailAttrs = string.Empty;
                var m = Regex.Match(
                    line.Trim(),
                    @"^(?<A>(?:\d{2}:)?\d{2}:\d{2}[,\.]\d{3})\s*-->\s*(?<B>(?:\d{2}:)?\d{2}:\d{2}[,\.]\d{3})(?<Tail>.*)$");
                if (!m.Success) return false;
                startMs = ParseVttToMs(m.Groups["A"].Value);
                endMs = ParseVttToMs(m.Groups["B"].Value);
                tailAttrs = m.Groups["Tail"].Value;
                return true;
            }

            private static long ParseVttToMs(string ts)
            {
                ts = ts.Replace(',', '.');
                var m = Regex.Match(ts, @"^(?:(?<H>\d{2}):)?(?<M>\d{2}):(?<S>\d{2})\.(?<MS>\d{3})$");
                if (!m.Success) return 0;
                int H = m.Groups["H"].Success ? int.Parse(m.Groups["H"].Value) : 0;
                int M = int.Parse(m.Groups["M"].Value);
                int S = int.Parse(m.Groups["S"].Value);
                int MS = int.Parse(m.Groups["MS"].Value);
                return (int)(H * 3600000L + M * 60000L + S * 1000L + MS);
            }

            private static string FormatVttTimeline(long ms, string tail)
            {
                var t = TimeSpan.FromMilliseconds(ms);
                var core = string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}.{3:000}", (int)t.TotalHours, t.Minutes, t.Seconds, t.Milliseconds);
                return core + tail;
            }
        }

        // ---------------- BigMkvSplitter (unchanged behavior) ----------------
        internal class BigMkvSplitter
        {
            private readonly Config _cfg; private readonly WorkDirs _work; private readonly ErrorLogger _log;
            public BigMkvSplitter(Config cfg, WorkDirs work, ErrorLogger log) { _cfg = cfg; _work = work; _log = log; }

            public void SplitIfNeeded(string finalMkv, string parentName)
            {
                if (!File.Exists(finalMkv)) return;

                double durSec;
                try { durSec = GetVideoDurationSeconds(_cfg, finalMkv); }
                catch (Exception ex) { _log.Warn($"ffprobe failed on final MKV: {ex.Message}. Skipping split check."); return; }

                const int SplitThresholdHours = 12;
                const int SplitChunkHours = 11;
                if (durSec <= SplitThresholdHours * 3600.0 + 1e-6) return;

                var normalized = Path.Combine(_work.Root, SanitizeFileName(parentName) + ".normalized.mkv");
                try { if (File.Exists(normalized)) File.Delete(normalized); } catch { }

                using (var barNorm = new ConsoleProgressBar("Normalize PTS"))
                {
                    double? TryParseFfmpeg(string line)
                    {
                        if (line.StartsWith("out_time_us=") && double.TryParse(line[12..], NumberStyles.Any, CultureInfo.InvariantCulture, out var us))
                            return Math.Clamp((us / 1_000_000.0) / durSec, 0, 1);
                        if (line.StartsWith("out_time_ms=") && double.TryParse(line[12..], NumberStyles.Any, CultureInfo.InvariantCulture, out var ms))
                            return Math.Clamp((ms / 1_000_000.0) / durSec, 0, 1);
                        if (line.StartsWith("out_time=") && TimeSpan.TryParse(line[9..], out var ts))
                            return Math.Clamp(ts.TotalSeconds / durSec, 0, 1);
                        return null;
                    }

                    var psiNorm = new ProcessStartInfo
                    {
                        FileName = _cfg.FfmpegPath,
                        Arguments = $"-hide_banner -y -i {Quote(finalMkv)} -map 0 -c copy -fflags +genpts -avoid_negative_ts make_zero -progress pipe:1 -nostats {Quote(normalized)}"
                    };
                    var exitNorm = RunProcessWithProgress(psiNorm, TryParseFfmpeg, _work, bar: barNorm);
                    if (exitNorm != 0)
                    {
                        _log.Warn($"ffmpeg normalize failed (exit {exitNorm}). Will try to split original file.");
                        normalized = finalMkv;
                    }
                }

                var outPattern = Path.Combine(_work.Root, SanitizeFileName(parentName) + " Part %02d.mkv");
                using (var barSplit = new ConsoleProgressBar("Split 11h"))
                {
                    double? TryParseFfmpeg2(string line)
                    {
                        if (line.StartsWith("out_time_us=") && double.TryParse(line[12..], NumberStyles.Any, CultureInfo.InvariantCulture, out var us))
                            return Math.Clamp((us / 1_000_000.0) / durSec, 0, 1);
                        if (line.StartsWith("out_time_ms=") && double.TryParse(line[12..], NumberStyles.Any, CultureInfo.InvariantCulture, out var ms))
                            return Math.Clamp((ms / 1_000_000.0) / durSec, 0, 1);
                        if (line.StartsWith("out_time=") && TimeSpan.TryParse(line[9..], out var ts))
                            return Math.Clamp(ts.TotalSeconds / durSec, 0, 1);
                        return null;
                    }

                    var psiSeg = new ProcessStartInfo
                    {
                        FileName = _cfg.FfmpegPath,
                        Arguments = $"-hide_banner -y -i {Quote(normalized)} -map 0 -c copy -f segment -segment_time {SplitChunkHours * 3600} -reset_timestamps 1 -progress pipe:1 -nostats {Quote(outPattern)}"
                    };
                    var exitSeg = RunProcessWithProgress(psiSeg, TryParseFfmpeg2, _work, bar: barSplit);
                    if (exitSeg != 0)
                    {
                        _log.Error($"ffmpeg segment split failed (exit {exitSeg}).");
                        return;
                    }
                }

                if (!Path.GetFullPath(normalized).Equals(Path.GetFullPath(finalMkv), StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(normalized); } catch { }
                }
                try { File.Delete(finalMkv); } catch { }

                var partFiles = Directory.EnumerateFiles(_work.Root, SanitizeFileName(parentName) + " Part *.mkv")
                    .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                if (partFiles.Count > 1)
                {
                    var splitTimes = new List<double> { 0 };
                    double acc = 0;
                    foreach (var part in partFiles)
                    {
                        try { acc += GetVideoDurationSeconds(_cfg, part); }
                        catch { }
                        splitTimes.Add(acc);
                    }
                    var srt = Path.Combine(_work.Root, SanitizeFileName(parentName) + ".srt");
                    var vtt = Path.Combine(_work.Root, SanitizeFileName(parentName) + ".vtt");
                    if (File.Exists(srt))
                    {
                        SubtitleSplitter.SplitSubtitleByTimeline(
                            srt,
                            Path.Combine(_work.Root, SanitizeFileName(parentName) + " Part {0:00}.srt"),
                            splitTimes,
                            ".srt"
                        );
                    }
                    else if (File.Exists(vtt))
                    {
                        SubtitleSplitter.SplitSubtitleByTimeline(
                            vtt,
                            Path.Combine(_work.Root, SanitizeFileName(parentName) + " Part {0:00}.vtt"),
                            splitTimes,
                            ".vtt"
                        );
                    }
                }
            }
        }
    }
}
