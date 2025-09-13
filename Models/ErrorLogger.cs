using System.Text;

namespace MergeVideo.Models
{
    internal class ErrorLogger
    {
        private readonly string _file;
        private readonly object _lock = new object();
        public ErrorLogger(string reportDir)
        {
            _file = Path.Combine(reportDir, "errors.txt");
        }
        public void Warn(string message)
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] WARN: {message}";
            Console.WriteLine(line);
            lock (_lock)
            {
                File.AppendAllText(_file, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        public void Error(string message)
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR: {message}";
            Console.WriteLine(line);
            lock (_lock)
            {
                File.AppendAllText(_file, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
    }
}
