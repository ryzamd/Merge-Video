using MergeVideo.Models;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MergeVideo.Utilities
{
    class Utils
    {
        private static readonly string[] VideoExt = new[] { ".mp4", ".mkv", ".m4v", ".webm", ".mov", ".avi", ".wmv", ".ts" };
        private static readonly string[] SubExt = new[] { ".srt", ".vtt" /* extendable: .ass/.ssa requires different parser */ };
        public const double SplitThresholdHours = 12.0; // YouTube 12h limit
        public const double SplitChunkHours = 11.0;      // create parts of ~11h

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

        internal static string ZeroPad(int index, int width)
            => index.ToString("D" + width);


        /// <summary>
        /// Kiểm tra (và chuẩn hoá) mapping Video→Subtitle theo base name đã rename (001, 002,...).
        /// - Nếu StrictMapping = true: thiếu/duplicate sẽ ERROR và trả về false (dừng job).
        /// - Nếu StrictMapping = false: tuỳ OnMissingSubtitle → Warn/Skip/Tạo .srt rỗng.
        /// Trả về từ điển baseName -> subtitlePath (ưu tiên .srt, nếu không có thì .vtt).
        /// </summary>
        internal static bool ValidateSubtitleMapping(
            string subsDir,
            IEnumerable<string> videoPaths,
            RuntimeOptions opts,
            ErrorLogger logger,
            out Dictionary<string, string> baseToSubtitlePath,
            int previewLimit = 20)
        {
            baseToSubtitlePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Thu thập tất cả phụ đề .srt/.vtt
            var allSubFiles = Directory.EnumerateFiles(subsDir)
                .Where(p => p.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)
                         || p.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Gom theo base (001 -> [001.srt, 001.vtt, ...])
            var subBaseLookup = allSubFiles
                .GroupBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var missingBases = new List<string>();
            var duplicateBases = new List<string>();

            foreach (var v in videoPaths)
            {
                var baseName = Path.GetFileNameWithoutExtension(v);
                if (!subBaseLookup.TryGetValue(baseName, out var subsForThis) || subsForThis.Count == 0)
                {
                    missingBases.Add(baseName);
                    continue;
                }

                // Nếu vừa có .srt vừa có .vtt → duplicate
                if (subsForThis.Count > 1)
                {
                    duplicateBases.Add(baseName);
                }

                // Chọn 1 file ưu tiên .srt, nếu không có .srt thì lấy .vtt
                string? chosen = subsForThis
                    .FirstOrDefault(p => p.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
                    ?? subsForThis.FirstOrDefault(p => p.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrEmpty(chosen))
                    baseToSubtitlePath[baseName] = chosen!;
            }

            // Xử lý duplicate
            if (duplicateBases.Count > 0)
            {
                var dupMsg = $"Found both .srt and .vtt for: {string.Join(", ", duplicateBases.Take(previewLimit))}"
                             + (duplicateBases.Count > previewLimit ? " ..." : "");
                if (opts.StrictMapping)
                {
                    logger.Error(dupMsg);
                    return false;
                }
                else
                {
                    logger.Warn(dupMsg + " — will prefer .srt when merging.");
                }
            }

            // Xử lý thiếu phụ đề
            if (missingBases.Count > 0)
            {
                var missMsg = $"Missing subtitles for {missingBases.Count} video(s): " +
                              $"{string.Join(", ", missingBases.Take(previewLimit))}" +
                              (missingBases.Count > previewLimit ? " ..." : "");
                if (opts.StrictMapping)
                {
                    logger.Error(missMsg);
                    return false;
                }

                switch (opts.OnMissingSubtitle)
                {
                    case MergeVideo.Enums.OnMissingSubtitleModeContainer.OnMissingSubtitleMode.WarnOnly:
                        logger.Warn(missMsg);
                        break;

                    case MergeVideo.Enums.OnMissingSubtitleModeContainer.OnMissingSubtitleMode.CreateEmptyFile:
                        foreach (var b in missingBases)
                        {
                            var emptySrt = Path.Combine(subsDir, b + ".srt");
                            if (!File.Exists(emptySrt))
                                File.WriteAllText(emptySrt, "", new UTF8Encoding(false));
                            baseToSubtitlePath[b] = emptySrt;
                        }
                        logger.Warn(missMsg + " — created empty .srt placeholders to keep timeline alignment.");
                        break;

                    case MergeVideo.Enums.OnMissingSubtitleModeContainer.OnMissingSubtitleMode.Skip:
                    default:
                        logger.Warn(missMsg + " — will skip subtitles for those videos.");
                        break;
                }
            }

            return true;
        }

        /// Gộp phụ đề cho 1 group video theo đúng thứ tự,
        /// offset được cộng = tổng duration (ms) của *các video trước đó* trong group.
        internal static void MergeSubtitlesForGroupUsingVideoDurations(
            IEnumerable<string> orderedVideoPaths,
            Dictionary<string, string> baseToSubtitlePath, // base "001" -> path sub (srt hoặc vtt)
            Config cfg,
            string outputSrtPath,
            ErrorLogger logger)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputSrtPath)!);

            using var writer = new StreamWriter(outputSrtPath, false, new UTF8Encoding(false));
            long cumulativeMs = 0;
            int globalIndex = 0;

            foreach (var videoPath in orderedVideoPaths)
            {
                var baseName = Path.GetFileNameWithoutExtension(videoPath); // "001" hoặc "001.norm"
                if (baseName.EndsWith(".norm", StringComparison.OrdinalIgnoreCase))
                    baseName = baseName[..^5];

                // 1) Nếu có phụ đề cho clip này → append với offset = cumulativeMs
                if (baseToSubtitlePath.TryGetValue(baseName, out var subPath))
                {
                    string srtPath = subPath.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)
                        ? subPath
                        : EnsureVttConvertedToSrt(subPath);

                    if (!string.IsNullOrEmpty(srtPath) && File.Exists(srtPath))
                    {
                        AppendSrtWithOffset(srtPath, cumulativeMs, writer, ref globalIndex);
                    }
                    else
                    {
                        logger.Warn($"Subtitle '{Path.GetFileName(subPath)}' cannot be used; skipped.");
                    }
                }

                // 2) Tăng offset = duration của *video* hiện tại
                try
                {
                    var durSec = GetVideoDurationSeconds(cfg, videoPath);
                    var durMs = (long)Math.Round(durSec * 1000.0);
                    cumulativeMs += durMs;
                }
                catch (Exception ex)
                {
                    logger.Warn($"ffprobe failed for '{Path.GetFileName(videoPath)}': {ex.Message}. Offset may be wrong.");
                }
            }
        }

        /// Convert VTT → SRT (trả về đường dẫn .srt tạm, cùng thư mục với file gốc)
        private static string EnsureVttConvertedToSrt(string vttPath)
        {
            if (!vttPath.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase))
                return vttPath;

            var srtPath = Path.Combine(
                Path.GetDirectoryName(vttPath)!,
                Path.GetFileNameWithoutExtension(vttPath) + ".srt");

            try
            {
                var lines = File.ReadAllLines(vttPath, Encoding.UTF8).ToList();
                if (lines.Count > 0 && lines[0].Trim().Equals("WEBVTT", StringComparison.OrdinalIgnoreCase))
                {
                    lines.RemoveAt(0);
                    while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0])) lines.RemoveAt(0);
                }

                using var sw = new StreamWriter(srtPath, false, new UTF8Encoding(false));
                int globIndex = 0; int i = 0;
                while (i < lines.Count)
                {
                    // cue id?
                    if (i + 1 < lines.Count && HelperSRTVTT.IsVttTimelineLine(lines[i + 1])) i++;

                    if (i >= lines.Count || !HelperSRTVTT.IsVttTimelineLine(lines[i])) { i++; continue; }
                    var timeLine = lines[i++];
                    if (!HelperSRTVTT.TryParseVttTimeline(timeLine, out var startMs, out var endMs)) continue;

                    var textLines = new List<string>();
                    while (i < lines.Count && !string.IsNullOrWhiteSpace(lines[i])) textLines.Add(lines[i++]);
                    if (i < lines.Count && string.IsNullOrWhiteSpace(lines[i])) i++;

                    globIndex++;
                    sw.WriteLine(globIndex.ToString());
                    sw.WriteLine($"{HelperSRTVTT.FormatSrtTimestamp(startMs)} --> {HelperSRTVTT.FormatSrtTimestamp(endMs)}");
                    foreach (var t in textLines) sw.WriteLine(t);
                    sw.WriteLine();
                }
                return srtPath;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// Đọc 1 file .srt và append vào writer với offset (ms) + renumber index
        private static void AppendSrtWithOffset(string srtPath, long offsetMs, StreamWriter writer, ref int globalIndex)
        {
            var lines = File.ReadAllLines(srtPath, Encoding.UTF8);
            int i = 0;
            while (i < lines.Length)
            {
                while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i])) i++;
                if (i >= lines.Length) break;

                // bỏ index cũ
                i++;

                if (i >= lines.Length) break;
                var timeline = lines[i++];
                if (!HelperSRTVTT.TryParseSrtTimeline(timeline, out var startMs, out var endMs))
                {
                    bool found = false;
                    while (i < lines.Length)
                    {
                        if (HelperSRTVTT.TryParseSrtTimeline(lines[i], out startMs, out endMs))
                        {
                            i++; found = true; break;
                        }
                        i++;
                    }
                    if (!found) continue;
                }

                var text = new List<string>();
                while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i])) text.Add(lines[i++]);
                if (i < lines.Length && string.IsNullOrWhiteSpace(lines[i])) i++;

                globalIndex++;
                long ns = startMs + offsetMs, ne = endMs + offsetMs;
                writer.WriteLine(globalIndex.ToString());
                writer.WriteLine($"{HelperSRTVTT.FormatSrtTimestamp(ns)} --> {HelperSRTVTT.FormatSrtTimestamp(ne)}");
                foreach (var t in text) writer.WriteLine(t);
                writer.WriteLine();
            }
        }

        /// Concat demuxer (-c copy) cho danh sách input đã đúng thứ tự (không re-encode).
        internal static int FfmpegConcatCopy(IReadOnlyList<string> orderedInputs, string outputPath, string ffmpegPath, Action<string>? log = null)
        {
            if (orderedInputs == null || orderedInputs.Count == 0)
                throw new ArgumentException("No inputs to concat.", nameof(orderedInputs));

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            // Tạo list file cho concat demuxer
            var listPath = Path.Combine(Path.GetDirectoryName(outputPath)!, $"concat_{Guid.NewGuid():N}.txt");
            using (var sw = new StreamWriter(listPath, false, new UTF8Encoding(false)))
            {
                foreach (var p in orderedInputs)
                {
                    var safe = p.Replace("\\", "\\\\").Replace("'", "''");
                    sw.WriteLine($"file '{safe}'");
                }
            }

            if (File.Exists(outputPath)) File.Delete(outputPath);

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-hide_banner -y -f concat -safe 0 -i \"{listPath}\" -c copy \"{outputPath}\"",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi)!;
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) log?.Invoke(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) log?.Invoke(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            proc.WaitForExit();

            try { File.Delete(listPath); } catch { /* ignore */ }
            return proc.ExitCode;
        }
    }
}
