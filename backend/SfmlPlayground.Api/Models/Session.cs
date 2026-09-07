namespace SfmlPlayground.Api.Models;

/// <summary>
/// Represents the status of an execution session.
/// </summary>
public enum SessionStatus
{
    Starting,
    Compiling,
    CompileError,
    Running,
    Stopping,
    Stopped,
    TimedOut,
    Error
}

/// <summary>
/// Request to create a new execution session.
/// </summary>
public record CreateSessionRequest
{
    public required string SourceCode { get; init; }
}

/// <summary>
/// Response containing session information.
/// </summary>
public record SessionResponse
{
    public required string SessionId { get; init; }
    public required SessionStatus Status { get; init; }
    public string? DisplayUrl { get; init; }
    public string? CompilerOutput { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// Internal representation of a running session.
/// </summary>
public class Session
{
    public string Id { get; set; } = string.Empty;
    public string ContainerId { get; set; } = string.Empty;
    public SessionStatus Status { get; set; }
    public string SourceCode { get; set; } = string.Empty;
    public string CompilerOutput { get; set; } = string.Empty;
    public string RuntimeOutput { get; set; } = string.Empty;
    public int DisplayPort { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
    public string? ErrorMessage { get; set; }

    public SessionResponse ToResponse(string hostBaseUrl) => new()
    {
        SessionId = Id,
        Status = Status,
        DisplayUrl = Status == SessionStatus.Running
            ? $"{hostBaseUrl}/vnc/?session={Id}"
            : null,
        CompilerOutput = CompilerOutput,
        ErrorMessage = ErrorMessage,
        CreatedAt = CreatedAt
    };
}
