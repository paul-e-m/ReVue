using System.ComponentModel;
using System.Diagnostics;
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

    public async Task CanonicalizeAsync(
        string sourcePath,
        string outputPath,
        long maximumOutputBytes,
        CancellationToken cancellationToken)
    {
        await _processingGate.WaitAsync(cancellationToken);
        try
        {
            var input = await ProbeAsync(sourcePath, cancellationToken);
            ValidateProbe(input, "uploaded video");

            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
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

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length <= 0)
                throw new InvalidOperationException("FFmpeg did not produce a playable MP4 file.");
            if (new FileInfo(outputPath).Length > maximumOutputBytes)
                throw new InvalidOperationException("The processed video exceeds the 10 GiB video size limit.");

            var output = await ProbeAsync(outputPath, cancellationToken);
            ValidateProbe(output, "processed video");
            if (!output.FormatNames.Contains("mp4", StringComparer.OrdinalIgnoreCase) &&
                !output.FormatNames.Contains("mov", StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("The processed video is not an MP4 file.");
        }
        finally
        {
            _processingGate.Release();
        }
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
                        GetString(stream, "pix_fmt")));
                }
            }

            var formatNames = Array.Empty<string>();
            if (document.RootElement.TryGetProperty("format", out var format))
            {
                formatNames = GetString(format, "format_name")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            return new ProbeResult(streams, formatNames);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("ffprobe returned unreadable video metadata.", ex);
        }
    }

    private static void ValidateProbe(ProbeResult probe, string description)
    {
        var video = probe.Streams.FirstOrDefault(stream =>
            string.Equals(stream.CodecType, "video", StringComparison.OrdinalIgnoreCase));
        if (video is null)
            throw new InvalidOperationException($"The {description} does not contain a video track.");
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

    private static string GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) ? property.GetString() ?? "" : "";

    private static string DisplayCodec(string? value)
        => string.IsNullOrWhiteSpace(value) ? "an unknown codec" : value.ToUpperInvariant();

    private static string CleanError(string value)
    {
        var text = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 600 ? text : text[..600] + "…";
    }

    private static string FormatGiB(long bytes)
        => $"{bytes / 1024d / 1024d / 1024d:0.0} GiB";

    private sealed record ProbeStream(string CodecType, string CodecName, string PixelFormat);
    private sealed record ProbeResult(IReadOnlyList<ProbeStream> Streams, IReadOnlyList<string> FormatNames);
    private sealed record ProcessOutput(string StandardOutput, string StandardError);
}
