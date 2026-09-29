using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using ReVueVRO.Models;

namespace ReVueVRO.Services;

public sealed partial class RemotePlaybackManager : IDisposable
{
    private readonly HttpClient _httpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(15)
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly object _stateLock = new();
    private double? _recordingSourceStartSeconds;

    [GeneratedRegex("^[A-Z0-9]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionCodeRegex();

    public static string NormalizeSessionCode(string? value)
        => (value ?? "").Trim().ToUpperInvariant();

    public static bool IsValidSessionCode(string? value)
        => SessionCodeRegex().IsMatch(NormalizeSessionCode(value));

    public static bool IsRemoteMode(AppConfig cfg)
        => string.Equals(cfg.VideoSourceMode, "Remote", StringComparison.OrdinalIgnoreCase);

    public static Uri BuildVideoUri(AppConfig cfg)
    {
        ValidateConnection(cfg.RemoteHostUrl, cfg.RemoteSessionCode);
        if (string.IsNullOrWhiteSpace(cfg.RemoteVideoId))
            throw new InvalidOperationException("Select a downloaded video in the ReVue VRO main window.");

        return BuildUri(
            cfg.RemoteHostUrl,
            $"api/sessions/{Uri.EscapeDataString(NormalizeSessionCode(cfg.RemoteSessionCode))}/videos/{Uri.EscapeDataString(cfg.RemoteVideoId.Trim())}/content");
    }

    public static string BuildLocalVideoPath(AppConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.RemoteSessionCode) || string.IsNullOrWhiteSpace(cfg.RemoteVideoId))
            throw new InvalidOperationException("Select a downloaded Remote video in the ReVue VRO main window.");
        var path = AppPaths.GetRemoteVideoCachePath(NormalizeSessionCode(cfg.RemoteSessionCode), cfg.RemoteVideoId.Trim());
        if (!File.Exists(path)) throw new InvalidOperationException("The selected Remote video has not finished downloading.");
        return path;
    }

    public async Task<IReadOnlyList<RemoteVideoDescriptor>> ListVideosAsync(
        string hostUrl,
        string sessionCode,
        CancellationToken cancellationToken)
    {
        ValidateConnection(hostUrl, sessionCode);
        using var request = CreateRequest(
            HttpMethod.Get,
            hostUrl,
            $"api/sessions/{Uri.EscapeDataString(NormalizeSessionCode(sessionCode))}/videos");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<RemoteVideoDescriptor>>(cancellationToken: cancellationToken)
            ?? [];
    }

