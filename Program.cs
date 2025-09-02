// VideoSubtitleConcat - .NET 8 Console (with console progress bars)
// ------------------------------------------------------------
// Features:
// 0) Create ./work structure (videos, subtitles, path, logs, report, files)
// 1) Scan all sub-folders of the provided parent folder, rename videos & subtitles
//    to a global increasing index (001..NNN with adaptive zero-padding),
//    copy them into ./work/videos and ./work/subtitles respectively.
//    Non-video/subtitle files (.txt, .pdf, .html, etc.) are preserved into
//    ./work/files/<OriginalSubFolderName>/
// 2) Concatenate all renamed videos (no re-encode) into a single MKV named
//    after the parent folder using FFmpeg concat demuxer. Live progress via
//    `-progress pipe:1` parsing of `out_time_*`. 
// 3) Build a single subtitle file by time-shifting & stitching (SRT/VTT).
// 4) Detailed Excel logs (EPPlus 8) in ./work/logs/rename-video.xlsx and rename-subtitle.xlsx
// 5) All errors/warnings appended to ./work/report/errors.txt
// 6) Paths to external tools (FFmpeg + FFprobe + MKVToolNix) configurable below
// 7) Idempotent: skip already-completed steps when possible
// 8) If final MKV duration > 12h, split into ~11h parts via mkvmerge `--gui-mode`
//    with a progress bar (#GUI#progress parsing), then rename parts to "Part N".
// ------------------------------------------------------------
// NuGet deps: EPPlus (for Excel). Install:
//   dotnet add package EPPlus
// EPPlus 8 requires setting license via ExcelPackage.License.* before use.
// ------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Timers;
using OfficeOpenXml; // EPPlus

