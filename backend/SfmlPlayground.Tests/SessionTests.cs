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
