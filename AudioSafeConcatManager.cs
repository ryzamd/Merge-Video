using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MergeVideo
{
    /// <summary>
    /// Audio-safe pipeline:
    /// - Probe audio bằng ffprobe để quyết định: remux (copy) hay normalize (encode AAC).
    /// - Mặc định chuẩn đồng bộ: AAC-LC, 2ch, 48kHz.
    /// - Với file thiếu audio: thêm silent audio đúng chuẩn.
    /// - Sau đó concat với -c:v copy -c:a copy (nhanh).
    /// </summary>
    public sealed class AudioSafeConcatManager
    {
        public sealed class Options
        {
            public Options(
                string workDir,
                string outputFileName,
                string? ffmpegPath = null,
                string? ffprobePath = null,
                int? segmentTimeSeconds = null,
                string audioCodec = "aac",
                int audioBitrateKbps = 192,
                int audioChannels = 2,
                int audioSampleRate = 48000,
                bool selectiveNormalize = true,
                int maxDegreeOfParallelism = 0 // 0 => auto
            )
            {
                WorkDir = workDir ?? throw new ArgumentNullException(nameof(workDir));
                OutputFileName = outputFileName ?? throw new ArgumentNullException(nameof(outputFileName));
                FfmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath!;
                FfprobePath = string.IsNullOrWhiteSpace(ffprobePath) ? "ffprobe" : ffprobePath!;
                SegmentTimeSeconds = (segmentTimeSeconds.HasValue && segmentTimeSeconds.Value > 0)
                    ? segmentTimeSeconds
                    : null;

                // Audio standard
                AudioCodec = audioCodec;                 // "aac"
                AudioBitrateKbps = audioBitrateKbps;     // 192
                AudioChannels = audioChannels;           // 2
                AudioSampleRate = audioSampleRate;       // 48000

                SelectiveNormalize = selectiveNormalize; // chỉ encode khi cần
                MaxDegreeOfParallelism = maxDegreeOfParallelism <= 0
                    ? Math.Max(1, Environment.ProcessorCount - 1)
                    : maxDegreeOfParallelism;
            }

            public string WorkDir { get; }
            public string OutputFileName { get; }
            public string FfmpegPath { get; }
            public string FfprobePath { get; }
            public int? SegmentTimeSeconds { get; }

            // Chuẩn audio
            public string AudioCodec { get; }
            public int AudioBitrateKbps { get; }
            public int AudioChannels { get; }
            public int AudioSampleRate { get; }

            public bool SelectiveNormalize { get; }
            public int MaxDegreeOfParallelism { get; }
        }

        private readonly Options _opt;
        private readonly Action<string>? _log;
        public AudioSafeConcatManager(Options options, Action<string>? logger = null)
        {
            _opt = options;
            _log = logger;
        }

        public async Task<string> RunAsync(IEnumerable<string> inputFiles, CancellationToken ct = default)
        {
            Directory.CreateDirectory(_opt.WorkDir);
            var normDir = Path.Combine(_opt.WorkDir, "norm");
            Directory.CreateDirectory(normDir);

            var inputs = inputFiles.ToList();
            if (inputs.Count == 0) throw new InvalidOperationException("No input files.");

            // 1) Probe + chuẩn hoá có chọn lọc
            _log?.Invoke($"[Probe] Checking audio compliance ({inputs.Count} files)...");
            var normalized = new List<string>(inputs.Count);

            using var sem = new SemaphoreSlim(_opt.MaxDegreeOfParallelism);
            var tasks = inputs.Select(async src =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    var dst = Path.Combine(normDir, Path.GetFileNameWithoutExtension(src) + ".norm.mkv");
                    var info = await ProbeAudioAsync(src, ct);

                    if (!_opt.SelectiveNormalize)
                    {
                        // luôn encode audio (bản cũ)
                        await NormalizeAudioAsync(src, dst, info, ct);
                    }
                    else
                    {
                        switch (GetCompliance(info))
                        {
                            case AudioCompliance.Compliant:
                                // remux copy cực nhanh sang mkv
                                await RemuxCopyAsync(src, dst, ct);
                                _log?.Invoke($"[OK] {Path.GetFileName(src)} already compliant → remux copy.");
                                break;

                            case AudioCompliance.MissingAudio:
                                // Thêm silent audio đúng chuẩn
                                await NormalizeAddSilentAsync(src, dst, ct);
                                _log?.Invoke($"[Fix] {Path.GetFileName(src)} has NO audio → add silent AAC.");
                                break;

                            default:
                                // Encode audio về chuẩn
                                await NormalizeAudioAsync(src, dst, info, ct);
                                _log?.Invoke($"[Fix] {Path.GetFileName(src)} non-compliant → re-encode AAC.");
                                break;
                        }
                    }

                    lock (normalized) normalized.Add(dst);
                }
                finally { sem.Release(); }
            }).ToList();

            await Task.WhenAll(tasks);

            // 2) videos.txt cho concat
            var videosTxt = Path.Combine(_opt.WorkDir, "videos.txt");
            WriteConcatList(normalized.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase), videosTxt);

            // 3) Concat: copy cả video và audio (vì mọi .norm.mkv đã đồng bộ)
            var finalMkv = Path.Combine(_opt.WorkDir, _opt.OutputFileName);
            await ConcatCopyAsync(videosTxt, finalMkv, ct);

            // 4) Optional split
            if (_opt.SegmentTimeSeconds is int seg && seg > 0)
            {
                var pattern = Path.Combine(
                    _opt.WorkDir,
                    Path.GetFileNameWithoutExtension(_opt.OutputFileName) + " Part%02d.mkv"
                );
                await SplitAsync(finalMkv, pattern, seg, ct);
            }

            return finalMkv;
        }

        // =================== Probe & Compliance ===================

        private enum AudioCompliance { Compliant, MissingAudio, NonCompliant }

        private AudioCompliance GetCompliance(AudioInfo info)
        {
            if (!info.HasAudio) return AudioCompliance.MissingAudio;

            var codecOk = string.Equals(info.CodecName, _opt.AudioCodec, StringComparison.OrdinalIgnoreCase);
            var chOk = info.Channels == _opt.AudioChannels;
            var rateOk = info.SampleRate == _opt.AudioSampleRate;

            // profile: ưu tiên LC; nếu ffprobe không điền profile thì bỏ qua
            var profileOk = string.IsNullOrWhiteSpace(info.Profile)
                            || info.Profile!.Contains("LC", StringComparison.OrdinalIgnoreCase);

            return (codecOk && chOk && rateOk && profileOk)
                ? AudioCompliance.Compliant
                : AudioCompliance.NonCompliant;
        }

        private sealed class AudioInfo
        {
            public bool HasAudio { get; init; }
            public string? CodecName { get; init; }
            public int? Channels { get; init; }
            public int? SampleRate { get; init; }
            public string? Profile { get; init; }
            public double? DurationSec { get; init; }
        }

        private sealed class FfprobeRoot
        {
            [JsonPropertyName("streams")] public List<FfprobeStream>? Streams { get; set; }
            [JsonPropertyName("format")] public FfprobeFormat? Format { get; set; }
        }
        private sealed class FfprobeStream
        {
            [JsonPropertyName("codec_type")] public string? CodecType { get; set; }
            [JsonPropertyName("codec_name")] public string? CodecName { get; set; }
            [JsonPropertyName("channels")] public int? Channels { get; set; }
            [JsonPropertyName("sample_rate")] public string? SampleRate { get; set; }
            [JsonPropertyName("profile")] public string? Profile { get; set; }
        }
        private sealed class FfprobeFormat
        {
            [JsonPropertyName("duration")] public string? Duration { get; set; }
        }

        private async Task<AudioInfo> ProbeAudioAsync(string path, CancellationToken ct)
        {
            // Gom show_entries lại cho gọn (không bắt buộc)
            var args = string.Join(" ", new[]
            {
                "-v error",
                "-select_streams a:0",
                "-show_entries",
                "stream=codec_type,codec_name,channels,sample_rate,profile:format=duration",
                "-of json",
                Q(path)
            });

            var (exit, stdout, stderr) = await RunToolCaptureSplitAsync(_opt.FfprobePath, args, _opt.WorkDir, ct);

            if (exit != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                _log?.Invoke($"[ffprobe] exit={exit}, stderr={(stderr ?? "").Trim()}");
                // Không crash: coi như không lấy được info -> buộc normalize
                return new AudioInfo { HasAudio = true }; // HasAudio=true để rơi vào NonCompliant
            }

            try
            {
                var root = JsonSerializer.Deserialize<FfprobeRoot>(stdout, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                var a = root?.Streams?.FirstOrDefault(s =>
                    string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase));

                if (a == null)
                {
                    return new AudioInfo { HasAudio = false };
                }

                int? sr = null;
                if (int.TryParse(a.SampleRate, out var srParsed)) sr = srParsed;

                double? dur = null;
                if (double.TryParse(root?.Format?.Duration,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var d))
                    dur = d;

                return new AudioInfo
                {
                    HasAudio = true,
                    CodecName = a.CodecName,
                    Channels = a.Channels,
                    SampleRate = sr,
                    Profile = a.Profile,
                    DurationSec = dur
                };
            }
            catch (Exception ex)
            {
                // JSON không sạch (do warning ở stderr, ký tự lạ, v.v.) -> log và đánh dấu NonCompliant
                _log?.Invoke($"[ffprobe-parse] {ex.GetType().Name}: {ex.Message}");
                if (!string.IsNullOrWhiteSpace(stderr))
                    _log?.Invoke($"[ffprobe-stderr]\n{stderr.Trim()}");
                // Bắt normalize thay vì crash
                return new AudioInfo { HasAudio = true };
            }
        }


        // =================== Steps ===================

        // Remux copy cho file đã đạt chuẩn (rất nhanh)
        private async Task RemuxCopyAsync(string input, string outputNormMkv, CancellationToken ct)
        {
            var args = new StringBuilder()
                .Append("-hide_banner -y ")
                .Append($"-i {Q(input)} ")
                .Append("-map 0:v:0 -map 0:a:0? -sn -dn ")
                .Append("-c copy ")
                .Append("-fflags +genpts ")
                .Append("-avoid_negative_ts make_zero ")
                .Append("-max_interleave_delta 0 ")
                .Append(Q(outputNormMkv))
                .ToString();

            await RunFfmpegAsync(args, _opt.WorkDir, ct);
        }

        // Encode audio về chuẩn (giữ nguyên video)
        private async Task NormalizeAudioAsync(string input, string outputNormMkv, AudioInfo info, CancellationToken ct)
        {
            var args = new StringBuilder()
                .Append("-hide_banner -y ")
                .Append($"-i {Q(input)} ")
                .Append("-map 0:v:0 -map 0:a:0? -sn -dn ")
                .Append("-c:v copy ")
                .Append($"-c:a {_opt.AudioCodec} -b:a {_opt.AudioBitrateKbps}k -ar {_opt.AudioSampleRate} -ac {_opt.AudioChannels} ")
                .Append("-fflags +genpts ")
                .Append("-avoid_negative_ts make_zero ")
                .Append("-max_interleave_delta 0 ")
                .Append(Q(outputNormMkv))
                .ToString();

            await RunFfmpegAsync(args, _opt.WorkDir, ct);
        }

        // Trường hợp thiếu audio: thêm silent AAC chuẩn, bám theo độ dài video (-shortest)
        private async Task NormalizeAddSilentAsync(string input, string outputNormMkv, CancellationToken ct)
        {
            var args = new StringBuilder()
                .Append("-hide_banner -y ")
                .Append($"-i {Q(input)} ")
                .Append($"-f lavfi -i anullsrc=r={_opt.AudioSampleRate}:cl=stereo ")
                .Append("-shortest ")
                .Append("-map 0:v:0 -map 1:a:0 -sn -dn ")
                .Append("-c:v copy ")
                .Append($"-c:a {_opt.AudioCodec} -b:a {_opt.AudioBitrateKbps}k -ar {_opt.AudioSampleRate} -ac {_opt.AudioChannels} ")
                .Append("-fflags +genpts ")
                .Append("-avoid_negative_ts make_zero ")
                .Append("-max_interleave_delta 0 ")
                .Append(Q(outputNormMkv))
                .ToString();

            await RunFfmpegAsync(args, _opt.WorkDir, ct);
        }

        // Concat sau khi tất cả .norm.mkv đã đồng bộ → copy cả audio & video
        private async Task ConcatCopyAsync(string videosTxtPath, string outputMkv, CancellationToken ct)
        {
            var args = new StringBuilder()
                .Append("-hide_banner -y ")
                .Append("-f concat -safe 0 ")
                .Append($"-i {Q(videosTxtPath)} ")
                .Append("-map 0:v:0 -map 0:a:0? ")
                .Append("-c:v copy -c:a copy ")
                .Append("-fflags +genpts ")
                .Append("-avoid_negative_ts make_zero ")
                .Append("-max_interleave_delta 0 ")
                .Append(Q(outputMkv))
                .ToString();

            await RunFfmpegAsync(args, _opt.WorkDir, ct);
        }

        private async Task SplitAsync(string inputMkv, string outputPattern, int segmentTimeSeconds, CancellationToken ct)
        {
            var args = new StringBuilder()
                .Append("-hide_banner -y ")
                .Append($"-i {Q(inputMkv)} ")
                .Append("-map 0 ")
                .Append("-c copy ")
                .Append("-f segment ")
                .Append($"-segment_time {segmentTimeSeconds} ")
                .Append("-reset_timestamps 1 ")
                .Append(Q(outputPattern))
                .ToString();

            await RunFfmpegAsync(args, _opt.WorkDir, ct);
        }

        // =================== Process helpers ===================

        private async Task<int> RunFfmpegAsync(string arguments, string workingDir, CancellationToken ct)
        {
            _log?.Invoke($"[FFmpeg] {_opt.FfmpegPath} {arguments}");
            return await RunToolAsync(_opt.FfmpegPath, arguments, workingDir, ct);
        }

        private async Task<(int exitCode, string stdout)> RunToolCaptureAsync(string fileName, string arguments, string workingDir, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var p = new Process { StartInfo = psi };
            if (!p.Start()) throw new InvalidOperationException($"Cannot start {fileName}.");

            using (ct.Register(() => { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { } }))
            {
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                await Task.WhenAll(p.WaitForExitAsync(ct), outTask, errTask).ConfigureAwait(false);
                var combined = (outTask.Result ?? string.Empty) + (errTask.Result ?? string.Empty);
                return (p.ExitCode, combined);
            }
        }

        private async Task<int> RunToolAsync(string fileName, string arguments, string workingDir, CancellationToken ct)
        {
            var (exit, _) = await RunToolCaptureAsync(fileName, arguments, workingDir, ct);
            if (exit != 0) throw new InvalidOperationException($"{fileName} exited with code {exit}.");
            return exit;
        }

        private async Task<(int exitCode, string stdout, string stderr)> RunToolCaptureSplitAsync(string fileName, string arguments, string workingDir, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var p = new Process { StartInfo = psi };
            if (!p.Start()) throw new InvalidOperationException($"Cannot start {fileName}.");

            using (ct.Register(() => { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { } }))
            {
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                await Task.WhenAll(p.WaitForExitAsync(ct), outTask, errTask).ConfigureAwait(false);
                return (p.ExitCode, outTask.Result ?? string.Empty, errTask.Result ?? string.Empty);
            }
        }

        private void WriteConcatList(IEnumerable<string> files, string videosTxtPath)
        {
            var sb = new StringBuilder();
            foreach (var f in files)
            {
                var p = f.Replace("'", "''");
                sb.Append("file '").Append(p).AppendLine("'");
            }
            File.WriteAllText(videosTxtPath, sb.ToString(), new UTF8Encoding(false));
            _log?.Invoke($"[ConcatList] {videosTxtPath}");
        }

        private static string Q(string p) => $"\"{p}\"";
    }
}
