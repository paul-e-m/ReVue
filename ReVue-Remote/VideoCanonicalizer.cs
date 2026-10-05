using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace ReVueRemote;

public sealed class VideoCanonicalizer
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".ts", ".m2ts"
    };

    private static readonly HashSet<string> SupportedPixelFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "yuv420p", "yuvj420p"
    };

    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly ILogger<VideoCanonicalizer> _logger;
    private readonly SemaphoreSlim _processingGate = new(1, 1);

    public VideoCanonicalizer(IConfiguration configuration, ILogger<VideoCanonicalizer> logger)
    {
        _ffmpegPath = configuration["ReVueRemote:FFmpegPath"]?.Trim() is { Length: > 0 } ffmpeg
            ? ffmpeg
            : "ffmpeg";
        _ffprobePath = configuration["ReVueRemote:FFprobePath"]?.Trim() is { Length: > 0 } ffprobe
            ? ffprobe
            : "ffprobe";
        _logger = logger;
    }

    public static bool IsSupportedUploadFileName(string? fileName)
        => SupportedExtensions.Contains(Path.GetExtension(fileName ?? ""));

    public static string SupportedUploadExtensionsText
        => string.Join(", ", SupportedExtensions.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));

    public static string GetCanonicalFileName(string inputFileName)
        => Path.GetFileNameWithoutExtension(inputFileName) + ".mp4";

    public static void EnsureWorkingSpace(string path, long inputSizeBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root)) return;
            var available = new DriveInfo(root).AvailableFreeSpace;
            var safetyMargin = 512L * 1024 * 1024;
            var required = checked(inputSizeBytes * 2 + safetyMargin);
            if (available < required)
                throw new IOException(
                    $"The server needs at least {FormatGiB(required)} free to process this video, " +
                    $"but only {FormatGiB(available)} is available.");
        }
        catch (IOException) { throw; }
        catch (UnauthorizedAccessException) { throw; }
        catch
        {
            // Some network-backed filesystems do not expose capacity through
            // DriveInfo. Let the actual file operations report failures there.
        }
    }

    public async Task<CanonicalVideoMetadata> CanonicalizeAsync(
        string sourcePath,
        string outputPath,
        long maximumOutputBytes,
        CancellationToken cancellationToken,
        string? transcodeMode = null,
        Action<double>? onTranscodeProgress = null)
    {
        await _processingGate.WaitAsync(cancellationToken);
        try
        {
            var input = await ProbeAsync(sourcePath, cancellationToken);
            ValidateProbe(input, "uploaded video");

            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
            if (transcodeMode is not null)
            {
                var inputVideo = input.Streams.First(stream =>
                    string.Equals(stream.CodecType, "video", StringComparison.OrdinalIgnoreCase));
                if (transcodeMode == VideoTranscodeModes.Resolution && inputVideo.Height != 1080)
                    throw new InvalidOperationException("Only 1080-line videos can be converted to 720p.");
                if (transcodeMode == VideoTranscodeModes.FrameRate && inputVideo.FramesPerSecond is not > 30)
                    throw new InvalidOperationException("Only videos above 30 fps can be converted to 29.97 fps.");
                if (transcodeMode != VideoTranscodeModes.Resolution &&
                    transcodeMode != VideoTranscodeModes.FrameRate)
                    throw new InvalidOperationException("Unsupported video conversion.");

                // Force the source's keyframe timestamps. The frame-rate
                // conversion changes the GOP length in frames accordingly.
                GopMetadata? sourceGop = null;
                try { sourceGop = await ProbeGopAsync(sourcePath, cancellationToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not inspect source GOP for {FileName}", Path.GetFileName(sourcePath));
                }
                var fps = inputVideo.FramesPerSecond ?? 30;
                var sourceGopFrames = sourceGop?.MaxFrames is long frames && frames > 0
                    ? frames : Math.Round(fps * 2);
                var outputFps = transcodeMode == VideoTranscodeModes.FrameRate ? 30_000d / 1001 : fps;
                var gopFrames = (int)Math.Clamp(
                    Math.Round(sourceGopFrames * outputFps / fps), 1, int.MaxValue);
                // A source already encoded below 5 Mbps should not become a
                // larger transfer merely because it was scaled to 720p.
                var sourceBitrate = EstimateVideoBitrate(input, inputVideo, sourcePath) ?? 10_000_000L;
                var peakBitrate = Math.Clamp(
                    (long)Math.Round(sourceBitrate * 0.8),
                    100_000, transcodeMode == VideoTranscodeModes.Resolution ? 5_000_000 : 25_000_000);
                var videoFilter = transcodeMode == VideoTranscodeModes.Resolution
                    ? (inputVideo.FieldOrder is "tt" or "bb" or "tb" or "bt"
                        ? "bwdif=mode=send_frame,scale=1280:720:force_original_aspect_ratio=decrease:flags=lanczos,pad=1280:720:(ow-iw)/2:(oh-ih)/2,format=yuv420p"
                        : "scale=1280:720:force_original_aspect_ratio=decrease:flags=lanczos,pad=1280:720:(ow-iw)/2:(oh-ih)/2,format=yuv420p")
                    : "fps=30000/1001,format=yuv420p";
                var arguments = new[]
                {
                    "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                    "-stats_period", "1", "-progress", "pipe:1",
                    "-threads", "2",
                    "-i", sourcePath,
                    "-map", "0:v:0", "-map", "0:a:0?", "-map_metadata", "0",
                    "-vf", videoFilter, "-fps_mode", "passthrough",
                    "-c:v", "libx264", "-preset", "fast", "-crf", "19",
                    "-threads", "2", "-filter_threads", "2",
                    "-maxrate", peakBitrate.ToString(CultureInfo.InvariantCulture),
                    "-bufsize", (peakBitrate * 2).ToString(CultureInfo.InvariantCulture),
                    "-g", gopFrames.ToString(CultureInfo.InvariantCulture),
                    "-keyint_min", "1",
                    "-sc_threshold", "0",
                    "-force_key_frames", "source",
                    "-c:a", "copy", "-movflags", "+faststart",
                    "-avoid_negative_ts", "make_zero", "-f", "mp4", outputPath
                };
                await RunTranscodeAsync(arguments, input.DurationSeconds, onTranscodeProgress, cancellationToken);
            }
            else
            {
                var arguments = new[]
                {
                    "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                    "-i", sourcePath,
                    "-map", "0:v:0", "-map", "0:a:0?",
                    "-c", "copy",
                    "-movflags", "+faststart",
                    "-avoid_negative_ts", "make_zero",
                    "-f", "mp4",
                    outputPath
                };
                await RunProcessAsync(_ffmpegPath, arguments, cancellationToken);
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length <= 0)
                throw new InvalidOperationException("FFmpeg did not produce a playable MP4 file.");
            if (new FileInfo(outputPath).Length > maximumOutputBytes)
                throw new InvalidOperationException("The processed video exceeds the 10 GiB video size limit.");

            var output = await ProbeAsync(outputPath, cancellationToken);
            ValidateProbe(output, "processed video");
            if (!output.FormatNames.Contains("mp4", StringComparer.OrdinalIgnoreCase) &&
                !output.FormatNames.Contains("mov", StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("The processed video is not an MP4 file.");

            var video = output.Streams.First(stream =>
                string.Equals(stream.CodecType, "video", StringComparison.OrdinalIgnoreCase));
            GopMetadata? gop = null;
            try
            {
                gop = await ProbeGopAsync(outputPath, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not measure keyframe intervals for {FileName}", Path.GetFileName(outputPath));
            }
            return new CanonicalVideoMetadata(
                video.Width, video.Height, video.FramesPerSecond,
                EstimateVideoBitrate(output, video, outputPath), video.FieldOrder, gop);
        }
        finally
        {
            _processingGate.Release();
        }
    }

    public async Task<CanonicalVideoMetadata> InspectVideoAsync(
        string path, CancellationToken cancellationToken)
    {
        var probe = await ProbeAsync(path, cancellationToken);
        ValidateProbe(probe, "stored video");
        var video = probe.Streams.First(stream =>
            string.Equals(stream.CodecType, "video", StringComparison.OrdinalIgnoreCase));
        GopMetadata? gop = null;
        try { gop = await ProbeGopAsync(path, cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not measure keyframe intervals for {FileName}", Path.GetFileName(path));
        }
        return new CanonicalVideoMetadata(
            video.Width, video.Height, video.FramesPerSecond,
            EstimateVideoBitrate(probe, video, path), video.FieldOrder, gop);
    }

    public async Task EnsureThumbnailAsync(
        string videoPath, string thumbnailPath, CancellationToken cancellationToken)
    {
        if (File.Exists(thumbnailPath) && new FileInfo(thumbnailPath).Length > 0) return;
        await _processingGate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(thumbnailPath) && new FileInfo(thumbnailPath).Length > 0) return;
            var temporaryPath = thumbnailPath + "." + Guid.NewGuid().ToString("N") + ".jpg";
            try
            {
                // Input seeking decodes forward to the frame at 28 seconds.
                // Short videos have no such frame, so use their first frame.
                foreach (var second in new[] { "28", "0" })
                {
                    try
                    {
                        await RunProcessAsync(_ffmpegPath, new[]
                        {
                            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                            "-ss", second, "-i", videoPath,
                            "-map", "0:v:0", "-frames:v", "1", "-vf", "scale=256:-2",
                            "-threads", "1", "-q:v", "4", "-f", "image2", "-update", "1",
                            temporaryPath
                        }, cancellationToken);
                    }
                    catch (Exception ex) when (second == "28" && ex is not OperationCanceledException)
                    {
                        // Some FFmpeg builds fail when seeking beyond EOF.
                        try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
                    }
                    if (File.Exists(temporaryPath) && new FileInfo(temporaryPath).Length > 0)
                    {
                        File.Move(temporaryPath, thumbnailPath, overwrite: true);
                        return;
                    }
                }
                throw new InvalidOperationException("FFmpeg could not create a video thumbnail.");
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            }
        }
        finally { _processingGate.Release(); }
    }

    private async Task<ProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var output = await RunProcessAsync(
            _ffprobePath,
            new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path },
            cancellationToken,
            captureStandardOutput: true);
        try
        {
            using var document = JsonDocument.Parse(output.StandardOutput);
            var streams = new List<ProbeStream>();
            if (document.RootElement.TryGetProperty("streams", out var streamElements))
            {
                foreach (var stream in streamElements.EnumerateArray())
                {
                    streams.Add(new ProbeStream(
                        GetString(stream, "codec_type"),
                        GetString(stream, "codec_name"),
                        GetString(stream, "pix_fmt"),
                        GetPositiveInteger(stream, "width"),
                        GetPositiveInteger(stream, "height"),
                        GetFrameRate(stream),
                        GetBitrate(stream),
                        GetString(stream, "field_order")));
                }
            }

            var formatNames = Array.Empty<string>();
            if (document.RootElement.TryGetProperty("format", out var format))
            {
                formatNames = GetString(format, "format_name")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            var duration = document.RootElement.TryGetProperty("format", out var formatDuration) &&
                double.TryParse(GetString(formatDuration, "duration"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds) && seconds > 0
                    ? seconds : (double?)null;
            if (duration is null && document.RootElement.TryGetProperty("streams", out var durationStreams))
            {
                foreach (var stream in durationStreams.EnumerateArray())
                {
                    if (!string.Equals(GetString(stream, "codec_type"), "video", StringComparison.OrdinalIgnoreCase) ||
                        !double.TryParse(GetString(stream, "duration"), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out var streamSeconds) ||
                        !double.IsFinite(streamSeconds) || streamSeconds <= 0)
                        continue;
                    duration = streamSeconds;
                    break;
                }
            }
            return new ProbeResult(streams, formatNames, duration);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("ffprobe returned unreadable video metadata.", ex);
        }
    }

    private async Task<GopMetadata> ProbeGopAsync(string path, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _ffprobePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "-v", "error", "-select_streams", "v:0",
            "-show_entries", "packet=pts_time,flags", "-of", "compact=p=0:nk=0", path
        }) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("Could not start ffprobe for keyframe analysis.");
        try
        {
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            long packetIndex = 0;
            long keyframeCount = 0;
            long? previousKeyframeIndex = null;
            double? previousKeyframeTime = null;
            long? minFrames = null;
            long? maxFrames = null;
            double? minSeconds = null;
            double? maxSeconds = null;

            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                if (!line.Contains("flags=", StringComparison.Ordinal)) continue;
                if (line.Contains("flags=K", StringComparison.Ordinal))
                {
                    keyframeCount++;
                    var timeStart = line.IndexOf("pts_time=", StringComparison.Ordinal);
                    double keyframeTime = 0;
                    var hasTime = false;
                    if (timeStart >= 0)
                    {
                        timeStart += "pts_time=".Length;
                        var timeEnd = line.IndexOf('|', timeStart);
                        var timeText = timeEnd >= 0 ? line[timeStart..timeEnd] : line[timeStart..];
                        hasTime = double.TryParse(
                            timeText, NumberStyles.Float, CultureInfo.InvariantCulture,
                            out keyframeTime) && double.IsFinite(keyframeTime);
                    }
                    if (previousKeyframeIndex is long previousIndex)
                    {
                        var frames = packetIndex - previousIndex;
                        if (frames > 0)
                        {
                            minFrames = Math.Min(minFrames ?? frames, frames);
                            maxFrames = Math.Max(maxFrames ?? frames, frames);
                        }
                        if (hasTime && previousKeyframeTime is double previousTime && keyframeTime > previousTime)
                        {
                            var seconds = keyframeTime - previousTime;
                            minSeconds = Math.Min(minSeconds ?? seconds, seconds);
                            maxSeconds = Math.Max(maxSeconds ?? seconds, seconds);
                        }
                    }
                    previousKeyframeIndex = packetIndex;
                    previousKeyframeTime = hasTime ? keyframeTime : null;
                }
                packetIndex++;
            }

            await process.WaitForExitAsync(cancellationToken);
            var standardError = await standardErrorTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(standardError)
                    ? "ffprobe could not analyze video keyframes."
                    : CleanError(standardError));
            return new GopMetadata(keyframeCount, minFrames, maxFrames, minSeconds, maxSeconds);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
    }

    private static void ValidateProbe(ProbeResult probe, string description)
    {
        var video = probe.Streams.FirstOrDefault(stream =>
            string.Equals(stream.CodecType, "video", StringComparison.OrdinalIgnoreCase));
        if (video is null)
            throw new InvalidOperationException($"The {description} does not contain a video track.");
        if (video.Height is not (720 or 1080))
            throw new InvalidOperationException(
                $"The {description} must have 720 or 1080 vertical pixels.");
        if (!string.Equals(video.CodecName, "h264", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"The {description} uses {DisplayCodec(video.CodecName)} video. " +
                "Only H.264/AVC video can be processed without transcoding.");
        if (!string.IsNullOrWhiteSpace(video.PixelFormat) &&
            !SupportedPixelFormats.Contains(video.PixelFormat))
            throw new InvalidOperationException(
                $"The {description} uses the unsupported {video.PixelFormat} pixel format. " +
                "Use 8-bit 4:2:0 H.264 video (yuv420p).");

        var audio = probe.Streams.FirstOrDefault(stream =>
            string.Equals(stream.CodecType, "audio", StringComparison.OrdinalIgnoreCase));
        if (audio is not null && !string.Equals(audio.CodecName, "aac", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"The {description} uses {DisplayCodec(audio.CodecName)} audio. " +
                "Only AAC audio, or no audio, can be processed without transcoding.");
    }

    private static int? GetPositiveInteger(JsonElement stream, string name)
        => int.TryParse(GetString(stream, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : null;

    private static double? GetFrameRate(JsonElement stream)
    {
        foreach (var name in new[] { "avg_frame_rate", "r_frame_rate" })
        {
            var value = GetString(stream, name);
            var parts = value.Split('/');
            if (parts.Length != 2 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) ||
                denominator <= 0)
                continue;
            var framesPerSecond = numerator / denominator;
            if (double.IsFinite(framesPerSecond) && framesPerSecond > 0)
                return framesPerSecond;
        }
        return null;
    }

    private static long? GetBitrate(JsonElement stream)
    {
        var value = GetString(stream, "bit_rate");
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bitrate) && bitrate > 0
            ? bitrate
            : null;
    }

    private static long? EstimateVideoBitrate(ProbeResult probe, ProbeStream video, string path)
    {
        if (video.BitRate is > 0) return video.BitRate;
        if (probe.DurationSeconds is not > 0) return null;
        var totalBitsPerSecond = new FileInfo(path).Length * 8d / probe.DurationSeconds.Value;
        var audioBitsPerSecond = probe.Streams
            .Where(stream => string.Equals(stream.CodecType, "audio", StringComparison.OrdinalIgnoreCase))
            .Sum(stream => stream.BitRate ?? 0);
        var estimate = totalBitsPerSecond - audioBitsPerSecond;
        return double.IsFinite(estimate) && estimate > 0 && estimate < long.MaxValue
            ? (long)Math.Round(estimate)
            : null;
    }

    private async Task<ProcessOutput> RunProcessAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        bool captureStandardOutput = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"Could not start {Path.GetFileName(executable)}.");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(executable)} is not installed or could not be started on the ReVue-Remote server.", ex);
        }

        try
        {
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var standardOutput = await standardOutputTask;
            var standardError = await standardErrorTask;
            if (process.ExitCode != 0)
            {
                var detail = CleanError(standardError);
                _logger.LogWarning("Video processing command {Executable} failed: {Detail}", executable, detail);
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                    ? $"{Path.GetFileName(executable)} could not process the video."
                    : detail);
            }
            return new ProcessOutput(captureStandardOutput ? standardOutput : "", standardError);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }

    private async Task RunTranscodeAsync(
        IEnumerable<string> arguments,
        double? durationSeconds,
        Action<double>? onProgress,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Could not start FFmpeg transcoding.");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException("FFmpeg is not installed or could not be started on the ReVue-Remote server.", ex);
        }

        try
        {
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                if (!line.StartsWith("out_time_us=", StringComparison.Ordinal) ||
                    durationSeconds is not > 0 ||
                    !long.TryParse(line[12..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
                    continue;
                onProgress?.Invoke(Math.Clamp(microseconds / 1_000_000d / durationSeconds.Value * 100, 0, 99.9));
            }
            await process.WaitForExitAsync(cancellationToken);
            var standardError = await standardErrorTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(standardError)
                    ? "FFmpeg could not transcode the video."
                    : CleanError(standardError));
            onProgress?.Invoke(100);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }

    private static string GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property)
            ? property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : property.ToString()
            : "";

    private static string DisplayCodec(string? value)
        => string.IsNullOrWhiteSpace(value) ? "an unknown codec" : value.ToUpperInvariant();

    private static string CleanError(string value)
    {
        var text = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 600 ? text : text[..600] + "…";
    }

    private static string FormatGiB(long bytes)
        => $"{bytes / 1024d / 1024d / 1024d:0.0} GiB";

    private sealed record ProbeStream(
        string CodecType, string CodecName, string PixelFormat,
        int? Width, int? Height, double? FramesPerSecond, long? BitRate, string FieldOrder);
    private sealed record ProbeResult(
        IReadOnlyList<ProbeStream> Streams, IReadOnlyList<string> FormatNames, double? DurationSeconds);
    private sealed record ProcessOutput(string StandardOutput, string StandardError);
}

public sealed record CanonicalVideoMetadata(
    int? Width, int? Height, double? FramesPerSecond, long? VideoBitrateBitsPerSecond,
    string FieldOrder, GopMetadata? Gop);

public static class VideoTranscodeModes
{
    public const string Resolution = "resolution";
    public const string FrameRate = "frame_rate";
}

public sealed record GopMetadata(
    long KeyframeCount, long? MinFrames, long? MaxFrames,
    double? MinSeconds, double? MaxSeconds);
