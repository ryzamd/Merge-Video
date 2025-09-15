using System.Globalization;
using System.Text.RegularExpressions;

namespace MergeVideo.Utilities
{
    class HelperSRTVTT
    {
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
