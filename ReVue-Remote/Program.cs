using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;
using ReVueRemote;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("System.Net.Http.HttpClient.mediamtx-status", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient.live-preview", LogLevel.Warning);
var configuredStorageRoot = builder.Configuration["ReVueRemote:StorageRoot"]?.Trim();
var storageRoot = string.IsNullOrWhiteSpace(configuredStorageRoot)
    ? Path.Combine(builder.Environment.ContentRootPath, "data", "sessions")
    : Path.GetFullPath(configuredStorageRoot, builder.Environment.ContentRootPath);
var dataProtectionRoot = Path.Combine(storageRoot, "_system", "data-protection");
Directory.CreateDirectory(dataProtectionRoot);
if (!OperatingSystem.IsWindows())
{
    try { File.SetUnixFileMode(dataProtectionRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    catch { }
}
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = null);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = long.MaxValue);
builder.Services.AddSingleton<VideoCanonicalizer>();
builder.Services.AddSingleton<RemoteSessionStore>();
builder.Services.AddSingleton<LiveStreamService>();
builder.Services.AddHttpClient("live-preview", client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient("mediamtx-status", client => client.Timeout = TimeSpan.FromSeconds(3));
builder.Services.AddSingleton<LiveStreamStatusService>();
builder.Services.AddSingleton<VideoTransferTracker>();
builder.Services.AddHostedService<RemotePlaybackLeaseService>();
builder.Services.AddSingleton<VideoProcessingService>();
builder.Services.AddHostedService<VideoProcessingService>(
    services => services.GetRequiredService<VideoProcessingService>());
builder.Services.AddSingleton<CloudAccountStore>();
builder.Services.AddSingleton<RinkIdAccessGuard>();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionRoot))
    .SetApplicationName("ReVue-Remote");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "ReVueRemoteAuth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
        options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
        options.Events.OnValidatePrincipal = async context =>
        {
            var id = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
            var version = context.Principal?.FindFirstValue("authVersion") ?? "";
            var user = await context.HttpContext.RequestServices.GetRequiredService<CloudAccountStore>().FindUserAsync(id, context.HttpContext.RequestAborted);
            if (user == null || user.IsLocked || version != user.AuthVersion.ToString())
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync();
            }
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();
await app.Services.GetRequiredService<CloudAccountStore>().InitializeAsync();
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = 1,
    KnownProxies = { IPAddress.Loopback, IPAddress.IPv6Loopback }
});
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = fileContext =>
    {
        var assetPath = fileContext.Context.Request.Path.Value;
        if (string.Equals(assetPath, "/index.html", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(assetPath, "/app.js", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(assetPath, "/config.html", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(assetPath, "/config.js", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(assetPath, "/config.css", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(assetPath, "/video-cache-sw.js", StringComparison.OrdinalIgnoreCase))
        {
            fileContext.Context.Response.Headers["Cache-Control"] = "no-store";
        }
    }
});
app.Use(async (context, next) =>
{
    var isSessionApi = context.Request.Path.StartsWithSegments("/api/sessions");
    var isValidationRequest = context.Request.Path.Value?.EndsWith("/validate", StringComparison.OrdinalIgnoreCase) == true;
    if (isSessionApi && !isValidationRequest)
    {
        var accessGuard = context.RequestServices.GetRequiredService<RinkIdAccessGuard>();
        var block = await accessGuard.GetBlockAsync(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            context.RequestAborted);
        if (block != null)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers["Retry-After"] = block.RetryAfterSeconds.ToString();
            await context.Response.WriteAsJsonAsync(new
            {
                error = "This IP address is blocked for 24 hours after ten invalid Rink ID attempts.",
                blocked = true,
                retryAfterSeconds = block.RetryAfterSeconds,
                blockedUntilUtc = block.BlockedUntilUtc
            }, context.RequestAborted);
            return;
        }
    }
    await next();
});
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true &&
        (context.Request.Path.StartsWithSegments("/api/manage") || context.Request.Path.StartsWithSegments("/api/admin") ||
         (context.Request.Path.StartsWithSegments("/api/sessions") && !HttpMethods.IsGet(context.Request.Method))))
    {
        var id = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var user = await context.RequestServices.GetRequiredService<CloudAccountStore>().FindUserAsync(id, context.RequestAborted);
        if (user == null)
        {
            await context.SignOutAsync();
            context.Response.StatusCode = 401;
            return;
        }
        if (user.MustChangePassword)
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = "Change your temporary password before continuing." }, context.RequestAborted);
            return;
        }
    }
    await next();
});
app.UseAuthorization();

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

