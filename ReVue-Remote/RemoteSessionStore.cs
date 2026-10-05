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
    private readonly SemaphoreSlim _storageAdmissionGate = new(1, 1);
    private readonly ConcurrentDictionary<string, double> _transcodeProgress = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _metadataBackfillAttempted = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, RemotePlaybackSubscriber>> _subscribers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Guid> _competitorScoredExpiryJobs = new(StringComparer.Ordinal);
    private readonly VideoCanonicalizer _canonicalizer;
    private readonly ILogger<RemoteSessionStore> _logger;

    public const long MaximumUploadChunkBytes = 8L * 1024 * 1024;
    public const long MaximumVideoSizeBytes = 10L * 1024 * 1024 * 1024;
    private const long MinimumProcessingAllowanceBytes = 64L * 1024 * 1024;
    // VRO renews every two seconds; three missed renewals expire its connection
    // status. Live media capture has an independent lifecycle.
    public static readonly TimeSpan OperatorLeaseDuration = TimeSpan.FromSeconds(6);
    // Connection status expires promptly, but a missed control heartbeat must
    // not terminate video in the middle of a performance.
    private static readonly TimeSpan LivePreparingDisconnectGrace = TimeSpan.FromSeconds(60);

    public RemoteSessionStore(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        VideoCanonicalizer canonicalizer,
        ILogger<RemoteSessionStore> logger)
    {
        _canonicalizer = canonicalizer;
        _logger = logger;
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

    public ServerStorageStatus? GetStorageStatus()
    {
        var storagePath = Path.GetFullPath(_storageRoot);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        DriveInfo? storageDrive = null;
        var longestRoot = -1;
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    var root = Path.GetFullPath(drive.RootDirectory.FullName);
                    var rootWithSeparator = Path.EndsInDirectorySeparator(root)
                        ? root : root + Path.DirectorySeparatorChar;
                    if (!drive.IsReady ||
                        (!storagePath.Equals(root.TrimEnd(Path.DirectorySeparatorChar), comparison) &&
                         !storagePath.StartsWith(rootWithSeparator, comparison)) ||
                        root.Length <= longestRoot)
                        continue;
                    storageDrive = drive;
                    longestRoot = root.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // A different mounted drive may not be readable by this process.
                }
            }
            if (storageDrive is null) return null;
            var total = storageDrive.TotalSize;
            var availableBefore = storageDrive.AvailableFreeSpace;
            if (total <= 0 || availableBefore < 0) return null;
            var revueVideoBytes = MeasureRevueVideoBytes();
            var availableAfter = storageDrive.AvailableFreeSpace;
            var available = Math.Min(availableBefore, availableAfter);
            return available >= 0
                ? new ServerStorageStatus(total, Math.Min(available, total), revueVideoBytes)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private long MeasureRevueVideoBytes()
    {
        long total = 0;
        foreach (var sessionDirectory in Directory.EnumerateDirectories(_storageRoot))
        {
            if (!IsValidSessionCode(Path.GetFileName(sessionDirectory))) continue;
            foreach (var folderName in new[] { "videos", "uploads", "live" })
            {
                var directory = Path.Combine(sessionDirectory, folderName);
                if (!Directory.Exists(directory)) continue;
                try
                {
                    foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var length = file.Length;
                            total = length > long.MaxValue - total ? long.MaxValue : total + length;
                        }
                        catch (IOException) { /* A file may finish or be removed while measuring. */ }
                    }
                }
                catch (DirectoryNotFoundException) { /* Next Competitor may delete a subtree during enumeration. */ }
            }
        }
        return total;
    }

    private async Task EnsureStorageCapacityAsync(long additionalPeakBytes, CancellationToken cancellationToken)
    {
        var reserved = await EstimateOutstandingStorageBytesAsync(cancellationToken);
        var storage = GetStorageStatus()
            ?? throw new InvalidOperationException("Server storage capacity is unavailable. The upload cannot start.");
        var remaining = Math.Max(0, storage.AvailableWithinLimitBytes - reserved);
        if (additionalPeakBytes <= remaining) return;
        throw new InvalidOperationException(
            $"Maximum ReVue video storage capacity (90% of space available to ReVue) would be exceeded. " +
            $"This video needs about {additionalPeakBytes / 1_073_741_824d:0.0} GiB during upload and processing, " +
            $"but only {remaining / 1_073_741_824d:0.0} GiB is available before the limit. " +
            "Finish or cancel other uploads, delete videos, or free server space.");
    }

    private static long ProcessingAllowance(long sizeBytes)
        => Math.Max(MinimumProcessingAllowanceBytes, sizeBytes / 20);

    private static long ExpectedUploadPeakBytes(long sizeBytes)
        => checked(sizeBytes * 2 + ProcessingAllowance(sizeBytes));

    private static long ExpectedTranscodePeakBytes(long sizeBytes)
        => checked(sizeBytes + ProcessingAllowance(sizeBytes));

    private void EnsureStorageWriteFits(long additionalBytes)
    {
        var storage = GetStorageStatus()
            ?? throw new InvalidOperationException("Server storage capacity is unavailable. The upload cannot continue.");
        if (additionalBytes > storage.AvailableWithinLimitBytes)
            throw new InvalidOperationException("Maximum ReVue video storage capacity has been reached. Free server space before continuing the upload.");
    }

    private async Task<long> EstimateOutstandingStorageBytesAsync(CancellationToken cancellationToken)
    {
        long reserved = 0;
        var cutoff = DateTime.UtcNow.AddDays(-7);
        foreach (var sessionDirectory in Directory.EnumerateDirectories(_storageRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsValidSessionCode(Path.GetFileName(sessionDirectory))) continue;
            var uploadsDirectory = Path.Combine(sessionDirectory, "uploads");
            if (!Directory.Exists(uploadsDirectory)) continue;
            foreach (var manifestPath in Directory.EnumerateFiles(uploadsDirectory, "*.json"))
            {
                if (File.GetLastWriteTimeUtc(manifestPath) < cutoff) continue;
                var manifest = await ReadUploadManifestAsync(manifestPath, cancellationToken);
                if (manifest is null)
                {
                    if (File.Exists(manifestPath))
                        throw new InvalidOperationException("Cannot verify pending uploads against the server storage limit.");
                    continue;
                }
                if (manifest.SizeBytes <= 0 ||
                    (manifest.Stage != RemoteUploadStages.Uploading &&
                     manifest.Stage != RemoteUploadStages.Processing &&
                     manifest.Stage != RemoteUploadStages.Transcoding)) continue;

                var uploadId = Path.GetFileNameWithoutExtension(manifestPath);
                var peakBytes = !string.IsNullOrEmpty(manifest.SourceVideoId)
                    ? ExpectedTranscodePeakBytes(manifest.SizeBytes)
                    : ExpectedUploadPeakBytes(manifest.SizeBytes);
                var storedBytes = SumJobFileBytes(uploadsDirectory, uploadId) +
                    SumJobFileBytes(Path.Combine(sessionDirectory, "videos"), uploadId);
                var outstanding = Math.Max(0, peakBytes - storedBytes);
                reserved = outstanding > long.MaxValue - reserved
                    ? long.MaxValue : reserved + outstanding;
            }
        }
        return reserved;
    }

    private static long SumJobFileBytes(string directory, string uploadId)
    {
        if (!Directory.Exists(directory)) return 0;
        long total = 0;
        foreach (var path in Directory.EnumerateFiles(directory, uploadId + ".*"))
        {
            try
            {
                var length = new FileInfo(path).Length;
                total = length > long.MaxValue - total ? long.MaxValue : total + length;
            }
            catch (IOException) { }
        }
        return total;
    }

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

    public async Task<IReadOnlyList<RemoteVideoDescriptor>> ListVideosWithMetadataAsync(
        string sessionCode, CancellationToken cancellationToken = default)
    {
        sessionCode = RequireSessionCode(sessionCode);
        var videos = await ListVideosAsync(sessionCode, cancellationToken);
        foreach (var video in videos)
        {
            if (video.Width is > 0 && video.Height is > 0 &&
                video.FramesPerSecond is > 0 && video.VideoBitrateBitsPerSecond is > 0 &&
                !string.IsNullOrWhiteSpace(video.FieldOrder) && video.Gop is not null)
                continue;
            if (!_metadataBackfillAttempted.TryAdd(sessionCode + ":" + video.Id, 0)) continue;
            var path = Path.Combine(GetSessionDirectory(sessionCode), "videos", video.Id + ".mp4");
            if (!File.Exists(path)) continue;
            try
            {
                var metadata = await _canonicalizer.InspectVideoAsync(path, cancellationToken);
                var gate = GetSessionLock(sessionCode);
                await gate.WaitAsync(cancellationToken);
                try
                {
                    var latest = (await ReadVideosNoLockAsync(sessionCode, cancellationToken)).ToList();
                    var stored = latest.FirstOrDefault(item => item.Id == video.Id);
                    if (stored is null) continue;
                    stored.Width = metadata.Width;
                    stored.Height = metadata.Height;
                    stored.FramesPerSecond = metadata.FramesPerSecond;
                    stored.VideoBitrateBitsPerSecond = metadata.VideoBitrateBitsPerSecond;
                    stored.FieldOrder = metadata.FieldOrder;
                    stored.Gop = metadata.Gop;
                    await WriteJsonAtomicAsync(GetVideosMetadataPath(sessionCode), latest, cancellationToken);
                }
                finally { gate.Release(); }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // An old or damaged file remains listed, but cannot offer
                // conversion controls without trustworthy metadata.
            }
        }
        return await ListVideosAsync(sessionCode, cancellationToken);
    }

    public async Task<IReadOnlyList<RemoteVideoDescriptor>> SaveUploadsAsync(
        string sessionCode,
        IFormFileCollection files,
        CancellationToken cancellationToken)
    {
        sessionCode = RequireSessionCode(sessionCode);
        if (files.Count == 0)
            throw new InvalidOperationException("Select at least one supported video to upload.");

        await _storageAdmissionGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var file in files)
            {
                var originalName = NormalizeUploadFileName(file.FileName ?? "video.mp4");
                if (file.Length <= 0)
                    throw new InvalidOperationException($"{originalName} is empty.");
                if (file.Length > MaximumVideoSizeBytes)
                    throw new InvalidOperationException($"{originalName} exceeds the 10 GiB video size limit.");
                await EnsureStorageCapacityAsync(ExpectedUploadPeakBytes(file.Length), cancellationToken);

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

                    EnsureStorageWriteFits(file.Length + ProcessingAllowance(file.Length));
                    var metadata = await _canonicalizer.CanonicalizeAsync(
                            sourcePath, processingPath, MaximumVideoSizeBytes, cancellationToken);
                        File.Move(processingPath, finalPath, overwrite: true);

                        var video = new RemoteVideoDescriptor
                        {
                            Id = id,
                            FileName = VideoCanonicalizer.GetCanonicalFileName(originalName),
                            SizeBytes = new FileInfo(finalPath).Length,
                            UploadedAtUtc = DateTimeOffset.UtcNow,
                            Width = metadata.Width,
                            Height = metadata.Height,
                            FramesPerSecond = metadata.FramesPerSecond,
                            VideoBitrateBitsPerSecond = metadata.VideoBitrateBitsPerSecond,
                            FieldOrder = metadata.FieldOrder,
                            Gop = metadata.Gop
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
        finally { _storageAdmissionGate.Release(); }
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

        await _storageAdmissionGate.WaitAsync(cancellationToken);
        try
        {
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
                    if (existing is null || !string.IsNullOrWhiteSpace(existing.SourceVideoId) ||
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

                await EnsureStorageCapacityAsync(ExpectedUploadPeakBytes(sizeBytes), cancellationToken);
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
        finally { _storageAdmissionGate.Release(); }
    }

    public async Task<RemoteUploadStatus> GetUploadStatusAsync(
        string sessionCode,
        string uploadId,
        CancellationToken cancellationToken = default)
    {
        sessionCode = RequireSessionCode(sessionCode);
        uploadId = RequireUploadId(uploadId);
        // Transcoding holds the upload lock while FFmpeg reads its source.
        // The manifest is written atomically, so progress can be read safely
        // without waiting for the whole encode to finish.
        if (_transcodeProgress.TryGetValue(sessionCode + ":" + uploadId, out var percent))
        {
            var activeManifest = await RequireUploadManifestAsync(sessionCode, uploadId, cancellationToken);
            return GetUploadStatusNoLock(sessionCode, activeManifest, percent);
        }
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

            EnsureStorageWriteFits(contentLength);

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
            EnsureStorageWriteFits(manifest.SizeBytes);
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

    public async Task<RemoteUploadStatus> BeginVideoTranscodeAsync(
        string sessionCode, string videoId, string? requestedMode,
        CancellationToken cancellationToken = default)
    {
        sessionCode = RequireSessionCode(sessionCode);
        videoId = RequireUploadId(videoId);
        var mode = (requestedMode ?? "").Trim().ToLowerInvariant();
        if (mode != VideoTranscodeModes.Resolution && mode != VideoTranscodeModes.FrameRate)
            throw new InvalidOperationException("Choose a supported video conversion.");

        await _storageAdmissionGate.WaitAsync(cancellationToken);
        try
        {
            var sessionGate = GetSessionLock(sessionCode);
            await sessionGate.WaitAsync(cancellationToken);
            try
            {
                var videos = await ReadVideosNoLockAsync(sessionCode, cancellationToken);
                var video = videos.FirstOrDefault(item => item.Id == videoId)
                    ?? throw new FileNotFoundException("Video not found.");
                var sourcePath = Path.Combine(GetSessionDirectory(sessionCode), "videos", videoId + ".mp4");
                if (!File.Exists(sourcePath))
                    throw new FileNotFoundException("The video file is no longer available for conversion.");
                if (mode == VideoTranscodeModes.Resolution && video.Height != 1080)
                    throw new InvalidOperationException("Only 1080-line videos can be converted to 720p.");
                if (mode == VideoTranscodeModes.FrameRate && video.FramesPerSecond is not > 30)
                    throw new InvalidOperationException("Only videos above 30 fps can be converted to 29.97 fps.");
                var playback = await ReadPlaybackStateNoLockAsync(sessionCode, cancellationToken);
                if (playback.VideoId == videoId)
                    throw new InvalidOperationException("The operator has selected this video. Choose another video or press Next Competitor before converting it.");

                var uploadsDirectory = GetUploadsDirectory(sessionCode);
                Directory.CreateDirectory(uploadsDirectory);
                foreach (var path in Directory.EnumerateFiles(uploadsDirectory, "*.json"))
                {
                    var existing = await ReadUploadManifestAsync(path, cancellationToken);
                    if (existing?.SourceVideoId != videoId ||
                        existing.Stage != RemoteUploadStages.Transcoding) continue;
                    if (existing.TranscodeMode == mode)
                        return GetUploadStatusNoLock(sessionCode, existing);
                    throw new InvalidOperationException("This video already has a conversion in progress.");
                }

                await EnsureStorageCapacityAsync(ExpectedTranscodePeakBytes(video.SizeBytes), cancellationToken);
                VideoCanonicalizer.EnsureWorkingSpace(uploadsDirectory, video.SizeBytes);
                var manifest = new RemoteUploadManifest
                {
                    UploadId = Guid.NewGuid().ToString("N"),
                    SourceVideoId = videoId,
                    TranscodeMode = mode,
                    FileName = video.FileName,
                    SizeBytes = video.SizeBytes,
                    Stage = RemoteUploadStages.Transcoding,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                await WriteJsonAtomicAsync(GetUploadManifestPath(sessionCode, manifest.UploadId), manifest, cancellationToken);
                return GetUploadStatusNoLock(sessionCode, manifest);
            }
            finally { sessionGate.Release(); }
        }
        finally { _storageAdmissionGate.Release(); }
    }

    public async Task<IReadOnlyList<RemoteUploadStatus>> ListActiveVideoTranscodesAsync(
        string sessionCode, CancellationToken cancellationToken = default)
    {
        sessionCode = RequireSessionCode(sessionCode);
        var uploadsDirectory = GetUploadsDirectory(sessionCode);
        if (!Directory.Exists(uploadsDirectory)) return [];
        var jobs = new List<RemoteUploadStatus>();
        foreach (var path in Directory.EnumerateFiles(uploadsDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = await ReadUploadManifestAsync(path, cancellationToken);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.SourceVideoId) ||
                manifest.Stage != RemoteUploadStages.Transcoding) continue;
            var active = _transcodeProgress.TryGetValue(
                sessionCode + ":" + manifest.UploadId, out var percent);
            jobs.Add(GetUploadStatusNoLock(sessionCode, manifest, active ? percent : null));
        }
        return jobs;
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
                    (manifest.Stage != RemoteUploadStages.Processing &&
                     manifest.Stage != RemoteUploadStages.Transcoding))
                    return;
                if (!string.IsNullOrWhiteSpace(manifest.SourceVideoId))
                {
                    await ProcessVideoTranscodeNoLockAsync(sessionCode, manifest, manifestPath, cancellationToken);
                    return;
                }

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

                    EnsureStorageWriteFits(manifest.SizeBytes + ProcessingAllowance(manifest.SizeBytes));
                    var metadata = await _canonicalizer.CanonicalizeAsync(
                        sourcePath, processingPath, MaximumVideoSizeBytes, cancellationToken);
                    File.Move(processingPath, finalPath, overwrite: true);

                    var video = new RemoteVideoDescriptor
                    {
                        Id = uploadId,
                        FileName = VideoCanonicalizer.GetCanonicalFileName(manifest.FileName),
                        SizeBytes = new FileInfo(finalPath).Length,
                        UploadedAtUtc = DateTimeOffset.UtcNow,
                        Width = metadata.Width,
                        Height = metadata.Height,
                        FramesPerSecond = metadata.FramesPerSecond,
                        VideoBitrateBitsPerSecond = metadata.VideoBitrateBitsPerSecond,
                        FieldOrder = metadata.FieldOrder,
                        Gop = metadata.Gop
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

    private async Task ProcessVideoTranscodeNoLockAsync(
        string sessionCode, RemoteUploadManifest manifest, string manifestPath,
        CancellationToken cancellationToken)
    {
        var videosDirectory = Path.Combine(GetSessionDirectory(sessionCode), "videos");
        var sourcePath = Path.Combine(videosDirectory, manifest.SourceVideoId + ".mp4");
        var processingPath = Path.Combine(videosDirectory, manifest.UploadId + ".mp4.processing");
        var finalPath = Path.Combine(videosDirectory, manifest.UploadId + ".mp4");
        var progressKey = sessionCode + ":" + manifest.UploadId;
        var published = false;
        try
        {
            // A restart may happen after publishing the replacement but
            // before marking the job complete. Finish that commit once.
            var existingVideos = await ReadVideosNoLockAsync(sessionCode, cancellationToken);
            if (existingVideos.Any(video => video.Id == manifest.UploadId))
            {
                published = true;
            }
            else
            {
                if (!File.Exists(sourcePath))
                    throw new FileNotFoundException("The source video is no longer available for conversion.");
                EnsureStorageWriteFits(manifest.SizeBytes + ProcessingAllowance(manifest.SizeBytes));
                _transcodeProgress[progressKey] = 0;
                var metadata = await _canonicalizer.CanonicalizeAsync(
                    sourcePath, processingPath, MaximumVideoSizeBytes, cancellationToken,
                    manifest.TranscodeMode,
                    percent => _transcodeProgress[progressKey] = percent);
                File.Move(processingPath, finalPath, overwrite: true);

                var sessionGate = GetSessionLock(sessionCode);
                await sessionGate.WaitAsync(cancellationToken);
                try
                {
                    var videos = (await ReadVideosNoLockAsync(sessionCode, cancellationToken)).ToList();
                    var index = videos.FindIndex(video => video.Id == manifest.SourceVideoId);
                    if (index < 0) throw new FileNotFoundException("The source video was removed during conversion.");
                    var playback = await ReadPlaybackStateNoLockAsync(sessionCode, cancellationToken);
                    if (playback.VideoId == manifest.SourceVideoId)
                        throw new InvalidOperationException("The operator selected this video during conversion. The original video was retained.");
                    videos[index] = new RemoteVideoDescriptor
                    {
                        Id = manifest.UploadId,
                        FileName = videos[index].FileName,
                        SizeBytes = new FileInfo(finalPath).Length,
                        UploadedAtUtc = DateTimeOffset.UtcNow,
                        Width = metadata.Width,
                        Height = metadata.Height,
                        FramesPerSecond = metadata.FramesPerSecond,
                        VideoBitrateBitsPerSecond = metadata.VideoBitrateBitsPerSecond,
                        FieldOrder = metadata.FieldOrder,
                        Gop = metadata.Gop
                    };
                    await WriteJsonAtomicAsync(GetVideosMetadataPath(sessionCode), videos, cancellationToken);
                    published = true;
                }
                finally { sessionGate.Release(); }
            }

            await PreserveTranscodedThumbnailAsync(
                videosDirectory, manifest.SourceVideoId, manifest.UploadId, finalPath, cancellationToken);
            TryDeleteFile(sourcePath);
            TryDeleteFile(Path.Combine(videosDirectory, manifest.SourceVideoId + ".thumb.jpg"));
            TryDeleteFile(Path.Combine(videosDirectory, manifest.SourceVideoId + ".thumb-28.jpg"));
            manifest.Stage = RemoteUploadStages.Completed;
            manifest.VideoId = manifest.UploadId;
            manifest.ErrorMessage = "";
            manifest.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await WriteJsonAtomicAsync(manifestPath, manifest, CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDeleteFile(processingPath);
            if (!published) TryDeleteFile(finalPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDeleteFile(processingPath);
            if (!published) TryDeleteFile(finalPath);
            manifest.Stage = published ? RemoteUploadStages.Completed : RemoteUploadStages.Failed;
            manifest.VideoId = published ? manifest.UploadId : "";
            manifest.ErrorMessage = published ? "" : CleanProcessingError(ex.Message);
            manifest.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await WriteJsonAtomicAsync(manifestPath, manifest, CancellationToken.None);
        }
        finally { _transcodeProgress.TryRemove(progressKey, out _); }
    }

    private async Task PreserveTranscodedThumbnailAsync(
        string videosDirectory, string sourceVideoId, string newVideoId,
        string newVideoPath, CancellationToken cancellationToken)
    {
        var oldThumbnail = Path.Combine(videosDirectory, sourceVideoId + ".thumb-28.jpg");
        var newThumbnail = Path.Combine(videosDirectory, newVideoId + ".thumb-28.jpg");
        try
        {
            if (File.Exists(oldThumbnail) && new FileInfo(oldThumbnail).Length > 0)
            {
                try
                {
                    File.Move(oldThumbnail, newThumbnail, overwrite: true);
                    return;
                }
                catch (IOException) { /* Generate a new thumbnail if the old one is in use. */ }
            }
            await _canonicalizer.EnsureThumbnailAsync(newVideoPath, newThumbnail, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not preserve thumbnail for transcoded video {VideoId}", newVideoId);
        }
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
                    (string.Equals(manifest.Stage, RemoteUploadStages.Processing, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(manifest.Stage, RemoteUploadStages.Transcoding, StringComparison.OrdinalIgnoreCase)))
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

    public async Task<string?> FindVideoThumbnailAsync(
        string sessionCode, string videoId, CancellationToken cancellationToken = default)
    {
        var video = await FindVideoAsync(sessionCode, videoId, cancellationToken);
        if (!video.HasValue) return null;
        var thumbnailPath = Path.Combine(
            GetSessionDirectory(RequireSessionCode(sessionCode)), "videos", video.Value.Video.Id + ".thumb-28.jpg");
        await _canonicalizer.EnsureThumbnailAsync(video.Value.Path, thumbnailPath, cancellationToken);
        TryDeleteFile(Path.Combine(
            GetSessionDirectory(RequireSessionCode(sessionCode)), "videos", video.Value.Video.Id + ".thumb.jpg"));
        return thumbnailPath;
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
            TryDeleteFile(Path.Combine(GetSessionDirectory(sessionCode), "videos", video.Id + ".thumb.jpg"));
            TryDeleteFile(Path.Combine(GetSessionDirectory(sessionCode), "videos", video.Id + ".thumb-28.jpg"));
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
                    CommunicationStatus = current.CommunicationStatus ?? new RemoteCommunicationStatus(),
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

    public async Task RenameSessionAsync(
        string oldCode,
        string newCode,
        Func<Task> persistKeyRename,
        CancellationToken cancellationToken = default)
    {
        oldCode = RequireSessionCode(oldCode);
        newCode = RequireSessionCode(newCode);
        var processingGate = GetProcessingLock(oldCode);
        await processingGate.WaitAsync(cancellationToken);
        try
        {
            var sessionGate = GetSessionLock(oldCode);
            await sessionGate.WaitAsync(cancellationToken);
            try
            {
                var oldDirectory = GetSessionDirectory(oldCode);
                var newDirectory = GetSessionDirectory(newCode);
                if (Directory.Exists(newDirectory) || File.Exists(newDirectory))
                    throw new InvalidOperationException("A video folder already exists for the new Rink ID.");

                var moved = false;
                if (Directory.Exists(oldDirectory))
                {
                    var playback = await ReadPlaybackStateNoLockAsync(oldCode, cancellationToken);
                    if (!string.Equals(playback.Mode, "operator-offline", StringComparison.OrdinalIgnoreCase) &&
                        playback.OperatorLeaseExpiresAtUnixMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                        throw new InvalidOperationException("The VRO is online for this Rink ID. Disconnect it before renaming.");

                    var uploadsDirectory = GetUploadsDirectory(oldCode);
                    if (Directory.Exists(uploadsDirectory))
                    {
                        foreach (var manifestPath in Directory.EnumerateFiles(uploadsDirectory, "*.json"))
                        {
                            var manifest = await ReadUploadManifestAsync(manifestPath, cancellationToken);
                            if (manifest is null ||
                                (!string.Equals(manifest.Stage, RemoteUploadStages.Completed, StringComparison.OrdinalIgnoreCase) &&
                                 !string.Equals(manifest.Stage, RemoteUploadStages.Failed, StringComparison.OrdinalIgnoreCase)))
                                throw new InvalidOperationException("Finish or cancel uploads and transcodes for this Rink ID before renaming.");
                        }
                    }
                    Directory.Move(oldDirectory, newDirectory);
                    moved = true;
                }

                try { await persistKeyRename(); }
                catch
                {
                    if (moved) Directory.Move(newDirectory, oldDirectory);
                    throw;
                }

                Broadcast(oldCode, new PlaybackState
                {
                    Revision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
            }
            finally { sessionGate.Release(); }
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
            var state = await ReadPlaybackStateNoLockAsync(sessionCode, cancellationToken);
            var status = state.CommunicationStatus ?? new RemoteCommunicationStatus();
            if (status.CompetitorScored && status.CompetitorScoredActivationId is null)
            {
                status.CompetitorScoredActivationId = Guid.NewGuid();
                status.CompetitorScoredExpiresAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 15_000;
                state.CommunicationStatus = status;
                state.Revision++;
                await WriteJsonAtomicAsync(GetPlaybackStatePath(sessionCode), state, cancellationToken);
                Broadcast(sessionCode, state);
            }
            if (status.CompetitorScored && status.CompetitorScoredActivationId is Guid activationId)
            {
                if (status.CompetitorScoredExpiresAtUnixMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                {
                    status.CompetitorScored = false;
                    status.CompetitorScoredActivationId = null;
                    status.CompetitorScoredExpiresAtUnixMs = 0;
                    state.CommunicationStatus = status;
                    state.Revision++;
                    await WriteJsonAtomicAsync(GetPlaybackStatePath(sessionCode), state, cancellationToken);
                    Broadcast(sessionCode, state);
                    _competitorScoredExpiryJobs.TryRemove(sessionCode, out _);
                }
                else
                {
                    ScheduleCompetitorScoredExpiration(sessionCode, activationId, status.CompetitorScoredExpiresAtUnixMs);
                }
            }
            return state;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PlaybackState> UpdatePlaybackStateAsync(
        string sessionCode,
        PlaybackCommand command,
        CancellationToken cancellationToken,
        Func<PlaybackState, PlaybackState, Task>? beforeCommit = null)
    {
        sessionCode = RequireSessionCode(sessionCode);
        var isLive = string.Equals(command.SourceType, SessionKeyModes.Live, StringComparison.OrdinalIgnoreCase);
        var clearSelection = string.IsNullOrWhiteSpace(command.VideoId) &&
            string.Equals(command.Mode, "ready", StringComparison.OrdinalIgnoreCase);
        if (!clearSelection && !Guid.TryParseExact(command.VideoId, "N", out _))
            throw new InvalidOperationException("Select a valid uploaded video.");
        if (!double.IsFinite(command.PositionSeconds) || command.PositionSeconds < 0)
            throw new InvalidOperationException("Playback position is invalid.");
        if (!double.IsFinite(command.PlaybackRate) || command.PlaybackRate <= 0 || command.PlaybackRate > 4)
            throw new InvalidOperationException("Playback rate is invalid.");
        if (!double.IsFinite(command.TimelinePositionSeconds) || command.TimelinePositionSeconds < 0 ||
            !double.IsFinite(command.TimelineDurationSeconds) || command.TimelineDurationSeconds < 0)
            throw new InvalidOperationException("Timeline position or duration is invalid.");
        if (command.FramesPerSecond is double commandFps &&
            (!double.IsFinite(commandFps) || commandFps <= 0 || commandFps > 240))
            throw new InvalidOperationException("Video frame rate is invalid.");
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
            var selectedVideo = clearSelection ? null : videos.FirstOrDefault(video =>
                string.Equals(video.Id, command.VideoId, StringComparison.OrdinalIgnoreCase));
            if (!clearSelection && selectedVideo == null && !isLive)
                throw new FileNotFoundException("The selected video does not exist in this Remote session.");
            var storedFps = selectedVideo?.FramesPerSecond;

            var current = await ReadPlaybackStateNoLockAsync(sessionCode, cancellationToken);
            if (!IsNewerOperatorCommand(current, command))
                return current;
            var nextMode = NormalizePlaybackMode(command.Mode);
            var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var selectionChanged = !string.Equals(current.VideoId, command.VideoId, StringComparison.OrdinalIgnoreCase);
            var operatorReconnected = string.Equals(current.Mode, "operator-offline", StringComparison.OrdinalIgnoreCase);
            var playbackEnabled = clearSelection ? false : nextMode == "recording"
                ? true
                : (nextMode is "ready" or "preparing") || selectionChanged
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
                SourceType = isLive ? SessionKeyModes.Live : SessionKeyModes.Recorded,
                Revision = current.Revision + 1,
                CommunicationStatus = current.VideoId.Length > 0 && selectionChanged
                    ? new RemoteCommunicationStatus()
                    : current.CommunicationStatus ?? new RemoteCommunicationStatus(),
                OperatorInstanceId = command.OperatorInstanceId,
                OperatorGeneration = command.OperatorGeneration,
                OperatorSequence = command.OperatorSequence,
                OperatorLeaseExpiresAtUnixMs = nowUnixMs + (long)OperatorLeaseDuration.TotalMilliseconds,
                VideoId = clearSelection ? "" : command.VideoId,
                VideoFileName = isLive ? "Live feed" : selectedVideo?.FileName ?? "",
                FramesPerSecond = storedFps is > 0 ? storedFps : command.FramesPerSecond,
                PositionSeconds = command.PositionSeconds,
                TimelinePositionSeconds = command.TimelinePositionSeconds,
                TimelineDurationSeconds = command.TimelineDurationSeconds,
                IsPlaying = playbackEnabled && command.IsPlaying,
                PlaybackEnabled = playbackEnabled,
                PlaybackRate = command.PlaybackRate,
                PlaybackDiscontinuity = Math.Max(0, command.PlaybackDiscontinuity),
                Mode = nextMode,
                ReviewStartedAtUnixMs = (nextMode is "recording" or "ready" or "preparing") || selectionChanged
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

            // Serialize media lifecycle with the accepted command. Once that
            // lifecycle begins, an HTTP disconnect cannot abandon its state.
            if (beforeCommit is not null)
            {
                await beforeCommit(current, next);
                cancellationToken = CancellationToken.None;
                next.UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                next.OperatorLeaseExpiresAtUnixMs = next.UpdatedAtUnixMs + (long)OperatorLeaseDuration.TotalMilliseconds;
            }
            await WriteJsonAtomicAsync(GetPlaybackStatePath(sessionCode), next, cancellationToken);
            Broadcast(sessionCode, next);
        }
        finally
        {
            gate.Release();
        }

        return next;
    }

    internal async Task WithPlaybackStateAsync(string sessionCode, Func<PlaybackState, Task> action)
    {
        sessionCode = RequireSessionCode(sessionCode);
        var gate = GetSessionLock(sessionCode);
        await gate.WaitAsync();
        try { await action(await ReadPlaybackStateNoLockAsync(sessionCode, CancellationToken.None)); }
        finally { gate.Release(); }
    }

    public async Task ExpireOperatorLeasesAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_storageRoot)) return;
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
                var expiresAtUnixMs = Math.Min(
                    current.OperatorLeaseExpiresAtUnixMs > 0
                        ? current.OperatorLeaseExpiresAtUnixMs
                        : long.MaxValue,
                    current.UpdatedAtUnixMs + (long)OperatorLeaseDuration.TotalMilliseconds);
                if (string.IsNullOrWhiteSpace(current.OperatorInstanceId) ||
                    string.Equals(current.Mode, "operator-offline", StringComparison.OrdinalIgnoreCase) ||
                    expiresAtUnixMs > nowUnixMs)
                    continue;

                // An active live recording ends with Stop/Next, not with a
                // control lease. The independent storage monitor still caps
                // capture. Abandoned setup receives a finite grace period.
                if (current.SourceType == SessionKeyModes.Live &&
                    (current.Mode == "recording" ||
                     (current.Mode == "preparing" &&
                      nowUnixMs - expiresAtUnixMs < LivePreparingDisconnectGrace.TotalMilliseconds)))
                    continue;

                current.Revision++;
                current.IsPlaying = false;
                current.PlaybackEnabled = false;
                current.Mode = "operator-offline";
                current.UpdatedAtUnixMs = nowUnixMs;
                await WriteJsonAtomicAsync(GetPlaybackStatePath(sessionCode), current, cancellationToken);
                Broadcast(sessionCode, current);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    public RemotePlaybackSubscription Subscribe(string sessionCode, string? viewerRole = null)
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
            _ => new ConcurrentDictionary<Guid, RemotePlaybackSubscriber>());
        sessionSubscribers[id] = new RemotePlaybackSubscriber(channel, RemoteViewerRoles.Normalize(viewerRole));
        return new RemotePlaybackSubscription(id.ToString("N"), channel.Reader,
            () => RemoveSubscriber(sessionCode, id));
    }

    public async Task<PlaybackState> SetCommunicationStatusAsync(
        string sessionCode, string viewerSessionId, string indicator, bool active,
        CancellationToken cancellationToken)
    {
        sessionCode = RequireSessionCode(sessionCode);
        if (!Guid.TryParseExact(viewerSessionId, "N", out var subscriberId) ||
            !_subscribers.TryGetValue(sessionCode, out var subscribers) ||
            !subscribers.TryGetValue(subscriberId, out var subscriber))
            throw new UnauthorizedAccessException("Reconnect the viewer before changing a status.");

        var allowedRoles = indicator switch
        {
            "judges-ready" => new[] { "referee", "data-specialist" },
            "tech-panel-ready" => new[] { "technical-controller", "data-specialist" },
            "competitor-scored" => new[] { "data-specialist" },
            _ => throw new ArgumentException("Unknown status indicator.", nameof(indicator))
        };
        if (!allowedRoles.Contains(subscriber.Role, StringComparer.Ordinal))
            throw new UnauthorizedAccessException("This viewer role cannot change that status.");

        var gate = GetSessionLock(sessionCode);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!_subscribers.TryGetValue(sessionCode, out subscribers) ||
                !subscribers.TryGetValue(subscriberId, out var currentSubscriber) ||
                !ReferenceEquals(subscriber, currentSubscriber))
                throw new UnauthorizedAccessException("Reconnect the viewer before changing a status.");

            var state = await ReadPlaybackStateNoLockAsync(sessionCode, cancellationToken);
            var status = state.CommunicationStatus ?? new RemoteCommunicationStatus();
            var previous = indicator switch
            {
                "judges-ready" => status.JudgesReady,
                "tech-panel-ready" => status.TechPanelReady,
                _ => status.CompetitorScored
            };
            if (previous == active) return state;
            switch (indicator)
            {
                case "judges-ready": status.JudgesReady = active; break;
                case "tech-panel-ready": status.TechPanelReady = active; break;
                case "competitor-scored":
                    status.CompetitorScored = active;
                    status.CompetitorScoredActivationId = active ? Guid.NewGuid() : null;
                    status.CompetitorScoredExpiresAtUnixMs = active
                        ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 15_000
                        : 0;
                    break;
            }
            state.CommunicationStatus = status;
            state.Revision++;
            await WriteJsonAtomicAsync(GetPlaybackStatePath(sessionCode), state, cancellationToken);
            Broadcast(sessionCode, state);
            if (indicator == "competitor-scored" && active && status.CompetitorScoredActivationId is Guid activationId)
                ScheduleCompetitorScoredExpiration(sessionCode, activationId, status.CompetitorScoredExpiresAtUnixMs);
            if (indicator == "competitor-scored" && !active)
                _competitorScoredExpiryJobs.TryRemove(sessionCode, out _);
            return state;
        }
        finally { gate.Release(); }
    }

    private void ScheduleCompetitorScoredExpiration(string sessionCode, Guid activationId, long expiresAtUnixMs)
    {
        if (_competitorScoredExpiryJobs.TryGetValue(sessionCode, out var currentJob) && currentJob == activationId)
            return;
        _competitorScoredExpiryJobs[sessionCode] = activationId;
        _ = ExpireCompetitorScoredAsync(sessionCode, activationId, expiresAtUnixMs);
    }

    private async Task ExpireCompetitorScoredAsync(string sessionCode, Guid activationId, long expiresAtUnixMs)
    {
        try
        {
            var remainingMilliseconds = Math.Max(0, expiresAtUnixMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (remainingMilliseconds > 0) await Task.Delay(TimeSpan.FromMilliseconds(remainingMilliseconds));
            var gate = GetSessionLock(sessionCode);
            await gate.WaitAsync();
            try
            {
                var state = await ReadPlaybackStateNoLockAsync(sessionCode, CancellationToken.None);
                var status = state.CommunicationStatus ?? new RemoteCommunicationStatus();
                if (!status.CompetitorScored || status.CompetitorScoredActivationId != activationId ||
                    status.CompetitorScoredExpiresAtUnixMs != expiresAtUnixMs)
                {
                    if (_competitorScoredExpiryJobs.TryGetValue(sessionCode, out var currentJob) && currentJob == activationId)
                        _competitorScoredExpiryJobs.TryRemove(sessionCode, out _);
                    return;
                }
                status.CompetitorScored = false;
                status.CompetitorScoredActivationId = null;
                status.CompetitorScoredExpiresAtUnixMs = 0;
                state.CommunicationStatus = status;
                state.Revision++;
                await WriteJsonAtomicAsync(GetPlaybackStatePath(sessionCode), state, CancellationToken.None);
                Broadcast(sessionCode, state);
                _competitorScoredExpiryJobs.TryRemove(sessionCode, out _);
            }
            finally { gate.Release(); }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to automatically clear Competitor Scored for session {SessionCode}.", sessionCode);
        }
    }

    public async Task<RemoteConnectionStatus> GetRemoteConnectionStatusAsync(
        string sessionCode, CancellationToken cancellationToken = default)
    {
        sessionCode = RequireSessionCode(sessionCode);
        var state = await GetPlaybackStateAsync(sessionCode, cancellationToken);
        var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var vroConnected = !string.IsNullOrWhiteSpace(state.OperatorInstanceId) &&
            !string.Equals(state.Mode, "operator-offline", StringComparison.OrdinalIgnoreCase) &&
            state.OperatorLeaseExpiresAtUnixMs > nowUnixMs;
        var roles = _subscribers.TryGetValue(sessionCode, out var subscribers)
            ? subscribers.Values
                .GroupBy(subscriber => subscriber.Role, StringComparer.Ordinal)
                .OrderBy(group => RemoteViewerRoles.SortOrder(group.Key))
                .Select(group => new RemoteViewerRoleCount(group.Key, group.Count()))
                .ToArray()
            : Array.Empty<RemoteViewerRoleCount>();
        return new RemoteConnectionStatus(vroConnected, roles.Sum(role => role.Count), roles);
    }

    private void Broadcast(string sessionCode, PlaybackState state)
    {
        if (!_subscribers.TryGetValue(sessionCode, out var subscribers)) return;
        foreach (var subscriber in subscribers.Values)
            subscriber.Channel.Writer.TryWrite(state);
    }

    private void RemoveSubscriber(string sessionCode, Guid id)
    {
        if (!_subscribers.TryGetValue(sessionCode, out var subscribers)) return;
        if (subscribers.TryRemove(id, out var subscriber)) subscriber.Channel.Writer.TryComplete();
        if (subscribers.IsEmpty) _subscribers.TryRemove(sessionCode, out _);
    }

    private sealed record RemotePlaybackSubscriber(Channel<PlaybackState> Channel, string Role);

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

    private RemoteUploadStatus GetUploadStatusNoLock(
        string sessionCode, RemoteUploadManifest manifest, double? transcodePercent = null)
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
            VideoId = manifest.VideoId,
            SourceVideoId = manifest.SourceVideoId,
            TranscodeMode = manifest.TranscodeMode,
            TranscodePercent = transcodePercent
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
            "preparing" => "preparing",
            _ => "paused"
        };
}

public sealed class RemoteVideoDescriptor
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTimeOffset UploadedAtUtc { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? FramesPerSecond { get; set; }
    public long? VideoBitrateBitsPerSecond { get; set; }
    public string FieldOrder { get; set; } = "";
    public GopMetadata? Gop { get; set; }
}

public sealed record ServerStorageStatus(long TotalBytes, long AvailableBytes, long RevueUsedBytes)
{
    public long SpaceAvailableToRevueBytes => Math.Min(
        TotalBytes,
        RevueUsedBytes > long.MaxValue - AvailableBytes ? long.MaxValue : AvailableBytes + RevueUsedBytes);
    public long MaximumStorageBytes => SpaceAvailableToRevueBytes / 10 * 9 +
        SpaceAvailableToRevueBytes % 10 * 9 / 10;
    public long UsedBytes => RevueUsedBytes;
    public long AvailableWithinLimitBytes => Math.Max(0, MaximumStorageBytes - UsedBytes);
}

public sealed class BeginRemoteUploadRequest
{
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public long LastModifiedUnixMs { get; set; }
}

public sealed class VideoTranscodeRequest
{
    public string Mode { get; set; } = "";
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
    public string SourceVideoId { get; set; } = "";
    public string TranscodeMode { get; set; } = "";
    public double? TranscodePercent { get; set; }
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
    public string SourceVideoId { get; set; } = "";
    public string TranscodeMode { get; set; } = "";
}

public static class RemoteUploadStages
{
    public const string Uploading = "uploading";
    public const string Processing = "processing";
    public const string Transcoding = "transcoding";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

public class PlaybackCommand
{
    public string SourceType { get; set; } = "Recorded";
    public string OperatorInstanceId { get; set; } = "";
    public long OperatorGeneration { get; set; }
    public long OperatorSequence { get; set; }
    public string VideoId { get; set; } = "";
    public string VideoFileName { get; set; } = "";
    public double PositionSeconds { get; set; }
    public double TimelinePositionSeconds { get; set; }
    public double TimelineDurationSeconds { get; set; }
    public double? FramesPerSecond { get; set; }
    public int? LiveDelaySeconds { get; set; }
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
    public RemoteCommunicationStatus CommunicationStatus { get; set; } = new();
    public long UpdatedAtUnixMs { get; set; }
    public long ReviewStartedAtUnixMs { get; set; }
    public bool PlaybackEnabled { get; set; }
    public bool OperatorConnected { get; set; }
    public long OperatorLeaseExpiresAtUnixMs { get; set; }
}

public sealed class RemoteCommunicationStatus
{
    public bool JudgesReady { get; set; }
    public bool TechPanelReady { get; set; }
    public bool CompetitorScored { get; set; }
    public Guid? CompetitorScoredActivationId { get; set; }
    public long CompetitorScoredExpiresAtUnixMs { get; set; }
}

public sealed record SetRemoteCommunicationStatusRequest(
    string ViewerSessionId, string Indicator, bool Active);

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

    public RemotePlaybackSubscription(string id, ChannelReader<PlaybackState> reader, Action dispose)
    {
        Id = id;
        Reader = reader;
        _dispose = dispose;
    }

    public string Id { get; }
    public ChannelReader<PlaybackState> Reader { get; }
    public void Dispose() => _dispose();
}

public static class RemoteViewerRoles
{
    private static readonly string[] Values =
    [
        "technical-controller", "technical-specialist-1", "technical-specialist-2",
        "judging", "referee", "data-specialist", "announcer",
        "data-input-operator", "video-replay-operator", "unknown"
    ];

    public static string Normalize(string? role)
    {
        role = (role ?? "").Trim().ToLowerInvariant();
        return Values.Contains(role, StringComparer.Ordinal) ? role : "unknown";
    }

    public static int SortOrder(string role)
    {
        var index = Array.IndexOf(Values, role);
        return index < 0 ? Values.Length : index;
    }
}

public sealed record RemoteViewerRoleCount(string Role, int Count);
public sealed record RemoteConnectionStatus(
    bool VroConnected, int RemoteClientCount, RemoteViewerRoleCount[] Roles);
