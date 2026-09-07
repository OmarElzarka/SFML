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
}
