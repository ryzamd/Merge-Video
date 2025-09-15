using Spectre.Console;
using MergeVideo.Enums;
using MergeVideo.Models;
using MergeVideo.Utilities;
using System.Text;
using OfficeOpenXml;

namespace MergeVideo
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            ExcelPackage.License.SetNonCommercialPersonal("<My Name>");

            try
            {
                if (args.Length == 0)
                {
                    Console.Write("Root path: ");
                    var root = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(root)) return 0;
                    return RunOneJob(Path.GetFullPath(root), new RuntimeOptions());
                }
                else
                {
                    var root = Path.GetFullPath(args[0]);
                    return RunOneJob(root, new RuntimeOptions());
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
            if (!Directory.Exists(parentFolder))
            {
                Console.WriteLine($"Parent folder not found: {parentFolder}");
                return 2;
            }

            var cfg = Config.DefaultWithToolPaths();
            var work = WorkDirs.Prepare(parentFolder);
            var logger = new ErrorLogger(work.ReportDir);
            var excel = new ExcelLoggers(work.LogsDir);

            // [1/4] Scan + Rename (giữ nguyên cách bạn làm)
            Console.WriteLine("[1/4] Scanning sub-folders & renaming files...");
            var renamer = new Renamer(cfg, work, logger, excel, opts);
            var renameState = new RenameState();
            renamer.ScanAndRenameIfNeeded(parentFolder, renameState);
            AnsiConsole.MarkupLine("[green]########################### - 100%[/]");

            // Chuẩn bị danh sách video đầu vào
            var inputs = Directory.EnumerateFiles(work.VideosDir)
                .Where(Utils.IsVideo)
                .OrderBy(n => n, new NumericNameComparer())
                .ToList();

            if (inputs.Count == 0)
            {
                logger.Error("No videos found.");
                return 2;
            }

            // Kiểm tra mapping subtitle (giữ logic hiện tại)
            if (!Utils.ValidateSubtitleMapping(work.SubsDir, inputs, opts, logger, out var subMap))
                return 3;

            // [2/4] Concatenating...
            Console.WriteLine("[2/4] Concatenating videos (audio-safe) ...");

            var parentName = Path.GetFileName(parentFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var baseName = Utils.SanitizeFileName(parentName); // dùng làm prefix output

            var manager = new AudioSafeConcatManager(
                new AudioSafeConcatManager.Options(
                    workDir: work.Root,
                    outputBaseName: baseName,    // ví dụ: "<CourseName>"
                    ffmpegPath: cfg.FfmpegPath,
                    ffprobePath: cfg.FfprobePath,
                    maxPartHours: 11.0,          // chia Part 11h
                    selectiveNormalize: true
                ),
                logger: s => Console.WriteLine(s)
            );

            // Manager sẽ tự in [2.1/4], [2.2/4] và hiển thị progress
            var outputs = manager.RunAsync(inputs).GetAwaiter().GetResult();

            // Ghi timeline (giữ nguyên)
            TimelineHelper.WriteConcatTimelineWithOriginalNames(
                work.LogsDir,
                work.VideosDir,
                Utils.IsVideo,
                path => Utils.GetVideoDurationSeconds(cfg, path)
            );

            Console.WriteLine("[3/4] Merge subtitle:");
            ConsoleProgressBar.RunPerOutputBarsAsync(
                groupHeader: "", // header đã in ở trên
                outputs,
                title: part => Path.GetFileName(Path.ChangeExtension(part.OutputPath, ".srt")),
                runner: async (part, onPercent) =>
                {
                    var srtPath = Path.ChangeExtension(part.OutputPath, ".srt");

                    // Merge subtitle theo danh sách clip đã concat cho part này
                    Utils.MergeSubtitlesForGroupUsingVideoDurations(
                        orderedVideoPaths: part.Inputs,
                        baseToSubtitlePath: subMap,
                        cfg: cfg,
                        outputSrtPath: srtPath,
                        logger: logger
                    );

                    await onPercent(100);
                }).GetAwaiter().GetResult();

            // [4/4] Done
            Console.WriteLine("[4/4] Done. Outputs in: " + work.Root);
            foreach (var mkv in outputs.Select(o => o.OutputPath))
                Console.WriteLine(" - Video: " + mkv);
            foreach (var srt in outputs.Select(o => Path.ChangeExtension(o.OutputPath, ".srt")).Where(File.Exists))
                Console.WriteLine(" - Subtitle: " + srt);


            // Cleanup (nếu bạn đã có)
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
