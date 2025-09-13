using MergeVideo.Utilities;

namespace MergeVideo.Models
{
    public class WorkDirs
    {
        public string Root { get; init; } = default!;
        public string VideosDir { get; init; } = default!;
        public string SubsDir { get; init; } = default!;
        public string PathDir { get; init; } = default!;
        public string LogsDir { get; init; } = default!;
        public string ReportDir { get; init; } = default!;
        public string FilesDir { get; init; } = default!;

        public static WorkDirs Prepare(string parent)
        {
            var workRoot = Path.Combine(parent, "work");
            Utils.EnsureDir(workRoot);
            var wd = new WorkDirs
            {
                Root = workRoot,
                VideosDir = Path.Combine(workRoot, "videos"),
                SubsDir = Path.Combine(workRoot, "subtitles"),
                PathDir = Path.Combine(workRoot, "path"),
                LogsDir = Path.Combine(workRoot, "logs"),
                ReportDir = Path.Combine(workRoot, "report"),
                FilesDir = Path.Combine(workRoot, "files")
            };
            Utils.EnsureDir(wd.VideosDir);
            Utils.EnsureDir(wd.SubsDir);
            Utils.EnsureDir(wd.PathDir);
            Utils.EnsureDir(wd.LogsDir);
            Utils.EnsureDir(wd.ReportDir);
            Utils.EnsureDir(wd.FilesDir);
            return wd;
        }
    }
}
