using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ReVueRemote;

public sealed class VideoProcessingService : BackgroundService
{
    private readonly Channel<PendingVideoProcessing> _queue = Channel.CreateUnbounded<PendingVideoProcessing>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.Ordinal);
    private readonly RemoteSessionStore _store;
    private readonly ILogger<VideoProcessingService> _logger;

    public VideoProcessingService(RemoteSessionStore store, ILogger<VideoProcessingService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public void Queue(string sessionCode, string uploadId)
    {
        var key = sessionCode + ":" + uploadId;
        if (!_queued.TryAdd(key, 0)) return;
        if (!_queue.Writer.TryWrite(new PendingVideoProcessing(sessionCode, uploadId)))
            _queued.TryRemove(key, out _);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            foreach (var pending in await _store.ListPendingVideoProcessingAsync(stoppingToken))
                Queue(pending.SessionCode, pending.UploadId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not recover pending video-processing jobs.");
        }

        await foreach (var pending in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            var key = pending.SessionCode + ":" + pending.UploadId;
            try
            {
                await _store.ProcessPreparedUploadAsync(
                    pending.SessionCode,
                    pending.UploadId,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Unexpected failure while processing upload {UploadId} for Rink ID {SessionCode}.",
                    pending.UploadId,
                    pending.SessionCode);
            }
            finally
            {
                _queued.TryRemove(key, out _);
            }
        }
    }
}

public sealed record PendingVideoProcessing(string SessionCode, string UploadId);
