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

// Create a new session
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
        var session = await sessionService.CreateSessionAsync(request.SourceCode);

        var scheme = context.Request.Scheme;
        var host = context.Request.Host.ToString();
        var baseUrl = $"{scheme}://{host}";

        return Results.Ok(session.ToResponse(baseUrl));
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            detail: "An error occurred creating the session.",
            statusCode: 500);
    }
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
