using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace ReVue.Diagnostics;

// All filesystem work, serialization, and redaction happen on one background worker.
// Producers only enqueue bounded text and never wait for disk or queue space.
internal sealed class RuntimeLog : IDisposable
{
    internal sealed record Options
    {
        public int QueueCapacity { get; init; } = 512;
        public long FileBytes { get; init; } = 1024 * 1024;
        public long TotalBytes { get; init; } = 32 * 1024 * 1024;
        public int MaximumFiles { get; init; } = 64;
        public TimeSpan Retention { get; init; } = TimeSpan.FromDays(14);
        public TimeSpan MaintenanceInterval { get; init; } = TimeSpan.FromMinutes(5);
        public TimeSpan RetryInterval { get; init; } = TimeSpan.FromMinutes(1);
        public long MinimumFreeBytes { get; init; } = 250 * 1024 * 1024;
    }

    private sealed record Entry(DateTimeOffset utc, string level, string category, string message, string? exception);
    private const int MaximumTextLength = 2048;
    private static readonly Regex UrlPattern = new(@"\b(?:https?|rtsps?)://[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex SecretPattern = new(@"\b(?:authorization|password|passwd|token|api[-_]?key|secret)\b[""']?\s*[:=]\s*(?:""[^""]*(?:""|$)|'[^']*(?:'|$)|(?:Bearer\s+)?[^\s,;""']+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static RuntimeLog? _current;
    private readonly string _directory;
    private readonly Options _options;
    private readonly Channel<Entry> _queue;
    private readonly Task _worker;
    private readonly string _run = Guid.NewGuid().ToString("N");
    private FileStream? _file;
    private string? _filePath;
    private long _fileBytes;
    private int _part;
    private long _omitted;
    private int _disposed;
    private DateTime _fileDay;
    private long _nextMaintenance;
    private long _retryAt;
    private long _nextSpaceCheck;

    internal RuntimeLog(string directory, Options? options = null)
    {
        _directory = directory;
        _options = options ?? new Options();
        _queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            AllowSynchronousContinuations = false
        });
        _worker = Task.Run(ConsumeAsync);
    }

    public static RuntimeLog? Start(string application, string version)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReVue", application, "logs");
            var log = new RuntimeLog(directory);
            Volatile.Write(ref _current, log);
            Write("Information", "Application", $"Started {application}; version={version}; pid={Environment.ProcessId}; OS={Environment.OSVersion}; runtime={Environment.Version}; run={log._run}");
            return log;
        }
        catch { return null; } // Diagnostics must never prevent startup.
    }

    public static void Write(string level, string category, string message, Exception? exception = null)
    {
        try { Volatile.Read(ref _current)?.Enqueue(level, category, message, exception); }
        catch { } // Includes exception formatting supplied by third-party code.
    }

    internal void Enqueue(string level, string category, string message, Exception? exception = null)
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var entry = new Entry(DateTimeOffset.UtcNow, Limit(level, 16), Limit(category, 128), Limit(message),
                exception == null ? null : Limit($"{exception.GetType().FullName}: {exception.Message}\n{exception.StackTrace}"));
            if (!_queue.Writer.TryWrite(entry)) Interlocked.Increment(ref _omitted);
        }
        catch { Interlocked.Increment(ref _omitted); }
    }

    private static string Limit(string? text, int length = MaximumTextLength) =>
        text == null ? "" : text.Length <= length ? text : text[..length] + " [truncated]";

    private static string Redact(string text)
    {
        // If redaction fails (including a regex timeout), never write the raw text.
        try { return SecretPattern.Replace(UrlPattern.Replace(text, "[url redacted]"), "[secret redacted]"); }
        catch { return "[text omitted: redaction failed]"; }
    }

    private async Task ConsumeAsync()
    {
        try
        {
            do
            {
                try
                {
                    var now = Stopwatch.GetTimestamp();
                    if (now >= _retryAt && now >= _nextMaintenance)
                    {
                        Directory.CreateDirectory(_directory);
                        if (_file != null && _fileDay != DateTime.UtcNow.Date) CloseFile();
                        if (!Cleanup(reserveFile: false)) throw new IOException("Log storage budget unavailable.");
                        _nextMaintenance = After(_options.MaintenanceInterval);
                    }
                    var wrote = false;
                    for (var count = 0; count < _options.QueueCapacity && _queue.Reader.TryRead(out var entry); count++)
                    {
                        if (Stopwatch.GetTimestamp() < _retryAt)
                        {
                            Interlocked.Increment(ref _omitted);
                            continue;
                        }
                        var omitted = Interlocked.Exchange(ref _omitted, 0);
                        if (omitted > 0)
                        {
                            try { Append(new Entry(DateTimeOffset.UtcNow, "Warning", "Logging", $"Omitted {omitted} log entries due to queue pressure or unavailable storage.", null)); }
                            catch
                            {
                                Interlocked.Add(ref _omitted, omitted);
                                throw;
                            }
                        }
                        Append(entry);
                        wrote = true;
                    }
                    if (wrote) _file?.Flush(); // OS buffering; no forced disk sync on the recording thread.
                }
                catch
                {
                    Interlocked.Increment(ref _omitted);
                    CloseFile();
                    _retryAt = After(_options.RetryInterval);
                }
                if (!_queue.Reader.Completion.IsCompleted)
                    await Task.Delay(250).ConfigureAwait(false);
            } while (!_queue.Reader.Completion.IsCompleted);
        }
        catch { } // The background task must not create an unobserved exception.
        finally { CloseFile(); }
    }

    private void Append(Entry entry)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry with
        {
            category = Redact(entry.category), message = Redact(entry.message),
            exception = entry.exception == null ? null : Redact(entry.exception)
        });
        if (bytes.Length + 1 > _options.FileBytes) return;
        if (_file != null && (_fileBytes + bytes.Length + 1 > _options.FileBytes || _fileDay != DateTime.UtcNow.Date))
            CloseFile();
        if (_file == null)
        {
            Directory.CreateDirectory(_directory);
            if (!Cleanup(reserveFile: true)) throw new IOException("Log storage budget unavailable.");
            CheckFreeSpace();
            _fileDay = DateTime.UtcNow.Date;
            _filePath = Path.Combine(_directory, $"revue-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{_run}-{++_part:D4}.jsonl");
            _file = new FileStream(_filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096);
            _fileBytes = 0;
        }
        if (Stopwatch.GetTimestamp() >= _nextSpaceCheck) CheckFreeSpace();
        _file.Write(bytes);
        _file.WriteByte((byte)'\n');
        _fileBytes += bytes.Length + 1;
    }

    private void CheckFreeSpace()
    {
        if (_options.MinimumFreeBytes > 0 && new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_directory))!).AvailableFreeSpace < _options.MinimumFreeBytes)
            throw new IOException("Logging suspended to preserve free disk space.");
        _nextSpaceCheck = After(TimeSpan.FromMinutes(1));
    }

    private bool Cleanup(bool reserveFile)
    {
        var files = new DirectoryInfo(_directory).GetFiles("revue-*.jsonl")
            .OrderBy(file => file.LastWriteTimeUtc).ToList();
        var cutoff = DateTime.UtcNow - _options.Retention;
        long total = files.Sum(file => file.Length);
        var count = files.Count;
        var byteBudget = _options.TotalBytes - (reserveFile ? _options.FileBytes : 0);
        var fileBudget = _options.MaximumFiles - (reserveFile ? 1 : 0);
        foreach (var file in files)
        {
            if (file.FullName == _filePath) continue;
            if (file.LastWriteTimeUtc >= cutoff && total <= byteBudget && count <= fileBudget) continue;
            try
            {
                var size = file.Length;
                file.Delete();
                total -= size;
                count--;
            }
            catch { } // In-use files are left alone. Never delete media or other files.
        }
        return total <= byteBudget && count <= fileBudget;
    }

    private static long After(TimeSpan interval) => Stopwatch.GetTimestamp() + (long)(interval.TotalSeconds * Stopwatch.Frequency);

    private void CloseFile()
    {
        try { _file?.Dispose(); } catch { }
        _file = null;
        _filePath = null;
        _fileBytes = 0;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.CompareExchange(ref _current, null, this);
        _queue.Writer.TryComplete();
        // A hung filesystem cannot hang application shutdown; only the worker owns the file.
        try { _worker.Wait(TimeSpan.FromMilliseconds(750)); } catch { }
    }
}
