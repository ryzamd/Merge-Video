using OfficeOpenXml;
using System.Text;

namespace MergeVideo
{
    public static class TimelineHelper
    {
        public static void WriteConcatTimelineWithOriginalNames(
            string logsDir,
            string videosDir,
            Func<string, bool> isVideo,
            Func<string, double> getVideoDurationSeconds)
        {
            var excelPath = Path.Combine(logsDir, "rename-video.xlsx");
            if (!File.Exists(excelPath)) return;

            // Đọc tên gốc từ Excel
            var originalNames = new List<string>();
            using (var pkg = new ExcelPackage(new FileInfo(excelPath)))
            {
                var ws = pkg.Workbook.Worksheets["rename-video"];
                int row = 2; // Bỏ qua header
                while (true)
                {
                    var val = ws.Cells[row, 3].Value;
                    if (val == null) break;
                    originalNames.Add(val.ToString()!);
                    row++;
                }
            }

            // Lấy danh sách file đã đổi tên (đảm bảo đúng thứ tự)
            var videoFiles = Directory.EnumerateFiles(videosDir)
                .Where(isVideo)
                .OrderBy(n => n, new NumericNameComparer())
                .ToList();

            if (videoFiles.Count == 0 || videoFiles.Count != originalNames.Count)
                return; // Không khớp số lượng

            var timelinePath = Path.Combine(logsDir, "timeline.txt");
            using var sw = new StreamWriter(timelinePath, false, new UTF8Encoding(false));
            sw.WriteLine("TRACKLIST :");

            double acc = 0;
            for (int i = 0; i < videoFiles.Count; i++)
            {
                double dur = 0;
                try { dur = getVideoDurationSeconds(videoFiles[i]); }
                catch { }
                var ts = TimeSpan.FromSeconds(acc);
                string tsStr = ts.TotalHours >= 1 ? $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes:00}:{ts.Seconds:00}";
                sw.WriteLine($"{tsStr} | {originalNames[i]}");
                acc += dur;
            }
        }
    }
}
