using System.Collections.Concurrent;

namespace ReVueVRO.Services;

public sealed class RemoteUploadProgressStore
{
    private readonly ConcurrentDictionary<string, RemoteUploadProgress> _jobs = new();

    public RemoteUploadProgress Create(long totalBytes, int totalFiles)
    {
        var job = new RemoteUploadProgress
        {
            JobId = Guid.NewGuid().ToString("N"),
            TotalBytes = Math.Max(0, totalBytes),
            TotalFiles = Math.Max(0, totalFiles),
            Status = "queued"
        };
        _jobs[job.JobId] = job;
        return job;
    }

    public RemoteUploadProgress? Get(string jobId)
        => _jobs.TryGetValue(jobId ?? "", out var job) ? job : null;
}

public sealed class RemoteUploadProgress
{
    private readonly object _gate = new();

    public string JobId { get; init; } = "";
    public long TotalBytes { get; init; }
    public int TotalFiles { get; init; }
    public long BytesSent { get; private set; }
    public int FilesCompleted { get; private set; }
    public string CurrentFile { get; private set; } = "";
    public string Status { get; set; } = "queued";
    public string Error { get; set; } = "";

    public void Report(long bytesSent, int filesCompleted, string currentFile)
    {
        lock (_gate)
        {
            BytesSent = Math.Clamp(bytesSent, 0, TotalBytes);
            FilesCompleted = Math.Clamp(filesCompleted, 0, TotalFiles);
            CurrentFile = currentFile ?? "";
        }
    }

    public object Snapshot()
    {
        lock (_gate)
        {
            return new
            {
                jobId = JobId,
                status = Status,
                bytesSent = BytesSent,
                totalBytes = TotalBytes,
                filesCompleted = FilesCompleted,
                totalFiles = TotalFiles,
                currentFile = CurrentFile,
                error = Error
            };
        }
    }
}
