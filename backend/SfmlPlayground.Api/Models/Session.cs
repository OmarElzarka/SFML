namespace SfmlPlayground.Api.Models;

/// <summary>
/// Represents the status of an execution session.
/// </summary>
public enum SessionStatus
{
    Ready,
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
/// Metadata for an asset uploaded to a session workspace.
/// </summary>
public record SessionAsset(string Name, long Size, DateTime UploadedAt);

/// <summary>
/// Request to create or run an execution session.
/// </summary>
public record CreateSessionRequest
{
    public string? SessionId { get; init; }
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
    public IReadOnlyList<SessionAsset> Assets { get; init; } = Array.Empty<SessionAsset>();
}

/// <summary>
/// Internal representation of a running session.
/// </summary>
public class Session
{
    public string Id { get; set; } = string.Empty;
    public string ContainerId { get; set; } = string.Empty;
    public SessionStatus Status { get; set; } = SessionStatus.Ready;
    public string SourceCode { get; set; } = string.Empty;
    public string CompilerOutput { get; set; } = string.Empty;
    public string RuntimeOutput { get; set; } = string.Empty;
    public int DisplayPort { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
    public string? ErrorMessage { get; set; }
    public string WorkspacePath { get; set; } = string.Empty;
    public List<SessionAsset> Assets { get; set; } = new();

    public SessionResponse ToResponse(string hostBaseUrl) => new()
    {
        SessionId = Id,
        Status = Status,
        DisplayUrl = Status == SessionStatus.Running
            ? $"{hostBaseUrl}/vnc/?session={Id}"
            : null,
        CompilerOutput = CompilerOutput,
        ErrorMessage = ErrorMessage,
        CreatedAt = CreatedAt,
        Assets = Assets.ToList().AsReadOnly()
    };
}
