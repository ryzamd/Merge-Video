using MergeVideo.Models;
using MergeVideo.Utilities;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using static MergeVideo.Enums.OnMissingSubtitleModeContainer;

namespace MergeVideo
{
    class Renamer
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
            foreach (var sub in Utils.GetSubDirsSorted(parentFolder))
                foreach (var f in Directory.EnumerateFiles(sub, "*", SearchOption.TopDirectoryOnly))
                    if (Utils.IsVideo(f)) totalVideos++;

            state.PadWidth = totalVideos >= 1000 ? 4 : 3;
            using var bar = new ConsoleProgressBar("Rename");
            int processed = 0;

            state.GlobalIndex = 0; state.SubFolderKey = 0;
            foreach (var sub in Utils.GetSubDirsSorted(parentFolder))
            {
                state.SubFolderKey++;
                var subName = Path.GetFileName(sub);
                Console.WriteLine($"  - Processing sub-folder [{state.SubFolderKey}] {subName}");

                var filesDest = Path.Combine(_work.FilesDir, Utils.SanitizeFileName(subName));
                Utils.EnsureDir(filesDest);

                var files = Directory.EnumerateFiles(sub, "*", SearchOption.TopDirectoryOnly).ToList();

                var videos = files.Where(Utils.IsVideo)
                    .OrderBy(p => Utils.NumericPrefixOrDefault(Path.GetFileName(p)!))
                    .ThenBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                var subs = files.Where(Utils.IsSubtitle)
                    .OrderBy(p => Utils.NumericPrefixOrDefault(Path.GetFileName(p)!))
                    .ThenBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                var subMap = BuildSubtitleMap(videos, subs);

                foreach (var v in videos)
                {
                    state.GlobalIndex++;
                    var newStem = Utils.ZeroPad(state.GlobalIndex, state.PadWidth);

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
                        try { dur = Utils.GetVideoDurationSeconds(_cfg, v); } catch { }
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
                        bar.Report((double)processed / totalVideos);
                }

                foreach (var other in files.Where(f => !Utils.IsVideo(f) && !Utils.IsSubtitle(f)))
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

            bar.Done();
            state.RenameCompleted = true;
        }

        private Dictionary<string, string> BuildSubtitleMap(List<string> videos, List<string> subs)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var subsByNum = new Dictionary<int, List<string>>();
            foreach (var s in subs)
            {
                var num = Utils.NumericPrefixOrDefault(Path.GetFileName(s)!);
                if (!subsByNum.TryGetValue(num, out var list)) { list = new List<string>(); subsByNum[num] = list; }
                list.Add(s);
            }

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in videos)
            {
                var vn = Utils.NumericPrefixOrDefault(Path.GetFileName(v)!);
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
}