PlaybackState SnapshotForDelivery(PlaybackState state, bool advancePlayingPosition)
{
    var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var positionSeconds = state.PositionSeconds;
    var timelinePositionSeconds = state.TimelinePositionSeconds;
    if (advancePlayingPosition && state.IsPlaying && state.UpdatedAtUnixMs > 0)
    {
        // Advance only the initial snapshot for a viewer joining mid-playback.
        // Live transport events retain VRO's exact captured position so a
        // pause or seek lands on the same frame instead of a server estimate.
        var elapsedSeconds = Math.Max(0, (nowUnixMs - state.UpdatedAtUnixMs) / 1000d);
        positionSeconds += elapsedSeconds * state.PlaybackRate;
        timelinePositionSeconds += elapsedSeconds * state.PlaybackRate;
    }

    return new PlaybackState
    {
        SourceType = state.SourceType,
        Revision = state.Revision,
        CommunicationStatus = new RemoteCommunicationStatus
        {
            JudgesReady = state.CommunicationStatus?.JudgesReady == true,
            TechPanelReady = state.CommunicationStatus?.TechPanelReady == true,
            CompetitorScored = state.CommunicationStatus?.CompetitorScored == true
        },
        OperatorConnected = !string.IsNullOrWhiteSpace(state.OperatorInstanceId) &&
            !string.Equals(state.Mode, "operator-offline", StringComparison.OrdinalIgnoreCase) &&
            state.OperatorLeaseExpiresAtUnixMs > nowUnixMs,
        VideoId = state.VideoId,
        VideoFileName = state.VideoFileName,
        FramesPerSecond = state.FramesPerSecond,
        PositionSeconds = positionSeconds,
        TimelinePositionSeconds = timelinePositionSeconds,
        TimelineDurationSeconds = state.TimelineDurationSeconds,
        IsPlaying = state.IsPlaying,
        PlaybackEnabled = state.PlaybackEnabled,
        PlaybackRate = state.PlaybackRate,
        PlaybackDiscontinuity = state.PlaybackDiscontinuity,
        Mode = state.Mode,
        ProgramStartSeconds = state.ProgramStartSeconds,
        HalfwaySeconds = state.HalfwaySeconds,
        OpenClipStartSeconds = state.OpenClipStartSeconds,
        ZoomScale = state.ZoomScale,
        ZoomOffsetX = state.ZoomOffsetX,
        ZoomOffsetY = state.ZoomOffsetY,
        Clips = state.Clips,
        ReviewStartedAtUnixMs = state.ReviewStartedAtUnixMs,
        UpdatedAtUnixMs = nowUnixMs
    };
}

string CurrentUserId(HttpContext context) => context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
string ClientAddress(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
async Task<CloudUser?> CurrentUserAsync(HttpContext context, CloudAccountStore accounts)
    => string.IsNullOrWhiteSpace(CurrentUserId(context)) ? null : await accounts.FindUserAsync(CurrentUserId(context), context.RequestAborted);
IResult AccountError(Exception ex) => ex is KeyNotFoundException ? Results.NotFound(ex.Message) : Results.BadRequest(ex.Message);
bool IsAdmin(HttpContext context) => context.User.IsInRole(CloudRoles.Admin);
async Task<IResult?> RequireRinkManagementAsync(string code, HttpContext context, CloudAccountStore accounts)
{
    if (!await accounts.KeyExistsAsync(code, context.RequestAborted))
        return Results.NotFound("The Rink ID does not exist.");
    if (!await accounts.CanManageKeyAsync(code, CurrentUserId(context), IsAdmin(context), context.RequestAborted))
        return Results.Forbid();
    return null;
}

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", service = "ReVue-Remote" }));

// MediaMTX sends this callback for each RTMP publisher and RTSP/HLS reader.
// Its shared token must be configured on both services and must not be exposed
// through the public reverse proxy.
app.MapPost("/api/internal/mediamtx-auth", async (
    MediaMtxAuthRequest request, HttpContext context, CloudAccountStore accounts) =>
{
    var configuredToken = app.Configuration["ReVueRemote:MediaAuthToken"];
    var suppliedToken = context.Request.Query["token"].ToString();
    if (string.IsNullOrWhiteSpace(configuredToken) || configuredToken.Length < 32 ||
        !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(configuredToken),
            System.Text.Encoding.UTF8.GetBytes(suppliedToken)))
        return Results.Unauthorized();
    if (!RemoteSessionStore.IsValidSessionCode(request.Path) ||
        await accounts.GetKeyModeAsync(request.Path, context.RequestAborted) != SessionKeyModes.Live)
        return Results.Unauthorized();
    if (string.Equals(request.Action, "publish", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(request.Protocol, "rtmp", StringComparison.OrdinalIgnoreCase))
        return await accounts.AuthorizeLivePublisherAsync(request.Path, request.Password, context.RequestAborted)
            ? Results.Ok() : Results.Unauthorized();
    if (string.Equals(request.Action, "read", StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(request.Protocol, "rtsp", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(request.Protocol, "hls", StringComparison.OrdinalIgnoreCase)))
        return Results.Ok();
    return Results.Unauthorized();
});

app.MapGet("/config", (HttpContext context) =>
{
    context.Response.Headers["Cache-Control"] = "no-store";
    return Results.File(
        Path.Combine(app.Environment.WebRootPath, "config.html"),
        "text/html; charset=utf-8");
});
app.MapGet("/upload", () => Results.Redirect("/config", permanent: true));

app.MapGet("/api/manage/storage", (HttpContext context, RemoteSessionStore store) =>
{
    context.Response.Headers["Cache-Control"] = "no-store";
    var status = store.GetStorageStatus();
    return status is null ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable) : Results.Ok(status);
}).RequireAuthorization();

app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext context, CloudAccountStore accounts) =>
{
    var result = await accounts.AuthenticateAsync(request.Email, request.Password, context.RequestAborted);
    if (!result.Succeeded)
    {
        if (result.Locked) return Results.Json(new { error = "This account is locked. Ask an administrator to unlock it." }, statusCode: 423);
        if (result.RetryAfterSeconds > 0) context.Response.Headers["Retry-After"] = result.RetryAfterSeconds.ToString();
        return Results.Json(new { error = "The email or password is incorrect.", retryAfterSeconds = result.RetryAfterSeconds }, statusCode: 401);
    }
    var user = result.User!;
    var claims = new[] { new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim(ClaimTypes.Name, user.Email), new Claim(ClaimTypes.Role, user.Role), new Claim("authVersion", user.AuthVersion.ToString()) };
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Ok(user);
});
app.MapPost("/api/auth/logout", async (HttpContext context) => { await context.SignOutAsync(); return Results.Ok(); });
app.MapGet("/api/auth/me", async (HttpContext context, CloudAccountStore accounts) =>
{
    var user = await CurrentUserAsync(context, accounts);
    return user == null ? Results.Unauthorized() : Results.Ok(user);
});
app.MapPut("/api/account/profile", async (UpdateProfileRequest request, HttpContext context, CloudAccountStore accounts) =>
{
    try { return Results.Ok(await accounts.UpdateProfileAsync(CurrentUserId(context), request, context.RequestAborted)); }
    catch (Exception ex) { return AccountError(ex); }
}).RequireAuthorization();
app.MapPut("/api/account/password", async (ChangePasswordRequest request, HttpContext context, CloudAccountStore accounts) =>
{
    try { await accounts.ChangePasswordAsync(CurrentUserId(context), request.CurrentPassword, request.NewPassword, context.RequestAborted); return Results.Ok(); }
    catch (Exception ex) { return AccountError(ex); }
}).RequireAuthorization();

