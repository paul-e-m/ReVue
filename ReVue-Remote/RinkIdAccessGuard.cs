using System.Text.Json;

namespace ReVueRemote;

public sealed class RinkIdAccessGuard
{
    private static readonly TimeSpan BlockDuration = TimeSpan.FromHours(24);
    private readonly string _statePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private Dictionary<string, RinkIdAccessRecord>? _records;

    public RinkIdAccessGuard(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var configuredRoot = configuration["ReVueRemote:StorageRoot"]?.Trim();
        var storageRoot = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(environment.ContentRootPath, "data", "sessions")
            : Path.GetFullPath(configuredRoot, environment.ContentRootPath);
        _statePath = Path.Combine(storageRoot, "_system", "rink-id-access.json");
    }

    public async Task<RinkIdValidationDecision> ValidateAsync(
        string clientAddress,
        Func<CancellationToken, Task<bool>> validator,
        CancellationToken cancellationToken)
    {
        clientAddress = string.IsNullOrWhiteSpace(clientAddress) ? "unknown" : clientAddress.Trim();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            if (_records!.TryGetValue(clientAddress, out var record))
            {
                if (record.BlockedUntilUtc is DateTimeOffset blockedUntil)
                {
                    if (blockedUntil > now)
                        return RinkIdValidationDecision.Blocked(record.Attempts, SecondsUntil(blockedUntil, now), blockedUntil);

                    // A completed 24-hour block starts a fresh attempt sequence.
                    _records.Remove(clientAddress);
                    record = null;
                    await SaveAsync(cancellationToken);
                }
                else if (record.RetryAfterUtc > now)
                {
                    return RinkIdValidationDecision.Delayed(
                        record.Attempts,
                        SecondsUntil(record.RetryAfterUtc, now),
                        record.Attempts == 9);
                }
            }

            if (await validator(cancellationToken))
            {
                if (_records.Remove(clientAddress)) await SaveAsync(cancellationToken);
                return RinkIdValidationDecision.Valid();
            }

            record ??= new RinkIdAccessRecord();
            record.Attempts++;
            record.UpdatedAtUtc = now;
            if (record.Attempts >= 10)
            {
                record.BlockedUntilUtc = now.Add(BlockDuration);
                record.RetryAfterUtc = record.BlockedUntilUtc.Value;
            }
            else
            {
                record.BlockedUntilUtc = null;
                record.RetryAfterUtc = now.AddSeconds(record.Attempts * 5);
            }
            _records[clientAddress] = record;
            await SaveAsync(cancellationToken);

            return record.BlockedUntilUtc is DateTimeOffset newBlockedUntil
                ? RinkIdValidationDecision.Blocked(record.Attempts, SecondsUntil(newBlockedUntil, now), newBlockedUntil)
                : RinkIdValidationDecision.Delayed(record.Attempts, record.Attempts * 5, record.Attempts == 9);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RinkIdValidationDecision?> GetBlockAsync(
        string clientAddress,
        CancellationToken cancellationToken)
    {
        clientAddress = string.IsNullOrWhiteSpace(clientAddress) ? "unknown" : clientAddress.Trim();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            if (!_records!.TryGetValue(clientAddress, out var record) ||
                record.BlockedUntilUtc is not DateTimeOffset blockedUntil)
                return null;

            var now = DateTimeOffset.UtcNow;
            if (blockedUntil > now)
                return RinkIdValidationDecision.Blocked(record.Attempts, SecondsUntil(blockedUntil, now), blockedUntil);

            _records.Remove(clientAddress);
            await SaveAsync(cancellationToken);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_records != null) return;
        if (!File.Exists(_statePath))
        {
            _records = new Dictionary<string, RinkIdAccessRecord>(StringComparer.Ordinal);
            return;
        }

        try
        {
            await using var stream = File.OpenRead(_statePath);
            _records = await JsonSerializer.DeserializeAsync<Dictionary<string, RinkIdAccessRecord>>(
                stream,
                _jsonOptions,
                cancellationToken) ?? new Dictionary<string, RinkIdAccessRecord>(StringComparer.Ordinal);
            _records = new Dictionary<string, RinkIdAccessRecord>(_records, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            _records = new Dictionary<string, RinkIdAccessRecord>(StringComparer.Ordinal);
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var temporaryPath = _statePath + ".tmp";
        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, _records, _jsonOptions, cancellationToken);
        }
        File.Move(temporaryPath, _statePath, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(_statePath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch { }
        }
    }

    private static int SecondsUntil(DateTimeOffset target, DateTimeOffset now)
        => Math.Max(1, (int)Math.Ceiling((target - now).TotalSeconds));
}

public sealed class RinkIdAccessRecord
{
    public int Attempts { get; set; }
    public DateTimeOffset RetryAfterUtc { get; set; }
    public DateTimeOffset? BlockedUntilUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed record RinkIdValidationDecision(
    bool IsValid,
    bool IsBlocked,
    int Attempts,
    int RetryAfterSeconds,
    bool FinalAttempt,
    DateTimeOffset? BlockedUntilUtc)
{
    public static RinkIdValidationDecision Valid() => new(true, false, 0, 0, false, null);
    public static RinkIdValidationDecision Delayed(int attempts, int seconds, bool finalAttempt)
        => new(false, false, attempts, seconds, finalAttempt, null);
    public static RinkIdValidationDecision Blocked(int attempts, int seconds, DateTimeOffset blockedUntilUtc)
        => new(false, true, attempts, seconds, false, blockedUntilUtc);
}