namespace MergeVideo
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            // EPPlus 8+: set License before using ExcelPackage
            // Choose ONE of these depending on your case:
            ExcelPackage.License.SetNonCommercialPersonal("Your Name");
            // ExcelPackage.License.SetNonCommercialOrganization("Your Organization");
            // ExcelPackage.License.SetCommercial("<YOUR_LICENSE_KEY>");

            try
            {
                if (args.Length == 0)
                {
                    Console.WriteLine("Usage: MergeVideo <ParentFolderPath>");
                    Console.WriteLine("Example: MergeVideo \"D:\\UdemyCourse\\Udemy - Clean Architecture...\"");
                    return 1;
                }

                var parentFolder = Path.GetFullPath(args[0]);
                if (!Directory.Exists(parentFolder))
                {
                    Console.WriteLine($"Parent folder not found: {parentFolder}");
                    return 2;
                }

                var cfg = Config.DefaultWithToolPaths();
                var work = WorkDirs.Prepare(parentFolder);
                var logger = new ErrorLogger(work.ReportDir);
                var excel = new ExcelLoggers(work.LogsDir);

                // Persist parent path (idempotent help)
                File.WriteAllText(Path.Combine(work.PathDir, "parent_folder.txt"), parentFolder, new UTF8Encoding(false));

                // 1) SCAN + RENAME (skip if already present)
                Console.WriteLine("[1/5] Scanning sub-folders & renaming files...");
                var renameState = new RenameState();
                var renamer = new Renamer(cfg, work, logger, excel);
                renamer.ScanAndRenameIfNeeded(parentFolder, renameState);

                // 2) CONCAT VIDEOS -> work/<ParentName>.mkv (skip if exists)
                var parentName = Path.GetFileName(parentFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var finalMkv = Path.Combine(work.Root, SanitizeFileName(parentName) + ".mkv");
                Console.WriteLine("[2/5] Concatenating videos -> " + finalMkv);
                var videoConcat = new VideoConcatenator(cfg, work, logger);
                videoConcat.ConcatIfNeeded(finalMkv);
                TimelineHelper.WriteConcatTimelineWithOriginalNames(
                    work.LogsDir,
                    work.VideosDir,
                    IsVideo,
                    path => GetVideoDurationSeconds(cfg, path)
                );


                // 3) CONCAT SUBTITLES -> work/<ParentName>.<ext> (SRT or VTT) (skip if exists)
                Console.WriteLine("[3/5] Building single subtitle file...");
                var subMerger = new SubtitleMerger(cfg, work, logger);
                subMerger.MergeIfNeeded(finalMkv, parentName);

                // 4) POST: SPLIT if > 12h
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
            catch (Exception ex)
            {
                Console.WriteLine("FATAL: " + ex);
                return 99;
            }
        }

        // ---------------- Configuration & Constants ----------------
        private static readonly string[] VideoExt = new[] {  ".mp4",".mkv",".m4v",".webm",".mov",".avi",".wmv",".ts" };
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
                // Prevent console wrapping (which would create new lines)
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

        // Generic process runner that streams stdout/stderr and exposes a parser -> progress [0..1]
        static int RunProcessWithProgress(
            ProcessStartInfo psi,
            Func<string, double?> tryParseProgress,      // returns 0..1 or null if line not progress
            Action<string>? onLine = null,
            ConsoleProgressBar? bar = null)
        {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;

            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };

            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                var line = e.Data;
                onLine?.Invoke(line);
                var v = tryParseProgress(line);
                if (v.HasValue) bar?.Report(v.Value);
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                var line = e.Data;
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
            public string Root { get; init; } = default!; // <Parent>/work
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
                _pkgVideo.SaveAs(new FileInfo(videoXlsx));
                _pkgSub.SaveAs(new FileInfo(subXlsx));
            }
        }

        // ---------------- RenameState ----------------
        internal class RenameState
        {
            public int GlobalIndex = 0;
            public int PadWidth = 3; // will adjust after counting
            public int SubFolderKey = 0; // current subfolder index (1-based)
            public bool RenameCompleted = false;
        }

        // ---------------- Renamer ----------------
        internal class Renamer
        {
            private readonly Config _cfg;
            private readonly WorkDirs _work;
            private readonly ErrorLogger _log;
            private readonly ExcelLoggers _xlsx;

            public Renamer(Config cfg, WorkDirs work, ErrorLogger log, ExcelLoggers xlsx)
            { _cfg = cfg; _work = work; _log = log; _xlsx = xlsx; }

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

                // First pass: count videos to decide zero-padding & for progress denominator
                int totalVideos = 0;
                foreach (var sub in GetSubDirsSorted(parentFolder))
                    foreach (var f in Directory.EnumerateFiles(sub, "*", SearchOption.TopDirectoryOnly))
                        if (IsVideo(f)) totalVideos++;

                state.PadWidth = totalVideos >= 1000 ? 4 : 3;
                using var bar = new ConsoleProgressBar("Rename");
                int processed = 0;

                // Second pass: rename/copy
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
                        File.Copy(v, vNew, overwrite: true); // or Move
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
                            var sNewDefault = Path.Combine(_work.SubsDir, newStem + ".srt");
                            _xlsx.LogSubtitle(state.GlobalIndex, state.SubFolderKey, "(missing)", Path.GetFileName(sNewDefault)!);
                            _log.Warn($"Subtitle missing for video '{Path.GetFileName(v)}' -> expected index {newStem}");
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

        // ---------------- Video Concatenator (FFmpeg concat demuxer) ----------------
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

                // ---------- 0) Pre-normalize EACH input to unify layout & timestamps ----------
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
                            int exitN = RunProcessWithProgress(psiN, _ => (double)done / Math.Max(1, videoFiles.Count), bar: null);
                            if (exitN != 0) { _log.Warn($"normalize failed for '{src}', will use original"); dst = src; }
                        }
                        videoFiles[i] = dst; // use normalized if exists
                        done++; barN.Report((double)done / videoFiles.Count, Path.GetFileName(src)!);
                    }
                    barN.Done("Inputs ready");
                }

                // ---------- 1) Build concat list ----------
                var listPath = Path.Combine(_work.Root, "videos.txt");
                using (var sw = new StreamWriter(listPath, false, new UTF8Encoding(false)))
                {
                    foreach (var v in videoFiles)
                        sw.WriteLine($"file '{v.Replace("'", "'\''")}'");
                }

                // ---------- 2) Compute total duration for progress & sanity ----------
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
                    // Normalize timestamps on the fly as well to be extra safe
                    Arguments = $"-hide_banner -f concat -safe 0 -i {Quote(listPath)} -map 0 -c copy -fflags +genpts -avoid_negative_ts make_zero -progress pipe:1 -nostats {Quote(finalMkv)}"
                };

                double totalUs = totalDurSec * 1_000_000.0; // expected total
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
                    if (pct.HasValue && pct.Value < prevPct) pct = prevPct; // monotonic
                    if (pct.HasValue) prevPct = pct.Value;
                    return pct;
                }

                var exit = RunProcessWithProgress(psi, TryParseFfmpeg, bar: bar);
                if (exit != 0)
                {
                    _log.Error("ffmpeg concat failed");
                    throw new Exception("ffmpeg concat failed");
                }

                // ---------- 3) Sanity check duration ----------
                try
                {
                    var outDur = GetVideoDurationSeconds(_cfg, finalMkv);
                    var diff = Math.Abs(outDur - totalDurSec);
                    if (diff > 60)
                    {
                        _log.Warn($@"Duration mismatch after concat. Inputs sum = {TimeSpan.FromSeconds(totalDurSec):hh\:mm\:ss}, output = {TimeSpan.FromSeconds(outDur):hh\:mm\:ss}, diff = {TimeSpan.FromSeconds(diff):hh\:mm\:ss}.");
                        _log.Warn("Consider inspecting the first file after the 11h mark; pre-normalization should already fix most cases.");
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn($"Failed to ffprobe output for sanity check: {ex.Message}");
                }
            }
        }

        // ---------------- Subtitle Merger (SRT/VTT) ----------------
        internal class SubtitleMerger
        {
            private readonly Config _cfg; private readonly WorkDirs _work; private readonly ErrorLogger _log;
            public SubtitleMerger(Config cfg, WorkDirs work, ErrorLogger log) { _cfg = cfg; _work = work; _log = log; }

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

                var cntSrt = subs.Count(s => Path.GetExtension(s).Equals(".srt", StringComparison.OrdinalIgnoreCase));
                var cntVtt = subs.Count(s => Path.GetExtension(s).Equals(".vtt", StringComparison.OrdinalIgnoreCase));
                var targetExt = cntSrt >= cntVtt ? ".srt" : ".vtt";

                var outPath = Path.Combine(_work.Root, SanitizeFileName(parentName) + targetExt);
                if (File.Exists(outPath))
                {
                    Console.WriteLine("  - Final subtitle already exists, skipping.");
                    return;
                }

                var videos = Directory.EnumerateFiles(_work.VideosDir)
                    .Where(IsVideo)
                    .OrderBy(n => n, new NumericNameComparer())
                    .ToList();
                if (videos.Count != subs.Count)
                {
                    _log.Warn($"Subtitle count ({subs.Count}) != video count ({videos.Count}). We'll align best-effort by index.");
                }

                var offsetsMs = new List<long>();
                long cumMs = 0;
                for (int i = 0; i < videos.Count; i++)
                {
                    offsetsMs.Add(cumMs);
                    try
                    {
                        var durSec = GetVideoDurationSeconds(_cfg, videos[i]);
                        cumMs += (long)Math.Round(durSec * 1000.0);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"ffprobe failed on '{videos[i]}': {ex.Message}. Using 0 offset increment.");
                    }
                }

                using var bar = new ConsoleProgressBar("Merge subs");
                if (targetExt.Equals(".srt", StringComparison.OrdinalIgnoreCase))
                    MergeSrt(subs, offsetsMs, outPath, bar);
                else
                    MergeVtt(subs, offsetsMs, outPath, bar);
                bar.Done("Subtitles merged");
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
                        var idxLine = lines[pos++];
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

            private static bool TryParseSrtTimeline(string line, out long startMs, out long endMs)
            {
                var m = Regex.Match(line.Trim(), @"^(?<A>\d{2}:\d{2}:\d{2}[,.]\d{3})\s*-->\s*(?<B>\d{2}:\d{2}:\d{2}[,.]\d{3})");
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
                => Regex.IsMatch(line.Trim(), @"^\d{2}:\d{2}:\d{2}\.\d{3}\s*-->\s*\d{2}:\d{2}:\d{2}\.\d{3}");

            private static bool TryParseVttTimeline(string line, out long startMs, out long endMs, out string tailAttrs)
            {
                startMs = endMs = 0; tailAttrs = string.Empty;
                var m = Regex.Match(line.Trim(), @"^(?<A>\d{2}:\d{2}:\d{2}\.\d{3})\s*-->\s*(?<B>\d{2}:\d{2}:\d{2}\.\d{3})(?<Tail>.*)$");
                if (!m.Success) return false;
                startMs = ParseVttHmsMs(m.Groups["A"].Value);
                endMs = ParseVttHmsMs(m.Groups["B"].Value);
                tailAttrs = m.Groups["Tail"].Value;
                return true;
            }

            private static long ParseVttHmsMs(string h)
            {
                var m = Regex.Match(h, @"^(?<H>\d{2}):(?<M>\d{2}):(?<S>\d{2})\.(?<MS>\d{3})$");
                if (!m.Success) return 0;
                int H = int.Parse(m.Groups["H"].Value);
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

        // ---------------- BigMkvSplitter (mkvmerge) ----------------
        internal class BigMkvSplitter
        {
            private readonly Config _cfg; private readonly WorkDirs _work; private readonly ErrorLogger _log;
            public BigMkvSplitter(Config cfg, WorkDirs work, ErrorLogger log) { _cfg = cfg; _work = work; _log = log; }

            // Split into 11h parts if duration > 12h.
            // Fixes: odd part lengths & non‑zero starting timestamps by normalizing PTS first,
            // then using FFmpeg segmenter with -reset_timestamps 1 so each part starts at 00:00:00.
            public void SplitIfNeeded(string finalMkv, string parentName)
            {
                if (!File.Exists(finalMkv)) return;

                double durSec;
                try { durSec = GetVideoDurationSeconds(_cfg, finalMkv); }
                catch (Exception ex) { _log.Warn($"ffprobe failed on final MKV: {ex.Message}. Skipping split check."); return; }

                const int SplitThresholdHours = 12;
                const int SplitChunkHours = 11; // per requirement
                if (durSec <= SplitThresholdHours * 3600.0 + 1e-6) return; // <= 12h

                // 1) Normalize timestamps so the stream starts at 0 and has generated PTS where missing
                //    This prevents huge jumps (e.g. part starting at 7h) and makes splitting stable
                var normalized = Path.Combine(_work.Root, SanitizeFileName(parentName) + ".normalized.mkv");
                try { if (File.Exists(normalized)) File.Delete(normalized); } catch { }

                using (var barNorm = new ConsoleProgressBar("Normalize PTS"))
                {
                    double? TryParseFfmpeg(string line)
                    {
                        // Parse -progress key=value output
                        if (line.StartsWith("out_time_us=") && double.TryParse(line[12..], NumberStyles.Any, CultureInfo.InvariantCulture, out var us))
                            return Math.Clamp((us / 1_000_000.0) / durSec, 0, 1);
                        if (line.StartsWith("out_time_ms=") && double.TryParse(line[12..], NumberStyles.Any, CultureInfo.InvariantCulture, out var ms))
                            return Math.Clamp((ms / 1_000_000.0) / durSec, 0, 1); // note: actually microseconds on some builds
                        if (line.StartsWith("out_time=") && TimeSpan.TryParse(line[9..], out var ts))
                            return Math.Clamp(ts.TotalSeconds / durSec, 0, 1);
                        return null;
                    }

                    var psiNorm = new ProcessStartInfo
                    {
                        FileName = _cfg.FfmpegPath,
                        Arguments = $"-hide_banner -y -i {Quote(finalMkv)} -map 0 -c copy -fflags +genpts -avoid_negative_ts make_zero -progress pipe:1 -nostats {Quote(normalized)}"
                    };
                    var exitNorm = RunProcessWithProgress(psiNorm, TryParseFfmpeg, bar: barNorm);
                    if (exitNorm != 0)
                    {
                        _log.Warn($"ffmpeg normalize failed (exit {exitNorm}). Will try to split original file.");
                        normalized = finalMkv; // fallback
                    }
                }

                // 2) Split into 11h chunks with FFmpeg segmenter; reset timestamps so each part starts at 0
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
                    var exitSeg = RunProcessWithProgress(psiSeg, TryParseFfmpeg2, bar: barSplit);
                    if (exitSeg != 0)
                    {
                        _log.Error($"ffmpeg segment split failed (exit {exitSeg}).");
                        return;
                    }
                }

                // 3) Cleanup & remove the big file to save space (keep parts only)
                if (!Path.GetFullPath(normalized).Equals(Path.GetFullPath(finalMkv), StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(normalized); } catch { }
                }
                try { File.Delete(finalMkv); } catch { }

                // Tính toán các mốc splitTimes (giây)
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
                    // Tìm subtitle đã merge
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
