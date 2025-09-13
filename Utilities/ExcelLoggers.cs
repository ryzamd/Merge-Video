using OfficeOpenXml;

namespace MergeVideo.Utilities
{
    class ExcelLoggers
    {
        private readonly string _logsDir;
        private readonly ExcelPackage _pkgVideo;
        private readonly ExcelWorksheet _wsVideo;
        private readonly ExcelPackage _pkgSub;
        private readonly ExcelWorksheet _wsSub;
        private int _rowV = 1, _rowS = 1; // headers

        public ExcelLoggers(string logsDir)
        {
            _logsDir = logsDir;
            _pkgVideo = new ExcelPackage();
            _wsVideo = _pkgVideo.Workbook.Worksheets.Add("rename-video");
            _wsVideo.Cells[_rowV, 1].Value = "STT";
            _wsVideo.Cells[_rowV, 2].Value = "Key";
            _wsVideo.Cells[_rowV, 3].Value = "Original";
            _wsVideo.Cells[_rowV, 4].Value = "Renamed";
            _rowV++;

            _pkgSub = new ExcelPackage();
            _wsSub = _pkgSub.Workbook.Worksheets.Add("rename-subtitle");
            _wsSub.Cells[_rowS, 1].Value = "STT";
            _wsSub.Cells[_rowS, 2].Value = "Key";
            _wsSub.Cells[_rowS, 3].Value = "Original";
            _wsSub.Cells[_rowS, 4].Value = "Renamed";
            _rowS++;
        }

        public void LogVideo(int stt, int key, string original, string renamed)
        {
            _wsVideo.Cells[_rowV, 1].Value = stt;
            _wsVideo.Cells[_rowV, 2].Value = key;
            _wsVideo.Cells[_rowV, 3].Value = original;
            _wsVideo.Cells[_rowV, 4].Value = renamed;
            _rowV++;
        }

        public void LogSubtitle(int stt, int key, string original, string renamed)
        {
            _wsSub.Cells[_rowS, 1].Value = stt;
            _wsSub.Cells[_rowS, 2].Value = key;
            _wsSub.Cells[_rowS, 3].Value = original;
            _wsSub.Cells[_rowS, 4].Value = renamed;
            _rowS++;
        }

        public void FlushAndSave()
        {
            var videoXlsx = Path.Combine(_logsDir, "rename-video.xlsx");
            var subXlsx = Path.Combine(_logsDir, "rename-subtitle.xlsx");

            // Có dữ liệu mới nếu lớn hơn dòng header (row = 2)
            bool hasVideoRows = _rowV > 2;
            bool hasSubRows = _rowS > 2;

            if (hasVideoRows || !File.Exists(videoXlsx))
                _pkgVideo.SaveAs(new FileInfo(videoXlsx)); // ghi mới/ghi đè khi có data mới hoặc file chưa tồn tại

            if (hasSubRows || !File.Exists(subXlsx))
                _pkgSub.SaveAs(new FileInfo(subXlsx));
        }

    }
}
