using static MergeVideo.Enums.OnMissingSubtitleModeContainer;

namespace MergeVideo.Models
{
    internal sealed class RuntimeOptions
    {
        public OnMissingSubtitleMode OnMissingSubtitle { get; set; } = OnMissingSubtitleMode.Skip;
        public bool StrictMapping { get; set; } = false;       // if true -> fail fast when missing
        public string? TargetFormatForced { get; set; } = null; // ".srt" | ".vtt" | null (=auto by majority)
    }
}
