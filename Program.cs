// VideoSubtitleConcat - .NET 8 Console (with console progress bars) - UPDATED
// - VTT parser: supports mm:ss.mmm or hh:mm:ss.mmm and comma/dot milliseconds
// - Mixed SRT/VTT: auto unify by converting to target before merge
// - Missing subtitles: CSV + OnMissingSubtitle mode (Skip/WarnOnly/CreateEmptyFile)
// - Interactive console mode when no args: loop Y/N
// NOTE: EPPlus license init kept. No MKV muxing of subtitles (explicitly excluded).

using MergeVideo.Models;
using MergeVideo.Utilities;
using OfficeOpenXml; // EPPlus
using System.Text;
using static MergeVideo.Enums.OnMissingSubtitleModeContainer;

namespace MergeVideo
{
    internal static class Program
    {
        // ====== New runtime options ======
        public static readonly WorkDirs? _work;

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
            Console.WriteLine("[1/5] Scanning sub-folders & renaming files...");
            var renameState = new RenameState();
            var renamer = new Renamer(cfg, work, logger, excel, opts);
            renamer.ScanAndRenameIfNeeded(parentFolder, renameState);

            // 2) CONCAT VIDEOS (audio-safe)
            var parentName = Path.GetFileName(parentFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var finalMkv = Path.Combine(work.Root, Utils.SanitizeFileName(parentName) + ".mkv");
            Console.WriteLine("[2/5] Concatenating videos (audio-safe) -> " + finalMkv);

            // Lấy danh sách clip gốc trong work/videos theo đúng thứ tự số
            var inputs = Directory.EnumerateFiles(work.VideosDir)
                .Where(Utils.IsVideo)
                .OrderBy(n => n, new NumericNameComparer())
                .ToList();
            if (inputs.Count == 0)
            {
                logger.Error("No videos found in work/videos to concatenate.");
                return 2;
            }

            // Dùng class mới: copy video, encode audio -> AAC (fix lỗi mất tiếng khi codec audio khác nhau)
            var opt = new AudioSafeConcatManager.Options(
                workDir: work.Root,
                outputFileName: Utils.SanitizeFileName(parentName) + ".mkv",
                ffmpegPath: cfg.FfmpegPath,     // hoặc null nếu có trong PATH
                ffprobePath: cfg.FfprobePath,   // nếu có field; nếu không, bỏ qua -> "ffprobe" từ PATH
                segmentTimeSeconds: null,       // giữ BigMkvSplitter ở bước 4
                audioCodec: "aac",
                audioBitrateKbps: 192,
                audioChannels: 2,
                audioSampleRate: 48000,
                selectiveNormalize: true,       // CHỈ encode khi cần
                maxDegreeOfParallelism: 0       // 0 = auto theo CPU
            );
            var manager = new AudioSafeConcatManager(opt, s => Console.WriteLine(s));
            manager.RunAsync(inputs).GetAwaiter().GetResult();

            var normDir = Path.Combine(work.Root, "norm");
            var normList = Directory.Exists(normDir)
                ? Directory.EnumerateFiles(normDir, "*.norm.mkv")
                    .OrderBy(n => n, new NumericNameComparer())
                    .Select(Path.GetFullPath)
                    .ToList()
                : inputs.Select(Path.GetFullPath).ToList(); // fallback nếu norm chưa có (hiếm)

            var manifest = Path.Combine(work.LogsDir, "concat_sources.txt");
            File.WriteAllLines(manifest, normList, new UTF8Encoding(false));

            // Giữ nguyên ghi timeline như trước (phục vụ kiểm tra/ghi log)
            TimelineHelper.WriteConcatTimelineWithOriginalNames(
                work.LogsDir,
                work.VideosDir,
                Utils.IsVideo,
                path => Utils.GetVideoDurationSeconds(cfg, path)
            );

            // 3) SUBTITLE MERGE (now supports auto-unify mixed SRT/VTT)
            Console.WriteLine("[3/5] Building single subtitle file...");
            var subMerger = new SubtitleMerger(cfg, work, logger, opts);
            subMerger.MergeIfNeeded(finalMkv, parentName);

            // 4) SPLIT IF >12h
            Console.WriteLine("[4/5] Checking duration & splitting if > 12h...");
            var splitter = new BigMkvSplitter(cfg, work, logger);
            splitter.SplitIfNeeded(finalMkv, parentName);

            // 5) DONE
            Console.WriteLine("[5/5] Done. Outputs in: " + work.Root);
            Console.WriteLine(" - Video: " + finalMkv + (File.Exists(finalMkv) ? " (exists)" : ""));
            var outSub = subMerger.GetOutputSubtitlePath(parentName);
            if (outSub != null && File.Exists(outSub)) Console.WriteLine(" - Subtitle: " + outSub);

            excel.FlushAndSave();
            return 0;
        }
    }
}
