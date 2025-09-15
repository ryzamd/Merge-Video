using Spectre.Console;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MergeVideo
{
    public sealed class AudioSafeConcatManager
    {
        public sealed class Options
        {
            public Options(
                string workDir,
                string outputBaseName,         // ví dụ: "<CourseName>" (không kèm .mkv)
                string? ffmpegPath = null,
                string? ffprobePath = null,
                double maxPartHours = 11.0,    // chia Part theo ngưỡng 11h
                string audioCodec = "aac",
                int audioBitrateKbps = 192,
                int audioChannels = 2,
                int audioSampleRate = 48000,
                bool selectiveNormalize = true,
                int? degreeOfParallelism = null // null => CPU-1
            )
            {
                WorkDir = workDir ?? throw new ArgumentNullException(nameof(workDir));
                OutputBaseName = outputBaseName ?? throw new ArgumentNullException(nameof(outputBaseName));
                FfmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath!;
                FfprobePath = string.IsNullOrWhiteSpace(ffprobePath) ? "ffprobe" : ffprobePath!;
                MaxPartHours = Math.Max(0.1, maxPartHours);

                AudioCodec = audioCodec;
                AudioBitrateKbps = audioBitrateKbps;
                AudioChannels = audioChannels;
                AudioSampleRate = audioSampleRate;

                SelectiveNormalize = selectiveNormalize;
                DegreeOfParallelism = Math.Max(1, degreeOfParallelism ?? (Environment.ProcessorCount - 1));
            }

            public string WorkDir { get; }
            public string OutputBaseName { get; }
            public string FfmpegPath { get; }
            public string FfprobePath { get; }
            public double MaxPartHours { get; }
            public string AudioCodec { get; }
            public int AudioBitrateKbps { get; }
            public int AudioChannels { get; }
            public int AudioSampleRate { get; }
            public bool SelectiveNormalize { get; }
            public int DegreeOfParallelism { get; }
        }

        private readonly Options _opt;
        private readonly Action<string>? _log;
        public AudioSafeConcatManager(Options options, Action<string>? logger = null)
        {
            _opt = options;
            _log = logger;
        }
        public sealed record PartResult(string OutputPath, IReadOnlyList<string> Inputs);

        // Trả về danh sách output .mkv (1 hoặc nhiều Part)
        public async Task<List<PartResult>> RunAsync(IEnumerable<string> inputFiles, CancellationToken ct = default)
        {
            Directory.CreateDirectory(_opt.WorkDir);
            var normDir = Path.Combine(_opt.WorkDir, "norm");
            Directory.CreateDirectory(normDir);

            var inputs = inputFiles.ToList();
            if (inputs.Count == 0) throw new InvalidOperationException("No input files.");

            // [2.1/4] Normalize all video .... (1 progress bar tổng)
            ConsoleProgressBar.WriteHeader("[2.1/4] Normalize all video ....");

            var normLogs = new ConcurrentQueue<string>();

            await ConsoleProgressBar.RunSingleBarByCountAsync(
                label: "Normalize",
                totalCount: inputs.Count,
                work: async report =>
                {
                    using var sem = new SemaphoreSlim(_opt.DegreeOfParallelism);
                    var done = 0;

                    var tasks = inputs.Select(async src =>
                    {
                        await sem.WaitAsync(ct);
                        try
                        {
                            var info = await ProbeAudioAsync(src, ct);
                            var dst = Path.Combine(normDir, Path.GetFileNameWithoutExtension(src) + ".norm.mkv");
                            var baseName = Path.GetFileName(src);

                            if (!_opt.SelectiveNormalize)
                            {
                                normLogs.Enqueue($"[Fix] {baseName} non-compliant → AAC encode.");
                                await EncodeAacAsync(src, dst, info, ct);
                            }
                            else
                            {
                                switch (GetCompliance(info))
                                {
                                    case AudioCompliance.Compliant:
                                        normLogs.Enqueue($"[OK] {baseName} compliant → remux copy.");
                                        await RemuxCopyAsync(src, dst, ct);
                                        break;

                                    case AudioCompliance.MissingAudio:
                                        normLogs.Enqueue($"[Fix] {baseName} missing audio → add silent + AAC encode.");
                                        await AddSilentAsync(src, dst, ct);
                                        break;

                                    default:
                                        normLogs.Enqueue($"[Fix] {baseName} non-compliant → AAC encode.");
                                        await EncodeAacAsync(src, dst, info, ct);
                                        break;
                                }
                            }
                        }
                        finally
                        {
                            Interlocked.Increment(ref done);
                            await report(done);   // cập nhật % và xả log ngay trong Report()
                            sem.Release();
                        }
                    });

                    await Task.WhenAll(tasks);
                },
                logs: normLogs  // ✅ xả log ở ngay trong Progress.StartAsync
            );

            // Build danh sách norm theo thứ tự tự nhiên (số)
            var normFiles = Directory.EnumerateFiles(normDir, "*.norm.mkv")
                .OrderBy(n => n, new NumericNameComparer())
                .ToList();

            // Chia nhóm ≤ MaxPartHours
            var parts = BuildGroups(normFiles, _opt.MaxPartHours);

            // [2.2/4] Concatening video:
            await ConsoleProgressBar.RunPerOutputBarsAsync(
                "[2.2/4] Concatening video:",
                parts,
                title: part => part.OutputName,
                runner: async (part, onPercent) =>
                {
                    // Viết list concat
                    var listPath = Path.Combine(_opt.WorkDir, $"concat_{part.Index:00}.txt");
                    File.WriteAllLines(listPath, part.Inputs.Select(f => $"file '{f.Replace("'", "''")}'"), new UTF8Encoding(false));

                    var args = $"-f concat -safe 0 -i \"{listPath}\" -map 0:v:0 -map 0:a:0? -c:v copy -c:a copy -fflags +genpts -avoid_negative_ts make_zero -max_interleave_delta 0 \"{part.OutputPath}\"";

                    await ConsoleProgressBar.RunFfmpegWithProgressAsync(
                        _opt.FfmpegPath,
                        args,
                        totalDurationSec: part.TotalDurationSec,
                        workingDir: _opt.WorkDir,
                        onPercent: onPercent
                    );

                    TryDeleteQuiet(listPath);
                });

            return parts.Select(p => new PartResult(p.OutputPath, p.Inputs)).ToList();
        }

        // ================= Normalize or Copy =================

        private async Task NormalizeOrCopyAsync(string input, string outputNormMkv, AudioInfo info, CancellationToken ct)
        {
            if (!_opt.SelectiveNormalize)
            {
                await EncodeAacAsync(input, outputNormMkv, info, ct);
                return;
            }

            switch (GetCompliance(info))
            {
                case AudioCompliance.Compliant:
                    await RemuxCopyAsync(input, outputNormMkv, ct);
                    _log?.Invoke($"[OK] {Path.GetFileName(input)} compliant → remux copy.");
                    break;
                case AudioCompliance.MissingAudio:
                    await AddSilentAsync(input, outputNormMkv, ct);
                    _log?.Invoke($"[Fix] {Path.GetFileName(input)} missing audio → add silent.");
                    break;
                default:
                    await EncodeAacAsync(input, outputNormMkv, info, ct);
                    _log?.Invoke($"[Fix] {Path.GetFileName(input)} non-compliant → AAC encode.");
                    break;
            }
        }

        private async Task RemuxCopyAsync(string input, string output, CancellationToken ct)
        {
            var args = $"-i \"{input}\" -map 0:v:0 -map 0:a:0? -sn -dn -c copy -fflags +genpts -avoid_negative_ts make_zero -max_interleave_delta 0 \"{output}\"";
            await RunFfmpegSilentAsync(args, ct);
        }

        private async Task AddSilentAsync(string input, string output, CancellationToken ct)
        {
            var args = $"-i \"{input}\" -f lavfi -i anullsrc=r={_opt.AudioSampleRate}:cl=stereo -shortest -map 0:v:0 -map 1:a:0 -sn -dn -c:v copy -c:a {_opt.AudioCodec} -b:a {_opt.AudioBitrateKbps}k -ar {_opt.AudioSampleRate} -ac {_opt.AudioChannels} -fflags +genpts -avoid_negative_ts make_zero -max_interleave_delta 0 \"{output}\"";
            await RunFfmpegSilentAsync(args, ct);
        }

        private async Task EncodeAacAsync(string input, string output, AudioInfo info, CancellationToken ct)
        {
            var args = $"-i \"{input}\" -map 0:v:0 -map 0:a:0? -sn -dn -c:v copy -c:a {_opt.AudioCodec} -b:a {_opt.AudioBitrateKbps}k -ar {_opt.AudioSampleRate} -ac {_opt.AudioChannels} -fflags +genpts -avoid_negative_ts make_zero -max_interleave_delta 0 \"{output}\"";
            await RunFfmpegSilentAsync(args, ct);
        }

        private async Task RunFfmpegSilentAsync(string args, CancellationToken ct)
        {
            if (!args.Contains("-loglevel")) args = "-loglevel error -xerror " + args;

            var psi = new ProcessStartInfo
            {
                FileName = _opt.FfmpegPath,
                Arguments = "-hide_banner -y " + args,
                WorkingDirectory = _opt.WorkDir,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            p.Exited += (_, __) => tcs.TrySetResult(p.ExitCode);

            if (!p.Start()) throw new InvalidOperationException("Cannot start ffmpeg.");
            p.BeginOutputReadLine(); p.BeginErrorReadLine();

            using (ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } }))
            {
                var exit = await tcs.Task.ConfigureAwait(false);
                if (exit != 0) throw new InvalidOperationException($"ffmpeg exited with code {exit}");
            }
        }

        // ================= Probe =================

        private sealed class AudioInfo
        {
            public bool HasAudio { get; init; }
            public string? CodecName { get; init; }
            public int? Channels { get; init; }
            public int? SampleRate { get; init; }
            public string? Profile { get; init; }
            public double? DurationSec { get; init; }
        }

        private enum AudioCompliance { Compliant, MissingAudio, NonCompliant }

        private AudioCompliance GetCompliance(AudioInfo info)
        {
            if (!info.HasAudio) return AudioCompliance.MissingAudio;

            bool codecOk = string.Equals(info.CodecName, _opt.AudioCodec, StringComparison.OrdinalIgnoreCase);
            bool chOk = info.Channels == _opt.AudioChannels;
            bool rateOk = info.SampleRate == _opt.AudioSampleRate;
            bool profileOk = string.IsNullOrWhiteSpace(info.Profile) ||
                             info.Profile.Contains("LC", StringComparison.OrdinalIgnoreCase);

            return (codecOk && chOk && rateOk && profileOk)
                ? AudioCompliance.Compliant
                : AudioCompliance.NonCompliant;
        }

        private async Task<AudioInfo> ProbeAudioAsync(string path, CancellationToken ct)
        {
            var args = "-v error -select_streams a:0 -show_entries stream=codec_type,codec_name,channels,sample_rate,profile:format=duration -of json " + Quote(path);
            var (exit, stdout, stderr) = await RunToolCaptureAsync(_opt.FfprobePath, args, ct);
            if (exit != 0 || string.IsNullOrWhiteSpace(stdout))
                return new AudioInfo { HasAudio = true };

            try
            {
                var root = JsonSerializer.Deserialize<FfprobeRoot>(stdout, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                var a = root?.Streams?.FirstOrDefault(s =>
                    string.Equals(s.CodecType, "audio", StringComparison.OrdinalIgnoreCase));

                if (a == null) return new AudioInfo { HasAudio = false };

                int? sr = null;
                if (int.TryParse(a.SampleRate, out var srParsed)) sr = srParsed;

                double? dur = null;
                if (double.TryParse(root?.Format?.Duration, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
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
            catch
            {
                return new AudioInfo { HasAudio = true };
            }
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

        private async Task<(int exit, string stdout, string stderr)> RunToolCaptureAsync(string exe, string args, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                WorkingDirectory = _opt.WorkDir,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var so = new StringBuilder(); var se = new StringBuilder();

            p.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) so.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) se.AppendLine(e.Data); };
            p.Exited += (_, __) => tcs.TrySetResult(p.ExitCode);

            if (!p.Start()) throw new InvalidOperationException($"Cannot start {exe}");
            p.BeginOutputReadLine(); p.BeginErrorReadLine();

            using (ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } }))
            {
                var exit = await tcs.Task.ConfigureAwait(false);
                return (exit, so.ToString(), se.ToString());
            }
        }

        // ================= Concat grouping =================

        private sealed record PartSpec(int Index, string OutputName, string OutputPath, IReadOnlyList<string> Inputs, double TotalDurationSec);

        private List<PartSpec> BuildGroups(List<string> normFiles, double maxHours)
        {
            double maxSec = maxHours * 3600.0;
            var parts = new List<PartSpec>();
            var cur = new List<string>();
            double curSec = 0;
            int idx = 1;

            foreach (var f in normFiles)
            {
                var dur = GetDurationSafe(f);
                if (cur.Count > 0 && (curSec + dur) > maxSec)
                {
                    parts.Add(MakePart(idx++, cur, curSec));
                    cur = new List<string>();
                    curSec = 0;
                }
                cur.Add(f);
                curSec += dur;
            }
            if (cur.Count > 0) parts.Add(MakePart(idx++, cur, curSec));
            return parts;
        }

        private PartSpec MakePart(int index, List<string> inputs, double totalSec)
        {
            var name = (_opt.OutputBaseName) + (index == 1 && totalSec < _opt.MaxPartHours * 3600.0 ? $" - Part {index:00}.mkv" : $" - Part {index:00}.mkv");
            var path = Path.Combine(_opt.WorkDir, name);
            return new PartSpec(index, name, path, inputs, totalSec);
        }

        private double GetDurationSafe(string f)
        {
            try
            {
                var (exit, stdout, _) = RunToolCaptureAsync(_opt.FfprobePath,
                    $"-v error -show_entries format=duration -of default=nk=1:nw=1 \"{f}\"", CancellationToken.None)
                    .GetAwaiter().GetResult();

                if (exit == 0 && double.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
                    return s;
            }
            catch { }
            return 0;
        }

        private static void TryDeleteQuiet(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static string Quote(string p) => $"\"{p}\"";
    }
}
