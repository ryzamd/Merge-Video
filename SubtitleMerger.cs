using MergeVideo.Models;
using MergeVideo.Utilities;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using static MergeVideo.Program;

namespace MergeVideo
{
    class SubtitleMerger
    {
        private readonly Config _cfg; private readonly WorkDirs _work; private readonly ErrorLogger _log;
        private readonly RuntimeOptions _opts;

        public SubtitleMerger(Config cfg, WorkDirs work, ErrorLogger log, RuntimeOptions opts)
        { _cfg = cfg; _work = work; _log = log; _opts = opts; }

        public string? GetOutputSubtitlePath(string parentName)
        {
            var srt = Path.Combine(_work.Root, Utils.SanitizeFileName(parentName) + ".srt");
            var vtt = Path.Combine(_work.Root, Utils.SanitizeFileName(parentName) + ".vtt");
            if (File.Exists(srt)) return srt;
            if (File.Exists(vtt)) return vtt;
            return null;
        }

        public void MergeIfNeeded(string finalMkv, string parentName)
        {
            var subs = Directory.EnumerateFiles(_work.SubsDir)
                .Where(Utils.IsSubtitle)
                .OrderBy(n => n, new NumericNameComparer())
                .ToList();

            if (subs.Count == 0)
            {
                Console.WriteLine("  - No subtitles found, skipping subtitle merge.");
                return;
            }

            string targetExt = ".srt";

            var outPath = Path.Combine(_work.Root, Utils.SanitizeFileName(parentName) + targetExt);
            if (File.Exists(outPath))
            {
                Console.WriteLine("  - Final subtitle already exists, skipping.");
                return;
            }

            // Mixed-format unify: convert inputs to target
            if (!subs.All(s => Path.GetExtension(s).Equals(targetExt, StringComparison.OrdinalIgnoreCase)))
            {
                var convDir = Path.Combine(_work.Root, "subs_conv");
                Utils.EnsureDir(convDir);
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
                           .Where(Utils.IsVideo)
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
                    var durSec = Utils.GetVideoDurationSeconds(_cfg, segments[i]);
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
                var totalVidSec = Utils.GetVideoDurationSeconds(_cfg, finalMkv);
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
}