app.MapGet("/api/admin/users", async (CloudAccountStore accounts, HttpContext context) => Results.Ok(await accounts.ListUsersAsync(context.RequestAborted)))
    .RequireAuthorization(policy => policy.RequireRole(CloudRoles.Admin));
app.MapPost("/api/admin/users", async (CreateUserRequest request, CloudAccountStore accounts, HttpContext context) =>
{
    try { return Results.Ok(await accounts.CreateUserAsync(request, context.RequestAborted)); }
    catch (Exception ex) { return AccountError(ex); }
}).RequireAuthorization(policy => policy.RequireRole(CloudRoles.Admin));
app.MapPut("/api/admin/users/{id}", async (string id, AdminUpdateUserRequest request, CloudAccountStore accounts, HttpContext context) =>
{
    try { await accounts.AdminUpdateUserAsync(id, request, context.RequestAborted); return Results.Ok(); }
    catch (Exception ex) { return AccountError(ex); }
}).RequireAuthorization(policy => policy.RequireRole(CloudRoles.Admin));
app.MapDelete("/api/admin/users/{id}", async (string id, CloudAccountStore accounts, HttpContext context) =>
{
    try { await accounts.DeleteUserAsync(id, CurrentUserId(context), context.RequestAborted); return Results.Ok(); }
    catch (Exception ex) { return AccountError(ex); }
}).RequireAuthorization(policy => policy.RequireRole(CloudRoles.Admin));

app.MapGet("/api/manage/keys", async (CloudAccountStore accounts, HttpContext context) =>
    Results.Ok(await accounts.ListManagedKeysAsync(
        CurrentUserId(context),
        IsAdmin(context),
        context.RequestAborted))).RequireAuthorization();
