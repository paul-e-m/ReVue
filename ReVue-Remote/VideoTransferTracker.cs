using System.Collections.Concurrent;
using System.Diagnostics;

namespace ReVueRemote;

public sealed class VideoTransferTracker
{
    private readonly ConcurrentDictionary<string, long> _transferredBytes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _sessionTransferredBytes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RateGate> _rateGates = new(StringComparer.Ordinal);
    private long _reservations;
    private const int MaximumReadBytes = 64 * 1024;

    public void Record(string sessionCode, string videoId, string viewerId, long byteCount)
    {
        if (byteCount <= 0) return;
        _transferredBytes.AddOrUpdate(Key(sessionCode, videoId, viewerId), byteCount, (_, total) => total + byteCount);
        RecordSession(sessionCode, viewerId, byteCount);
    }

    public void RecordSession(string sessionCode, string viewerId, long byteCount)
    {
        if (byteCount <= 0) return;
        _sessionTransferredBytes.AddOrUpdate(Key(sessionCode, viewerId), byteCount, (_, total) => total + byteCount);
    }

    public long GetSessionTransferredBytes(string sessionCode, string viewerId)
        => _sessionTransferredBytes.TryGetValue(Key(sessionCode, viewerId), out var total) ? total : 0;

    public long GetTransferredBytes(string sessionCode, string videoId, string viewerId)
        => _transferredBytes.TryGetValue(Key(sessionCode, videoId, viewerId), out var total) ? total : 0;

    public async Task ReserveTransferAsync(string viewerKey, int byteCount, CancellationToken cancellationToken)
    {
        if (byteCount <= 0) return;
        var gate = _rateGates.GetOrAdd(viewerKey, _ => new RateGate());
        await gate.ReserveAsync(byteCount, cancellationToken);
        if ((Interlocked.Increment(ref _reservations) & 4095) == 0)
        {
            var cutoff = Stopwatch.GetTimestamp() - 30L * 60 * Stopwatch.Frequency;
            foreach (var entry in _rateGates)
                if (entry.Value.LastReservedAt < cutoff)
                    ((ICollection<KeyValuePair<string, RateGate>>)_rateGates).Remove(entry);
        }
    }

    public static int LimitRead(int requested) => Math.Min(requested, MaximumReadBytes);

    private static string Key(string sessionCode, string videoId, string viewerId)
        => $"{sessionCode}\n{videoId}\n{viewerId}";

    private static string Key(string sessionCode, string viewerId)
        => $"{sessionCode}\n{viewerId}";

    private sealed class RateGate
    {
        private const double BytesPerSecond = 30_000_000d / 8d;
        private readonly object _gate = new();
        private long _nextReadAt;
        private long _lastReservedAt = Stopwatch.GetTimestamp();
        public long LastReservedAt => Interlocked.Read(ref _lastReservedAt);

        public async Task ReserveAsync(int byteCount, CancellationToken cancellationToken)
        {
            long delayTicks;
            lock (_gate)
            {
                var now = Stopwatch.GetTimestamp();
                Interlocked.Exchange(ref _lastReservedAt, now);
                var start = Math.Max(now, _nextReadAt);
                _nextReadAt = start + (long)Math.Ceiling(byteCount * Stopwatch.Frequency / BytesPerSecond);
                delayTicks = _nextReadAt - now;
            }
            await Task.Delay(TimeSpan.FromSeconds(delayTicks / (double)Stopwatch.Frequency), cancellationToken);
        }
    }
}

public sealed class TrackingReadStream(
    Stream inner, Action<int> recordBytes, VideoTransferTracker tracker, string viewerKey) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        count = VideoTransferTracker.LimitRead(count);
        tracker.ReserveTransferAsync(viewerKey, count, CancellationToken.None).GetAwaiter().GetResult();
        var read = inner.Read(buffer, offset, count);
        Record(read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        buffer = buffer[..VideoTransferTracker.LimitRead(buffer.Length)];
        tracker.ReserveTransferAsync(viewerKey, buffer.Length, CancellationToken.None).GetAwaiter().GetResult();
        var read = inner.Read(buffer);
        Record(read);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        count = VideoTransferTracker.LimitRead(count);
        await tracker.ReserveTransferAsync(viewerKey, count, cancellationToken);
        var read = await inner.ReadAsync(buffer, offset, count, cancellationToken);
        Record(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        buffer = buffer[..VideoTransferTracker.LimitRead(buffer.Length)];
        await tracker.ReserveTransferAsync(viewerKey, buffer.Length, cancellationToken);
        var read = await inner.ReadAsync(buffer, cancellationToken);
        Record(read);
        return read;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private void Record(int byteCount)
    {
        if (byteCount > 0) recordBytes(byteCount);
    }
}
