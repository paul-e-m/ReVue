namespace ReVueRemote;

public sealed class RemotePlaybackLeaseService : BackgroundService
{
    private readonly RemoteSessionStore _store;
    private readonly LiveStreamService _live;
    private readonly ILogger<RemotePlaybackLeaseService> _logger;

    public RemotePlaybackLeaseService(
        RemoteSessionStore store,
        LiveStreamService live,
        ILogger<RemotePlaybackLeaseService> logger)
    {
        _store = store;
        _live = live;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await _store.ExpireOperatorLeasesAsync(stoppingToken);
                await _live.StopInactiveEventsAsync();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not expire stale Remote playback leases.");
            }
        }
    }
}
