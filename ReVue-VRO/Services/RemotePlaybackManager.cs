using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
    private readonly object _publisherLock = new();
    private readonly SemaphoreSlim _publisherSignal = new(0, 1);
    private readonly CancellationTokenSource _publisherStop = new();
    private readonly Task _publisherTask;
    private readonly string _operatorInstanceId = Guid.NewGuid().ToString("N");
    // UTC ticks give a restarted publisher a strictly newer generation than
    // any command produced by the previous process, including fast restarts.
    private readonly long _operatorGeneration = DateTime.UtcNow.Ticks;
    private RemotePlaybackIntent? _desiredIntent;
    private RemotePlaybackIntent? _outboxIntent;
    private CancellationTokenSource? _activePublishCancellation;
    private long _nextIntentVersion;
    private long _nextOperatorSequence;
    private double? _recordingSourceStartSeconds;

    // Replay and recording each provide a fresh position every second. This
    // is only the fallback that renews the lease if that source heartbeat is
    // delayed (for example, when a browser is temporarily throttled).
    private const int RemoteHeartbeatMilliseconds = 2_000;
    private const int RemoteRetryMaximumMilliseconds = 5_000;

    public RemotePlaybackManager()
    {
        _outboxIntent = LoadOutbox();
        if (_outboxIntent is not null && DateTimeOffset.UtcNow - _outboxIntent.QueuedAtUtc > TimeSpan.FromSeconds(5))
        {
            _outboxIntent = null;
            DeleteOutbox();
        }
        _desiredIntent = _outboxIntent;
        if (_desiredIntent is not null)
            _nextIntentVersion = _desiredIntent.Version;
        _publisherTask = Task.Run(PublishLoopAsync);
        SignalPublisher();
    }

    [GeneratedRegex("^[A-Z0-9]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionCodeRegex();

    public static string NormalizeSessionCode(string? value)
        => (value ?? "").Trim().ToUpperInvariant();

    public static bool IsValidSessionCode(string? value)
        => SessionCodeRegex().IsMatch(NormalizeSessionCode(value));

    public static bool IsRemoteMode(AppConfig cfg)
        => IsRemoteRecordedMode(cfg) || IsRemoteLiveMode(cfg);

    public static bool IsRemoteRecordedMode(AppConfig cfg)
        => string.Equals(cfg.VideoSourceMode, "RemoteRecorded", StringComparison.OrdinalIgnoreCase);

    public static bool IsRemoteLiveMode(AppConfig cfg)
        => string.Equals(cfg.VideoSourceMode, "RemoteLive", StringComparison.OrdinalIgnoreCase);

    public static Uri BuildLiveEventUri(AppConfig cfg, string fileName = "index.m3u8")
    {
        ValidateConnection(cfg.RemoteHostUrl, cfg.RemoteSessionCode);
        if (!Guid.TryParseExact(cfg.RemoteLiveEventId, "N", out _))
            throw new InvalidOperationException("No Remote Live recording is active.");
        return BuildUri(cfg.RemoteHostUrl,
            $"api/sessions/{Uri.EscapeDataString(NormalizeSessionCode(cfg.RemoteSessionCode))}/live/events/{cfg.RemoteLiveEventId}/{fileName}");
    }

    public static Uri BuildLivePreviewUri(AppConfig cfg, string fileName = "index.m3u8")
    {
        ValidateConnection(cfg.RemoteHostUrl, cfg.RemoteSessionCode);
        return BuildUri(cfg.RemoteHostUrl,
            $"api/sessions/{Uri.EscapeDataString(NormalizeSessionCode(cfg.RemoteSessionCode))}/live/preview/{fileName}");
    }

    public async Task WaitForLiveEventAsync(AppConfig cfg, CancellationToken cancellationToken)
    {
        var url = BuildLiveEventUri(cfg);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            try
            {
                using var response = await _httpClient.GetAsync(url, timeout.Token);
                if (response.IsSuccessStatusCode &&
                    (await response.Content.ReadAsStringAsync(timeout.Token)).Contains(".m4s", StringComparison.Ordinal))
                    return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(200, timeout.Token);
        }
    }

    public static Uri BuildVideoUri(AppConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.RemoteVideoId))
            throw new InvalidOperationException("Click 'Select Video' button");
        ValidateConnection(cfg.RemoteHostUrl, cfg.RemoteSessionCode);

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

    public async Task ValidateLiveSessionAsync(string hostUrl, string sessionCode, CancellationToken cancellationToken)
    {
        ValidateConnection(hostUrl, sessionCode);
        using var response = await _httpClient.GetAsync(BuildUri(hostUrl,
            $"api/sessions/{Uri.EscapeDataString(NormalizeSessionCode(sessionCode))}/validate"), cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("The Rink ID is not valid on this ReVue-Remote server.");
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("mode", out var mode) ||
            !string.Equals(mode.GetString(), "Live", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This Rink ID is set to Recorded. Change it to Live in /config first.");
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
            if ((int)response.StatusCode >= 500)
                throw new HttpRequestException($"ReVue-Remote returned HTTP {(int)response.StatusCode} while downloading the video.",
                    null, response.StatusCode);
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
                if (response.Content.Headers.ContentLength is long expectedLength && downloaded != expectedLength)
                    throw new IOException($"Video transfer ended after {downloaded} of {expectedLength} bytes.");
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

    public Task PublishRecordingStartedAsync(
        AppConfig cfg,
        double sourceStartSeconds,
        int? liveDelaySeconds = null,
        CancellationToken cancellationToken = default)
    {
        SetRecordingSourceStart(sourceStartSeconds);
        return QueuePublish(cfg, new RemotePlaybackCommand
        {
            VideoId = cfg.RemoteVideoId,
            PositionSeconds = Math.Max(0, sourceStartSeconds),
            TimelinePositionSeconds = 0,
            TimelineDurationSeconds = 175,
            IsPlaying = true,
            PlaybackRate = 1,
            LiveDelaySeconds = liveDelaySeconds,
            Mode = "recording"
        }, cancellationToken);
    }

    public Task PublishRecordingPreparingAsync(
        AppConfig cfg,
        double sourceStartSeconds,
        int? liveDelaySeconds = null,
        CancellationToken cancellationToken = default)
        => QueuePublish(cfg, new RemotePlaybackCommand
        {
            VideoId = cfg.RemoteVideoId,
            PositionSeconds = Math.Max(0, sourceStartSeconds),
            TimelinePositionSeconds = 0,
            TimelineDurationSeconds = 175,
            IsPlaying = false,
            PlaybackRate = 1,
            LiveDelaySeconds = liveDelaySeconds,
            Mode = "preparing"
        }, cancellationToken);

    public Task PublishRecordingStoppedAsync(
        AppConfig cfg,
        double durationSeconds,
        CancellationToken cancellationToken = default)
    {
        var sourceStart = GetRecordingSourceStart();
        return QueuePublish(cfg, new RemotePlaybackCommand
        {
            VideoId = cfg.RemoteVideoId,
            PositionSeconds = Math.Max(0, sourceStart + Math.Max(0, durationSeconds)),
            TimelinePositionSeconds = Math.Max(0, durationSeconds),
            TimelineDurationSeconds = Math.Max(0, durationSeconds),
            FramesPerSecond = cfg.SourceFps is > 0 and <= 240 ? cfg.SourceFps : 60,
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
        return QueuePublish(cfg, new RemotePlaybackCommand
        {
            VideoId = cfg.RemoteVideoId,
            PositionSeconds = Math.Max(0, sourceStart + Math.Max(0, local.PositionSeconds)),
            TimelinePositionSeconds = Math.Max(0, local.TimelinePositionSeconds),
            TimelineDurationSeconds = Math.Max(0, local.TimelineDurationSeconds),
            FramesPerSecond = cfg.SourceFps is > 0 and <= 240 ? cfg.SourceFps : 60,
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
        => QueuePublish(cfg, new RemotePlaybackCommand
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

    public void StopPublishing()
    {
        lock (_publisherLock)
        {
            _desiredIntent = null;
            _outboxIntent = null;
            DeleteOutbox();
            _activePublishCancellation?.Cancel();
        }
        SignalPublisher();
    }

    public async Task PublishNoSignalImmediatelyAsync(AppConfig cfg, CancellationToken cancellationToken = default)
    {
        if (!IsRemoteMode(cfg) || !IsValidSessionCode(cfg.RemoteSessionCode)) return;
        ValidateConnection(cfg.RemoteHostUrl, cfg.RemoteSessionCode);
        await SendIntentAsync(new RemotePlaybackIntent
        {
            HostUrl = cfg.RemoteHostUrl,
            SessionCode = NormalizeSessionCode(cfg.RemoteSessionCode),
            Command = new RemotePlaybackCommand
            {
                SourceType = IsRemoteLiveMode(cfg) ? "Live" : "Recorded",
                VideoId = "",
                PlaybackRate = 1,
                Mode = "ready"
            },
            QueuedAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    private Task QueuePublish(
        AppConfig cfg,
        RemotePlaybackCommand command,
        CancellationToken cancellationToken)
    {
        if (!IsRemoteMode(cfg)) return Task.CompletedTask;
        ValidateConnection(cfg.RemoteHostUrl, cfg.RemoteSessionCode);
        if (IsRemoteLiveMode(cfg))
        {
            command.SourceType = "Live";
            command.VideoId = string.Equals(command.Mode, "ready", StringComparison.OrdinalIgnoreCase)
                ? "" : cfg.RemoteLiveEventId;
            command.FramesPerSecond = 60000d / 1001d;
        }

        lock (_publisherLock)
        {
            var transportBoundary = IsTransportBoundary(_desiredIntent?.Command, command);
            var intent = new RemotePlaybackIntent
            {
                Version = ++_nextIntentVersion,
                HostUrl = cfg.RemoteHostUrl,
                SessionCode = NormalizeSessionCode(cfg.RemoteSessionCode),
                Command = CloneCommand(command),
                QueuedAtUtc = DateTimeOffset.UtcNow
            };
            _desiredIntent = intent;
            _outboxIntent = intent;
            SaveOutbox(intent);
            // Preempt only a transport boundary. Cancelling routine position
            // heartbeats on a slow connection could otherwise keep replacing
            // the active request before it has a chance to complete.
            if (transportBoundary)
                _activePublishCancellation?.Cancel();
        }
        SignalPublisher();
        return Task.CompletedTask;
    }

    private async Task PublishLoopAsync()
    {
        var retryDelayMilliseconds = 0;
        while (!_publisherStop.IsCancellationRequested)
        {
            RemotePlaybackIntent? intent;
            lock (_publisherLock) intent = _desiredIntent;
            if (intent is null)
            {
                await WaitForPublisherSignalAsync(Timeout.Infinite, _publisherStop.Token);
                continue;
            }

            try
            {
                using var activePublishCancellation = CancellationTokenSource.CreateLinkedTokenSource(_publisherStop.Token);
                lock (_publisherLock)
                {
                    if (_desiredIntent?.Version != intent.Version)
                        continue;
                    _activePublishCancellation = activePublishCancellation;
                }
                try
                {
                    await SendIntentAsync(intent, activePublishCancellation.Token);
                }
                finally
                {
                    lock (_publisherLock)
                    {
                        if (ReferenceEquals(_activePublishCancellation, activePublishCancellation))
                            _activePublishCancellation = null;
                    }
                }
                retryDelayMilliseconds = 0;
                lock (_publisherLock)
                {
                    if (_outboxIntent?.Version == intent.Version)
                    {
                        _outboxIntent = null;
                        DeleteOutbox();
                    }
                }
                await WaitForPublisherSignalAsync(RemoteHeartbeatMilliseconds, _publisherStop.Token);
            }
            catch (OperationCanceledException) when (_publisherStop.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (IsSuperseded(intent))
            {
                retryDelayMilliseconds = 0;
            }
            catch
            {
                lock (_publisherLock)
                {
                    if (_desiredIntent?.Version == intent.Version)
                    {
                        _outboxIntent = intent;
                        SaveOutbox(intent);
                    }
                }
                retryDelayMilliseconds = retryDelayMilliseconds == 0
                    ? 250
                    : Math.Min(retryDelayMilliseconds * 2, RemoteRetryMaximumMilliseconds);
                await WaitForPublisherSignalAsync(retryDelayMilliseconds, _publisherStop.Token);
            }
        }
    }

    private bool IsSuperseded(RemotePlaybackIntent intent)
    {
        lock (_publisherLock)
            return _desiredIntent?.Version != intent.Version;
    }

    private static bool IsTransportBoundary(RemotePlaybackCommand? previous, RemotePlaybackCommand next)
    {
        if (previous is null) return true;
        return !string.Equals(previous.VideoId, next.VideoId, StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(previous.Mode, next.Mode, StringComparison.OrdinalIgnoreCase) ||
               previous.IsPlaying != next.IsPlaying ||
               Math.Abs(previous.PlaybackRate - next.PlaybackRate) > 0.001 ||
               previous.PlaybackDiscontinuity != next.PlaybackDiscontinuity;
    }

    private async Task SendIntentAsync(RemotePlaybackIntent intent, CancellationToken cancellationToken)
    {
        var command = CloneCommand(intent.Command);
        command.OperatorInstanceId = _operatorInstanceId;
        command.OperatorGeneration = _operatorGeneration;
        command.OperatorSequence = Interlocked.Increment(ref _nextOperatorSequence);

        using var request = CreateRequest(
            HttpMethod.Post,
            intent.HostUrl,
            $"api/sessions/{Uri.EscapeDataString(intent.SessionCode)}/playback");
        request.Content = JsonContent.Create(command);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
        using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
        await EnsureSuccessAsync(response, timeoutCts.Token);
    }

    private async Task WaitForPublisherSignalAsync(int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        if (timeoutMilliseconds == Timeout.Infinite)
        {
            await _publisherSignal.WaitAsync(cancellationToken);
            return;
        }

        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var signal = _publisherSignal.WaitAsync(waitCts.Token);
        var delay = Task.Delay(timeoutMilliseconds, cancellationToken);
        if (await Task.WhenAny(signal, delay) == signal)
        {
            await signal;
            return;
        }
        waitCts.Cancel();
        try { await signal; } catch (OperationCanceledException) { }
    }

    private void SignalPublisher()
    {
        // A wake-up is only a coalesced notification: one pending signal is
        // enough because the publisher always reads the latest desired state.
        try { _publisherSignal.Release(); }
        catch (SemaphoreFullException) { }
    }

    private static RemotePlaybackCommand CloneCommand(RemotePlaybackCommand source) => new()
    {
        SourceType = source.SourceType,
        VideoId = source.VideoId,
        PositionSeconds = source.PositionSeconds,
        TimelinePositionSeconds = source.TimelinePositionSeconds,
        TimelineDurationSeconds = source.TimelineDurationSeconds,
        FramesPerSecond = source.FramesPerSecond,
        LiveDelaySeconds = source.LiveDelaySeconds,
        IsPlaying = source.IsPlaying,
        PlaybackRate = source.PlaybackRate,
        PlaybackDiscontinuity = source.PlaybackDiscontinuity,
        Mode = source.Mode,
        ProgramStartSeconds = source.ProgramStartSeconds,
        HalfwaySeconds = source.HalfwaySeconds,
        OpenClipStartSeconds = source.OpenClipStartSeconds,
        ZoomScale = source.ZoomScale,
        ZoomOffsetX = source.ZoomOffsetX,
        ZoomOffsetY = source.ZoomOffsetY,
        Clips = source.Clips.Select(clip => new RemoteTimelineClip
        {
            Index = clip.Index,
            StartSeconds = clip.StartSeconds,
            EndSeconds = clip.EndSeconds
        }).ToList()
    };

    private static RemotePlaybackIntent? LoadOutbox()
    {
        try
        {
            if (!File.Exists(AppPaths.LocalVroRemotePlaybackOutboxPath)) return null;
            return JsonSerializer.Deserialize<RemotePlaybackIntent>(File.ReadAllText(AppPaths.LocalVroRemotePlaybackOutboxPath));
        }
        catch
        {
            return null;
        }
    }

    private static void SaveOutbox(RemotePlaybackIntent intent)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.LocalVroRemotePlaybackOutboxPath)!);
            var path = AppPaths.LocalVroRemotePlaybackOutboxPath;
            var temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(intent));
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
        }
    }

    private static void DeleteOutbox()
    {
        try { if (File.Exists(AppPaths.LocalVroRemotePlaybackOutboxPath)) File.Delete(AppPaths.LocalVroRemotePlaybackOutboxPath); }
        catch { }
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
        throw new HttpRequestException(
            string.IsNullOrWhiteSpace(detail)
                ? $"ReVue-Remote returned HTTP {(int)response.StatusCode}."
                : detail,
            null,
            response.StatusCode);
    }

    public void Dispose()
    {
        _publisherStop.Cancel();
        try { _publisherTask.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _publisherStop.Dispose();
        _publisherSignal.Dispose();
        _httpClient.Dispose();
    }

    private sealed class RemotePlaybackIntent
    {
        public long Version { get; set; }
        public string HostUrl { get; set; } = "";
        public string SessionCode { get; set; } = "";
        public RemotePlaybackCommand Command { get; set; } = new();
        public DateTimeOffset QueuedAtUtc { get; set; }
    }

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
