using System.Collections.Concurrent;
using ReVueVRO.Models;

namespace ReVueVRO.Services;

public sealed class RemoteDownloadStore
{
    private readonly ConcurrentDictionary<string, RemoteDownloadJob> _jobs = new();
    private readonly SemaphoreSlim _slots = new(8, 8);
    private readonly object _startGate = new();
    private long _nextSequence;

    public RemoteDownloadJob Start(AppConfig cfg, RemoteVideoDescriptor video, RemotePlaybackManager remote)
    {
        RemoteDownloadJob job;
        lock (_startGate)
        {
            var existing = _jobs.Values.FirstOrDefault(candidate =>
                candidate.SessionCode == cfg.RemoteSessionCode && candidate.VideoId == video.Id && !candidate.IsTerminal);
            if (existing != null) return existing;
            job = new RemoteDownloadJob
            {
                JobId = Guid.NewGuid().ToString("N"), SessionCode = cfg.RemoteSessionCode,
                VideoId = video.Id, FileName = video.FileName, TotalBytes = video.SizeBytes,
                Sequence = ++_nextSequence
            };
            _jobs[job.JobId] = job;
        }
        _ = RunAsync(job, cfg, video, remote);
        return job;
    }

    public RemoteDownloadJob? Get(string id) => _jobs.TryGetValue(id, out var job) ? job : null;
    public IReadOnlyDictionary<string, RemoteDownloadJob> LatestFor(string code) => _jobs.Values
        .Where(job => job.SessionCode == code)
        .GroupBy(job => job.VideoId, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.MaxBy(job => job.Sequence)!, StringComparer.OrdinalIgnoreCase);
    public bool Cancel(string id) { var job = Get(id); if (job == null) return false; job.Cancellation.Cancel(); return true; }

    private async Task RunAsync(RemoteDownloadJob job, AppConfig cfg, RemoteVideoDescriptor video, RemotePlaybackManager remote)
    {
        try
        {
            job.Status = "queued";
            await _slots.WaitAsync(job.Cancellation.Token);
            try
            {
                job.Status = "downloading";
                var destination = AppPaths.GetRemoteVideoCachePath(job.SessionCode, job.VideoId);
                var progress = new DownloadProgress(job);
                for (var attempt = 1; ; attempt++)
                {
                    job.DownloadedBytes = 0;
                    try
                    {
                        await remote.DownloadVideoAsync(cfg.RemoteHostUrl, cfg.RemoteSessionCode, video, destination, progress, job.Cancellation.Token);
                        break;
                    }
                    catch (Exception ex) when (attempt < 3 && !job.Cancellation.IsCancellationRequested &&
                                               (ex is HttpRequestException || ex is IOException))
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(attempt * 500), job.Cancellation.Token);
                    }
                }
                job.DownloadedBytes = job.TotalBytes;
                RemoteVideoCacheMaintenance.Cleanup(
                    cfg.RemoteVideoCacheExpirationHours,
                    cfg.RemoteVideoCacheMaximumFiles);
                if (!File.Exists(destination) || new FileInfo(destination).Length == 0)
                    throw new InvalidOperationException("The downloaded video was removed from the local cache. Check the Remote video cache settings.");
                job.Status = "downloaded";
            }
            finally { _slots.Release(); }
        }
        catch (OperationCanceledException) { job.Status = "cancelled"; }
        catch (Exception ex) { job.Status = "failed"; job.Error = ex.Message; }
    }

    private sealed class DownloadProgress(RemoteDownloadJob job) : IProgress<(long Bytes, long Total)>
    {
        public void Report((long Bytes, long Total) value)
        {
            job.DownloadedBytes = value.Bytes;
            job.TotalBytes = value.Total;
        }
    }
}

public sealed class RemoteDownloadJob
{
    public string JobId { get; set; } = "";
    public string SessionCode { get; set; } = "";
    public string VideoId { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Status { get; set; } = "queued";
    public long DownloadedBytes { get; set; }
    public long TotalBytes { get; set; }
    public string Error { get; set; } = "";
    internal long Sequence { get; set; }
    public double ProgressPercent => TotalBytes <= 0 ? 0 : Math.Clamp(DownloadedBytes * 100d / TotalBytes, 0, 100);
    public bool IsTerminal => Status is "downloaded" or "cancelled" or "failed";
    internal CancellationTokenSource Cancellation { get; } = new();
}
