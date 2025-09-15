// VideoSubtitleConcat - .NET 8 Console (with console progress bars) - UPDATED
// - VTT parser: supports mm:ss.mmm or hh:mm:ss.mmm and comma/dot milliseconds
// - Mixed SRT/VTT: auto unify by converting to target before merge
// - Missing subtitles: CSV + OnMissingSubtitle mode (Skip/WarnOnly/CreateEmptyFile)
// - Interactive console mode when no args: loop Y/N
// NOTE: EPPlus license init kept. No MKV muxing of subtitles (explicitly excluded).

using MergeVideo.Models;
using MergeVideo.Utilities;
using OfficeOpenXml; // EPPlus
using System.Runtime.CompilerServices;
using System.Text;
using static MergeVideo.Enums.OnMissingSubtitleModeContainer;

namespace MergeVideo
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            ExcelPackage.License.SetNonCommercialPersonal("Your Name");

            try
            {
                if (args.Length == 0)
                {
                    // ===== Interactive console mode =====
                    while (true)
                    {
                        Console.Write("Root path (leave empty to exit): ");
                        var inp = Console.ReadLine();
                        if (string.IsNullOrWhiteSpace(inp)) return 0;
                        var parentFolder = Path.GetFullPath(inp);
                        if (!Directory.Exists(parentFolder))
                        {
                            Console.WriteLine("  ! Folder not found. Try again.\n");
                            continue;
                        }

                        var opts = new RuntimeOptions();
                        Console.Write("OnMissingSubtitle [S]kip/[W]arnOnly/[C]reateEmptyFile (default S): ");
                        var m = (Console.ReadLine() ?? "").Trim().ToUpperInvariant();
                        if (m == "W") opts.OnMissingSubtitle = OnMissingSubtitleMode.WarnOnly;
                        else if (m == "C") opts.OnMissingSubtitle = OnMissingSubtitleMode.CreateEmptyFile;
                        else opts.OnMissingSubtitle = OnMissingSubtitleMode.Skip;

                        // optional strict toggle
                        Console.Write("Strict mapping? stop when missing (y/N): ");
                        var sm = (Console.ReadLine() ?? "").Trim().ToUpperInvariant();
                        opts.StrictMapping = sm == "Y";

                        // optional forced target format
                        Console.Write("Force target subtitle format [.srt/.vtt/empty=auto]: ");
                        var tf = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
                        if (tf == ".srt" || tf == "srt") opts.TargetFormatForced = ".srt";
                        else if (tf == ".vtt" || tf == "vtt") opts.TargetFormatForced = ".vtt";
                        else opts.TargetFormatForced = null;

                        try
                        {
                            var exit = RunOneJob(parentFolder, opts);
                            Console.WriteLine($"Job finished with exit code {exit}.");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("FATAL: " + ex);
                        }

                        Console.Write("Run another job? (Y/N): ");
                        var ans = (Console.ReadLine() ?? "").Trim().ToUpperInvariant();
                        if (ans != "Y") break;
                    }
                    return 0;
                }
                else
                {
                    // ===== Non-interactive (legacy) =====
                    var parentFolder = Path.GetFullPath(args[0]);
                    if (!Directory.Exists(parentFolder))
                    {
                        Console.WriteLine($"Parent folder not found: {parentFolder}");
                        return 2;
                    }
                    return RunOneJob(parentFolder, new RuntimeOptions());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL: " + ex);
                return 99;
            }
        }

        private static int RunOneJob(string parentFolder, RuntimeOptions opts)
        {
            var cfg = Config.DefaultWithToolPaths();
            var work = WorkDirs.Prepare(parentFolder);
            var logger = new ErrorLogger(work.ReportDir);
            var excel = new ExcelLoggers(work.LogsDir);

            // Persist parent path (idempotent help)
            File.WriteAllText(Path.Combine(work.PathDir, "parent_folder.txt"), parentFolder, new UTF8Encoding(false));

            // 1) SCAN + RENAME
            Console.WriteLine("[1/4] Scanning sub-folders & renaming files...");
            var renameState = new RenameState();
            var renamer = new Renamer(cfg, work, logger, excel, opts);
            renamer.ScanAndRenameIfNeeded(parentFolder, renameState);

            // 2) CONCAT VIDEOS (audio-safe) + ưu tiên cache norm nếu có đủ
            var parentName = Path.GetFileName(parentFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            Console.WriteLine("[2/4] Concatenating videos (audio-safe) ...");

            // Lấy danh sách clip gốc trong work/videos theo đúng thứ tự số
            var inputsOriginal = Directory.EnumerateFiles(work.VideosDir)
                .Where(Utils.IsVideo)
                .OrderBy(n => n, new NumericNameComparer())
                .ToList();
            if (inputsOriginal.Count == 0)
            {
                logger.Error("No videos found in work/videos to concatenate.");
                return 2;
            }

            // Kiểm tra/chuẩn hoá mapping video↔subtitle
            if (!Utils.ValidateSubtitleMapping(work.SubsDir, inputsOriginal, opts, logger, out var subMap))
                return 3;

            // Thử dùng cache ./work/norm nếu đầy đủ
            var inputsForConcat = inputsOriginal;
            bool useNorm = false;
            var normDir = Path.Combine(work.Root, "norm");
            if (Directory.Exists(normDir))
            {
                // build lookup theo base "001"
                var normLookup = Directory.EnumerateFiles(normDir, "*.norm.mkv")
                    .ToLookup(p =>
                    {
                        var bn = Path.GetFileNameWithoutExtension(p); // "001.norm"
                        if (bn.EndsWith(".norm", StringComparison.OrdinalIgnoreCase)) bn = bn[..^5];
                        return bn;
                    }, StringComparer.OrdinalIgnoreCase);

                bool allPresent = inputsOriginal.All(v =>
                    normLookup.Contains(Path.GetFileNameWithoutExtension(v)));

                if (allPresent)
                {
                    inputsForConcat = inputsOriginal.Select(v =>
                    {
                        var b = Path.GetFileNameWithoutExtension(v);
                        return normLookup[b].OrderBy(x => x).First();
                    }).ToList();

                    useNorm = true;
                    Console.WriteLine($"   - Using normalized cache from 'norm' ({inputsForConcat.Count} files) → concat with -c copy.");
                }
                else
                {
                    Console.WriteLine("   - 'norm' exists but is incomplete → will normalize as needed.");
                }
            }

            // Tính tổng thời lượng theo inputsForConcat và chia nhóm <= 11h
            double totalDurationSec = 0;
            foreach (string file in inputsForConcat)
            {
                try { totalDurationSec += Utils.GetVideoDurationSeconds(cfg, file); }
                catch (Exception ex) { logger.Warn($"ffprobe failed for {Path.GetFileName(file)}: {ex.Message}"); }
            }

            List<List<string>> videoGroups = new();
            if (totalDurationSec <= Utils.SplitThresholdHours * 3600)
            {
                videoGroups.Add(inputsForConcat);
            }
            else
            {
                double MAX_SEGMENT_SEC = Utils.SplitChunkHours * 3600;
                List<string> currentGroup = new(); double currentDur = 0;
                foreach (string file in inputsForConcat)
                {
                    double dur = 0;
                    try { dur = Utils.GetVideoDurationSeconds(cfg, file); } catch { }
                    if (currentGroup.Count > 0 && currentDur + dur > MAX_SEGMENT_SEC)
                    {
                        videoGroups.Add(currentGroup);
                        currentGroup = new List<string>(); currentDur = 0;
                    }
                    currentGroup.Add(file); currentDur += dur;
                }
                if (currentGroup.Count > 0) videoGroups.Add(currentGroup);
            }

            // Concat từng nhóm
            int partNumber = 1;
            foreach (var group in videoGroups)
            {
                string sanitizedName = Utils.SanitizeFileName(parentName);
                string outputFileName = (videoGroups.Count == 1)
                    ? sanitizedName + ".mkv"
                    : $"{sanitizedName} - Part {partNumber:00}.mkv";
                string outputPath = Path.Combine(work.Root, outputFileName);
                Console.WriteLine($"   -> Creating {outputFileName}");

                if (useNorm)
                {
                    // Dùng concat demuxer (-c copy) → không re-encode
                    var exit = Utils.FfmpegConcatCopy(group, outputPath, cfg.FfmpegPath, log: null);
                    if (exit != 0)
                    {
                        logger.Error($"ffmpeg concat copy failed for {outputFileName} (exit={exit}).");
                        return 4;
                    }
                }
                else
                {
                    // Chuẩn hoá & concat theo pipeline hiện có
                    ConsoleProgressBar.ResetRegion();
                    var opt = new AudioSafeConcatManager.Options(
                        workDir: work.Root,
                        outputFileName: outputFileName,
                        ffmpegPath: cfg.FfmpegPath,
                        ffprobePath: cfg.FfprobePath,
                        segmentTimeSeconds: null,
                        audioCodec: "aac",
                        audioBitrateKbps: 192,
                        audioChannels: 2,
                        audioSampleRate: 48000,
                        selectiveNormalize: true
                    );
                    var manager = new AudioSafeConcatManager(opt, logger: null);
                    manager.RunAsync(group).GetAwaiter().GetResult();
                    ConsoleProgressBar.ResetRegion();
                }

                partNumber++;
            }

            // Ghi timeline
            TimelineHelper.WriteConcatTimelineWithOriginalNames(
                work.LogsDir,
                work.VideosDir,
                Utils.IsVideo,
                path => Utils.GetVideoDurationSeconds(cfg, path)
            );

            // 3) SUBTITLE MERGE (per-part; offset = tổng độ dài *video*)
            Console.WriteLine("[3/4] Merging subtitles for each video part...");
            partNumber = 1;
            foreach (var group in videoGroups)
            {
                string sanitizedName = Utils.SanitizeFileName(parentName);
                string subOutputPath = (videoGroups.Count == 1)
                    ? Path.Combine(work.Root, sanitizedName + ".srt")
                    : Path.Combine(work.Root, $"{sanitizedName} - Part {partNumber:00}.srt");

                // Nếu group không có sub nào (tuỳ Strict/OnMissing) thì bỏ qua
                bool hasAnySub = group.Any(v =>
                {
                    var bn = Path.GetFileNameWithoutExtension(v);
                    if (bn.EndsWith(".norm", StringComparison.OrdinalIgnoreCase)) bn = bn[..^5];
                    return subMap.ContainsKey(bn);
                });
                if (!hasAnySub)
                {
                    Console.WriteLine($"   - No subtitles for Part {partNumber:00}; skipping subtitle merge.");
                    partNumber++;
                    continue;
                }

                Utils.MergeSubtitlesForGroupUsingVideoDurations(group, subMap, cfg, subOutputPath, logger);
                Console.WriteLine($"   - Created subtitle: {Path.GetFileName(subOutputPath)}");
                partNumber++;
            }

            // 4) DONE – list output files
            Console.WriteLine("[4/4] Done. Outputs in: " + work.Root);
            var allMkvs = Directory.GetFiles(work.Root, Utils.SanitizeFileName(parentName) + "*.mkv");
            foreach (string file in allMkvs)
                Console.WriteLine(" - Video: " + file + (File.Exists(file) ? " (exists)" : ""));
            var allSrts = Directory.GetFiles(work.Root, Utils.SanitizeFileName(parentName) + "*.srt");
            if (allSrts.Length > 0)
            {
                foreach (string file in allSrts)
                    Console.WriteLine(" - Subtitle: " + file);
            }

            // Cleanup: delete work/videos để tiết kiệm dung lượng
            try
            {
                if (Directory.Exists(work.VideosDir))
                {
                    Directory.Delete(work.VideosDir, recursive: true);
                    Console.WriteLine("Cleaned up: " + work.VideosDir);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: failed to delete {work.VideosDir}: {ex.Message}");
            }

            excel.FlushAndSave();
            return 0;
        }
    }
}
