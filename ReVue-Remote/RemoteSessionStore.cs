using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace ReVueRemote;

public sealed partial class RemoteSessionStore
{
    private readonly string _storageRoot;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _uploadLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _processingLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<PlaybackState>>> _subscribers = new(StringComparer.Ordinal);
    private readonly VideoCanonicalizer _canonicalizer;

    public const long MaximumUploadChunkBytes = 8L * 1024 * 1024;
    public const long MaximumVideoSizeBytes = 10L * 1024 * 1024 * 1024;
    public static readonly TimeSpan OperatorLeaseDuration = TimeSpan.FromSeconds(10);

    public RemoteSessionStore(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        VideoCanonicalizer canonicalizer)
    {
        _canonicalizer = canonicalizer;
        var configuredRoot = configuration["ReVueRemote:StorageRoot"]?.Trim();
        _storageRoot = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(environment.ContentRootPath, "data", "sessions")
            : Path.GetFullPath(configuredRoot, environment.ContentRootPath);
        Directory.CreateDirectory(_storageRoot);
    }

    [GeneratedRegex("^[A-Z0-9]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionCodeRegex();

    public static string NormalizeSessionCode(string? value)
        => (value ?? "").Trim().ToUpperInvariant();

    public static bool IsValidSessionCode(string? value)
        => SessionCodeRegex().IsMatch(NormalizeSessionCode(value));

    public async Task<IReadOnlyList<RemoteVideoDescriptor>> ListVideosAsync(
        string sessionCode,
        CancellationToken cancellationToken = default)
    {
        sessionCode = RequireSessionCode(sessionCode);
        var gate = GetSessionLock(sessionCode);
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadVideosNoLockAsync(sessionCode, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<RemoteVideoDescriptor>> SaveUploadsAsync(
        string sessionCode,
        IFormFileCollection files,
        CancellationToken cancellationToken)
    {
        sessionCode = RequireSessionCode(sessionCode);
        if (files.Count == 0)
            throw new InvalidOperationException("Select at least one supported video to upload.");

        foreach (var file in files)
        {
            var originalName = NormalizeUploadFileName(file.FileName ?? "video.mp4");
            if (file.Length <= 0)
                throw new InvalidOperationException($"{originalName} is empty.");
            if (file.Length > MaximumVideoSizeBytes)
                throw new InvalidOperationException($"{originalName} exceeds the 10 GiB video size limit.");

            var id = Guid.NewGuid().ToString("N");
            var uploadsDirectory = GetUploadsDirectory(sessionCode);
            var videosDirectory = Path.Combine(GetSessionDirectory(sessionCode), "videos");
            Directory.CreateDirectory(uploadsDirectory);
            Directory.CreateDirectory(videosDirectory);
            VideoCanonicalizer.EnsureWorkingSpace(uploadsDirectory, file.Length);

            var sourcePath = GetPreparedSourcePath(sessionCode, id, originalName);
            var processingPath = Path.Combine(videosDirectory, id + ".mp4.processing");
            var finalPath = Path.Combine(videosDirectory, id + ".mp4");
            var processingGate = GetProcessingLock(sessionCode);
            await processingGate.WaitAsync(cancellationToken);
            try
            {
                try
                {
                    await using (var output = new FileStream(
                        sourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        await file.CopyToAsync(output, cancellationToken);
                        await output.FlushAsync(cancellationToken);
                    }

                    await _canonicalizer.CanonicalizeAsync(
                        sourcePath, processingPath, MaximumVideoSizeBytes, cancellationToken);
                    File.Move(processingPath, finalPath, overwrite: true);

                    var video = new RemoteVideoDescriptor
                    {
                        Id = id,
                        FileName = VideoCanonicalizer.GetCanonicalFileName(originalName),
                        SizeBytes = new FileInfo(finalPath).Length,
                        UploadedAtUtc = DateTimeOffset.UtcNow
                    };
                    var sessionGate = GetSessionLock(sessionCode);
                    await sessionGate.WaitAsync(cancellationToken);
                    try
                    {
                        var videos = (await ReadVideosNoLockAsync(sessionCode, cancellationToken)).ToList();
                        videos.Add(video);
                        await WriteJsonAtomicAsync(GetVideosMetadataPath(sessionCode), videos, cancellationToken);
                    }
                    catch
                    {
                        try { if (File.Exists(finalPath)) File.Delete(finalPath); } catch { }
                        throw;
                    }
                    finally { sessionGate.Release(); }
                }
                finally
                {
                    TryDeleteFile(sourcePath);
                    TryDeleteFile(processingPath);
                }
            }
            finally { processingGate.Release(); }
        }

        return await ListVideosAsync(sessionCode, cancellationToken);
    }

    public async Task<RemoteUploadStatus> BeginUploadAsync(
        string sessionCode,
        string? requestedFileName,
        long sizeBytes,
        long lastModifiedUnixMs,
        CancellationToken cancellationToken = default)
    {
        sessionCode = RequireSessionCode(sessionCode);
        var fileName = NormalizeUploadFileName(requestedFileName);
        if (sizeBytes <= 0) throw new InvalidOperationException("The selected video is empty.");
        if (sizeBytes > MaximumVideoSizeBytes)
            throw new InvalidOperationException("The selected video exceeds the 10 GiB video size limit.");

        var gate = GetSessionLock(sessionCode);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var uploadsDirectory = GetUploadsDirectory(sessionCode);
            Directory.CreateDirectory(uploadsDirectory);
            await DeleteExpiredUploadsNoLockAsync(sessionCode, cancellationToken);

            foreach (var manifestPath in Directory.EnumerateFiles(uploadsDirectory, "*.json"))
            {
                var existing = await ReadUploadManifestAsync(manifestPath, cancellationToken);
                if (existing is null ||
                    !string.Equals(existing.FileName, fileName, StringComparison.Ordinal) ||
                    existing.SizeBytes != sizeBytes ||
                    existing.LastModifiedUnixMs != lastModifiedUnixMs)
                    continue;
                if (string.Equals(existing.Stage, RemoteUploadStages.Failed, StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteFile(manifestPath);
                    DeleteUploadMediaFiles(sessionCode, existing.UploadId);
                    continue;
                }
                if (string.Equals(existing.Stage, RemoteUploadStages.Completed, StringComparison.OrdinalIgnoreCase) &&
                    !File.Exists(Path.Combine(GetSessionDirectory(sessionCode), "videos", existing.UploadId + ".mp4")))
                {
                    TryDeleteFile(manifestPath);
                    DeleteUploadMediaFiles(sessionCode, existing.UploadId);
                    continue;
                }
                return GetUploadStatusNoLock(sessionCode, existing);
            }

            var uploadId = Guid.NewGuid().ToString("N");
            var manifest = new RemoteUploadManifest
            {
                UploadId = uploadId,
                FileName = fileName,
                SizeBytes = sizeBytes,
                LastModifiedUnixMs = lastModifiedUnixMs,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await WriteJsonAtomicAsync(GetUploadManifestPath(sessionCode, uploadId), manifest, cancellationToken);
            return GetUploadStatusNoLock(sessionCode, manifest);
        }
        finally { gate.Release(); }
    }

    public async Task<RemoteUploadStatus> GetUploadStatusAsync(
        string sessionCode,
        string uploadId,
        CancellationToken cancellationToken = default)
    {
        sessionCode = RequireSessionCode(sessionCode);
        uploadId = RequireUploadId(uploadId);
        var gate = GetUploadLock(sessionCode, uploadId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var manifest = await RequireUploadManifestAsync(sessionCode, uploadId, cancellationToken);
            return GetUploadStatusNoLock(sessionCode, manifest);
        }
        finally { gate.Release(); }
    }

    public async Task<RemoteUploadStatus> AppendUploadChunkAsync(
        string sessionCode,
        string uploadId,
        long offset,
        long contentLength,
        Stream input,
        CancellationToken cancellationToken = default)
    {
        sessionCode = RequireSessionCode(sessionCode);
        uploadId = RequireUploadId(uploadId);
        if (offset < 0 || offset % MaximumUploadChunkBytes != 0)
            throw new InvalidOperationException("The upload offset is invalid.");
        if (contentLength <= 0 || contentLength > MaximumUploadChunkBytes)
            throw new InvalidOperationException($"Upload chunks must contain between 1 byte and {MaximumUploadChunkBytes} bytes.");

        var chunkIndex = checked((int)(offset / MaximumUploadChunkBytes));
        var gate = GetUploadLock(sessionCode, uploadId, chunkIndex);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var manifest = await RequireUploadManifestAsync(sessionCode, uploadId, cancellationToken);
            if (!string.Equals(manifest.Stage, RemoteUploadStages.Uploading, StringComparison.OrdinalIgnoreCase))
                return GetUploadStatusNoLock(sessionCode, manifest);
            var expectedLength = Math.Min(MaximumUploadChunkBytes, manifest.SizeBytes - offset);
            if (expectedLength <= 0 || contentLength != expectedLength)
                throw new InvalidOperationException("The upload chunk length does not match the declared video size.");
            var chunkPath = GetUploadChunkPath(sessionCode, uploadId, chunkIndex);
            if (File.Exists(chunkPath) && new FileInfo(chunkPath).Length == expectedLength)
                return GetUploadStatusNoLock(sessionCode, manifest);

            var tempPath = GetUploadChunkTempPath(sessionCode, uploadId, chunkIndex);
            try
            {
                await using var output = new FileStream(
                    tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[128 * 1024];
                var remaining = contentLength;
                while (remaining > 0)
                {
                    var read = await input.ReadAsync(
                        buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                    if (read == 0) throw new EndOfStreamException("The upload chunk ended before all bytes were received.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    remaining -= read;
                }
                await output.FlushAsync(cancellationToken);
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }

            // Cancellation can race a chunk already being received. Do not
            // resurrect a cancelled upload after its manifest is removed.
            var manifestPath = GetUploadManifestPath(sessionCode, uploadId);
            if (!File.Exists(manifestPath))
            {
                try { File.Delete(tempPath); } catch { }
                throw new FileNotFoundException("The resumable upload was cancelled.");
            }
            File.Move(tempPath, chunkPath, overwrite: true);
            File.SetLastWriteTimeUtc(manifestPath, DateTime.UtcNow);
            return GetUploadStatusNoLock(sessionCode, manifest);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RemoteUploadStatus> PrepareUploadForProcessingAsync(
        string sessionCode,
        string uploadId,
        CancellationToken cancellationToken = default)
    {
        sessionCode = RequireSessionCode(sessionCode);
        uploadId = RequireUploadId(uploadId);
        var uploadGate = GetUploadLock(sessionCode, uploadId);
        await uploadGate.WaitAsync(cancellationToken);
        try
        {
            var manifest = await ReadUploadManifestAsync(
                GetUploadManifestPath(sessionCode, uploadId), cancellationToken);
            if (manifest is null)
                throw new FileNotFoundException("The resumable upload could not be found.");
            if (string.Equals(manifest.Stage, RemoteUploadStages.Completed, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(manifest.Stage, RemoteUploadStages.Processing, StringComparison.OrdinalIgnoreCase))
                return GetUploadStatusNoLock(sessionCode, manifest);
            if (string.Equals(manifest.Stage, RemoteUploadStages.Failed, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(manifest.ErrorMessage ?? "Video processing failed.");

            var uploadStatus = GetUploadStatusNoLock(sessionCode, manifest);
            var expectedChunkCount = GetUploadChunkCount(manifest.SizeBytes);
            if (uploadStatus.CompletedChunkIndexes.Count != expectedChunkCount ||
                uploadStatus.BytesReceived != manifest.SizeBytes)
                throw new InvalidOperationException("The video upload is not complete.");

            var uploadsDirectory = GetUploadsDirectory(sessionCode);
            VideoCanonicalizer.EnsureWorkingSpace(uploadsDirectory, manifest.SizeBytes);
            var sourcePath = GetPreparedSourcePath(sessionCode, uploadId, manifest.FileName);
            var assemblingPath = sourcePath + ".assembling";
            try
            {
                await using (var output = new FileStream(
                    assemblingPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    for (var index = 0; index < expectedChunkCount; index++)
                    {
                        await using var input = new FileStream(
                            GetUploadChunkPath(sessionCode, uploadId, index),
                            FileMode.Open, FileAccess.Read, FileShare.Read,
                            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await input.CopyToAsync(output, 1024 * 1024, cancellationToken);
                    }
                    await output.FlushAsync(cancellationToken);
                }
                if (new FileInfo(assemblingPath).Length != manifest.SizeBytes)
                    throw new IOException("The assembled video size is incorrect.");
                File.Move(assemblingPath, sourcePath, overwrite: true);

                manifest.Stage = RemoteUploadStages.Processing;
                manifest.ErrorMessage = "";
                manifest.UpdatedAtUtc = DateTimeOffset.UtcNow;
                await WriteJsonAtomicAsync(GetUploadManifestPath(sessionCode, uploadId), manifest, cancellationToken);
                DeleteUploadChunkParts(sessionCode, uploadId);
                return GetUploadStatusNoLock(sessionCode, manifest);
            }
            catch
            {
                TryDeleteFile(assemblingPath);
                throw;
            }
        }
        finally
        {
            uploadGate.Release();
        }
    }

    public async Task ProcessPreparedUploadAsync(
        string sessionCode,
        string uploadId,
        CancellationToken cancellationToken = default)
    {
        sessionCode = RequireSessionCode(sessionCode);
        uploadId = RequireUploadId(uploadId);
        var processingGate = GetProcessingLock(sessionCode);
        await processingGate.WaitAsync(cancellationToken);
        try
        {
            var uploadGate = GetUploadLock(sessionCode, uploadId);
            await uploadGate.WaitAsync(cancellationToken);
            try
            {
                var manifestPath = GetUploadManifestPath(sessionCode, uploadId);
                var manifest = await ReadUploadManifestAsync(manifestPath, cancellationToken);
                if (manifest is null ||
                    !string.Equals(manifest.Stage, RemoteUploadStages.Processing, StringComparison.OrdinalIgnoreCase))
                    return;

                var videosDirectory = Path.Combine(GetSessionDirectory(sessionCode), "videos");
                Directory.CreateDirectory(videosDirectory);
                var sourcePath = GetPreparedSourcePath(sessionCode, uploadId, manifest.FileName);
                var processingPath = Path.Combine(videosDirectory, uploadId + ".mp4.processing");
                var finalPath = Path.Combine(videosDirectory, uploadId + ".mp4");
                var published = false;
                try
                {
                    if (!File.Exists(sourcePath))
                        throw new FileNotFoundException("The uploaded source video is no longer available for processing.");

                    await _canonicalizer.CanonicalizeAsync(
                        sourcePath, processingPath, MaximumVideoSizeBytes, cancellationToken);
                    File.Move(processingPath, finalPath, overwrite: true);

                    var video = new RemoteVideoDescriptor
                    {
                        Id = uploadId,
                        FileName = VideoCanonicalizer.GetCanonicalFileName(manifest.FileName),
                        SizeBytes = new FileInfo(finalPath).Length,
                        UploadedAtUtc = DateTimeOffset.UtcNow
                    };
                    var sessionGate = GetSessionLock(sessionCode);
                    await sessionGate.WaitAsync(CancellationToken.None);
                    try
                    {
                        var videos = (await ReadVideosNoLockAsync(sessionCode, CancellationToken.None)).ToList();
                        var existing = videos.FirstOrDefault(item =>
                            string.Equals(item.Id, uploadId, StringComparison.OrdinalIgnoreCase));
                        if (existing is null)
                        {
                            videos.Add(video);
                            await WriteJsonAtomicAsync(
                                GetVideosMetadataPath(sessionCode), videos, CancellationToken.None);
                        }
                        else
                        {
                            video = existing;
                        }
                        published = true;
                    }
                    finally { sessionGate.Release(); }

                    manifest.Stage = RemoteUploadStages.Completed;
                    manifest.VideoId = video.Id;
                    manifest.ErrorMessage = "";
                    manifest.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    await WriteJsonAtomicAsync(manifestPath, manifest, CancellationToken.None);
                    DeleteUploadMediaFiles(sessionCode, uploadId);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    TryDeleteFile(processingPath);
                    if (File.Exists(finalPath) && !published) TryDeleteFile(finalPath);
                    throw;
                }
                catch (Exception ex)
                {
                    TryDeleteFile(processingPath);
                    if (!published)
                    {
                        TryDeleteFile(finalPath);
                        DeleteUploadMediaFiles(sessionCode, uploadId);
                        manifest.Stage = RemoteUploadStages.Failed;
                        manifest.ErrorMessage = CleanProcessingError(ex.Message);
                    }
                    else
                    {
                        manifest.Stage = RemoteUploadStages.Completed;
                        manifest.VideoId = uploadId;
                        manifest.ErrorMessage = "";
                    }
                    manifest.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    await WriteJsonAtomicAsync(manifestPath, manifest, CancellationToken.None);
                }
            }
            finally { uploadGate.Release(); }
        }
        finally { processingGate.Release(); }
    }

    public async Task<IReadOnlyList<PendingVideoProcessing>> ListPendingVideoProcessingAsync(
        CancellationToken cancellationToken = default)
    {
        var pending = new List<PendingVideoProcessing>();
        if (!Directory.Exists(_storageRoot)) return pending;
        foreach (var sessionDirectory in Directory.EnumerateDirectories(_storageRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sessionCode = Path.GetFileName(sessionDirectory);
            if (!IsValidSessionCode(sessionCode)) continue;
            var uploadsDirectory = Path.Combine(sessionDirectory, "uploads");
            if (!Directory.Exists(uploadsDirectory)) continue;
            foreach (var manifestPath in Directory.EnumerateFiles(uploadsDirectory, "*.json"))
            {
                var manifest = await ReadUploadManifestAsync(manifestPath, cancellationToken);
                if (manifest is not null &&
                    string.Equals(manifest.Stage, RemoteUploadStages.Processing, StringComparison.OrdinalIgnoreCase))
                    pending.Add(new PendingVideoProcessing(sessionCode, manifest.UploadId));
            }
        }
        return pending;
    }

    public async Task CancelUploadAsync(
        string sessionCode,
        string uploadId,
        CancellationToken cancellationToken = default)
    {
        sessionCode = RequireSessionCode(sessionCode);
        uploadId = RequireUploadId(uploadId);
        var gate = GetUploadLock(sessionCode, uploadId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var manifestPath = GetUploadManifestPath(sessionCode, uploadId);
            if (File.Exists(manifestPath)) File.Delete(manifestPath);
            DeleteUploadChunkFiles(sessionCode, uploadId);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<(string Path, RemoteVideoDescriptor Video)?> FindVideoAsync(
        string sessionCode,
        string videoId,
        CancellationToken cancellationToken = default)
    {
        sessionCode = RequireSessionCode(sessionCode);
        if (!Guid.TryParseExact(videoId, "N", out _)) return null;

        var videos = await ListVideosAsync(sessionCode, cancellationToken);
        var video = videos.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, videoId, StringComparison.OrdinalIgnoreCase));
        if (video == null) return null;

        var path = Path.Combine(GetSessionDirectory(sessionCode), "videos", video.Id + ".mp4");
        return File.Exists(path) ? (path, video) : null;
    }

    public async Task DeleteVideoAsync(string sessionCode, string videoId, CancellationToken cancellationToken = default)
    {
        sessionCode = RequireSessionCode(sessionCode);
        if (!Guid.TryParseExact(videoId, "N", out _)) throw new InvalidOperationException("Invalid video identifier.");
        var gate = GetSessionLock(sessionCode);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var videos = (await ReadVideosNoLockAsync(sessionCode, cancellationToken)).ToList();
            var video = videos.FirstOrDefault(item => string.Equals(item.Id, videoId, StringComparison.OrdinalIgnoreCase))
                ?? throw new FileNotFoundException("Video not found.");
            var path = Path.Combine(GetSessionDirectory(sessionCode), "videos", video.Id + ".mp4");
            if (File.Exists(path)) File.Delete(path);
            videos.Remove(video);
            await WriteJsonAtomicAsync(GetVideosMetadataPath(sessionCode), videos, cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async Task<RemoteVideoDescriptor> RenameVideoAsync(
        string sessionCode,
        string videoId,
        string? requestedFileName,
        CancellationToken cancellationToken = default)
    {
        sessionCode = RequireSessionCode(sessionCode);
        if (!Guid.TryParseExact(videoId, "N", out _))
            throw new InvalidOperationException("Invalid video identifier.");
        var fileName = NormalizeVideoFileName(requestedFileName);
        var gate = GetSessionLock(sessionCode);
        PlaybackState? updatedPlayback = null;
        RemoteVideoDescriptor renamed;
        await gate.WaitAsync(cancellationToken);
        try
        {
            var videos = (await ReadVideosNoLockAsync(sessionCode, cancellationToken)).ToList();
            renamed = videos.FirstOrDefault(item => string.Equals(item.Id, videoId, StringComparison.OrdinalIgnoreCase))
                ?? throw new FileNotFoundException("Video not found.");
            if (videos.Any(item => item.Id != renamed.Id &&
                                   string.Equals(item.FileName, fileName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Another video in this Rink ID already has that name.");

            renamed.FileName = fileName;
            await WriteJsonAtomicAsync(GetVideosMetadataPath(sessionCode), videos, cancellationToken);

            var playback = await ReadPlaybackStateNoLockAsync(sessionCode, cancellationToken);
            if (string.Equals(playback.VideoId, videoId, StringComparison.OrdinalIgnoreCase))
            {
                playback.VideoFileName = fileName;
                playback.Revision++;
                playback.UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await WriteJsonAtomicAsync(GetPlaybackStatePath(sessionCode), playback, cancellationToken);
                updatedPlayback = playback;
            }
        }
        finally { gate.Release(); }

        if (updatedPlayback is not null) Broadcast(sessionCode, updatedPlayback);
        return renamed;
    }

    public async Task DeleteAllVideosAsync(string sessionCode, CancellationToken cancellationToken = default)
    {
        sessionCode = RequireSessionCode(sessionCode);
        var processingGate = GetProcessingLock(sessionCode);
        await processingGate.WaitAsync(cancellationToken);
        var gate = GetSessionLock(sessionCode);
        try
        {
            PlaybackState cleared;
            await gate.WaitAsync(cancellationToken);
            try
            {
                var current = await ReadPlaybackStateNoLockAsync(sessionCode, cancellationToken);
                var videosDirectory = Path.Combine(GetSessionDirectory(sessionCode), "videos");
                if (Directory.Exists(videosDirectory)) Directory.Delete(videosDirectory, recursive: true);
                var metadataPath = GetVideosMetadataPath(sessionCode);
                if (File.Exists(metadataPath)) File.Delete(metadataPath);

                cleared = new PlaybackState
                {
                    Revision = current.Revision + 1,
                    UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                await WriteJsonAtomicAsync(GetPlaybackStatePath(sessionCode), cleared, cancellationToken);
            }
            finally { gate.Release(); }

            Broadcast(sessionCode, cleared);
        }
        finally { processingGate.Release(); }
    }

    public async Task DeleteSessionAsync(string sessionCode, CancellationToken cancellationToken = default)
    {
        sessionCode = RequireSessionCode(sessionCode);
        var processingGate = GetProcessingLock(sessionCode);
        await processingGate.WaitAsync(cancellationToken);
        var gate = GetSessionLock(sessionCode);
        try
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var sessionDirectory = GetSessionDirectory(sessionCode);
                if (Directory.Exists(sessionDirectory)) Directory.Delete(sessionDirectory, recursive: true);
            }
            finally { gate.Release(); }

            Broadcast(sessionCode, new PlaybackState
            {
                Revision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }
        finally { processingGate.Release(); }
    }

    public async Task<IReadOnlyList<RemoteVideoDescriptor>> ReorderVideosAsync(
        string sessionCode,
        IReadOnlyList<string>? videoIds,
        CancellationToken cancellationToken = default)
    {
        sessionCode = RequireSessionCode(sessionCode);
        var gate = GetSessionLock(sessionCode);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var videos = await ReadVideosNoLockAsync(sessionCode, cancellationToken);
            var requestedIds = (videoIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
            if (requestedIds.Count != videos.Count ||
                requestedIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != videos.Count)
                throw new InvalidOperationException("The reordered list must contain every video exactly once.");

            var byId = videos.ToDictionary(video => video.Id, StringComparer.OrdinalIgnoreCase);
            if (requestedIds.Any(id => !byId.ContainsKey(id)))
                throw new InvalidOperationException("The reordered list contains an unknown video.");

            var reordered = requestedIds.Select(id => byId[id]).ToList();
            await WriteJsonAtomicAsync(GetVideosMetadataPath(sessionCode), reordered, cancellationToken);
            return reordered;
        }
        finally { gate.Release(); }
    }

    public async Task<PlaybackState> GetPlaybackStateAsync(
        string sessionCode,
        CancellationToken cancellationToken = default)
    {
        sessionCode = RequireSessionCode(sessionCode);
        var gate = GetSessionLock(sessionCode);
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadPlaybackStateNoLockAsync(sessionCode, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PlaybackState> UpdatePlaybackStateAsync(
        string sessionCode,
        PlaybackCommand command,
        CancellationToken cancellationToken)
    {
        sessionCode = RequireSessionCode(sessionCode);
        if (!Guid.TryParseExact(command.VideoId, "N", out _))
            throw new InvalidOperationException("Select a valid uploaded video.");
        if (!double.IsFinite(command.PositionSeconds) || command.PositionSeconds < 0)
            throw new InvalidOperationException("Playback position is invalid.");
        if (!double.IsFinite(command.PlaybackRate) || command.PlaybackRate <= 0 || command.PlaybackRate > 4)
            throw new InvalidOperationException("Playback rate is invalid.");
        if (!double.IsFinite(command.TimelinePositionSeconds) || command.TimelinePositionSeconds < 0 ||
            !double.IsFinite(command.TimelineDurationSeconds) || command.TimelineDurationSeconds < 0)
            throw new InvalidOperationException("Timeline position or duration is invalid.");
        if (command.ProgramStartSeconds is double programStart && (!double.IsFinite(programStart) || programStart < 0))
            throw new InvalidOperationException("Program start position is invalid.");
        if (command.HalfwaySeconds is double halfway && (!double.IsFinite(halfway) || halfway <= 0))
            throw new InvalidOperationException("Halfway duration is invalid.");
        if (command.OpenClipStartSeconds is double openClipStart &&
            (!double.IsFinite(openClipStart) || openClipStart < 0))
            throw new InvalidOperationException("Open clip start position is invalid.");
        if (!double.IsFinite(command.ZoomScale) || !double.IsFinite(command.ZoomOffsetX) || !double.IsFinite(command.ZoomOffsetY))
            throw new InvalidOperationException("Zoom state is invalid.");
        if (!Guid.TryParseExact(command.OperatorInstanceId, "N", out _))
            throw new InvalidOperationException("Remote playback publisher identity is invalid.");
        if (command.OperatorGeneration <= 0 || command.OperatorSequence <= 0)
            throw new InvalidOperationException("Remote playback publisher sequence is invalid.");

        var gate = GetSessionLock(sessionCode);
        PlaybackState next;
        await gate.WaitAsync(cancellationToken);
        try
        {
            var videos = await ReadVideosNoLockAsync(sessionCode, cancellationToken);
            var selectedVideo = videos.FirstOrDefault(video =>
                string.Equals(video.Id, command.VideoId, StringComparison.OrdinalIgnoreCase));
            if (selectedVideo == null)
                throw new FileNotFoundException("The selected video does not exist in this Remote session.");

            var current = await ReadPlaybackStateNoLockAsync(sessionCode, cancellationToken);
            if (!IsNewerOperatorCommand(current, command))
                return current;
            var nextMode = NormalizePlaybackMode(command.Mode);
            var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var selectionChanged = !string.Equals(current.VideoId, command.VideoId, StringComparison.OrdinalIgnoreCase);
            var operatorReconnected = string.Equals(current.Mode, "operator-offline", StringComparison.OrdinalIgnoreCase);
            var playbackEnabled = nextMode == "recording"
                ? true
                : nextMode == "ready" || selectionChanged
                    ? false
                    : operatorReconnected || current.PlaybackEnabled;
            var normalizedClips = (command.Clips ?? [])
                .Where(clip => clip.Index > 0 &&
                               double.IsFinite(clip.StartSeconds) &&
                               double.IsFinite(clip.EndSeconds) &&
                               clip.StartSeconds >= 0 &&
                               clip.EndSeconds > clip.StartSeconds)
                .Take(100)
                .Select(clip => new RemoteTimelineClip
                {
                    Index = clip.Index,
                    StartSeconds = clip.StartSeconds,
                    EndSeconds = clip.EndSeconds
                })
                .ToList();
            var preserveRecordingMetadata =
                nextMode == "replay" &&
                normalizedClips.Count == 0 &&
                string.Equals(current.Mode, "recording", StringComparison.OrdinalIgnoreCase);
            next = new PlaybackState
            {
                Revision = current.Revision + 1,
                OperatorInstanceId = command.OperatorInstanceId,
                OperatorGeneration = command.OperatorGeneration,
                OperatorSequence = command.OperatorSequence,
                OperatorLeaseExpiresAtUnixMs = nowUnixMs + (long)OperatorLeaseDuration.TotalMilliseconds,
                VideoId = command.VideoId,
                VideoFileName = selectedVideo.FileName,
                PositionSeconds = command.PositionSeconds,
                TimelinePositionSeconds = command.TimelinePositionSeconds,
                TimelineDurationSeconds = command.TimelineDurationSeconds,
                IsPlaying = playbackEnabled && command.IsPlaying,
                PlaybackEnabled = playbackEnabled,
                PlaybackRate = command.PlaybackRate,
                PlaybackDiscontinuity = Math.Max(0, command.PlaybackDiscontinuity),
                Mode = nextMode,
                ReviewStartedAtUnixMs = nextMode == "recording" || nextMode == "ready" || selectionChanged
                    ? 0
                    : string.Equals(current.Mode, "recording", StringComparison.OrdinalIgnoreCase)
                        ? nowUnixMs
                        : current.ReviewStartedAtUnixMs,
                ProgramStartSeconds = command.ProgramStartSeconds ??
                    (preserveRecordingMetadata ? current.ProgramStartSeconds : null),
                HalfwaySeconds = command.HalfwaySeconds ??
                    (preserveRecordingMetadata ? current.HalfwaySeconds : null),
                OpenClipStartSeconds = nextMode == "recording"
                    ? command.OpenClipStartSeconds
                    : null,
                ZoomScale = Math.Clamp(command.ZoomScale, 1, 2.5),
                ZoomOffsetX = Math.Clamp(command.ZoomOffsetX, -2, 0),
                ZoomOffsetY = Math.Clamp(command.ZoomOffsetY, -2, 0),
                Clips = preserveRecordingMetadata ? (current.Clips ?? []) : normalizedClips,
                UpdatedAtUnixMs = nowUnixMs
            };

            await WriteJsonAtomicAsync(GetPlaybackStatePath(sessionCode), next, cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        Broadcast(sessionCode, next);
        return next;
    }

    public async Task ExpireOperatorLeasesAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_storageRoot)) return;
        var expiredStates = new List<(string SessionCode, PlaybackState State)>();
        foreach (var directory in Directory.EnumerateDirectories(_storageRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sessionCode = Path.GetFileName(directory);
            if (!IsValidSessionCode(sessionCode)) continue;

            var gate = GetSessionLock(sessionCode);
            await gate.WaitAsync(cancellationToken);
            try
            {
                var current = await ReadPlaybackStateNoLockAsync(sessionCode, cancellationToken);
                var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var expiresAtUnixMs = current.OperatorLeaseExpiresAtUnixMs > 0
                    ? current.OperatorLeaseExpiresAtUnixMs
                    : current.UpdatedAtUnixMs + (long)OperatorLeaseDuration.TotalMilliseconds;
                if (string.IsNullOrWhiteSpace(current.VideoId) ||
                    string.Equals(current.Mode, "operator-offline", StringComparison.OrdinalIgnoreCase) ||
                    expiresAtUnixMs > nowUnixMs)
                    continue;

                current.Revision++;
                current.IsPlaying = false;
                current.PlaybackEnabled = false;
                current.Mode = "operator-offline";
                current.UpdatedAtUnixMs = nowUnixMs;
                await WriteJsonAtomicAsync(GetPlaybackStatePath(sessionCode), current, cancellationToken);
                expiredStates.Add((sessionCode, current));
            }
            finally
            {
                gate.Release();
            }
        }

        foreach (var expired in expiredStates)
            Broadcast(expired.SessionCode, expired.State);
    }

    public RemotePlaybackSubscription Subscribe(string sessionCode)
    {
        sessionCode = RequireSessionCode(sessionCode);
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<PlaybackState>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        var sessionSubscribers = _subscribers.GetOrAdd(
            sessionCode,
            _ => new ConcurrentDictionary<Guid, Channel<PlaybackState>>());
        sessionSubscribers[id] = channel;
        return new RemotePlaybackSubscription(channel.Reader, () => RemoveSubscriber(sessionCode, id));
    }

    private void Broadcast(string sessionCode, PlaybackState state)
    {
        if (!_subscribers.TryGetValue(sessionCode, out var subscribers)) return;
        foreach (var channel in subscribers.Values)
            channel.Writer.TryWrite(state);
    }

    private void RemoveSubscriber(string sessionCode, Guid id)
    {
        if (!_subscribers.TryGetValue(sessionCode, out var subscribers)) return;
        if (subscribers.TryRemove(id, out var channel)) channel.Writer.TryComplete();
        if (subscribers.IsEmpty) _subscribers.TryRemove(sessionCode, out _);
    }

    private SemaphoreSlim GetSessionLock(string sessionCode)
        => _sessionLocks.GetOrAdd(sessionCode, _ => new SemaphoreSlim(1, 1));

    private static bool IsNewerOperatorCommand(PlaybackState current, PlaybackCommand command)
    {
        if (current.OperatorGeneration <= 0) return true;
        if (command.OperatorGeneration != current.OperatorGeneration)
            return command.OperatorGeneration > current.OperatorGeneration;

        var sourceComparison = string.CompareOrdinal(command.OperatorInstanceId, current.OperatorInstanceId);
        if (sourceComparison != 0) return sourceComparison > 0;
        return command.OperatorSequence > current.OperatorSequence;
    }

    private SemaphoreSlim GetProcessingLock(string sessionCode)
        => _processingLocks.GetOrAdd(sessionCode, _ => new SemaphoreSlim(1, 1));

    private static string GetUploadLockKey(string sessionCode, string uploadId)
        => sessionCode + ":" + uploadId;

    private SemaphoreSlim GetUploadLock(string sessionCode, string uploadId)
        => _uploadLocks.GetOrAdd(GetUploadLockKey(sessionCode, uploadId), _ => new SemaphoreSlim(1, 1));

    private static string GetUploadLockKey(string sessionCode, string uploadId, int chunkIndex)
        => sessionCode + ":" + uploadId + ":" + chunkIndex;

    private SemaphoreSlim GetUploadLock(string sessionCode, string uploadId, int chunkIndex)
        => _uploadLocks.GetOrAdd(
            GetUploadLockKey(sessionCode, uploadId, chunkIndex),
            _ => new SemaphoreSlim(1, 1));

    private string GetSessionDirectory(string sessionCode)
        => Path.Combine(_storageRoot, sessionCode);

    private string GetVideosMetadataPath(string sessionCode)
        => Path.Combine(GetSessionDirectory(sessionCode), "videos.json");

    private string GetUploadsDirectory(string sessionCode)
        => Path.Combine(GetSessionDirectory(sessionCode), "uploads");

    private string GetUploadManifestPath(string sessionCode, string uploadId)
        => Path.Combine(GetUploadsDirectory(sessionCode), uploadId + ".json");

    private string GetUploadChunkPath(string sessionCode, string uploadId, int chunkIndex)
        => Path.Combine(GetUploadsDirectory(sessionCode), $"{uploadId}.{chunkIndex:D8}.chunk");

    private string GetUploadChunkTempPath(string sessionCode, string uploadId, int chunkIndex)
        => Path.Combine(GetUploadsDirectory(sessionCode), $"{uploadId}.{chunkIndex:D8}.uploading");

    private string GetPreparedSourcePath(string sessionCode, string uploadId, string fileName)
        => Path.Combine(
            GetUploadsDirectory(sessionCode),
            uploadId + ".source" + Path.GetExtension(fileName).ToLowerInvariant());

    private string GetPlaybackStatePath(string sessionCode)
        => Path.Combine(GetSessionDirectory(sessionCode), "playback.json");

    private async Task<List<RemoteVideoDescriptor>> ReadVideosNoLockAsync(
        string sessionCode,
        CancellationToken cancellationToken)
    {
        var path = GetVideosMetadataPath(sessionCode);
        if (!File.Exists(path)) return [];
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<RemoteVideoDescriptor>>(stream, _jsonOptions, cancellationToken)
            ?? [];
    }

    private async Task<PlaybackState> ReadPlaybackStateNoLockAsync(
        string sessionCode,
        CancellationToken cancellationToken)
    {
        var path = GetPlaybackStatePath(sessionCode);
        if (!File.Exists(path)) return new PlaybackState();
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<PlaybackState>(stream, _jsonOptions, cancellationToken)
            ?? new PlaybackState();
    }

    private async Task<RemoteUploadManifest?> ReadUploadManifestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<RemoteUploadManifest>(stream, _jsonOptions, cancellationToken);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    private async Task<RemoteUploadManifest> RequireUploadManifestAsync(
        string sessionCode,
        string uploadId,
        CancellationToken cancellationToken)
    {
        var manifest = await ReadUploadManifestAsync(
            GetUploadManifestPath(sessionCode, uploadId), cancellationToken);
        return manifest ?? throw new FileNotFoundException("The resumable upload could not be found.");
    }

    private async Task DeleteExpiredUploadsNoLockAsync(
        string sessionCode,
        CancellationToken cancellationToken)
    {
        var directory = GetUploadsDirectory(sessionCode);
        if (!Directory.Exists(directory)) return;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
        foreach (var manifestPath in Directory.EnumerateFiles(directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = await ReadUploadManifestAsync(manifestPath, cancellationToken);
            if (manifest is not null && File.GetLastWriteTimeUtc(manifestPath) >= cutoff.UtcDateTime) continue;
            var uploadId = Path.GetFileNameWithoutExtension(manifestPath);
            try { File.Delete(manifestPath); } catch { }
            DeleteUploadChunkFiles(sessionCode, uploadId);
        }
    }

    private RemoteUploadStatus GetUploadStatusNoLock(string sessionCode, RemoteUploadManifest manifest)
    {
        var completed = new List<int>();
        long bytesReceived = 0;
        var count = GetUploadChunkCount(manifest.SizeBytes);
        if (!string.Equals(manifest.Stage, RemoteUploadStages.Uploading, StringComparison.OrdinalIgnoreCase))
        {
            completed.AddRange(Enumerable.Range(0, count));
            bytesReceived = manifest.SizeBytes;
        }
        else
        {
            for (var index = 0; index < count; index++)
            {
                var path = GetUploadChunkPath(sessionCode, manifest.UploadId, index);
                var expectedLength = Math.Min(
                    MaximumUploadChunkBytes,
                    manifest.SizeBytes - (long)index * MaximumUploadChunkBytes);
                if (!File.Exists(path) || new FileInfo(path).Length != expectedLength) continue;
                completed.Add(index);
                bytesReceived += expectedLength;
            }
        }

        var manifestPath = GetUploadManifestPath(sessionCode, manifest.UploadId);
        return new RemoteUploadStatus
        {
            UploadId = manifest.UploadId,
            FileName = manifest.FileName,
            SizeBytes = manifest.SizeBytes,
            BytesReceived = Math.Clamp(bytesReceived, 0, manifest.SizeBytes),
            CompletedChunkIndexes = completed,
            LastModifiedUnixMs = manifest.LastModifiedUnixMs,
            CreatedAtUtc = manifest.CreatedAtUtc,
            UpdatedAtUtc = File.Exists(manifestPath)
                ? File.GetLastWriteTimeUtc(manifestPath)
                : manifest.UpdatedAtUtc,
            ChunkSizeBytes = MaximumUploadChunkBytes,
            Stage = manifest.Stage,
            ErrorMessage = manifest.ErrorMessage,
            VideoId = manifest.VideoId
        };
    }

    private static int GetUploadChunkCount(long sizeBytes)
        => checked((int)((sizeBytes + MaximumUploadChunkBytes - 1) / MaximumUploadChunkBytes));

    private void DeleteUploadChunkFiles(string sessionCode, string uploadId)
    {
        var directory = GetUploadsDirectory(sessionCode);
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, uploadId + ".*"))
        {
            try { File.Delete(path); } catch { }
        }
        // Remove a sequential-format partial file left by the previous
        // uploader version, if one exists.
        var legacyPartPath = Path.Combine(directory, uploadId + ".part");
        try { if (File.Exists(legacyPartPath)) File.Delete(legacyPartPath); } catch { }
    }

    private void DeleteUploadChunkParts(string sessionCode, string uploadId)
    {
        var directory = GetUploadsDirectory(sessionCode);
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, uploadId + ".*"))
        {
            var extension = Path.GetExtension(path);
            if (string.Equals(extension, ".chunk", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".uploading", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".part", StringComparison.OrdinalIgnoreCase))
                TryDeleteFile(path);
        }
    }

    private void DeleteUploadMediaFiles(string sessionCode, string uploadId)
    {
        var directory = GetUploadsDirectory(sessionCode);
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, uploadId + ".*"))
        {
            if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
                TryDeleteFile(path);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string CleanProcessingError(string value)
    {
        var text = string.Join(" ", (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 700 ? text : text[..700] + "…";
    }

    private async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + ".tmp";
        await using (var stream = new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, value, _jsonOptions, cancellationToken);
        }
        File.Move(tempPath, path, overwrite: true);
    }

    private static string RequireSessionCode(string value)
    {
        var normalized = NormalizeSessionCode(value);
        return IsValidSessionCode(normalized)
            ? normalized
            : throw new InvalidOperationException("Rink ID must contain exactly six letters or digits.");
    }

    private static string RequireUploadId(string? value)
    {
        var normalized = (value ?? "").Trim().ToLowerInvariant();
        return Guid.TryParseExact(normalized, "N", out _)
            ? normalized
            : throw new InvalidOperationException("Invalid upload identifier.");
    }

    private static string NormalizeVideoFileName(string? value)
    {
        var fileName = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidOperationException("Enter a video name.");
        if (fileName.Length > 180)
            throw new InvalidOperationException("Video names cannot exceed 180 characters.");
        if (fileName.Any(char.IsControl) || fileName.IndexOfAny(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }) >= 0)
            throw new InvalidOperationException("The video name contains an invalid character.");

        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension)) fileName += ".mp4";
        else if (!string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Video names must use the .mp4 extension.");
        return fileName;
    }

    private static string NormalizeUploadFileName(string? value)
    {
        var fileName = Path.GetFileName((value ?? "").Trim());
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidOperationException("Select a video file to upload.");
        if (fileName.Length > 180)
            throw new InvalidOperationException("Video names cannot exceed 180 characters.");
        if (fileName.Any(char.IsControl) || fileName.IndexOfAny(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }) >= 0)
            throw new InvalidOperationException("The video name contains an invalid character.");
        if (!VideoCanonicalizer.IsSupportedUploadFileName(fileName))
            throw new InvalidOperationException(
                $"Unsupported video type. Choose {VideoCanonicalizer.SupportedUploadExtensionsText}.");
        return fileName;
    }

    private static string NormalizePlaybackMode(string? mode)
        => (mode ?? "").Trim().ToLowerInvariant() switch
        {
            "recording" => "recording",
            "replay" => "replay",
            "reverse-unavailable" => "reverse-unavailable",
            "ready" => "ready",
            _ => "paused"
        };
}

public sealed class RemoteVideoDescriptor
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTimeOffset UploadedAtUtc { get; set; }
}

public sealed class BeginRemoteUploadRequest
{
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public long LastModifiedUnixMs { get; set; }
}

public sealed class RemoteUploadStatus
{
    public string UploadId { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public long BytesReceived { get; set; }
    public List<int> CompletedChunkIndexes { get; set; } = [];
    public long LastModifiedUnixMs { get; set; }
    public long ChunkSizeBytes { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string Stage { get; set; } = RemoteUploadStages.Uploading;
    public string ErrorMessage { get; set; } = "";
    public string VideoId { get; set; } = "";
}

internal sealed class RemoteUploadManifest
{
    public string UploadId { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public long LastModifiedUnixMs { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string Stage { get; set; } = RemoteUploadStages.Uploading;
    public string ErrorMessage { get; set; } = "";
    public string VideoId { get; set; } = "";
}

public static class RemoteUploadStages
{
    public const string Uploading = "uploading";
    public const string Processing = "processing";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

public class PlaybackCommand
{
    public string OperatorInstanceId { get; set; } = "";
    public long OperatorGeneration { get; set; }
    public long OperatorSequence { get; set; }
    public string VideoId { get; set; } = "";
    public string VideoFileName { get; set; } = "";
    public double PositionSeconds { get; set; }
    public double TimelinePositionSeconds { get; set; }
    public double TimelineDurationSeconds { get; set; }
    public bool IsPlaying { get; set; }
    public double PlaybackRate { get; set; } = 1;
    public long PlaybackDiscontinuity { get; set; }
    public string Mode { get; set; } = "paused";
    public double? ProgramStartSeconds { get; set; }
    public double? HalfwaySeconds { get; set; }
    public double? OpenClipStartSeconds { get; set; }
    public double ZoomScale { get; set; } = 1;
    public double ZoomOffsetX { get; set; }
    public double ZoomOffsetY { get; set; }
    public List<RemoteTimelineClip> Clips { get; set; } = [];
}

public sealed class RemoteTimelineClip
{
    public int Index { get; set; }
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
}

public sealed class PlaybackState : PlaybackCommand
{
    public long Revision { get; set; }
    public long UpdatedAtUnixMs { get; set; }
    public long ReviewStartedAtUnixMs { get; set; }
    public bool PlaybackEnabled { get; set; }
    public long OperatorLeaseExpiresAtUnixMs { get; set; }
}

public sealed class ReorderVideosRequest
{
    public List<string> VideoIds { get; set; } = [];
}

public sealed class RenameVideoRequest
{
    public string FileName { get; set; } = "";
}

public sealed class RemotePlaybackSubscription : IDisposable
{
    private readonly Action _dispose;

    public RemotePlaybackSubscription(ChannelReader<PlaybackState> reader, Action dispose)
    {
        Reader = reader;
        _dispose = dispose;
    }

    public ChannelReader<PlaybackState> Reader { get; }
    public void Dispose() => _dispose();
}
