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
app.UseStaticFiles();
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
        Revision = state.Revision,
        VideoId = state.VideoId,
        VideoFileName = state.VideoFileName,
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

app.MapGet("/config", () => Results.File(
    Path.Combine(app.Environment.WebRootPath, "config.html"),
    "text/html; charset=utf-8"));
app.MapGet("/upload", () => Results.Redirect("/config", permanent: true));

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
app.MapPost("/api/manage/keys", async (CreateKeyRequest request, CloudAccountStore accounts, HttpContext context) =>
{
    try { return Results.Ok(await accounts.CreateKeyAsync(request.Code, CurrentUserId(context), context.RequestAborted)); }
    catch (Exception ex) { return AccountError(ex); }
}).RequireAuthorization();
app.MapDelete("/api/manage/keys/{code}", async (
    string code, CloudAccountStore accounts, RemoteSessionStore store, HttpContext context) =>
{
    try
    {
        var denied = await RequireRinkManagementAsync(code, context, accounts);
        if (denied is not null) return denied;
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
        return Results.Ok(new { valid = true, code = CloudAccountStore.NormalizeKey(sessionCode) });

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
        return Results.Ok(await store.ListVideosAsync(sessionCode, context.RequestAborted));
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
        if (string.Equals(status.Stage, "processing", StringComparison.OrdinalIgnoreCase))
            processing.Queue(RemoteSessionStore.NormalizeSessionCode(sessionCode), uploadId);
        return Results.Accepted(value: status);
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
    HttpContext context,
    RemoteSessionStore store,
    CloudAccountStore accounts) =>
{
    try
    {
        if (!await accounts.KeyExistsAsync(sessionCode, context.RequestAborted)) return Results.NotFound();
        var result = await store.FindVideoAsync(sessionCode, videoId, context.RequestAborted);
        if (!result.HasValue) return Results.NotFound();
        context.Response.Headers["Cache-Control"] = "private, max-age=31536000, immutable";
        return Results.File(
            result.Value.Path,
            contentType: "video/mp4",
            fileDownloadName: null,
            enableRangeProcessing: true);
    }
    catch
    {
        return Results.NotFound();
    }
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

app.MapPost("/api/sessions/{sessionCode}/playback", async (
    string sessionCode,
    PlaybackCommand command,
    HttpContext context,
    RemoteSessionStore store,
    CloudAccountStore accounts) =>
{
    try
    {
        if (!await accounts.KeyExistsAsync(sessionCode, context.RequestAborted)) return Results.NotFound("The Rink ID does not exist.");
        return Results.Ok(await store.UpdatePlaybackStateAsync(
            sessionCode,
            command,
            context.RequestAborted));
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

    using var subscription = store.Subscribe(sessionCode);
    await WriteStateAsync(
        await store.GetPlaybackStateAsync(sessionCode, context.RequestAborted),
        advancePlayingPosition: true);

    try
    {
        while (!context.RequestAborted.IsCancellationRequested)
        {
            var waitForState = subscription.Reader.WaitToReadAsync(context.RequestAborted).AsTask();
            var heartbeat = Task.Delay(TimeSpan.FromSeconds(15), context.RequestAborted);
            var completed = await Task.WhenAny(waitForState, heartbeat);

            if (completed == heartbeat)
            {
                await context.Response.WriteAsync(": heartbeat\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                continue;
            }

            if (!await waitForState) break;
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
