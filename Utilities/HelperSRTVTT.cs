using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MergeVideo.Utilities
{
    class HelperSRTVTT
    {
        /// <summary>Convert a subtitle file to SRT format (supports .vtt to .srt).</summary>
        public static bool ConvertSubtitleToSrt(string srcPath, string dstPath)
        {
            string srcExt = Path.GetExtension(srcPath).ToLowerInvariant();
            try
            {
                if (srcExt == ".srt")
                {
                    // Already SRT, just copy
                    File.Copy(srcPath, dstPath, overwrite: true);
                    return true;
                }
                if (srcExt == ".vtt")
                {
                    var lines = File.ReadAllLines(srcPath, Encoding.UTF8).ToList();
                    // Remove any WEBVTT header line for .vtt files
                    if (lines.Count > 0 && lines[0].Trim().Equals("WEBVTT", StringComparison.OrdinalIgnoreCase))
                    {
                        lines.RemoveAt(0);
                        // Remove empty line after WEBVTT, if any
                        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
                            lines.RemoveAt(0);
                    }
                    using var sw = new StreamWriter(dstPath, false, new UTF8Encoding(false));
                    int globIndex = 0;
                    int idx = 0;
                    while (idx < lines.Count)
                    {
                        // Handle optional cue identifier line in VTT
                        string? cueId = null;
                        if (idx + 1 < lines.Count && IsVttTimelineLine(lines[idx + 1]))
                        {
                            cueId = lines[idx].Trim();
                            idx++;
                        }
                        if (idx >= lines.Count) break;
                        if (!IsVttTimelineLine(lines[idx]))
                        {
                            idx++;
                            continue;
                        }
                        string timeline = lines[idx++];
                        // Parse VTT timeline into start and end in milliseconds
                        if (!TryParseVttTimeline(timeline, out long startMs, out long endMs))
                            continue;
                        // Collect subtitle text lines
                        var textLines = new List<string>();
                        while (idx < lines.Count && !string.IsNullOrWhiteSpace(lines[idx]))
                            textLines.Add(lines[idx++]);
                        if (idx < lines.Count && string.IsNullOrWhiteSpace(lines[idx]))
                            idx++;  // skip blank line between cues

                        // Write out in SRT format
                        globIndex++;
                        sw.WriteLine(globIndex.ToString());
                        sw.WriteLine(FormatSrtTimestamp(startMs) + " --> " + FormatSrtTimestamp(endMs));
                        foreach (string t in textLines) sw.WriteLine(t);
                        sw.WriteLine();
                    }
                    return true;
                }
                // Other formats not handled
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ! ConvertSubtitleToSrt error: {ex.Message}");
                return false;
            }
        }

        /// <summary>Merge multiple SRT subtitle files into one SRT, applying time offsets for each sequentially.</summary>
        public static void MergeSubtitlesWithOffsets(List<string> srtFiles, string outputPath)
        {
            using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false));
            long cumulativeOffset = 0;
            int globalLineIndex = 0;
            foreach (string srt in srtFiles)
            {
                // Calculate time offset for this file (current cumulative duration)
                try
                {
                    // Use ffprobe to get duration of the corresponding video segment (or of this subtitle segment)
                    // In practice, we can parse the last cue's end time instead – but for simplicity, we use cumulativeOffset.
                }
                catch { /* ignore ffprobe failure, use last offset */ }

                var lines = File.ReadAllLines(srt, Encoding.UTF8);
                int i = 0;
                while (i < lines.Length)
                {
                    // Skip blank lines
                    while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i])) i++;
                    if (i >= lines.Length) break;

                    // Skip the original index line (we will renumber)
                    string indexLine = lines[i++];
                    if (i >= lines.Length) break;

                    // Parse timeline line
                    string timeline = lines[i++];
                    if (!TryParseSrtTimeline(timeline, out long startMs, out long endMs))
                    {
                        // If parsing failed (malformed timeline), try to find the timeline line by scanning ahead
                        bool found = false;
                        while (i < lines.Length)
                        {
                            if (TryParseSrtTimeline(lines[i], out startMs, out endMs))
                            {
                                found = true;
                                i++;
                                break;
                            }
                            i++;
                        }
                        if (!found) continue;
                    }

                    // Collect text lines for this subtitle cue
                    var textBuilder = new StringBuilder();
                    while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
                    {
                        textBuilder.AppendLine(lines[i]);
                        i++;
                    }
                    if (i < lines.Length && string.IsNullOrWhiteSpace(lines[i])) i++;

                    // Write the merged subtitle entry with adjusted timing
                    globalLineIndex++;
                    long newStart = startMs + cumulativeOffset;
                    long newEnd = endMs + cumulativeOffset;
                    writer.WriteLine(globalLineIndex.ToString());
                    writer.WriteLine(FormatSrtTimestamp(newStart) + " --> " + FormatSrtTimestamp(newEnd));
                    writer.Write(textBuilder.ToString());
                    writer.WriteLine();
                }

                // After processing this file, increment the cumulative offset by its duration
                try
                {
                    // Derive duration from last cue end time:
                    var lastCue = lines.Reverse().FirstOrDefault(l => TryParseSrtTimeline(l, out _, out _));
                    if (lastCue != null && TryParseSrtTimeline(lastCue, out long lastStart, out long lastEnd))
                        cumulativeOffset += lastEnd;
                }
                catch { /* ignore errors in offset calculation */ }
            }
        }

        // Helper parsing/formatting functions for timestamps:
        public static bool TryParseSrtTimeline(string line, out long startMs, out long endMs)
        {
            startMs = endMs = 0;
            var match = Regex.Match(line.Trim(),
                @"^(?<A>\d{2}:\d{2}:\d{2}[,\.]\d{3})\s*-->\s*(?<B>\d{2}:\d{2}:\d{2}[,\.]\d{3})");
            if (!match.Success) return false;
            startMs = ParseHmsToMilliseconds(match.Groups["A"].Value);
            endMs = ParseHmsToMilliseconds(match.Groups["B"].Value);
            return true;
        }
        public static bool IsVttTimelineLine(string line)
        {
            return Regex.IsMatch(line.Trim(),
                @"^(?:\d{2}:)?\d{2}:\d{2}[,\.]\d{3}\s*-->\s*(?:\d{2}:)?\d{2}:\d{2}[,\.]\d{3}");
        }
        public static bool TryParseVttTimeline(string line, out long startMs, out long endMs)
        {
            startMs = endMs = 0;
            // Regex with optional hour for VTT
            var match = Regex.Match(line.Trim(),
                @"^(?<A>(?:\d{2}:)?\d{2}:\d{2}[,\.]\d{3})\s*-->\s*(?<B>(?:\d{2}:)?\d{2}:\d{2}[,\.]\d{3})");
            if (!match.Success) return false;
            startMs = ParseTimestampToMs(match.Groups["A"].Value);
            endMs = ParseTimestampToMs(match.Groups["B"].Value);
            return true;
        }
        private static long ParseHmsToMilliseconds(string hms)
        {
            // Support comma or dot as decimal separator for milliseconds
            string fixedFormat = hms.Replace('.', ',');
            var m = Regex.Match(fixedFormat,
                @"^(?<H>\d{2}):(?<M>\d{2}):(?<S>\d{2}),(?<MS>\d{3})$");
            if (!m.Success) return 0;
            int H = int.Parse(m.Groups["H"].Value),
                M = int.Parse(m.Groups["M"].Value),
                S = int.Parse(m.Groups["S"].Value),
                MS = int.Parse(m.Groups["MS"].Value);
            long totalMs = H * 3600000L + M * 60000L + S * 1000L + MS;
            return totalMs;
        }
        public static string FormatSrtTimestamp(long ms)
        {
            var t = TimeSpan.FromMilliseconds(ms);
            // Format hours:minutes:seconds,milliseconds (with comma as separator)
            return string.Format(CultureInfo.InvariantCulture,
                "{0:00}:{1:00}:{2:00},{3:000}",
                (int)t.TotalHours, t.Minutes, t.Seconds, t.Milliseconds);
        }
        private static long ParseTimestampToMs(string ts)
        {
            // Parse VTT timestamp which may omit hours
            string fixedFormat = ts.Replace(',', '.');
            var m = Regex.Match(fixedFormat,
                @"^(?:(?<H>\d{2}):)?(?<M>\d{2}):(?<S>\d{2})\.(?<MS>\d{3})$");
            if (!m.Success) return 0;
            int H = m.Groups["H"].Success ? int.Parse(m.Groups["H"].Value) : 0;
            int M = int.Parse(m.Groups["M"].Value),
                S = int.Parse(m.Groups["S"].Value),
                MS = int.Parse(m.Groups["MS"].Value);
            return H * 3600000L + M * 60000L + S * 1000L + MS;
        }
    }
}
