using System.Collections.Concurrent;
using ReVueVRO.Models;

namespace ReVueVRO.Services;

public sealed class RemoteDownloadStore
{
    private readonly ConcurrentDictionary<string, RemoteDownloadJob> _jobs = new();
    private readonly SemaphoreSlim _slots = new(8, 8);

    public RemoteDownloadJob Start(AppConfig cfg, RemoteVideoDescriptor video, RemotePlaybackManager remote)
    {
        var existing = _jobs.Values.FirstOrDefault(job => job.SessionCode == cfg.RemoteSessionCode && job.VideoId == video.Id && !job.IsTerminal);
        if (existing != null) return existing;
        var job = new RemoteDownloadJob
        {
            JobId = Guid.NewGuid().ToString("N"), SessionCode = cfg.RemoteSessionCode,
            VideoId = video.Id, FileName = video.FileName, TotalBytes = video.SizeBytes
        };
        _jobs[job.JobId] = job;
        _ = RunAsync(job, cfg, video, remote);
        return job;
    }

    public RemoteDownloadJob? Get(string id) => _jobs.TryGetValue(id, out var job) ? job : null;
    public IReadOnlyList<RemoteDownloadJob> ActiveFor(string code) => _jobs.Values.Where(job => job.SessionCode == code && !job.IsTerminal).ToList();
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
                var progress = new Progress<(long Bytes, long Total)>(value => { job.DownloadedBytes = value.Bytes; job.TotalBytes = value.Total; });
                await remote.DownloadVideoAsync(cfg.RemoteHostUrl, cfg.RemoteSessionCode, video, destination, progress, job.Cancellation.Token);
                job.DownloadedBytes = job.TotalBytes;
                job.Status = "downloaded";
                RemoteVideoCacheMaintenance.Cleanup(
                    cfg.RemoteVideoCacheExpirationHours,
                    cfg.RemoteVideoCacheMaximumFiles);
            }
            finally { _slots.Release(); }
        }
        catch (OperationCanceledException) { job.Status = "cancelled"; }
        catch (Exception ex) { job.Status = "failed"; job.Error = ex.Message; }
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
    public double ProgressPercent => TotalBytes <= 0 ? 0 : Math.Clamp(DownloadedBytes * 100d / TotalBytes, 0, 100);
    public bool IsTerminal => Status is "downloaded" or "cancelled" or "failed";
    internal CancellationTokenSource Cancellation { get; } = new();
}