    public async Task ValidateSessionAsync(string hostUrl, string sessionCode, CancellationToken cancellationToken)
    {
        ValidateConnection(hostUrl, sessionCode);
        using var response = await _httpClient.GetAsync(BuildUri(hostUrl,
            $"api/sessions/{Uri.EscapeDataString(NormalizeSessionCode(sessionCode))}/validate"), cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("The Rink ID is not valid on this cloud service.");
    }

    public async Task DownloadVideoAsync(string hostUrl, string sessionCode, RemoteVideoDescriptor video,
        string destinationPath, IProgress<(long Bytes, long Total)> progress, CancellationToken cancellationToken)
    {
        ValidateConnection(hostUrl, sessionCode);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var tempPath = destinationPath + ".downloading";
        try
        {
            using var response = await _httpClient.GetAsync(BuildUri(hostUrl,
                $"api/sessions/{Uri.EscapeDataString(NormalizeSessionCode(sessionCode))}/videos/{Uri.EscapeDataString(video.Id)}/content"),
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            var total = response.Content.Headers.ContentLength ?? video.SizeBytes;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[1024 * 1024];
            long downloaded = 0;
            await using (var output = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (true)
                {
                    var count = await input.ReadAsync(buffer, cancellationToken);
                    if (count == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    downloaded += count;
                    progress.Report((downloaded, total));
                }
                await output.FlushAsync(cancellationToken);
            }

            // Windows will not move a file that is still held by a FileShare.None
            // stream. Dispose the completed download before publishing it to the
            // cache so the library can immediately mark it downloaded/selectable.
            File.Move(tempPath, destinationPath, true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    public async Task<IReadOnlyList<RemoteVideoDescriptor>> UploadVideosAsync(
        string hostUrl,
        string sessionCode,
        IReadOnlyList<IFormFile> files,
        CancellationToken cancellationToken)
    {
        ValidateConnection(hostUrl, sessionCode);
        if (files.Count == 0)
            throw new InvalidOperationException("Select at least one MP4 video to upload.");

        using var multipart = new MultipartFormDataContent();
        foreach (var file in files)
        {
            if (!string.Equals(Path.GetExtension(file.FileName), ".mp4", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{file.FileName} is not an MP4 file.");

            var streamContent = new StreamContent(file.OpenReadStream());
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            multipart.Add(streamContent, "files", Path.GetFileName(file.FileName));
        }

        using var request = CreateRequest(
            HttpMethod.Post,
            hostUrl,
            $"api/sessions/{Uri.EscapeDataString(NormalizeSessionCode(sessionCode))}/videos");
        request.Content = multipart;

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<RemoteVideoDescriptor>>(cancellationToken: cancellationToken)
            ?? [];
    }

    public async Task<IReadOnlyList<UploadedLocalVideoResult>> UploadLocalVideosAsync(
        string hostUrl,
        string sessionCode,
        IReadOnlyList<string> localPaths,
        Action<long, int, string>? reportProgress,
        CancellationToken cancellationToken)
    {
        ValidateConnection(hostUrl, sessionCode);
        if (localPaths.Count == 0) return [];

        var totalSent = 0L;
        var completed = 0;
        var results = new List<UploadedLocalVideoResult>();

        foreach (var localPath in localPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = new FileInfo(localPath);
            if (!file.Exists || file.Length <= 0)
                throw new InvalidOperationException($"{file.Name} is missing or empty.");
            if (!string.Equals(file.Extension, ".mp4", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{file.Name} is not an MP4 file.");

            var beforeThisFile = totalSent;
            reportProgress?.Invoke(beforeThisFile, completed, file.Name);

            using var multipart = new MultipartFormDataContent();
            await using var input = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var progressStream = new ProgressReadStream(input, bytesRead =>
                reportProgress?.Invoke(beforeThisFile + bytesRead, completed, file.Name));
            using var streamContent = new StreamContent(progressStream);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            multipart.Add(streamContent, "files", file.Name);

            using var request = CreateRequest(
                HttpMethod.Post,
                hostUrl,
                $"api/sessions/{Uri.EscapeDataString(NormalizeSessionCode(sessionCode))}/videos");
            request.Content = multipart;
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            var videos = await response.Content.ReadFromJsonAsync<List<RemoteVideoDescriptor>>(
                cancellationToken: cancellationToken) ?? [];
            var uploaded = videos
                .Where(video => string.Equals(video.FileName, file.Name, StringComparison.OrdinalIgnoreCase) &&
                                video.SizeBytes == file.Length)
                .OrderByDescending(video => video.UploadedAtUtc)
                .FirstOrDefault()
                ?? throw new InvalidOperationException($"The Remote host did not return a record for {file.Name}.");

            totalSent += file.Length;
            completed++;
            reportProgress?.Invoke(totalSent, completed, file.Name);
            results.Add(new UploadedLocalVideoResult { LocalPath = file.FullName, RemoteVideo = uploaded });
        }

        return results;
    }

    public void SetRecordingSourceStart(double sourceStartSeconds)
    {
        lock (_stateLock)
        {
            _recordingSourceStartSeconds = Math.Max(0, sourceStartSeconds);
        }
    }

    public void ClearRecordingSourceStart()
    {
        lock (_stateLock)
        {
            _recordingSourceStartSeconds = null;
        }
    }

    public async Task PublishRecordingStartedAsync(
        AppConfig cfg,
        double sourceStartSeconds,
        CancellationToken cancellationToken = default)
    {
        SetRecordingSourceStart(sourceStartSeconds);
        await PublishAsync(cfg, new RemotePlaybackCommand
        {
            VideoId = cfg.RemoteVideoId,
            PositionSeconds = Math.Max(0, sourceStartSeconds),
            TimelinePositionSeconds = 0,
            TimelineDurationSeconds = 175,
            IsPlaying = true,
            PlaybackRate = 1,
            Mode = "recording"
        }, cancellationToken);
    }

    public async Task PublishRecordingStoppedAsync(
        AppConfig cfg,
        double durationSeconds,
        CancellationToken cancellationToken = default)
    {
        var sourceStart = GetRecordingSourceStart();
        await PublishAsync(cfg, new RemotePlaybackCommand
        {
            VideoId = cfg.RemoteVideoId,
            PositionSeconds = Math.Max(0, sourceStart + Math.Max(0, durationSeconds)),
            TimelinePositionSeconds = Math.Max(0, durationSeconds),
            TimelineDurationSeconds = Math.Max(0, durationSeconds),
            IsPlaying = false,
            PlaybackRate = 1,
            Mode = "replay"
        }, cancellationToken);
    }

    public Task PublishReplayStateAsync(
        AppConfig cfg,
        LocalRemotePlaybackCommand local,
        CancellationToken cancellationToken = default)
    {
        var sourceStart = GetRecordingSourceStart();
        return PublishAsync(cfg, new RemotePlaybackCommand
        {
            VideoId = cfg.RemoteVideoId,
            PositionSeconds = Math.Max(0, sourceStart + Math.Max(0, local.PositionSeconds)),
            TimelinePositionSeconds = Math.Max(0, local.TimelinePositionSeconds),
            TimelineDurationSeconds = Math.Max(0, local.TimelineDurationSeconds),
            IsPlaying = local.IsPlaying && !local.IsReverse,
            PlaybackRate = Math.Clamp(local.PlaybackRate, 0.05, 4),
            PlaybackDiscontinuity = Math.Max(0, local.PlaybackDiscontinuity),
            Mode = local.IsReverse
                ? "reverse-unavailable"
                : string.Equals(local.Mode, "recording", StringComparison.OrdinalIgnoreCase)
                    ? "recording"
                    : "replay",
            ProgramStartSeconds = local.ProgramStartSeconds,
            HalfwaySeconds = local.HalfwaySeconds,
            OpenClipStartSeconds = local.OpenClipStartSeconds,
            ZoomScale = Math.Clamp(local.ZoomScale, 1, 2.5),
            ZoomOffsetX = Math.Clamp(local.ZoomOffsetX, -2, 0),
            ZoomOffsetY = Math.Clamp(local.ZoomOffsetY, -2, 0),
            Clips = local.Clips ?? []
        }, cancellationToken);
    }

    public Task PublishIdleAsync(AppConfig cfg, CancellationToken cancellationToken = default)
        => PublishAsync(cfg, new RemotePlaybackCommand
        {
            VideoId = cfg.RemoteVideoId,
            PositionSeconds = 0,
            TimelinePositionSeconds = 0,
            TimelineDurationSeconds = 0,
            IsPlaying = false,
            PlaybackRate = 1,
            Mode = "ready"
        }, cancellationToken);

    private double GetRecordingSourceStart()
    {
        lock (_stateLock)
        {
            return _recordingSourceStartSeconds ?? 0;
        }
    }

    private async Task PublishAsync(
        AppConfig cfg,
        RemotePlaybackCommand command,
        CancellationToken cancellationToken)
    {
        if (!IsRemoteMode(cfg)) return;
        ValidateConnection(cfg.RemoteHostUrl, cfg.RemoteSessionCode);

        using var request = CreateRequest(
            HttpMethod.Post,
            cfg.RemoteHostUrl,
            $"api/sessions/{Uri.EscapeDataString(NormalizeSessionCode(cfg.RemoteSessionCode))}/playback");
        request.Content = JsonContent.Create(command);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
        using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
        await EnsureSuccessAsync(response, timeoutCts.Token);
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string hostUrl,
        string relativePath)
    {
        return new HttpRequestMessage(method, BuildUri(hostUrl, relativePath));
    }

    private static Uri BuildUri(string hostUrl, string relativePath)
    {
        if (!Uri.TryCreate(hostUrl?.Trim(), UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Remote Host URL must be a valid HTTP or HTTPS URL.");
        }

        var normalizedBase = new Uri(baseUri.ToString().TrimEnd('/') + "/");
        return new Uri(normalizedBase, relativePath.TrimStart('/'));
    }

    private static void ValidateConnection(string hostUrl, string sessionCode)
    {
        _ = BuildUri(hostUrl, "api/health");
        if (!IsValidSessionCode(sessionCode))
            throw new InvalidOperationException("Rink ID must contain exactly six letters or digits, for example AB1234.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        if (detail.Length > 500) detail = detail[..500];
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(detail)
                ? $"ReVue-Remote returned HTTP {(int)response.StatusCode}."
                : detail);
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed class ProgressReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly Action<long> _progress;
        private long _bytesRead;

        public ProgressReadStream(Stream inner, Action<long> progress)
        {
            _inner = inner;
            _progress = progress;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            Report(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken);
            Report(read);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
            Report(read);
            return read;
        }

        private void Report(int read)
        {
            if (read <= 0) return;
            _bytesRead += read;
            _progress(_bytesRead);
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}
