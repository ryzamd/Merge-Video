using MergeVideo.Models;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MergeVideo.Utilities
{
    class Utils
    {
        private static readonly string[] VideoExt = new[] { ".mp4", ".mkv", ".m4v", ".webm", ".mov", ".avi", ".wmv", ".ts" };
        private static readonly string[] SubExt = new[] { ".srt", ".vtt" /* extendable: .ass/.ssa requires different parser */ };
        private const double SplitThresholdHours = 12.0; // YouTube 12h limit
        private const double SplitChunkHours = 11.0;      // create parts of ~11h

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
    }
}
