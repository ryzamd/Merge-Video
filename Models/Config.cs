namespace MergeVideo.Models
{
    public class Config
    {
        public string FfmpegPath { get; init; } = @"C:\ffmpeg\bin\ffmpeg.exe";
        public string FfprobePath { get; init; } = @"C:\ffmpeg\bin\ffprobe.exe";
        public string MkvMergePath { get; init; } = @"C:\Program Files\MKVToolNix\mkvmerge.exe";

        public static Config DefaultWithToolPaths() => new Config();
    }
}