app.MapGet("/api/manage/live-streams", async (
    CloudAccountStore accounts, LiveStreamStatusService streams,
    RemoteSessionStore store, HttpContext context) =>
{
    context.Response.Headers["Cache-Control"] = "no-store";
    var keys = await accounts.ListManagedKeysAsync(
        CurrentUserId(context), IsAdmin(context), context.RequestAborted);
    return Results.Ok(await streams.GetAsync(
        keys.Where(key => key.Mode == SessionKeyModes.Live), store,
        context.RequestAborted));
}).RequireAuthorization();
app.MapPost("/api/manage/keys", async (CreateKeyRequest request, CloudAccountStore accounts, HttpContext context) =>
{
    try
    {
        var key = await accounts.CreateKeyAsync(
            request.Code, request.Mode, request.PublishPassword,
            CurrentUserId(context), context.RequestAborted);
        return Results.Ok(new
        {
            key.Code,
            mode = SessionKeyModes.Normalize(key.Mode),
            hasPublishPassword = !string.IsNullOrEmpty(key.PublishPasswordHash),
            liveFeedActive = key.LiveFeedActive
        });
    }
    catch (Exception ex) { return AccountError(ex); }
}).RequireAuthorization();
app.MapPut("/api/manage/keys/{code}/live-feed", async (
    string code, UpdateLiveFeedStateRequest request, CloudAccountStore accounts,
    LiveStreamService live, LiveStreamStatusService streams,
    HttpContext context) =>
{
    try
    {
        var denied = await RequireRinkManagementAsync(code, context, accounts);
        if (denied is not null) return denied;
        if (request.Active is null) return Results.BadRequest("Choose Active or Paused for this live feed.");
        var result = await accounts.SetLiveFeedActiveAsync(
            code, request.Active.Value, CurrentUserId(context), IsAdmin(context), context.RequestAborted);
        if (!request.Active.Value)
            await Task.WhenAll(
                live.StopAllForRinkAsync(code),
                streams.KickPublisherAsync(code, context.RequestAborted));
        return Results.Ok(result);
    }
    catch (UnauthorizedAccessException) { return Results.Forbid(); }
    catch (Exception ex) { return AccountError(ex); }
}).RequireAuthorization();
app.MapPut("/api/manage/keys/{code}", async (
    string code, RenameKeyRequest request, CloudAccountStore accounts, RemoteSessionStore store,
    LiveStreamService live, HttpContext context) =>
{
    try
    {
        var denied = await RequireRinkManagementAsync(code, context, accounts);
        if (denied is not null) return denied;
        if (await accounts.GetKeyModeAsync(code, context.RequestAborted) == SessionKeyModes.Live &&
            !string.Equals(code, request.Code, StringComparison.OrdinalIgnoreCase))
            await live.StopAllForRinkAsync(code);
        var key = await accounts.RenameKeyAsync(
            code, request.Code, CurrentUserId(context), IsAdmin(context), store, context.RequestAborted);
        return Results.Ok(new { key.Code, mode = SessionKeyModes.Normalize(key.Mode) });
    }
    catch (UnauthorizedAccessException) { return Results.Forbid(); }
    catch (Exception ex) { return AccountError(ex); }
}).RequireAuthorization();
app.MapGet("/api/manage/keys/{code}/media", async (
    string code, CloudAccountStore accounts, HttpContext context) =>
{
    try
    {
        context.Response.Headers["Cache-Control"] = "no-store";
        var denied = await RequireRinkManagementAsync(code, context, accounts);
        if (denied is not null) return denied;
        return Results.Ok(await accounts.GetKeyMediaAsync(
            code, CurrentUserId(context), IsAdmin(context), context.RequestAborted));
    }
    catch (UnauthorizedAccessException) { return Results.Forbid(); }
    catch (Exception ex) { return AccountError(ex); }
}).RequireAuthorization();
app.MapPut("/api/manage/keys/{code}/media", async (
    string code, UpdateKeyMediaRequest request, CloudAccountStore accounts,
    HttpContext context) =>
{
    try
    {
        var denied = await RequireRinkManagementAsync(code, context, accounts);
        if (denied is not null) return denied;
        if (request.Mode is not null &&
            !string.Equals(request.Mode, SessionKeyModes.Live, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest("Rink ID type cannot be changed after creation.");
        var result = await accounts.UpdateKeyMediaAsync(
            code, request.PublishPassword,
            CurrentUserId(context), IsAdmin(context), context.RequestAborted);
        return Results.Ok(result);
    }
    catch (UnauthorizedAccessException) { return Results.Forbid(); }
    catch (Exception ex) { return AccountError(ex); }
}).RequireAuthorization();
app.MapDelete("/api/manage/keys/{code}", async (
    string code, CloudAccountStore accounts, RemoteSessionStore store,
    LiveStreamService live, HttpContext context) =>
{
    try
    {
        var denied = await RequireRinkManagementAsync(code, context, accounts);
        if (denied is not null) return denied;
        await live.StopAllForRinkAsync(code);
        await store.DeleteSessionAsync(code, context.RequestAborted);
        await accounts.DeleteKeyAsync(code, CurrentUserId(context), IsAdmin(context), context.RequestAborted);
        return Results.Ok();
    }
    catch (UnauthorizedAccessException) { return Results.Forbid(); }
    catch (Exception ex) { return AccountError(ex); }
}).RequireAuthorization();

app.MapGet("/api/sessions/{sessionCode}/validate", async (
    string sessionCode,
    CloudAccountStore accounts,
    RinkIdAccessGuard accessGuard,
    HttpContext context) =>
{
    var decision = await accessGuard.ValidateAsync(
        ClientAddress(context),
        cancellationToken => accounts.KeyExistsAsync(sessionCode, cancellationToken),
        context.RequestAborted);
    if (decision.IsValid)
        return Results.Ok(new
        {
            valid = true,
            code = CloudAccountStore.NormalizeKey(sessionCode),
            mode = await accounts.GetKeyModeAsync(sessionCode, context.RequestAborted)
        });

    context.Response.Headers["Retry-After"] = decision.RetryAfterSeconds.ToString();
    return Results.Json(new
    {
        valid = false,
        attempts = decision.Attempts,
        retryAfterSeconds = decision.RetryAfterSeconds,
        finalAttempt = decision.FinalAttempt,
        blocked = decision.IsBlocked,
        blockedUntilUtc = decision.BlockedUntilUtc
    }, statusCode: StatusCodes.Status429TooManyRequests);
});

app.MapGet("/api/sessions/{sessionCode}/videos", async (
    string sessionCode,
    HttpContext context,
    RemoteSessionStore store,
    CloudAccountStore accounts) =>
{
    try
    {
        if (!await accounts.KeyExistsAsync(sessionCode, context.RequestAborted)) return Results.NotFound();
        var manage = context.User.Identity?.IsAuthenticated == true &&
            await accounts.CanManageKeyAsync(
                sessionCode, CurrentUserId(context), IsAdmin(context), context.RequestAborted);
        return Results.Ok(manage
            ? await store.ListVideosWithMetadataAsync(sessionCode, context.RequestAborted)
            : await store.ListVideosAsync(sessionCode, context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

app.MapPost("/api/sessions/{sessionCode}/uploads", async (
    string sessionCode, BeginRemoteUploadRequest request, HttpContext context,
    RemoteSessionStore store, CloudAccountStore accounts) =>
{
    try
    {
        var denied = await RequireRinkManagementAsync(sessionCode, context, accounts);
        if (denied is not null) return denied;
        return Results.Ok(await store.BeginUploadAsync(
            sessionCode,
            request.FileName,
            request.SizeBytes,
            request.LastModifiedUnixMs,
            context.RequestAborted));
    }
    catch (Exception ex) { return AccountError(ex); }
}).RequireAuthorization();

app.MapGet("/api/sessions/{sessionCode}/uploads/{uploadId}", async (
    string sessionCode, string uploadId, HttpContext context,
    RemoteSessionStore store, CloudAccountStore accounts) =>
{
    try
    {
        var denied = await RequireRinkManagementAsync(sessionCode, context, accounts);
        if (denied is not null) return denied;
        return Results.Ok(await store.GetUploadStatusAsync(
            sessionCode, uploadId, context.RequestAborted));
    }
    catch (Exception ex) { return AccountError(ex); }
}).RequireAuthorization();

app.MapPut("/api/sessions/{sessionCode}/uploads/{uploadId}", async (
    string sessionCode, string uploadId, long offset, HttpContext context,
    RemoteSessionStore store, CloudAccountStore accounts) =>
{
    try
    {
        var denied = await RequireRinkManagementAsync(sessionCode, context, accounts);
        if (denied is not null) return denied;
        var contentLength = context.Request.ContentLength;
        if (contentLength is null &&
            long.TryParse(context.Request.Headers["X-Upload-Chunk-Length"], out var declaredLength))
            contentLength = declaredLength;
        if (contentLength is null)
            return Results.StatusCode(StatusCodes.Status411LengthRequired);
        return Results.Ok(await store.AppendUploadChunkAsync(
            sessionCode,
            uploadId,
            offset,
            contentLength.Value,
            context.Request.Body,
            context.RequestAborted));
    }
    catch (Exception ex) { return AccountError(ex); }
}).RequireAuthorization();

app.MapPost("/api/sessions/{sessionCode}/uploads/{uploadId}/complete", async (
    string sessionCode, string uploadId, HttpContext context,
    RemoteSessionStore store, VideoProcessingService processing, CloudAccountStore accounts) =>
{
    try
    {
        var denied = await RequireRinkManagementAsync(sessionCode, context, accounts);
        if (denied is not null) return denied;
        var status = await store.PrepareUploadForProcessingAsync(
            sessionCode, uploadId, context.RequestAborted);
        if (string.Equals(status.Stage, RemoteUploadStages.Processing, StringComparison.OrdinalIgnoreCase))
            processing.Queue(RemoteSessionStore.NormalizeSessionCode(sessionCode), uploadId);
        return Results.Accepted(value: status);
    }
    catch (Exception ex) { return AccountError(ex); }
}).RequireAuthorization();

app.MapPost("/api/sessions/{sessionCode}/videos/{videoId}/transcode", async (
    string sessionCode, string videoId, VideoTranscodeRequest request, HttpContext context,
    RemoteSessionStore store, VideoProcessingService processing, CloudAccountStore accounts) =>
{
    try
    {
        var denied = await RequireRinkManagementAsync(sessionCode, context, accounts);
        if (denied is not null) return denied;
        var status = await store.BeginVideoTranscodeAsync(
            sessionCode, videoId, request.Mode, context.RequestAborted);
        processing.Queue(RemoteSessionStore.NormalizeSessionCode(sessionCode), status.UploadId);
        return Results.Accepted(value: status);
    }
    catch (Exception ex) { return AccountError(ex); }
}).RequireAuthorization();

app.MapGet("/api/sessions/{sessionCode}/transcodes", async (
    string sessionCode, HttpContext context, RemoteSessionStore store, CloudAccountStore accounts) =>
{
    try
    {
        var denied = await RequireRinkManagementAsync(sessionCode, context, accounts);
        if (denied is not null) return denied;
        return Results.Ok(await store.ListActiveVideoTranscodesAsync(sessionCode, context.RequestAborted));
    }
    catch (Exception ex) { return AccountError(ex); }
}).RequireAuthorization();

app.MapDelete("/api/sessions/{sessionCode}/uploads/{uploadId}", async (
    string sessionCode, string uploadId, HttpContext context,
    RemoteSessionStore store, CloudAccountStore accounts) =>
{
    try
    {
        var denied = await RequireRinkManagementAsync(sessionCode, context, accounts);
        if (denied is not null) return denied;
        await store.CancelUploadAsync(sessionCode, uploadId, context.RequestAborted);
        return Results.Ok();
    }
    catch (Exception ex) { return AccountError(ex); }
}).RequireAuthorization();

app.MapPost("/api/sessions/{sessionCode}/videos", async (
    string sessionCode,
    HttpContext context,
    RemoteSessionStore store,
    CloudAccountStore accounts) =>
{
    try
    {
        var denied = await RequireRinkManagementAsync(sessionCode, context, accounts);
        if (denied is not null) return denied;
        if (!context.Request.HasFormContentType)
            return Results.BadRequest("Expected a multipart upload.");
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var videos = await store.SaveUploadsAsync(sessionCode, form.Files, context.RequestAborted);
        return Results.Ok(videos);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(ex.Message);
    }
}).RequireAuthorization();

app.MapPut("/api/sessions/{sessionCode}/videos/order", async (
    string sessionCode, ReorderVideosRequest request, HttpContext context, RemoteSessionStore store, CloudAccountStore accounts) =>
{
    try
    {
        var denied = await RequireRinkManagementAsync(sessionCode, context, accounts);
        if (denied is not null) return denied;
        return Results.Ok(await store.ReorderVideosAsync(sessionCode, request.VideoIds, context.RequestAborted));
    }
    catch (Exception ex) { return Results.BadRequest(ex.Message); }
}).RequireAuthorization();

app.MapPut("/api/sessions/{sessionCode}/videos/{videoId}/name", async (
    string sessionCode, string videoId, RenameVideoRequest request, HttpContext context,
    RemoteSessionStore store, CloudAccountStore accounts) =>
{
    try
    {
        var denied = await RequireRinkManagementAsync(sessionCode, context, accounts);
        if (denied is not null) return denied;
        return Results.Ok(await store.RenameVideoAsync(
            sessionCode, videoId, request.FileName, context.RequestAborted));
    }
    catch (Exception ex) { return AccountError(ex); }
}).RequireAuthorization();

app.MapDelete("/api/sessions/{sessionCode}/videos", async (
    string sessionCode, HttpContext context, RemoteSessionStore store, CloudAccountStore accounts) =>
{
    try
    {
        var denied = await RequireRinkManagementAsync(sessionCode, context, accounts);
        if (denied is not null) return denied;
        await store.DeleteAllVideosAsync(sessionCode, context.RequestAborted);
        return Results.Ok();
    }
    catch (Exception ex) { return Results.BadRequest(ex.Message); }
}).RequireAuthorization();

app.MapDelete("/api/sessions/{sessionCode}/videos/{videoId}", async (
    string sessionCode, string videoId, HttpContext context, RemoteSessionStore store, CloudAccountStore accounts) =>
{
    try
    {
        var denied = await RequireRinkManagementAsync(sessionCode, context, accounts);
        if (denied is not null) return denied;
        await store.DeleteVideoAsync(sessionCode, videoId, context.RequestAborted);
        return Results.Ok();
    }
    catch (Exception ex) { return AccountError(ex); }
}).RequireAuthorization();

app.MapGet("/api/sessions/{sessionCode}/videos/{videoId}/content", async (
    string sessionCode,
    string videoId,
    string? viewer,
    HttpContext context,
    RemoteSessionStore store,
    CloudAccountStore accounts,
    VideoTransferTracker transferTracker) =>
{
    try
    {
        if (!await accounts.KeyExistsAsync(sessionCode, context.RequestAborted)) return Results.NotFound();
        var result = await store.FindVideoAsync(sessionCode, videoId, context.RequestAborted);
        if (!result.HasValue) return Results.NotFound();
        context.Response.Headers["Cache-Control"] = "private, max-age=31536000, immutable";
        var source = new FileStream(
            result.Value.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var viewerKey = TransferViewerIdentity.RateLimitKey(context);
        Stream stream = new TrackingReadStream(
            source,
            bytes => { if (!string.IsNullOrWhiteSpace(viewer) && viewer.Length <= 64)
                transferTracker.Record(sessionCode, videoId, viewer, bytes); },
            transferTracker,
            viewerKey);
        context.Response.RegisterForDispose(stream);
        return Results.Stream(
            stream,
            contentType: "video/mp4",
            fileDownloadName: null,
            enableRangeProcessing: true);
    }
    catch
    {
        return Results.NotFound();
    }
});

app.MapGet("/api/sessions/{sessionCode}/videos/{videoId}/preview", async (
    string sessionCode,
    string videoId,
    HttpContext context,
    RemoteSessionStore store,
    CloudAccountStore accounts,
    VideoTransferTracker transferTracker) =>
{
    try
    {
        var denied = await RequireRinkManagementAsync(sessionCode, context, accounts);
        if (denied is not null) return denied;
        var result = await store.FindVideoAsync(sessionCode, videoId, context.RequestAborted);
        if (!result.HasValue) return Results.NotFound();
        context.Response.Headers["Cache-Control"] = "private, no-store";
        var source = new FileStream(
            result.Value.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var viewerKey = TransferViewerIdentity.RateLimitKey(context);
        Stream stream = new TrackingReadStream(source, _ => { }, transferTracker, viewerKey);
        context.Response.RegisterForDispose(stream);
        return Results.Stream(
            stream,
            contentType: "video/mp4",
            fileDownloadName: null,
            enableRangeProcessing: true);
    }
    catch (Exception ex) { return AccountError(ex); }
}).RequireAuthorization();

app.MapGet("/api/sessions/{sessionCode}/videos/{videoId}/thumbnail", async (
    string sessionCode,
    string videoId,
    HttpContext context,
    RemoteSessionStore store,
    CloudAccountStore accounts) =>
{
    try
    {
        var denied = await RequireRinkManagementAsync(sessionCode, context, accounts);
        if (denied is not null) return denied;
        var path = await store.FindVideoThumbnailAsync(sessionCode, videoId, context.RequestAborted);
        if (path is null) return Results.NotFound();
        context.Response.Headers["Cache-Control"] = "private, max-age=31536000, immutable";
        return Results.File(path, "image/jpeg");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Could not serve thumbnail for video {VideoId} in Rink ID {SessionCode}", videoId, sessionCode);
        return Results.Problem("The video thumbnail could not be generated.");
    }
}).RequireAuthorization();

app.MapGet("/api/sessions/{sessionCode}/videos/{videoId}/transfer", async (
    string sessionCode,
    string videoId,
    string? viewer,
    HttpContext context,
    CloudAccountStore accounts,
    VideoTransferTracker transferTracker) =>
{
    if (string.IsNullOrWhiteSpace(viewer) || viewer.Length > 64 ||
        !await accounts.KeyExistsAsync(sessionCode, context.RequestAborted))
        return Results.NotFound();
    return Results.Ok(new { bytes = transferTracker.GetTransferredBytes(sessionCode, videoId, viewer) });
});

app.MapGet("/api/sessions/{sessionCode}/transfer", async (
    string sessionCode,
    HttpContext context,
    CloudAccountStore accounts,
    VideoTransferTracker transferTracker) =>
{
    var viewer = TransferViewerIdentity.QueryToken(context);
    if (viewer is null || !await accounts.KeyExistsAsync(sessionCode, context.RequestAborted))
        return Results.NotFound();
    context.Response.Headers["Cache-Control"] = "no-store";
    return Results.Ok(new { bytes = transferTracker.GetSessionTransferredBytes(sessionCode, viewer) });
});

app.MapPost("/api/sessions/{sessionCode}/viewer-cache/clear", async (
    string sessionCode,
    HttpContext context,
    CloudAccountStore accounts) =>
{
    if (!await accounts.KeyExistsAsync(sessionCode, context.RequestAborted)) return Results.NotFound();
    // ReVue-Remote keeps only one current recording per viewer. Clearing the
    // browser's HTTP cache here releases the prior recording after Next.
    context.Response.Headers["Clear-Site-Data"] = "\"cache\"";
    return Results.NoContent();
});

app.MapGet("/api/sessions/{sessionCode}/playback", async (
    string sessionCode,
    HttpContext context,
    RemoteSessionStore store,
    CloudAccountStore accounts) =>
{
    try
    {
        if (!await accounts.KeyExistsAsync(sessionCode, context.RequestAborted)) return Results.NotFound();
        var state = await store.GetPlaybackStateAsync(sessionCode, context.RequestAborted);
        return Results.Ok(SnapshotForDelivery(state, advancePlayingPosition: true));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

app.MapPut("/api/sessions/{sessionCode}/communication-status", async (
    string sessionCode, SetRemoteCommunicationStatusRequest request, HttpContext context,
    RemoteSessionStore store, CloudAccountStore accounts) =>
{
    if (!await accounts.KeyExistsAsync(sessionCode, context.RequestAborted)) return Results.NotFound();
    try
    {
        var state = await store.SetCommunicationStatusAsync(
            sessionCode, request.ViewerSessionId, request.Indicator,
            request.Active, context.RequestAborted);
        return Results.Ok(new
        {
            state.CommunicationStatus.JudgesReady,
            state.CommunicationStatus.TechPanelReady,
            state.CommunicationStatus.CompetitorScored
        });
    }
    catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapGet("/api/sessions/{sessionCode}/live/preview/{fileName}", async (
    string sessionCode, string fileName, HttpContext context, CloudAccountStore accounts,
    IHttpClientFactory clients, VideoTransferTracker transferTracker) =>
{
    if (await accounts.GetKeyModeAsync(sessionCode, context.RequestAborted) != SessionKeyModes.Live ||
        fileName.Length > 160 || fileName is "." or ".." ||
        fileName.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-')))
        return Results.NotFound();
    var baseUrl = (app.Configuration["ReVueRemote:MediaMtxHlsBase"] ?? "http://127.0.0.1:8888").TrimEnd('/');
    var path = $"{baseUrl}/{RemoteSessionStore.NormalizeSessionCode(sessionCode)}/{fileName}{context.Request.QueryString}";
    using var upstream = await clients.CreateClient("live-preview").GetAsync(
        path, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
    if (!upstream.IsSuccessStatusCode) return Results.StatusCode((int)upstream.StatusCode);
    context.Response.ContentType = fileName.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
        ? "application/vnd.apple.mpegurl" : "video/mp4";
    context.Response.Headers["Cache-Control"] = "no-store";
    if (fileName.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
    {
        var playlist = await upstream.Content.ReadAsStringAsync(context.RequestAborted);
        playlist = playlist.Replace($"{baseUrl}/{RemoteSessionStore.NormalizeSessionCode(sessionCode)}/",
            $"/api/sessions/{RemoteSessionStore.NormalizeSessionCode(sessionCode)}/live/preview/",
            StringComparison.Ordinal);
        playlist = TransferViewerIdentity.AddViewerToPlaylist(
            playlist, TransferViewerIdentity.QueryToken(context));
        await context.Response.WriteAsync(playlist, context.RequestAborted);
    }
    else
    {
        var viewer = TransferViewerIdentity.QueryToken(context);
        await using var body = new TrackingReadStream(
            await upstream.Content.ReadAsStreamAsync(context.RequestAborted),
            bytes => { if (viewer is not null) transferTracker.RecordSession(sessionCode, viewer, bytes); },
            transferTracker, TransferViewerIdentity.RateLimitKey(context));
        await body.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
    return Results.Empty;
});

app.MapGet("/api/sessions/{sessionCode}/live/events/{eventId}/{fileName}", async (
    string sessionCode, string eventId, string fileName, HttpContext context,
    CloudAccountStore accounts, LiveStreamService live, VideoTransferTracker transferTracker) =>
{
    if (await accounts.GetKeyModeAsync(sessionCode, context.RequestAborted) != SessionKeyModes.Live)
        return Results.NotFound();
    try
    {
        var path = live.GetEventFile(sessionCode, eventId, fileName);
        if (path is null) return Results.NotFound();
        var playlist = fileName.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
        context.Response.Headers["Cache-Control"] = playlist ? "no-store" : "public, max-age=86400, immutable";
        if (playlist)
        {
            var text = await File.ReadAllTextAsync(path, context.RequestAborted);
            return Results.Text(TransferViewerIdentity.AddViewerToPlaylist(
                text, TransferViewerIdentity.QueryToken(context)), "application/vnd.apple.mpegurl");
        }
        var viewer = TransferViewerIdentity.QueryToken(context);
        var source = new TrackingReadStream(File.OpenRead(path),
            bytes => { if (viewer is not null) transferTracker.RecordSession(sessionCode, viewer, bytes); },
            transferTracker,
            TransferViewerIdentity.RateLimitKey(context));
        return Results.File(source, "video/mp4", enableRangeProcessing: true);
    }
    catch (ArgumentException) { return Results.BadRequest(); }
});

app.MapPost("/api/sessions/{sessionCode}/playback", async (
    string sessionCode,
    PlaybackCommand command,
    HttpContext context,
    RemoteSessionStore store,
    LiveStreamService live,
    CloudAccountStore accounts) =>
{
    try
    {
        var rinkMode = await accounts.GetKeyModeAsync(sessionCode, context.RequestAborted);
        if (rinkMode is null) return Results.NotFound("The Rink ID does not exist.");
        if (!string.Equals(rinkMode, SessionKeyModes.Normalize(command.SourceType), StringComparison.Ordinal))
            return Results.BadRequest("The VRO source mode does not match this Rink ID.");
        var next = await store.UpdatePlaybackStateAsync(sessionCode, command, context.RequestAborted,
            rinkMode == SessionKeyModes.Live ? async (prior, accepted) =>
            {
                if (accepted.Mode is "preparing" or "recording")
                    await live.StartEventAsync(sessionCode, accepted.VideoId, command.LiveDelaySeconds);
                if (accepted.Mode == "replay" && prior.Mode is "recording" or "preparing")
                    await live.FinishEventAsync(sessionCode, accepted.VideoId, accepted.TimelineDurationSeconds);
                if (prior.VideoId.Length > 0 && prior.VideoId != accepted.VideoId)
                    await live.DeleteEventAsync(sessionCode, prior.VideoId);
            } : null);
        return Results.Ok(next);
    }
    catch (FileNotFoundException ex)
    {
        return Results.NotFound(ex.Message);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

app.MapGet("/api/sessions/{sessionCode}/events", async (
    string sessionCode,
    HttpContext context,
    RemoteSessionStore store,
    CloudAccountStore accounts) =>
{
    if (!RemoteSessionStore.IsValidSessionCode(sessionCode))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("Invalid Rink ID.", context.RequestAborted);
        return;
    }
    if (!await accounts.KeyExistsAsync(sessionCode, context.RequestAborted))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    context.Response.StatusCode = StatusCodes.Status200OK;
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers["Cache-Control"] = "no-cache, no-store";
    context.Response.Headers["Connection"] = "keep-alive";
    context.Response.Headers["X-Accel-Buffering"] = "no";

    async Task WriteStateAsync(PlaybackState state, bool advancePlayingPosition)
    {
        var deliveredState = SnapshotForDelivery(state, advancePlayingPosition);
        await context.Response.WriteAsync("event: playback\n", context.RequestAborted);
        await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(deliveredState, jsonOptions)}\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    using var subscription = store.Subscribe(sessionCode, context.Request.Query["role"].ToString());
    await context.Response.WriteAsync("event: viewer-session\n", context.RequestAborted);
    await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(subscription.Id)}\n\n", context.RequestAborted);
    await context.Response.Body.FlushAsync(context.RequestAborted);
    await WriteStateAsync(
        await store.GetPlaybackStateAsync(sessionCode, context.RequestAborted),
        advancePlayingPosition: true);

    try
    {
        Task<bool>? waitForState = null;
        while (!context.RequestAborted.IsCancellationRequested)
        {
            waitForState ??= subscription.Reader.WaitToReadAsync(context.RequestAborted).AsTask();
            var heartbeat = Task.Delay(TimeSpan.FromSeconds(3), context.RequestAborted);
            var completed = await Task.WhenAny(waitForState, heartbeat);

            if (completed == heartbeat)
            {
                // Refresh the connection metric without changing the media
                // lifecycle when the VRO misses a control heartbeat.
                await WriteStateAsync(await store.GetPlaybackStateAsync(sessionCode, context.RequestAborted),
                    advancePlayingPosition: false);
                continue;
            }

            if (!await waitForState) break;
            waitForState = null;
            while (subscription.Reader.TryRead(out var state))
                await WriteStateAsync(state, advancePlayingPosition: false);
        }
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
    }
});

app.MapFallbackToFile("index.html");
app.Run();

public sealed class MediaMtxAuthRequest
{
    public string Action { get; set; } = "";
    public string Path { get; set; } = "";
    public string Protocol { get; set; } = "";
    public string Password { get; set; } = "";
}
