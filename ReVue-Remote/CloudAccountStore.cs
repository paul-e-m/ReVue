using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;

namespace ReVueRemote;

public sealed partial class CloudAccountStore
{
    private readonly string _systemRoot;
    private readonly string _usersPath;
    private readonly string _keysPath;
    private readonly IDataProtector _publishPasswordProtector;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public CloudAccountStore(
        IConfiguration configuration, IWebHostEnvironment environment,
        IDataProtectionProvider dataProtectionProvider)
    {
        var configuredRoot = configuration["ReVueRemote:StorageRoot"]?.Trim();
        var storageRoot = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(environment.ContentRootPath, "data", "sessions")
            : Path.GetFullPath(configuredRoot, environment.ContentRootPath);
        _systemRoot = Path.Combine(storageRoot, "_system");
        _usersPath = Path.Combine(_systemRoot, "users.json");
        _keysPath = Path.Combine(_systemRoot, "keys.json");
        _publishPasswordProtector = dataProtectionProvider.CreateProtector(
            "ReVueRemote.LivePublishPassword.v1");
    }

    [GeneratedRegex("^[A-Z0-9]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyRegex();

    public static string NormalizeEmail(string? value) => (value ?? "").Trim().ToLowerInvariant();
    public static string NormalizeKey(string? value) => (value ?? "").Trim().ToUpperInvariant();
    public static bool IsValidKeyFormat(string? value) => KeyRegex().IsMatch(NormalizeKey(value));

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_systemRoot);
            TryRestrictPermissions(_systemRoot, isDirectory: true);
            var users = await ReadAsync<List<CloudUser>>(_usersPath, cancellationToken) ?? [];
            if (users.Count == 0)
            {
                users.Add(new CloudUser
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Email = "admin@admin.com",
                    FirstName = "Initial",
                    LastName = "Administrator",
                    Role = CloudRoles.Admin,
                    PasswordHash = HashPassword("admin"),
                    MustChangePassword = true,
                    CreatedAtUtc = DateTimeOffset.UtcNow
                });
                await WriteAsync(_usersPath, users, cancellationToken);
            }
            if (!File.Exists(_keysPath)) await WriteAsync(_keysPath, new List<SessionKeyRecord>(), cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<LoginResult> AuthenticateAsync(string email, string password, CancellationToken cancellationToken)
    {
        email = NormalizeEmail(email);
        if (password is null || password.Length > 256) return LoginResult.Invalid();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var users = await ReadUsersAsync(cancellationToken);
            var user = users.FirstOrDefault(item => string.Equals(item.Email, email, StringComparison.OrdinalIgnoreCase));
            if (user == null) return LoginResult.Invalid();
            if (user.IsLocked) return LoginResult.LockedOut();
            var now = DateTimeOffset.UtcNow;
            if (user.RetryAfterUtc is DateTimeOffset retryAfter && retryAfter > now)
                return LoginResult.Waiting((int)Math.Ceiling((retryAfter - now).TotalSeconds));

            if (!VerifyPassword(password, user.PasswordHash ?? ""))
            {
                user.FailedLoginAttempts++;
                if (user.FailedLoginAttempts >= 10)
                {
                    user.IsLocked = true;
                    user.RetryAfterUtc = null;
                    await WriteAsync(_usersPath, users, cancellationToken);
                    return LoginResult.LockedOut();
                }
                if (user.FailedLoginAttempts >= 3)
                    user.RetryAfterUtc = now.AddSeconds((user.FailedLoginAttempts - 2) * 30);
                await WriteAsync(_usersPath, users, cancellationToken);
                return LoginResult.Invalid(user.RetryAfterUtc is null
                    ? 0
                    : (int)Math.Ceiling((user.RetryAfterUtc.Value - now).TotalSeconds));
            }

            user.FailedLoginAttempts = 0;
            user.RetryAfterUtc = null;
            user.LastLoginUtc = now;
            await WriteAsync(_usersPath, users, cancellationToken);
            return LoginResult.Success(user.CopyPublic());
        }
        finally { _gate.Release(); }
    }

