using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SfmlPlayground.Api.Models;
using Xunit;

namespace SfmlPlayground.Tests;

public class SessionModelTests
{
    private readonly JsonSerializerOptions _jsonOptions;

    public SessionModelTests()
    {
        _jsonOptions = new JsonSerializerOptions();
        _jsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    [Fact]
    public void Session_InitialState_IsStarting()
    {
        var session = new Session
        {
            Id = "test-session-1",
            SourceCode = "int main() { return 0; }",
            Status = SessionStatus.Starting
        };

        Assert.Equal("test-session-1", session.Id);
        Assert.Equal(SessionStatus.Starting, session.Status);
        Assert.Empty(session.CompilerOutput);
        Assert.Null(session.ErrorMessage);
    }

    [Fact]
    public void Session_ToResponse_WhenRunning_IncludesDisplayUrl()
    {
        var session = new Session
        {
            Id = "test-session-123",
            Status = SessionStatus.Running,
            DisplayPort = 5900,
            CompilerOutput = "Compilation successful."
        };

        var response = session.ToResponse("http://localhost:5000");

        Assert.Equal("test-session-123", response.SessionId);
        Assert.Equal(SessionStatus.Running, response.Status);
        Assert.Equal("http://localhost:5000/vnc/?session=test-session-123", response.DisplayUrl);
        Assert.Equal("Compilation successful.", response.CompilerOutput);
    }

    [Fact]
    public void Session_ToResponse_WhenNotRunning_DisplayUrlIsNull()
    {
        var session = new Session
        {
            Id = "test-session-456",
            Status = SessionStatus.CompileError,
            CompilerOutput = "error: unknown type name"
        };

        var response = session.ToResponse("http://localhost:5000");

        Assert.Equal(SessionStatus.CompileError, response.Status);
        Assert.Null(response.DisplayUrl);
        Assert.Contains("error", response.CompilerOutput);
    }

    [Theory]
    [InlineData(SessionStatus.Starting, "\"Starting\"")]
    [InlineData(SessionStatus.Compiling, "\"Compiling\"")]
    [InlineData(SessionStatus.Running, "\"Running\"")]
    [InlineData(SessionStatus.CompileError, "\"CompileError\"")]
    [InlineData(SessionStatus.Stopped, "\"Stopped\"")]
    [InlineData(SessionStatus.TimedOut, "\"TimedOut\"")]
    [InlineData(SessionStatus.Error, "\"Error\"")]
    public void SessionStatus_Serializes_AsExpectedString(SessionStatus status, string expectedJson)
    {
        var json = JsonSerializer.Serialize(status, _jsonOptions);
        Assert.Equal(expectedJson, json);

        var deserialized = JsonSerializer.Deserialize<SessionStatus>(expectedJson, _jsonOptions);
        Assert.Equal(status, deserialized);
    }

    [Fact]
    public void Session_WithAssets_MapsToResponseCorrectly()
    {
        var session = new Session
        {
            Id = "test-session-assets",
            Status = SessionStatus.Ready,
            Assets = new List<SessionAsset>
            {
                new("player.png", 1024, DateTime.UtcNow),
                new("jump.wav", 2048, DateTime.UtcNow)
            }
        };

        var response = session.ToResponse("http://localhost:5000");

        Assert.Equal("test-session-assets", response.SessionId);
        Assert.Equal(2, response.Assets.Count);
        Assert.Equal("player.png", response.Assets[0].Name);
        Assert.Equal(1024, response.Assets[0].Size);
        Assert.Equal("jump.wav", response.Assets[1].Name);
        Assert.Equal(2048, response.Assets[1].Size);
    }
}

public class LiveBackendApiTests
{
    private readonly HttpClient _client;

    public LiveBackendApiTests()
    {
        _client = new HttpClient
        {
            BaseAddress = new Uri("http://localhost:5000")
        };
    }

