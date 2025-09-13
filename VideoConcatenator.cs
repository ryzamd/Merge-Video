using MergeVideo.Models;
using MergeVideo.Utilities;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace MergeVideo
{
    class VideoConcatenator
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
                .Where(Utils.IsVideo)
                .OrderBy(n => n, new NumericNameComparer())
                .ToList();

            if (videoFiles.Count == 0)
            {
                _log.Error("No videos found in work/videos to concatenate.");
                return;
            }

            // Pre-normalize …
            var normDir = Path.Combine(_work.Root, "videos_norm");
            Utils.EnsureDir(normDir);
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
                            Arguments = $"-hide_banner -y -i {Utils.Quote(src)} -map 0:v:0 -map 0:a:0? -sn -dn -c copy -fflags +genpts -avoid_negative_ts make_zero -max_interleave_delta 0 {Utils.Quote(dst)}"
                        };
                        int exitN = ConsoleProgressBar.RunProcessWithProgress(psiN, _ => (double)done / Math.Max(1, videoFiles.Count), _work, bar: null);
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
                try { totalDurSec += Utils.GetVideoDurationSeconds(_cfg, v); }
                catch (Exception ex) { _log.Warn($"ffprobe failed on '{v}': {ex.Message}"); }
            }
            if (totalDurSec <= 0) totalDurSec = 1;

            using var bar = new ConsoleProgressBar("FFmpeg concat");
            var psi = new ProcessStartInfo
            {
                FileName = _cfg.FfmpegPath,
                Arguments = $"-hide_banner -f concat -safe 0 -i {Utils.Quote(listPath)} -map 0:v -map 0:a? -c copy -fflags +genpts -avoid_negative_ts make_zero -progress pipe:1 -nostats {Utils.Quote(finalMkv)}"
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

            var exit = ConsoleProgressBar.RunProcessWithProgress(psi, TryParseFfmpeg, _work, bar: bar);
            if (exit != 0)
            {
                _log.Error("ffmpeg concat failed");
                throw new Exception("ffmpeg concat failed");
            }

            try
            {
                var outDur = Utils.GetVideoDurationSeconds(_cfg, finalMkv);
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
}
