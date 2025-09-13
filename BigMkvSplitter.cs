using MergeVideo.Models;
using MergeVideo.Utilities;
using System.Diagnostics;
using System.Globalization;

namespace MergeVideo
{
    internal class BigMkvSplitter
    {
        private readonly Config _cfg; private readonly WorkDirs _work; private readonly ErrorLogger _log;
        public BigMkvSplitter(Config cfg, WorkDirs work, ErrorLogger log) { _cfg = cfg; _work = work; _log = log; }

        public void SplitIfNeeded(string finalMkv, string parentName)
        {
            if (!File.Exists(finalMkv)) return;

            double durSec;
            try { durSec = Utils.GetVideoDurationSeconds(_cfg, finalMkv); }
            catch (Exception ex) { _log.Warn($"ffprobe failed on final MKV: {ex.Message}. Skipping split check."); return; }

            const int SplitThresholdHours = 12;
            const int SplitChunkHours = 11;
            if (durSec <= SplitThresholdHours * 3600.0 + 1e-6) return;

            var normalized = Path.Combine(_work.Root, Utils.SanitizeFileName(parentName) + ".normalized.mkv");
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
                    Arguments = $"-hide_banner -y -i {Utils.Quote(finalMkv)} -map 0 -c copy -fflags +genpts -avoid_negative_ts make_zero -progress pipe:1 -nostats {Utils.Quote(normalized)}"
                };
                var exitNorm = ConsoleProgressBar.RunProcessWithProgress(psiNorm, TryParseFfmpeg, _work, bar: barNorm);
                if (exitNorm != 0)
                {
                    _log.Warn($"ffmpeg normalize failed (exit {exitNorm}). Will try to split original file.");
                    normalized = finalMkv;
                }
            }

            var outPattern = Path.Combine(_work.Root, Utils.SanitizeFileName(parentName) + " Part %02d.mkv");
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
                    Arguments = $"-hide_banner -y -i {Utils.Quote(normalized)} -map 0 -c copy -f segment -segment_time {SplitChunkHours * 3600} -reset_timestamps 1 -progress pipe:1 -nostats {Utils.Quote(outPattern)}"
                };
                var exitSeg = ConsoleProgressBar.RunProcessWithProgress(psiSeg, TryParseFfmpeg2, _work, bar: barSplit);
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

            var partFiles = Directory.EnumerateFiles(_work.Root, Utils.SanitizeFileName(parentName) + " Part *.mkv")
                .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (partFiles.Count > 1)
            {
                var splitTimes = new List<double> { 0 };
                double acc = 0;
                foreach (var part in partFiles)
                {
                    try { acc += Utils.GetVideoDurationSeconds(_cfg, part); }
                    catch { }
                    splitTimes.Add(acc);
                }
                var srt = Path.Combine(_work.Root, Utils.SanitizeFileName(parentName) + ".srt");
                var vtt = Path.Combine(_work.Root, Utils.SanitizeFileName(parentName) + ".vtt");
                if (File.Exists(srt))
                {
                    SubtitleSplitter.SplitSubtitleByTimeline(
                        srt,
                        Path.Combine(_work.Root, Utils.SanitizeFileName(parentName) + " Part {0:00}.srt"),
                        splitTimes,
                        ".srt"
                    );
                }
                else if (File.Exists(vtt))
                {
                    SubtitleSplitter.SplitSubtitleByTimeline(
                        vtt,
                        Path.Combine(_work.Root, Utils.SanitizeFileName(parentName) + " Part {0:00}.vtt"),
                        splitTimes,
                        ".vtt"
                    );
                }
            }
        }
    }
}
