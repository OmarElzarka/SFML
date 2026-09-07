using System.Text.Json.Serialization;

namespace SfmlPlayground.Api.Models;

/// <summary>
/// User profile for project ownership.
/// </summary>
public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public List<Project> Projects { get; set; } = new();
}

/// <summary>
/// Persistent C++ SFML project.
/// </summary>
public class Project
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public User? User { get; set; }

    public List<ProjectFile> Files { get; set; } = new();
    public List<ProjectAsset> Assets { get; set; } = new();
}

/// <summary>
/// Source code or header file (.cpp, .hpp) in a project.
/// </summary>
public class ProjectFile
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public Project? Project { get; set; }
}

/// <summary>
/// Uploaded binary asset (image, font, audio) in a project.
/// </summary>
public class ProjectAsset
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string StoragePath { get; set; } = string.Empty;
    public long Size { get; set; }
    public string? ContentType { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public Project? Project { get; set; }
}

// ─── DTOs ───────────────────────────────────────────────────────────────────

public record UserDto(int Id, string Username, DateTime CreatedAt, DateTime LastSeenAt);
public record CreateUserRequest(string Username);

public record ProjectSummaryDto(
    int Id,
    int UserId,
    string Name,
    string? Description,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int FileCount,
    int AssetCount);

public record ProjectFileDto(
    int Id,
    int ProjectId,
    string Path,
    string Content,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record ProjectAssetDto(
    int Id,
    int ProjectId,
    string FileName,
    string RelativePath,
    long Size,
    string? ContentType,
    DateTime CreatedAt);

public record ProjectDetailDto(
    int Id,
    int UserId,
    string Name,
    string? Description,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    List<ProjectFileDto> Files,
    List<ProjectAssetDto> Assets);

public record CreateProjectRequest(
    int UserId,
    string Name,
    string? Description,
    string? TemplateKey);

public record UpdateProjectRequest(
    string Name,
    string? Description);

public record CreateFileRequest(
    string Path,
    string Content);

public record UpdateFileRequest(
    string Content,
    string? Path = null);

public record RunProjectRequest(
    string? SessionId = null);
