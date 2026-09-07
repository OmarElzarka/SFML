using SfmlPlayground.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddSingleton<DockerSessionService>();
builder.Services.AddHostedService<SessionCleanupService>();

// Configure JSON serialization for enum strings
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

// Configure CORS for Angular dev server
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(
                "http://localhost:4200",
                "http://localhost:4201",
                "http://127.0.0.1:4200")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();

app.UseCors();

// ─── API Endpoints ──────────────────────────────────────────────────────────

// Initialize an empty session (for uploading assets prior to running)
app.MapPost("/api/sessions/init", (
    DockerSessionService sessionService,
    HttpContext context) =>
{
    try
    {
        var session = sessionService.InitializeSession();
        var scheme = context.Request.Scheme;
        var host = context.Request.Host.ToString();
        var baseUrl = $"{scheme}://{host}";
        return Results.Ok(session.ToResponse(baseUrl));
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
});

// Create or run a session
app.MapPost("/api/sessions", async (
    SfmlPlayground.Api.Models.CreateSessionRequest request,
    DockerSessionService sessionService,
    HttpContext context) =>
{
    if (string.IsNullOrWhiteSpace(request.SourceCode))
        return Results.BadRequest(new { error = "Source code is required." });

    if (request.SourceCode.Length > 100_000)
        return Results.BadRequest(new { error = "Source code exceeds maximum size (100KB)." });

    try
    {
        var session = await sessionService.CreateSessionAsync(request.SourceCode, request.SessionId);

        var scheme = context.Request.Scheme;
        var host = context.Request.Host.ToString();
        var baseUrl = $"{scheme}://{host}";

        return Results.Ok(session.ToResponse(baseUrl));
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
    catch (Exception)
    {
        return Results.Problem(
            detail: "An error occurred creating the session.",
            statusCode: 500);
    }
});

// Upload an asset to a session workspace
app.MapPost("/api/sessions/{sessionId}/assets", async (
    string sessionId,
    HttpRequest request,
    DockerSessionService sessionService) =>
{
    var session = sessionService.GetSession(sessionId);
    if (session == null)
        return Results.NotFound(new { error = "Session not found." });

    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Request must be multipart/form-data." });

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
    if (file == null || file.Length == 0)
        return Results.BadRequest(new { error = "No file was uploaded or file is empty." });

    try
    {
        using var stream = file.OpenReadStream();
        var asset = await sessionService.AddAssetAsync(sessionId, file.FileName, stream, file.Length);
        return Results.Ok(new
        {
            name = asset.Name,
            size = asset.Size,
            uploadedAt = asset.UploadedAt
        });
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: $"Asset upload failed: {ex.Message}", statusCode: 500);
    }
}).DisableAntiforgery();

// List all assets in a session
app.MapGet("/api/sessions/{sessionId}/assets", (
    string sessionId,
    DockerSessionService sessionService) =>
{
    var session = sessionService.GetSession(sessionId);
    if (session == null)
        return Results.NotFound(new { error = "Session not found." });

    var assets = sessionService.GetAssets(sessionId);
    return Results.Ok(assets.Select(a => new { name = a.Name, size = a.Size, uploadedAt = a.UploadedAt }));
});

// Delete an asset from a session workspace
app.MapDelete("/api/sessions/{sessionId}/assets/{assetName}", (
    string sessionId,
    string assetName,
    DockerSessionService sessionService) =>
{
    var session = sessionService.GetSession(sessionId);
    if (session == null)
        return Results.NotFound(new { error = "Session not found." });

    var deleted = sessionService.DeleteAsset(sessionId, assetName);
    if (!deleted)
        return Results.NotFound(new { error = $"Asset '{assetName}' not found in session." });

    return Results.Ok(new { message = $"Asset '{assetName}' deleted successfully." });
});

// Get session status
app.MapGet("/api/sessions/{sessionId}", (
    string sessionId,
    DockerSessionService sessionService,
    HttpContext context) =>
{
    var session = sessionService.GetSession(sessionId);
    if (session == null)
        return Results.NotFound(new { error = "Session not found." });

    var scheme = context.Request.Scheme;
    var host = context.Request.Host.ToString();
    var baseUrl = $"{scheme}://{host}";

    return Results.Ok(session.ToResponse(baseUrl));
});

// Stop a session
app.MapPost("/api/sessions/{sessionId}/stop", async (
    string sessionId,
    DockerSessionService sessionService) =>
{
    var session = sessionService.GetSession(sessionId);
    if (session == null)
        return Results.NotFound(new { error = "Session not found." });

    await sessionService.StopSessionAsync(sessionId);
    return Results.Ok(new { message = "Session stopped." });
});

// Heartbeat endpoint
app.MapPost("/api/sessions/{sessionId}/heartbeat", (
    string sessionId,
    DockerSessionService sessionService) =>
{
    var session = sessionService.GetSession(sessionId);
    if (session == null)
        return Results.NotFound(new { error = "Session not found." });

    session.LastHeartbeat = DateTime.UtcNow;
    return Results.Ok(new { status = session.Status.ToString() });
});

// Get VNC connection info for a session
app.MapGet("/api/sessions/{sessionId}/display", (
    string sessionId,
    DockerSessionService sessionService,
    HttpContext context) =>
{
    var session = sessionService.GetSession(sessionId);
    if (session == null)
        return Results.NotFound(new { error = "Session not found." });

    if (session.Status != SfmlPlayground.Api.Models.SessionStatus.Running)
        return Results.BadRequest(new { error = "Session is not running." });

    // Return the websockify connection info
    // The frontend will connect directly to the websockify port
    var host = context.Request.Host.Host;
    return Results.Ok(new
    {
        host = host,
        port = session.DisplayPort,
        path = "websockify"
    });
});

// List active sessions (for debugging)
app.MapGet("/api/sessions", (DockerSessionService sessionService) =>
{
    var sessions = sessionService.GetActiveSessions();
    return Results.Ok(new
    {
        count = sessions.Count,
        sessions = sessions.Select(s => new
        {
            s.Id,
            Status = s.Status.ToString(),
            s.DisplayPort,
            s.CreatedAt,
            s.LastHeartbeat
        })
    });
});

app.Run();