    public async Task<CloudUser?> FindUserAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return (await ReadUsersAsync(cancellationToken)).FirstOrDefault(user => user.Id == id)?.CopyPublic(); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<CloudUser>> ListUsersAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return (await ReadUsersAsync(cancellationToken)).Select(user => user.CopyPublic()).OrderBy(user => user.Email).ToList(); }
        finally { _gate.Release(); }
    }

    public async Task<CloudUser> CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken)
    {
        var email = RequireEmail(request.Email);
        var role = CloudRoles.Normalize(request.Role);
        RequirePassword(request.Password);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var users = await ReadUsersAsync(cancellationToken);
            if (users.Any(user => string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A user with that email address already exists.");
            var user = new CloudUser
            {
                Id = Guid.NewGuid().ToString("N"), Email = email,
                FirstName = (request.FirstName ?? "").Trim(), LastName = (request.LastName ?? "").Trim(),
                Role = role, PasswordHash = HashPassword(request.Password), MustChangePassword = true,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            users.Add(user);
            await WriteAsync(_usersPath, users, cancellationToken);
            return user.CopyPublic();
        }
        finally { _gate.Release(); }
    }

    public async Task<CloudUser> UpdateProfileAsync(string id, UpdateProfileRequest request, CancellationToken cancellationToken)
    {
        var email = RequireEmail(request.Email);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var users = await ReadUsersAsync(cancellationToken);
            var user = users.SingleOrDefault(item => item.Id == id) ?? throw new KeyNotFoundException("User not found.");
            if (users.Any(item => item.Id != id && string.Equals(item.Email, email, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A user with that email address already exists.");
            user.Email = email;
            user.FirstName = (request.FirstName ?? "").Trim();
            user.LastName = (request.LastName ?? "").Trim();
            await WriteAsync(_usersPath, users, cancellationToken);
            return user.CopyPublic();
        }
        finally { _gate.Release(); }
    }

    public async Task ChangePasswordAsync(string id, string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        RequirePassword(newPassword);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var users = await ReadUsersAsync(cancellationToken);
            var user = users.SingleOrDefault(item => item.Id == id) ?? throw new KeyNotFoundException("User not found.");
            if (!VerifyPassword(currentPassword, user.PasswordHash ?? "")) throw new InvalidOperationException("The current password is incorrect.");
            user.PasswordHash = HashPassword(newPassword);
            user.MustChangePassword = false;
            user.FailedLoginAttempts = 0; user.RetryAfterUtc = null;
            await WriteAsync(_usersPath, users, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task AdminUpdateUserAsync(string id, AdminUpdateUserRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var users = await ReadUsersAsync(cancellationToken);
            var user = users.SingleOrDefault(item => item.Id == id) ?? throw new KeyNotFoundException("User not found.");
            var nextRole = CloudRoles.Normalize(request.Role);
            if (user.Role == CloudRoles.Admin && nextRole != CloudRoles.Admin && users.Count(item => item.Role == CloudRoles.Admin) <= 1)
                throw new InvalidOperationException("The final administrator cannot be changed to a Regular user.");
            if (user.Role != nextRole) { user.Role = nextRole; user.AuthVersion++; }
            if (!string.IsNullOrWhiteSpace(request.NewPassword))
            {
                RequirePassword(request.NewPassword);
                user.PasswordHash = HashPassword(request.NewPassword);
                user.MustChangePassword = true;
                user.AuthVersion++;
            }
            if (request.Unlock)
            {
                user.IsLocked = false; user.FailedLoginAttempts = 0; user.RetryAfterUtc = null;
            }
            await WriteAsync(_usersPath, users, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteUserAsync(string id, string actingUserId, CancellationToken cancellationToken)
    {
        if (id == actingUserId) throw new InvalidOperationException("You cannot delete your own account.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var users = await ReadUsersAsync(cancellationToken);
            var user = users.SingleOrDefault(item => item.Id == id) ?? throw new KeyNotFoundException("User not found.");
            if (user.Role == CloudRoles.Admin && users.Count(item => item.Role == CloudRoles.Admin) <= 1)
                throw new InvalidOperationException("The final administrator cannot be deleted.");
            users.Remove(user);
            await WriteAsync(_usersPath, users, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ManagedSessionKeyRecord>> ListManagedKeysAsync(
        string userId,
        bool includeAllUsers,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var keys = await ReadKeysAsync(cancellationToken);
            var usersById = (await ReadUsersAsync(cancellationToken))
                .ToDictionary(user => user.Id, user => user.Email, StringComparer.Ordinal);
            return keys
                .Where(key => includeAllUsers || key.CreatedByUserId == userId)
                .OrderBy(key => key.Code)
                .Select(key => new ManagedSessionKeyRecord
                {
                    Code = key.Code,
                    CreatedByUserId = key.CreatedByUserId,
                    CreatedByEmail = usersById.GetValueOrDefault(key.CreatedByUserId) ?? "Unknown user",
                    CreatedAtUtc = key.CreatedAtUtc,
                    Mode = SessionKeyModes.Normalize(key.Mode),
                    HasPublishPassword = !string.IsNullOrEmpty(key.PublishPasswordHash),
                    LiveFeedActive = key.LiveFeedActive
                })
                .ToList();
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> CanManageKeyAsync(
        string code,
        string userId,
        bool isAdmin,
        CancellationToken cancellationToken = default)
    {
        code = NormalizeKey(code);
        if (!IsValidKeyFormat(code)) return false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var key = (await ReadKeysAsync(cancellationToken)).FirstOrDefault(item => item.Code == code);
            return key is not null && (isAdmin || key.CreatedByUserId == userId);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> KeyExistsAsync(string code, CancellationToken cancellationToken = default)
    {
        code = NormalizeKey(code);
        if (!IsValidKeyFormat(code)) return false;
        await _gate.WaitAsync(cancellationToken);
        try { return (await ReadKeysAsync(cancellationToken)).Any(key => key.Code == code); }
        finally { _gate.Release(); }
    }

    public async Task<string?> GetKeyModeAsync(string code, CancellationToken cancellationToken = default)
    {
        code = NormalizeKey(code);
        if (!IsValidKeyFormat(code)) return null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var key = (await ReadKeysAsync(cancellationToken)).FirstOrDefault(item => item.Code == code);
            return key is null ? null : SessionKeyModes.Normalize(key.Mode);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> AuthorizeLivePublisherAsync(string code, string? password, CancellationToken cancellationToken = default)
    {
        code = NormalizeKey(code);
        if (!IsValidKeyFormat(code)) return false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var keys = await ReadKeysAsync(cancellationToken);
            var key = keys.FirstOrDefault(item => item.Code == code);
            if (key is null || SessionKeyModes.Normalize(key.Mode) != SessionKeyModes.Live ||
                !key.LiveFeedActive) return false;
            var authorized = string.IsNullOrEmpty(key.PublishPasswordHash)
                ? string.IsNullOrEmpty(password)
                : VerifyPassword(password ?? "", key.PublishPasswordHash);
            // Migrate pre-reveal passwords the next time their publisher
            // successfully authenticates, when the plaintext is available.
            if (authorized && !string.IsNullOrEmpty(key.PublishPasswordHash) &&
                !string.IsNullOrEmpty(password) &&
                TryUnprotectPublishPassword(key.PublishPasswordProtected) is null)
            {
                key.PublishPasswordProtected = _publishPasswordProtector.Protect(password);
                await WriteAsync(_keysPath, keys, cancellationToken);
            }
            return authorized;
        }
        finally { _gate.Release(); }
    }

    public async Task<ManagedKeyMediaSettings> GetKeyMediaAsync(
        string code, string userId, bool isAdmin, CancellationToken cancellationToken)
    {
        code = NormalizeKey(code);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var key = (await ReadKeysAsync(cancellationToken)).FirstOrDefault(item => item.Code == code)
                ?? throw new KeyNotFoundException("Rink ID not found.");
            if (!isAdmin && key.CreatedByUserId != userId)
                throw new UnauthorizedAccessException("You do not have permission to edit this Rink ID.");
            if (SessionKeyModes.Normalize(key.Mode) != SessionKeyModes.Live)
                throw new InvalidOperationException("Recorded video Rink IDs do not have stream settings.");
            var hasPassword = !string.IsNullOrEmpty(key.PublishPasswordHash);
            var password = TryUnprotectPublishPassword(key.PublishPasswordProtected);
            return new ManagedKeyMediaSettings
            {
                HasPublishPassword = hasPassword,
                PublishPassword = password,
                PasswordCanBeRevealed = !hasPassword || password is not null
            };
        }
        finally { _gate.Release(); }
    }

    public async Task<ManagedSessionKeyRecord> UpdateKeyMediaAsync(
        string code, string? publishPassword, string userId, bool isAdmin,
        CancellationToken cancellationToken)
    {
        code = NormalizeKey(code);
        if (publishPassword is { Length: > 256 })
            throw new InvalidOperationException("The publish password cannot exceed 256 characters.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var keys = await ReadKeysAsync(cancellationToken);
            var key = keys.FirstOrDefault(item => item.Code == code)
                ?? throw new KeyNotFoundException("Rink ID not found.");
            if (!isAdmin && key.CreatedByUserId != userId)
                throw new UnauthorizedAccessException("You do not have permission to edit this Rink ID.");
            if (SessionKeyModes.Normalize(key.Mode) != SessionKeyModes.Live)
                throw new InvalidOperationException("Recorded video Rink IDs do not have stream settings. Rink ID type cannot be changed after creation.");
            if (publishPassword is not null)
            {
                key.PublishPasswordHash = publishPassword.Length == 0 ? "" : HashPassword(publishPassword);
                key.PublishPasswordProtected = publishPassword.Length == 0
                    ? "" : _publishPasswordProtector.Protect(publishPassword);
            }
            await WriteAsync(_keysPath, keys, cancellationToken);
            return new ManagedSessionKeyRecord
            {
                Code = key.Code,
                CreatedByUserId = key.CreatedByUserId,
                CreatedAtUtc = key.CreatedAtUtc,
                Mode = SessionKeyModes.Live,
                HasPublishPassword = !string.IsNullOrEmpty(key.PublishPasswordHash),
                LiveFeedActive = key.LiveFeedActive
            };
        }
        finally { _gate.Release(); }
    }

    public async Task<ManagedSessionKeyRecord> SetLiveFeedActiveAsync(
        string code, bool active, string userId, bool isAdmin,
        CancellationToken cancellationToken)
    {
        code = NormalizeKey(code);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var keys = await ReadKeysAsync(cancellationToken);
            var key = keys.FirstOrDefault(item => item.Code == code)
                ?? throw new KeyNotFoundException("Rink ID not found.");
            if (!isAdmin && key.CreatedByUserId != userId)
                throw new UnauthorizedAccessException("You do not have permission to edit this Rink ID.");
            if (SessionKeyModes.Normalize(key.Mode) != SessionKeyModes.Live)
                throw new InvalidOperationException("Only Live Rink IDs have a live feed status.");
            key.LiveFeedActive = active;
            await WriteAsync(_keysPath, keys, cancellationToken);
            return new ManagedSessionKeyRecord
            {
                Code = key.Code,
                CreatedByUserId = key.CreatedByUserId,
                CreatedAtUtc = key.CreatedAtUtc,
                Mode = SessionKeyModes.Live,
                HasPublishPassword = !string.IsNullOrEmpty(key.PublishPasswordHash),
                LiveFeedActive = key.LiveFeedActive
            };
        }
        finally { _gate.Release(); }
    }

    public async Task<SessionKeyRecord> CreateKeyAsync(
        string code, string mode, string? publishPassword, string userId, CancellationToken cancellationToken)
    {
        code = NormalizeKey(code);
        mode = SessionKeyModes.Require(mode);
        if (!IsValidKeyFormat(code)) throw new InvalidOperationException("The Rink ID must contain exactly six letters or digits.");
        if (publishPassword is { Length: > 256 })
            throw new InvalidOperationException("The publish password cannot exceed 256 characters.");
        if (mode == SessionKeyModes.Live && string.IsNullOrEmpty(publishPassword))
            throw new InvalidOperationException("Enter an RTMP publish password for this Live Rink ID.");
        if (mode == SessionKeyModes.Recorded && !string.IsNullOrEmpty(publishPassword))
            throw new InvalidOperationException("Publish passwords are only available for Live Rink IDs.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var keys = await ReadKeysAsync(cancellationToken);
            if (keys.Any(key => key.Code == code)) throw new InvalidOperationException("That Rink ID already exists.");
            var record = new SessionKeyRecord
            {
                Code = code,
                CreatedByUserId = userId,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Mode = mode,
                PublishPasswordHash = mode == SessionKeyModes.Live && !string.IsNullOrEmpty(publishPassword)
                    ? HashPassword(publishPassword) : "",
                PublishPasswordProtected = mode == SessionKeyModes.Live && !string.IsNullOrEmpty(publishPassword)
                    ? _publishPasswordProtector.Protect(publishPassword) : ""
            };
            keys.Add(record);
            await WriteAsync(_keysPath, keys, cancellationToken);
            return record;
        }
        finally { _gate.Release(); }
    }

    public async Task<SessionKeyRecord> RenameKeyAsync(
        string code,
        string newCode,
        string userId,
        bool isAdmin,
        RemoteSessionStore sessions,
        CancellationToken cancellationToken)
    {
        code = NormalizeKey(code);
        newCode = NormalizeKey(newCode);
        if (!IsValidKeyFormat(newCode))
            throw new InvalidOperationException("The Rink ID must contain exactly six letters or digits.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var keys = await ReadKeysAsync(cancellationToken);
            var key = keys.FirstOrDefault(item => item.Code == code)
                ?? throw new KeyNotFoundException("Rink ID not found.");
            if (!isAdmin && key.CreatedByUserId != userId)
                throw new UnauthorizedAccessException("You do not have permission to rename this Rink ID.");
            if (code == newCode) return key;
            if (keys.Any(item => item.Code == newCode))
                throw new InvalidOperationException("That Rink ID already exists.");

            await sessions.RenameSessionAsync(code, newCode, async () =>
            {
                key.Code = newCode;
                await WriteAsync(_keysPath, keys, cancellationToken);
            }, cancellationToken);
            return key;
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteKeyAsync(
        string code,
        string userId,
        bool isAdmin,
        CancellationToken cancellationToken)
    {
        code = NormalizeKey(code);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var keys = await ReadKeysAsync(cancellationToken);
            var key = keys.FirstOrDefault(item => item.Code == code)
                ?? throw new KeyNotFoundException("Rink ID not found.");
            if (!isAdmin && key.CreatedByUserId != userId)
                throw new UnauthorizedAccessException("You do not have permission to delete this Rink ID.");
            keys.Remove(key);
            await WriteAsync(_keysPath, keys, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task<List<CloudUser>> ReadUsersAsync(CancellationToken ct) => await ReadAsync<List<CloudUser>>(_usersPath, ct) ?? [];
    private async Task<List<SessionKeyRecord>> ReadKeysAsync(CancellationToken ct) => await ReadAsync<List<SessionKeyRecord>>(_keysPath, ct) ?? [];
    private async Task<T?> ReadAsync<T>(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return default;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, _json, ct);
    }
    private async Task WriteAsync<T>(string path, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            await JsonSerializer.SerializeAsync(stream, value, _json, ct);
        File.Move(temp, path, true);
        TryRestrictPermissions(path, isDirectory: false);
    }

    private static void TryRestrictPermissions(string path, bool isDirectory)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            if (isDirectory) mode |= UnixFileMode.UserExecute;
            File.SetUnixFileMode(path, mode);
        }
        catch { }
    }

    private static string RequireEmail(string? value)
    {
        var email = NormalizeEmail(value);
        if (email.Length < 3 || email.Length > 254 || !email.Contains('@')) throw new InvalidOperationException("Enter a valid email address.");
        return email;
    }
    private static void RequirePassword(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 8 || value.Length > 256)
            throw new InvalidOperationException("Passwords must contain between 8 and 256 characters.");
    }
    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256$210000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }
    private static bool VerifyPassword(string password, string encoded)
    {
        try
        {
            var parts = encoded.Split('$');
            var iterations = int.Parse(parts[1]);
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }

    private string? TryUnprotectPublishPassword(string? protectedPassword)
    {
        if (string.IsNullOrEmpty(protectedPassword)) return null;
        try { return _publishPasswordProtector.Unprotect(protectedPassword); }
        catch (CryptographicException) { return null; }
    }
}

public static class CloudRoles
{
    public const string Admin = "Admin";
    public const string Regular = "Regular";
    public static string Normalize(string? role) => string.Equals(role, Admin, StringComparison.OrdinalIgnoreCase) ? Admin : Regular;
}

public sealed class CloudUser
{
    public string Id { get; set; } = "";
    public string Email { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Role { get; set; } = CloudRoles.Regular;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PasswordHash { get; set; }
    public bool MustChangePassword { get; set; }
    public int FailedLoginAttempts { get; set; }
    public DateTimeOffset? RetryAfterUtc { get; set; }
    public bool IsLocked { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? LastLoginUtc { get; set; }
    public int AuthVersion { get; set; }
    public CloudUser CopyPublic() => new() { Id = Id, Email = Email, FirstName = FirstName, LastName = LastName, Role = Role, MustChangePassword = MustChangePassword, FailedLoginAttempts = FailedLoginAttempts, RetryAfterUtc = RetryAfterUtc, IsLocked = IsLocked, CreatedAtUtc = CreatedAtUtc, LastLoginUtc = LastLoginUtc, AuthVersion = AuthVersion };
}

public static class SessionKeyModes
{
    public const string Recorded = "Recorded";
    public const string Live = "Live";
    public static string Normalize(string? value)
        => string.Equals(value, Live, StringComparison.OrdinalIgnoreCase) ? Live : Recorded;
    public static string Require(string? value)
        => string.Equals(value, Live, StringComparison.OrdinalIgnoreCase) ? Live
            : string.Equals(value, Recorded, StringComparison.OrdinalIgnoreCase) ? Recorded
            : throw new InvalidOperationException("Choose Recorded or Live for this Rink ID.");
}

public sealed class SessionKeyRecord
{
    public string Code { get; set; } = "";
    public string CreatedByUserId { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string Mode { get; set; } = SessionKeyModes.Recorded;
    public string PublishPasswordHash { get; set; } = "";
    public string PublishPasswordProtected { get; set; } = "";
    // Existing Rink IDs predate this switch and remain enabled when loaded.
    public bool LiveFeedActive { get; set; } = true;
}
public sealed class ManagedSessionKeyRecord
{
    public string Code { get; set; } = "";
    public string CreatedByUserId { get; set; } = "";
    public string CreatedByEmail { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string Mode { get; set; } = SessionKeyModes.Recorded;
    public bool HasPublishPassword { get; set; }
    public bool LiveFeedActive { get; set; } = true;
}
public sealed class UpdateKeyMediaRequest
{
    public string? Mode { get; set; }
    public string? PublishPassword { get; set; }
}
public sealed class ManagedKeyMediaSettings
{
    public bool HasPublishPassword { get; set; }
    public string? PublishPassword { get; set; }
    public bool PasswordCanBeRevealed { get; set; }
}
public sealed class UpdateLiveFeedStateRequest { public bool? Active { get; set; } }
public sealed class LoginRequest { public string Email { get; set; } = ""; public string Password { get; set; } = ""; }
public sealed class CreateUserRequest { public string Email { get; set; } = ""; public string FirstName { get; set; } = ""; public string LastName { get; set; } = ""; public string Role { get; set; } = CloudRoles.Regular; public string Password { get; set; } = ""; }
public sealed class UpdateProfileRequest { public string Email { get; set; } = ""; public string FirstName { get; set; } = ""; public string LastName { get; set; } = ""; }
public sealed class ChangePasswordRequest { public string CurrentPassword { get; set; } = ""; public string NewPassword { get; set; } = ""; }
public sealed class AdminUpdateUserRequest { public string Role { get; set; } = CloudRoles.Regular; public string NewPassword { get; set; } = ""; public bool Unlock { get; set; } }
public sealed class CreateKeyRequest
{
    public string Code { get; set; } = "";
    public string Mode { get; set; } = "";
    public string? PublishPassword { get; set; }
}
public sealed class RenameKeyRequest { public string Code { get; set; } = ""; }
public sealed record LoginResult(bool Succeeded, bool Locked, int RetryAfterSeconds, CloudUser? User)
{
    public static LoginResult Success(CloudUser user) => new(true, false, 0, user);
    public static LoginResult Invalid(int retry = 0) => new(false, false, retry, null);
    public static LoginResult Waiting(int retry) => new(false, false, retry, null);
    public static LoginResult LockedOut() => new(false, true, 0, null);
}
