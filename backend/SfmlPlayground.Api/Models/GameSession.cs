namespace SfmlPlayground.Api.Models;

/// <summary>
/// Represents the conceptual game session in the persistent runtime architecture.
/// Tracks session metadata, active project, compilation output, and process lifecycle.
/// </summary>
public class GameSession
{
    public string SessionId { get; set; } = string.Empty;
    public int? ProjectId { get; set; }
    public string WorkspacePath { get; set; } = string.Empty;
    public string Display { get; set; } = ":99";
    public int? ProcessId { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Ready;
    public string CompilerOutput { get; set; } = string.Empty;
    public string RuntimeOutput { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public int DisplayPort { get; set; } = 6080;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;

    public SessionResponse ToResponse(string baseUrl)
    {
        return new SessionResponse
        {
            SessionId = SessionId,
            Status = Status,
            DisplayUrl = $"{baseUrl}/vnc/{SessionId}/vnc_lite.html?scale=true&path=vnc/{SessionId}/websockify",
            CompilerOutput = CompilerOutput,
            ErrorMessage = ErrorMessage,
            CreatedAt = CreatedAt,
            Assets = Array.Empty<SessionAsset>()
        };
    }
}
