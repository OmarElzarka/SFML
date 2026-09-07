using Microsoft.EntityFrameworkCore;
using SfmlPlayground.Api.Data;
using SfmlPlayground.Api.Models;
using SfmlPlayground.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Add database context
builder.Services.AddDbContext<PlaygroundDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// Add services
builder.Services.AddScoped<ProjectService>();
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

// ─── User Endpoints ─────────────────────────────────────────────────────────

// Get or create user by username
app.MapPost("/api/users", async (
    CreateUserRequest request,
    ProjectService projectService) =>
{
    if (string.IsNullOrWhiteSpace(request.Username))
        return Results.BadRequest(new { error = "Username is required." });

    try
    {
        var user = await projectService.GetOrCreateUserAsync(request.Username);
        return Results.Ok(user);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Get user profile
app.MapGet("/api/users/{username}", async (
    string username,
    ProjectService projectService) =>
{
    var user = await projectService.GetUserAsync(username);
    if (user == null)
        return Results.NotFound(new { error = "User not found." });

    return Results.Ok(user);
});

// ─── Project Endpoints ──────────────────────────────────────────────────────

// List user's projects
app.MapGet("/api/projects", async (
    int? userId,
    string? username,
    ProjectService projectService) =>
{
    if (!userId.HasValue && string.IsNullOrWhiteSpace(username))
        return Results.BadRequest(new { error = "userId or username query parameter is required." });

    int uid;
    if (userId.HasValue)
    {
        uid = userId.Value;
    }
    else
    {
        var user = await projectService.GetUserAsync(username!);
        if (user == null)
            return Results.NotFound(new { error = "User not found." });
        uid = user.Id;
    }

    var projects = await projectService.GetUserProjectsAsync(uid);
    return Results.Ok(projects);
});

// Create project
app.MapPost("/api/projects", async (
    CreateProjectRequest request,
    ProjectService projectService) =>
{
    if (string.IsNullOrWhiteSpace(request.Name))
        return Results.BadRequest(new { error = "Project name is required." });

    try
    {
        var project = await projectService.CreateProjectAsync(request);
        return Results.Created($"/api/projects/{project.Id}", project);
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Get project details (includes files and assets)
app.MapGet("/api/projects/{projectId:int}", async (
    int projectId,
    ProjectService projectService) =>
{
    var project = await projectService.GetProjectDetailAsync(projectId);
    if (project == null)
        return Results.NotFound(new { error = "Project not found." });

    return Results.Ok(project);
});

// Rename / update project
app.MapPut("/api/projects/{projectId:int}", async (
    int projectId,
    UpdateProjectRequest request,
    ProjectService projectService) =>
{
    if (string.IsNullOrWhiteSpace(request.Name))
        return Results.BadRequest(new { error = "Project name cannot be empty." });

    var updated = await projectService.UpdateProjectAsync(projectId, request);
    if (!updated)
        return Results.NotFound(new { error = "Project not found." });

    var project = await projectService.GetProjectDetailAsync(projectId);
    return Results.Ok(project);
});

// Delete project
app.MapDelete("/api/projects/{projectId:int}", async (
    int projectId,
    ProjectService projectService) =>
{
    var deleted = await projectService.DeleteProjectAsync(projectId);
    if (!deleted)
        return Results.NotFound(new { error = "Project not found." });

    return Results.Ok(new { message = "Project deleted successfully." });
});

// ─── Project File Endpoints ─────────────────────────────────────────────────

// Add file to project
app.MapPost("/api/projects/{projectId:int}/files", async (
    int projectId,
    CreateFileRequest request,
    ProjectService projectService) =>
{
    if (string.IsNullOrWhiteSpace(request.Path))
        return Results.BadRequest(new { error = "File path is required." });

    try
    {
        var file = await projectService.AddFileAsync(projectId, request);
        return Results.Created($"/api/projects/{projectId}/files/{file.Id}", file);
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound(new { error = "Project not found." });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Update file content / rename
app.MapPut("/api/projects/{projectId:int}/files/{fileId:int}", async (
    int projectId,
    int fileId,
    UpdateFileRequest request,
    ProjectService projectService) =>
{
    try
    {
        var file = await projectService.UpdateFileAsync(projectId, fileId, request);
        return Results.Ok(file);
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound(new { error = "File or project not found." });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Delete file
app.MapDelete("/api/projects/{projectId:int}/files/{fileId:int}", async (
    int projectId,
    int fileId,
    ProjectService projectService) =>
{
    try
    {
        var deleted = await projectService.DeleteFileAsync(projectId, fileId);
        if (!deleted)
            return Results.NotFound(new { error = "File not found." });

        return Results.Ok(new { message = "File deleted successfully." });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// ─── Project Asset Endpoints ────────────────────────────────────────────────

// Upload asset to persistent project storage
app.MapPost("/api/projects/{projectId:int}/assets", async (
    int projectId,
    HttpRequest request,
    ProjectService projectService) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Request must be multipart/form-data." });

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
    if (file == null || file.Length == 0)
        return Results.BadRequest(new { error = "No file was uploaded or file is empty." });

    try
    {
        var asset = await projectService.AddAssetAsync(projectId, file);
        return Results.Ok(asset);
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound(new { error = "Project not found." });
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

// Delete asset from project
app.MapDelete("/api/projects/{projectId:int}/assets/{assetId:int}", async (
    int projectId,
    int assetId,
    ProjectService projectService) =>
{
    var deleted = await projectService.DeleteAssetAsync(projectId, assetId);
    if (!deleted)
        return Results.NotFound(new { error = "Asset not found." });

    return Results.Ok(new { message = "Asset deleted successfully." });
});

// ─── Run Project Endpoint ───────────────────────────────────────────────────

app.MapPost("/api/projects/{projectId:int}/run", async (
    int projectId,
    RunProjectRequest? request,
    ProjectService projectService,
    DockerSessionService sessionService,
    HttpContext context) =>
{
    var project = await projectService.GetProjectEntityWithFilesAndAssetsAsync(projectId);
    if (project == null)
        return Results.NotFound(new { error = "Project not found." });

    if (project.Files.Count == 0)
        return Results.BadRequest(new { error = "Project has no files to run." });

    try
    {
        var session = await sessionService.CreateProjectSessionAsync(
            project.Files,
            project.Assets,
            request?.SessionId);

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
            detail: $"An error occurred starting the project execution session: {ex.Message}",
            statusCode: 500);
    }
});

// Ensure database and schema exist on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.Run();
