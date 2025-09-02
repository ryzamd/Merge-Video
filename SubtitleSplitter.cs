using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MergeVideo
{
    public static class SubtitleSplitter
    {
        /// <summary>
        /// Cắt subtitle (SRT hoặc VTT) thành nhiều phần theo các mốc splitTimes (giây).
        /// </summary>
        public static void SplitSubtitleByTimeline(
            string mergedSubtitlePath,
            string outPattern, // ví dụ: "output Part {0:00}.srt"
            List<double> splitTimes, // seconds, ví dụ: [0, 39600, 79200] cho 0h, 11h, 22h
            string format // ".srt" hoặc ".vtt"
        )
        {
            if (!File.Exists(mergedSubtitlePath)) return;
            var lines = File.ReadAllLines(mergedSubtitlePath, Encoding.UTF8);

            // Chuẩn bị danh sách phần
            int partCount = splitTimes.Count - 1;
            var partBuilders = new List<List<string>>();
            for (int i = 0; i < partCount; i++)
                partBuilders.Add(new List<string>());

            // Xử lý từng cue
            if (format.Equals(".srt", StringComparison.OrdinalIgnoreCase))
            {
                int idx = 0;
                while (idx < lines.Length)
                {
                    // Đọc 1 block SRT
                    int cueIdx = idx;
                    while (idx < lines.Length && string.IsNullOrWhiteSpace(lines[idx])) idx++;
                    if (idx >= lines.Length) break;
                    var numLine = lines[idx++];
                    if (idx >= lines.Length) break;
                    var timeLine = lines[idx++];
                    if (!TryParseSrtTimeline(timeLine, out var startMs, out var endMs))
                        continue;
                    var text = new List<string>();
                    while (idx < lines.Length && !string.IsNullOrWhiteSpace(lines[idx]))
                        text.Add(lines[idx++]);
                    if (idx < lines.Length && string.IsNullOrWhiteSpace(lines[idx])) idx++;

                    double startSec = startMs / 1000.0;
                    // Tìm part phù hợp
                    for (int p = 0; p < partCount; p++)
                    {
                        if (startSec >= splitTimes[p] && startSec < splitTimes[p + 1])
                        {
                            partBuilders[p].Add($"{partBuilders[p].Count + 1}");
                            partBuilders[p].Add(FormatSrtTimeline((long)(startSec - splitTimes[p]) * 1000) + " --> " +
                                                FormatSrtTimeline((long)(endMs - splitTimes[p] * 1000)));
                            partBuilders[p].AddRange(text);
                            partBuilders[p].Add("");
                            break;
                        }
                    }
                }
            }
            else if (format.Equals(".vtt", StringComparison.OrdinalIgnoreCase))
            {
                // Bỏ header
                int idx = 0;
                if (lines.Length > 0 && lines[0].Trim().Equals("WEBVTT", StringComparison.OrdinalIgnoreCase))
                    idx++;
                for (int p = 0; p < partCount; p++)
                {
                    partBuilders[p].Add("WEBVTT");
                    partBuilders[p].Add("");
                }
                while (idx < lines.Length)
                {
                    int cueStart = idx;
                    string? cueId = null;
                    if (idx + 1 < lines.Length && IsVttTimelineLine(lines[idx + 1]))
                    {
                        cueId = lines[idx].Trim();
                        idx++;
                    }
                    if (idx >= lines.Length) break;
                    if (!IsVttTimelineLine(lines[idx])) { idx++; continue; }
                    var timeLine = lines[idx++];
                    if (!TryParseVttTimeline(timeLine, out var startMs, out var endMs, out var tailAttrs))
                        continue;
                    var text = new List<string>();
                    while (idx < lines.Length && !string.IsNullOrWhiteSpace(lines[idx]))
                        text.Add(lines[idx++]);
                    if (idx < lines.Length && string.IsNullOrWhiteSpace(lines[idx])) idx++;

                    double startSec = startMs / 1000.0;
                    for (int p = 0; p < partCount; p++)
                    {
                        if (startSec >= splitTimes[p] && startSec < splitTimes[p + 1])
                        {
                            if (!string.IsNullOrEmpty(cueId)) partBuilders[p].Add(cueId);
                            partBuilders[p].Add(FormatVttTimeline((long)(startSec - splitTimes[p]) * 1000, tailAttrs) +
                                                " --> " +
                                                FormatVttTimeline((long)(endMs - splitTimes[p] * 1000), tailAttrs));
                            partBuilders[p].AddRange(text);
                            partBuilders[p].Add("");
                            break;
                        }
                    }
                }
            }

            // Ghi ra file
            for (int p = 0; p < partCount; p++)
            {
                var outPath = string.Format(outPattern, p + 1);
                File.WriteAllLines(outPath, partBuilders[p], Encoding.UTF8);
            }
        }

        // Các hàm phụ trợ lấy từ Program.cs
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
}