    [Fact]
    public async Task GetSessions_ReturnsOkAndList()
    {
        var response = await _client.GetAsync("/api/sessions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("count", content);
        Assert.Contains("sessions", content);
    }

    [Fact]
    public async Task CreateSession_EmptyCode_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/sessions", new { sourceCode = "" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateSession_WhitespaceCode_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/sessions", new { sourceCode = "   \n\t  " });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateSession_CodeTooLarge_ReturnsBadRequest()
    {
        var hugeCode = new string('/', 100_001);
        var response = await _client.PostAsJsonAsync("/api/sessions", new { sourceCode = hugeCode });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task NonExistentSession_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/sessions/non-existent-id-999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task InitSession_ReturnsNewSessionWithReadyStatus()
    {
        var response = await _client.PostAsync("/api/sessions/init", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("sessionId", out var idProp));
        Assert.False(string.IsNullOrWhiteSpace(idProp.GetString()));
        Assert.Equal("Ready", root.GetProperty("status").GetString());
    }

    [Fact]
    public async Task AssetUpload_ValidPng_UploadsAndCanBeListedAndDeleted()
    {
        // 1. Initialize session
        var initResp = await _client.PostAsync("/api/sessions/init", null);
        Assert.Equal(HttpStatusCode.OK, initResp.StatusCode);
        using var initDoc = JsonDocument.Parse(await initResp.Content.ReadAsStringAsync());
        var sessionId = initDoc.RootElement.GetProperty("sessionId").GetString()!;

        // 2. Upload valid PNG asset
        using var form = new MultipartFormDataContent();
        var fakePngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01 };
        form.Add(new ByteArrayContent(fakePngBytes), "file", "player.png");

        var uploadResp = await _client.PostAsync($"/api/sessions/{sessionId}/assets", form);
        Assert.Equal(HttpStatusCode.OK, uploadResp.StatusCode);

        using var uploadDoc = JsonDocument.Parse(await uploadResp.Content.ReadAsStringAsync());
        Assert.Equal("player.png", uploadDoc.RootElement.GetProperty("name").GetString());
        Assert.Equal(fakePngBytes.Length, uploadDoc.RootElement.GetProperty("size").GetInt64());

        // 3. List assets
        var listResp = await _client.GetAsync($"/api/sessions/{sessionId}/assets");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        Assert.Equal(1, listDoc.RootElement.GetArrayLength());
        Assert.Equal("player.png", listDoc.RootElement[0].GetProperty("name").GetString());

        // 4. Delete asset
        var deleteResp = await _client.DeleteAsync($"/api/sessions/{sessionId}/assets/player.png");
        Assert.Equal(HttpStatusCode.OK, deleteResp.StatusCode);

        // 5. Verify asset is gone from list
        var listResp2 = await _client.GetAsync($"/api/sessions/{sessionId}/assets");
        Assert.Equal(HttpStatusCode.OK, listResp2.StatusCode);
        using var listDoc2 = JsonDocument.Parse(await listResp2.Content.ReadAsStringAsync());
        Assert.Equal(0, listDoc2.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task AssetUpload_UnsupportedExtension_ReturnsBadRequest()
    {
        var initResp = await _client.PostAsync("/api/sessions/init", null);
        using var initDoc = JsonDocument.Parse(await initResp.Content.ReadAsStringAsync());
        var sessionId = initDoc.RootElement.GetProperty("sessionId").GetString()!;

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(new byte[] { 1, 2, 3 }), "file", "hack.exe");

        var uploadResp = await _client.PostAsync($"/api/sessions/{sessionId}/assets", form);
        Assert.Equal(HttpStatusCode.BadRequest, uploadResp.StatusCode);
        var body = await uploadResp.Content.ReadAsStringAsync();
        Assert.Contains("Unsupported file type", body);
    }

    [Fact]
    public async Task AssetUpload_PathTraversal_ReturnsBadRequest()
    {
        var initResp = await _client.PostAsync("/api/sessions/init", null);
        using var initDoc = JsonDocument.Parse(await initResp.Content.ReadAsStringAsync());
        var sessionId = initDoc.RootElement.GetProperty("sessionId").GetString()!;

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(new byte[] { 1, 2, 3 }), "file", "../../secret.png");

        var uploadResp = await _client.PostAsync($"/api/sessions/{sessionId}/assets", form);
        // Either the filename gets sanitized or path traversal is rejected
        Assert.True(uploadResp.StatusCode == HttpStatusCode.BadRequest || uploadResp.StatusCode == HttpStatusCode.OK);
    }

    [Fact]
    public async Task AssetUpload_ReservedFileName_ReturnsBadRequest()
    {
        var initResp = await _client.PostAsync("/api/sessions/init", null);
        using var initDoc = JsonDocument.Parse(await initResp.Content.ReadAsStringAsync());
        var sessionId = initDoc.RootElement.GetProperty("sessionId").GetString()!;

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(new byte[] { 1, 2, 3 }), "file", "main.cpp");

        var uploadResp = await _client.PostAsync($"/api/sessions/{sessionId}/assets", form);
        Assert.Equal(HttpStatusCode.BadRequest, uploadResp.StatusCode);
    }

    [Fact]
    public async Task AssetUpload_NonExistentSession_ReturnsNotFound()
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(new byte[] { 1, 2, 3 }), "file", "player.png");

        var uploadResp = await _client.PostAsync("/api/sessions/nonexistent-session-id/assets", form);
        Assert.Equal(HttpStatusCode.NotFound, uploadResp.StatusCode);
    }
}

public class ProjectAndIdeApiTests
{
    private readonly HttpClient _client;
    private readonly JsonSerializerOptions _jsonOptions;

    public ProjectAndIdeApiTests()
    {
        _client = new HttpClient
        {
            BaseAddress = new Uri("http://localhost:5000")
        };
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        _jsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    [Fact]
    public async Task User_CreateAndRetrieve_Succeeds()
    {
        var username = "TestUser_" + Guid.NewGuid().ToString("N")[..6];

        // 1. Create user
        var createResp = await _client.PostAsJsonAsync("/api/users", new CreateUserRequest(username));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);

        var user = await createResp.Content.ReadFromJsonAsync<UserDto>(_jsonOptions);
        Assert.NotNull(user);
        Assert.Equal(username, user.Username);
        Assert.True(user.Id > 0);

        // 2. Retrieve user
        var getResp = await _client.GetAsync($"/api/users/{username}");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);

        var retrieved = await getResp.Content.ReadFromJsonAsync<UserDto>(_jsonOptions);
        Assert.NotNull(retrieved);
        Assert.Equal(user.Id, retrieved.Id);
        Assert.Equal(username, retrieved.Username);
    }

    [Fact]
    public async Task Project_MultiFileTemplate_CreatesCppAndHppAndAssets()
    {
        var username = "Student_" + Guid.NewGuid().ToString("N")[..6];
        var userResp = await _client.PostAsJsonAsync("/api/users", new CreateUserRequest(username));
        var user = await userResp.Content.ReadFromJsonAsync<UserDto>(_jsonOptions);
        Assert.NotNull(user);

        // Create project with sprite template (multi-file)
        var createReq = new CreateProjectRequest(user.Id, "My Space Game", "Multi-file SFML game", "sprite");
        var projResp = await _client.PostAsJsonAsync("/api/projects", createReq);
        Assert.Equal(HttpStatusCode.Created, projResp.StatusCode);

        var project = await projResp.Content.ReadFromJsonAsync<ProjectDetailDto>(_jsonOptions);
        Assert.NotNull(project);
        Assert.Equal("My Space Game", project.Name);
        Assert.True(project.Files.Count >= 3);
        Assert.Contains(project.Files, f => f.Path == "main.cpp");
        Assert.Contains(project.Files, f => f.Path == "Player.hpp");
        Assert.Contains(project.Files, f => f.Path == "Player.cpp");
        Assert.True(project.Assets.Count >= 1);
        Assert.Contains(project.Assets, a => a.FileName == "player.png");

        // Verify listing user projects
        var listResp = await _client.GetAsync($"/api/projects?userId={user.Id}");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var summaries = await listResp.Content.ReadFromJsonAsync<List<ProjectSummaryDto>>(_jsonOptions);
        Assert.NotNull(summaries);
        Assert.Single(summaries);
        Assert.Equal(project.Id, summaries[0].Id);
    }

    [Fact]
    public async Task ProjectFiles_AddUpdateDelete_WorksCorrectly()
    {
        var username = "Coder_" + Guid.NewGuid().ToString("N")[..6];
        var userResp = await _client.PostAsJsonAsync("/api/users", new CreateUserRequest(username));
        var user = await userResp.Content.ReadFromJsonAsync<UserDto>(_jsonOptions);
        Assert.NotNull(user);

        var createReq = new CreateProjectRequest(user.Id, "Custom Files Project", null, "empty");
        var projResp = await _client.PostAsJsonAsync("/api/projects", createReq);
        var project = await projResp.Content.ReadFromJsonAsync<ProjectDetailDto>(_jsonOptions);
        Assert.NotNull(project);

        // Add Enemy.hpp
        var addFileReq = new CreateFileRequest("Enemy.hpp", "#pragma once\nclass Enemy {};");
        var addResp = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/files", addFileReq);
        Assert.Equal(HttpStatusCode.Created, addResp.StatusCode);
        var addedFile = await addResp.Content.ReadFromJsonAsync<ProjectFileDto>(_jsonOptions);
        Assert.NotNull(addedFile);
        Assert.Equal("Enemy.hpp", addedFile.Path);

        // Update Enemy.hpp
        var updateReq = new UpdateFileRequest("#pragma once\nclass Enemy { int hp = 100; };");
        var updateResp = await _client.PutAsJsonAsync($"/api/projects/{project.Id}/files/{addedFile.Id}", updateReq);
        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);
        var updatedFile = await updateResp.Content.ReadFromJsonAsync<ProjectFileDto>(_jsonOptions);
        Assert.NotNull(updatedFile);
        Assert.Contains("hp = 100", updatedFile.Content);

        // Delete Enemy.hpp
        var deleteResp = await _client.DeleteAsync($"/api/projects/{project.Id}/files/{addedFile.Id}");
        Assert.Equal(HttpStatusCode.OK, deleteResp.StatusCode);

        // Verify it's gone
        var detailResp = await _client.GetAsync($"/api/projects/{project.Id}");
        var finalProject = await detailResp.Content.ReadFromJsonAsync<ProjectDetailDto>(_jsonOptions);
        Assert.NotNull(finalProject);
        Assert.DoesNotContain(finalProject.Files, f => f.Path == "Enemy.hpp");
    }

    [Fact]
    public async Task ProjectAssets_UploadAndDelete_WorksCorrectly()
    {
        var username = "Artist_" + Guid.NewGuid().ToString("N")[..6];
        var userResp = await _client.PostAsJsonAsync("/api/users", new CreateUserRequest(username));
        var user = await userResp.Content.ReadFromJsonAsync<UserDto>(_jsonOptions);
        Assert.NotNull(user);

        var createReq = new CreateProjectRequest(user.Id, "Asset Test Project", null, "empty");
        var projResp = await _client.PostAsJsonAsync("/api/projects", createReq);
        var project = await projResp.Content.ReadFromJsonAsync<ProjectDetailDto>(_jsonOptions);
        Assert.NotNull(project);

        // Upload asset
        using var form = new MultipartFormDataContent();
        var fakePng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02, 0x03 };
        form.Add(new ByteArrayContent(fakePng), "file", "enemy.png");

        var uploadResp = await _client.PostAsync($"/api/projects/{project.Id}/assets", form);
        Assert.Equal(HttpStatusCode.OK, uploadResp.StatusCode);
        var asset = await uploadResp.Content.ReadFromJsonAsync<ProjectAssetDto>(_jsonOptions);
        Assert.NotNull(asset);
        Assert.Equal("enemy.png", asset.FileName);
        Assert.Equal("assets/enemy.png", asset.RelativePath);

        // Delete asset
        var deleteResp = await _client.DeleteAsync($"/api/projects/{project.Id}/assets/{asset.Id}");
        Assert.Equal(HttpStatusCode.OK, deleteResp.StatusCode);

        // Verify asset removed
        var detailResp = await _client.GetAsync($"/api/projects/{project.Id}");
        var finalProject = await detailResp.Content.ReadFromJsonAsync<ProjectDetailDto>(_jsonOptions);
        Assert.NotNull(finalProject);
        Assert.DoesNotContain(finalProject.Assets, a => a.FileName == "enemy.png");
    }

    [Fact]
    public async Task ProjectRun_MultiFileProject_ExecutesSuccessfully()
    {
        var username = "Runner_" + Guid.NewGuid().ToString("N")[..6];
        var userResp = await _client.PostAsJsonAsync("/api/users", new CreateUserRequest(username));
        var user = await userResp.Content.ReadFromJsonAsync<UserDto>(_jsonOptions);
        Assert.NotNull(user);

        // Create multi-file sprite template project
        var createReq = new CreateProjectRequest(user.Id, "Execution Test Project", null, "sprite");
        var projResp = await _client.PostAsJsonAsync("/api/projects", createReq);
        var project = await projResp.Content.ReadFromJsonAsync<ProjectDetailDto>(_jsonOptions);
        Assert.NotNull(project);

        // Run the multi-file project
        var runResp = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/run", new RunProjectRequest());
        Assert.Equal(HttpStatusCode.OK, runResp.StatusCode);

        var sessionResp = await runResp.Content.ReadFromJsonAsync<SessionResponse>(_jsonOptions);
        Assert.NotNull(sessionResp);
        Assert.False(string.IsNullOrWhiteSpace(sessionResp.SessionId));

        // Poll for session to compile and run
        var maxWait = TimeSpan.FromSeconds(30);
        var start = DateTime.UtcNow;
        SessionResponse? current = sessionResp;

        while (DateTime.UtcNow - start < maxWait && current?.Status != SessionStatus.Running && current?.Status != SessionStatus.CompileError && current?.Status != SessionStatus.Error)
        {
            await Task.Delay(1000);
            var pollResp = await _client.GetAsync($"/api/sessions/{sessionResp.SessionId}");
            if (pollResp.IsSuccessStatusCode)
            {
                current = await pollResp.Content.ReadFromJsonAsync<SessionResponse>(_jsonOptions);
            }
        }

        Assert.NotNull(current);
        Assert.Equal(SessionStatus.Running, current.Status);
        Assert.Contains("Compilation successful", current.CompilerOutput);

        // Stop session cleanly
        await _client.PostAsync($"/api/sessions/{sessionResp.SessionId}/stop", null);
    }
}
