using Microsoft.EntityFrameworkCore;
using SfmlPlayground.Api.Data;
using SfmlPlayground.Api.Models;

namespace SfmlPlayground.Api.Services;

public class ProjectService
{
    private readonly PlaygroundDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly IAssetStorageService _assetStorage;
    private readonly ILogger<ProjectService> _logger;

    public static readonly HashSet<string> AllowedAssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".psd",
        ".wav", ".ogg", ".flac",
        ".ttf", ".otf"
    };

    public const long MaxFileSizeBytes = 10 * 1024 * 1024;    // 10 MB
    public const long MaxTotalSizeBytes = 20 * 1024 * 1024;   // 20 MB
    public const int MaxAssetCount = 20;

    public ProjectService(PlaygroundDbContext db, IWebHostEnvironment env, IAssetStorageService assetStorage, ILogger<ProjectService> logger)
    {
        _db = db;
        _env = env;
        _assetStorage = assetStorage;
        _logger = logger;
    }

    public string GetProjectStorageDir(int projectId)
    {
        var baseDir = Path.Combine(_env.ContentRootPath, "storage", "projects", projectId.ToString());
        var assetsDir = Path.Combine(baseDir, "assets");
        Directory.CreateDirectory(assetsDir);
        return baseDir;
    }

    // ─── User Operations ────────────────────────────────────────────────────────

    public async Task<UserDto> GetOrCreateUserAsync(string username, CancellationToken ct = default)
    {
        var cleanName = username.Trim();
        if (string.IsNullOrWhiteSpace(cleanName))
            throw new ArgumentException("Username cannot be empty.", nameof(username));

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == cleanName, ct);
        if (user == null)
        {
            user = new User
            {
                Username = cleanName,
                CreatedAt = DateTime.UtcNow,
                LastSeenAt = DateTime.UtcNow
            };
            _db.Users.Add(user);
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Created new user '{Username}' (ID: {UserId})", cleanName, user.Id);
        }
        else
        {
            user.LastSeenAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        return new UserDto(user.Id, user.Username, user.CreatedAt, user.LastSeenAt);
    }

    public async Task<UserDto?> GetUserAsync(string username, CancellationToken ct = default)
    {
        var cleanName = username.Trim();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == cleanName, ct);
        return user == null ? null : new UserDto(user.Id, user.Username, user.CreatedAt, user.LastSeenAt);
    }

    // ─── Project Operations ─────────────────────────────────────────────────────

    public async Task<List<ProjectSummaryDto>> GetUserProjectsAsync(int userId, CancellationToken ct = default)
    {
        return await _db.Projects
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => new ProjectSummaryDto(
                p.Id,
                p.UserId,
                p.Name,
                p.Description,
                p.CreatedAt,
                p.UpdatedAt,
                p.Files.Count,
                p.Assets.Count))
            .ToListAsync(ct);
    }

    public async Task<ProjectDetailDto?> GetProjectDetailAsync(int projectId, CancellationToken ct = default)
    {
        var project = await _db.Projects
            .Include(p => p.Files)
            .Include(p => p.Assets)
            .FirstOrDefaultAsync(p => p.Id == projectId, ct);

        if (project == null) return null;

        return new ProjectDetailDto(
            project.Id,
            project.UserId,
            project.Name,
            project.Description,
            project.CreatedAt,
            project.UpdatedAt,
            project.Files.OrderBy(f => f.Path).Select(f => new ProjectFileDto(f.Id, f.ProjectId, f.Path, f.Content, f.CreatedAt, f.UpdatedAt)).ToList(),
            project.Assets.OrderBy(a => a.FileName).Select(a => new ProjectAssetDto(a.Id, a.ProjectId, a.FileName, a.RelativePath, a.Size, a.ContentType, a.CreatedAt)).ToList());
    }

    public async Task<Project> GetProjectEntityWithFilesAndAssetsAsync(int projectId, CancellationToken ct = default)
    {
        return await _db.Projects
            .Include(p => p.Files)
            .Include(p => p.Assets)
            .FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new KeyNotFoundException($"Project '{projectId}' not found.");
    }

    public async Task<ProjectDetailDto> CreateProjectAsync(CreateProjectRequest req, CancellationToken ct = default)
    {
        var user = await _db.Users.FindAsync(new object[] { req.UserId }, ct)
            ?? throw new KeyNotFoundException($"User '{req.UserId}' not found.");

        var project = new Project
        {
            UserId = user.Id,
            Name = string.IsNullOrWhiteSpace(req.Name) ? "Untitled SFML Project" : req.Name.Trim(),
            Description = req.Description,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Projects.Add(project);
        await _db.SaveChangesAsync(ct);

        // Ensure storage directory
        GetProjectStorageDir(project.Id);

        // Apply starter template
        ApplyTemplate(project, req.TemplateKey);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Created project '{ProjectName}' (ID: {ProjectId}) for user {UserId}", project.Name, project.Id, user.Id);
        return (await GetProjectDetailAsync(project.Id, ct))!;
    }

    public async Task<bool> UpdateProjectAsync(int projectId, UpdateProjectRequest req, CancellationToken ct = default)
    {
        var project = await _db.Projects.FindAsync(new object[] { projectId }, ct);
        if (project == null) return false;

        if (!string.IsNullOrWhiteSpace(req.Name))
            project.Name = req.Name.Trim();
        project.Description = req.Description;
        project.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteProjectAsync(int projectId, CancellationToken ct = default)
    {
        var project = await _db.Projects.FindAsync(new object[] { projectId }, ct);
        if (project == null) return false;

        _db.Projects.Remove(project);
        await _db.SaveChangesAsync(ct);

        // Delete physical storage directory
        var baseDir = Path.Combine(_env.ContentRootPath, "storage", "projects", projectId.ToString());
        if (Directory.Exists(baseDir))
        {
            try
            {
                Directory.Delete(baseDir, recursive: true);
                _logger.LogInformation("Deleted storage directory for project {ProjectId}", projectId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete storage directory {Dir}", baseDir);
            }
        }

        return true;
    }

    // ─── File Operations ────────────────────────────────────────────────────────

    public async Task<ProjectFileDto> AddFileAsync(int projectId, CreateFileRequest req, CancellationToken ct = default)
    {
        var project = await _db.Projects.FindAsync(new object[] { projectId }, ct)
            ?? throw new KeyNotFoundException($"Project '{projectId}' not found.");

        var cleanPath = req.Path?.Trim().Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(cleanPath))
            throw new ArgumentException("File path cannot be empty.");

        if (cleanPath.Contains(".."))
            throw new ArgumentException("Path traversal is not allowed.");

        var ext = Path.GetExtension(cleanPath).ToLowerInvariant();
        if (ext != ".cpp" && ext != ".hpp" && ext != ".h" && ext != ".txt")
            throw new ArgumentException($"Invalid source file extension: '{ext}'. Allowed: .cpp, .hpp, .h, .txt");

        var exists = await _db.ProjectFiles.AnyAsync(f => f.ProjectId == projectId && f.Path == cleanPath, ct);
        if (exists)
            throw new InvalidOperationException($"File '{cleanPath}' already exists in this project.");

        var file = new ProjectFile
        {
            ProjectId = projectId,
            Path = cleanPath,
            Content = req.Content ?? string.Empty,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.ProjectFiles.Add(file);
        project.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return new ProjectFileDto(file.Id, file.ProjectId, file.Path, file.Content, file.CreatedAt, file.UpdatedAt);
    }

    public async Task<ProjectFileDto> UpdateFileAsync(int projectId, int fileId, UpdateFileRequest req, CancellationToken ct = default)
    {
        var file = await _db.ProjectFiles.FirstOrDefaultAsync(f => f.ProjectId == projectId && f.Id == fileId, ct)
            ?? throw new KeyNotFoundException($"File '{fileId}' not found in project '{projectId}'.");

        file.Content = req.Content ?? string.Empty;
        file.UpdatedAt = DateTime.UtcNow;

        var project = await _db.Projects.FindAsync(new object[] { projectId }, ct);
        if (project != null)
            project.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return new ProjectFileDto(file.Id, file.ProjectId, file.Path, file.Content, file.CreatedAt, file.UpdatedAt);
    }

    public async Task<bool> DeleteFileAsync(int projectId, int fileId, CancellationToken ct = default)
    {
        var file = await _db.ProjectFiles.FirstOrDefaultAsync(f => f.ProjectId == projectId && f.Id == fileId, ct);
        if (file == null) return false;

        _db.ProjectFiles.Remove(file);

        var project = await _db.Projects.FindAsync(new object[] { projectId }, ct);
        if (project != null)
            project.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return true;
    }

    // ─── Asset Operations ───────────────────────────────────────────────────────

    public async Task<ProjectAssetDto> AddAssetAsync(int projectId, IFormFile formFile, CancellationToken ct = default)
    {
        var project = await _db.Projects
            .Include(p => p.Assets)
            .FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new KeyNotFoundException($"Project '{projectId}' not found.");

        if (formFile == null || formFile.Length == 0)
            throw new ArgumentException("Asset file cannot be empty.");

        var fileName = Path.GetFileName(formFile.FileName).Trim();
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("Invalid asset filename.");

        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || fileName.Contains(".."))
            throw new ArgumentException("Filename contains invalid characters or path traversal.");

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (!AllowedAssetExtensions.Contains(ext))
            throw new ArgumentException($"Unsupported asset extension: '{ext}'. Allowed: {string.Join(", ", AllowedAssetExtensions)}");

        if (formFile.Length > MaxFileSizeBytes)
            throw new ArgumentException($"Asset exceeds the 10 MB limit ({formFile.Length / (1024.0 * 1024.0):F1} MB).");

        if (project.Assets.Count >= MaxAssetCount && !project.Assets.Any(a => a.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Maximum asset limit reached ({MaxAssetCount} files).");

        long currentTotal = project.Assets.Where(a => !a.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase)).Sum(a => a.Size);
        if (currentTotal + formFile.Length > MaxTotalSizeBytes)
            throw new InvalidOperationException($"Total assets size would exceed 20 MB limit.");

        using var stream = formFile.OpenReadStream();
        var storagePath = await _assetStorage.UploadAssetAsync(projectId, fileName, stream, formFile.ContentType, ct);

        var asset = project.Assets.FirstOrDefault(a => a.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (asset == null)
        {
            asset = new ProjectAsset
            {
                ProjectId = projectId,
                FileName = fileName,
                RelativePath = $"assets/{fileName}",
                StoragePath = storagePath,
                Size = formFile.Length,
                ContentType = formFile.ContentType,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _db.ProjectAssets.Add(asset);
        }
        else
        {
            asset.Size = formFile.Length;
            asset.ContentType = formFile.ContentType;
            asset.StoragePath = storagePath;
            asset.UpdatedAt = DateTime.UtcNow;
        }

        project.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Saved asset '{FileName}' ({Size} bytes) for project {ProjectId}", fileName, formFile.Length, projectId);
        return new ProjectAssetDto(asset.Id, asset.ProjectId, asset.FileName, asset.RelativePath, asset.Size, asset.ContentType, asset.CreatedAt);
    }

    public async Task<bool> DeleteAssetAsync(int projectId, int assetId, CancellationToken ct = default)
    {
        var asset = await _db.ProjectAssets.FirstOrDefaultAsync(a => a.ProjectId == projectId && a.Id == assetId, ct);
        if (asset == null) return false;

        _db.ProjectAssets.Remove(asset);

        var project = await _db.Projects.FindAsync(new object[] { projectId }, ct);
        if (project != null)
            project.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        try
        {
            await _assetStorage.DeleteAssetAsync(asset.StoragePath, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete asset from storage {Path}", asset.StoragePath);
        }

        return true;
    }

    // ─── Templates ──────────────────────────────────────────────────────────────

    private void ApplyTemplate(Project project, string? templateKey)
    {
        switch (templateKey?.ToLowerInvariant())
        {
            case "shapes":
                project.Files.Add(new ProjectFile
                {
                    Path = "main.cpp",
                    Content = @"#include <SFML/Graphics.hpp>

int main()
{
    sf::RenderWindow window(sf::VideoMode(640, 480), ""SFML - Shapes"");
    window.setFramerateLimit(60);

    sf::CircleShape circle(50.f);
    circle.setFillColor(sf::Color(88, 166, 255));
    circle.setPosition(100.f, 190.f);

    sf::RectangleShape rect(sf::Vector2f(120.f, 80.f));
    rect.setFillColor(sf::Color(255, 123, 114));
    rect.setPosition(400.f, 200.f);

    while (window.isOpen())
    {
        sf::Event event;
        while (window.pollEvent(event))
        {
            if (event.type == sf::Event::Closed)
                window.close();
        }

        circle.rotate(1.f);
        rect.rotate(-1.f);

        window.clear(sf::Color(22, 27, 34));
        window.draw(circle);
        window.draw(rect);
        window.display();
    }
    return 0;
}"
                });
                break;

            case "texture":
            case "sprite":
                // Multi-file template with Player.hpp and Player.cpp
                project.Files.Add(new ProjectFile
                {
                    Path = "Player.hpp",
                    Content = @"#pragma once
#include <SFML/Graphics.hpp>

class Player
{
public:
    Player();
    bool load(const std::string& texturePath);
    void update();
    void draw(sf::RenderWindow& window);

private:
    sf::Texture m_texture;
    sf::Sprite m_sprite;
    float m_speed;
};
"
                });

                project.Files.Add(new ProjectFile
                {
                    Path = "Player.cpp",
                    Content = @"#include ""Player.hpp""
#include <iostream>

Player::Player()
    : m_speed(5.f)
{
}

bool Player::load(const std::string& texturePath)
{
    if (!m_texture.loadFromFile(texturePath))
    {
        std::cerr << ""Failed to load "" << texturePath << ""! Creating fallback circle shape.\n"";
        return false;
    }
    m_sprite.setTexture(m_texture);
    m_sprite.setOrigin(m_texture.getSize().x / 2.f, m_texture.getSize().y / 2.f);
    m_sprite.setPosition(320.f, 240.f);
    m_sprite.setScale(2.f, 2.f);
    return true;
}

void Player::update()
{
    if (sf::Keyboard::isKeyPressed(sf::Keyboard::Left) || sf::Keyboard::isKeyPressed(sf::Keyboard::A))
        m_sprite.move(-m_speed, 0.f);
    if (sf::Keyboard::isKeyPressed(sf::Keyboard::Right) || sf::Keyboard::isKeyPressed(sf::Keyboard::D))
        m_sprite.move(m_speed, 0.f);
    if (sf::Keyboard::isKeyPressed(sf::Keyboard::Up) || sf::Keyboard::isKeyPressed(sf::Keyboard::W))
        m_sprite.move(0.f, -m_speed);
    if (sf::Keyboard::isKeyPressed(sf::Keyboard::Down) || sf::Keyboard::isKeyPressed(sf::Keyboard::S))
        m_sprite.move(0.f, m_speed);
}

void Player::draw(sf::RenderWindow& window)
{
    window.draw(m_sprite);
}
"
                });

                project.Files.Add(new ProjectFile
                {
                    Path = "main.cpp",
                    Content = @"#include <SFML/Graphics.hpp>
#include ""Player.hpp""

int main()
{
    sf::RenderWindow window(sf::VideoMode(640, 480), ""SFML - Multi-File Sprite Demo"");
    window.setFramerateLimit(60);

    Player player;
    // Supports both assets/player.png and player.png
    player.load(""assets/player.png"");

    while (window.isOpen())
    {
        sf::Event event;
        while (window.pollEvent(event))
        {
            if (event.type == sf::Event::Closed)
                window.close();
        }

        player.update();

        window.clear(sf::Color(22, 27, 34));
        player.draw(window);
        window.display();
    }
    return 0;
}"
                });

                // Generate a starter player.png asset for this project
                try
                {
                    var starterDir = Path.Combine(GetProjectStorageDir(project.Id), "assets");
                    var starterFile = Path.Combine(starterDir, "player.png");
                    CreateDefaultShipPng(starterFile);
                    var fi = new FileInfo(starterFile);
                    project.Assets.Add(new ProjectAsset
                    {
                        FileName = "player.png",
                        RelativePath = "assets/player.png",
                        StoragePath = starterFile,
                        Size = fi.Length,
                        ContentType = "image/png",
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not generate starter player.png");
                }
                break;

            case "game":
            case "ball":
                project.Files.Add(new ProjectFile
                {
                    Path = "Ball.hpp",
                    Content = @"#pragma once
#include <SFML/Graphics.hpp>

class Ball
{
public:
    Ball(float x, float y, float radius);
    void update(const sf::RenderWindow& window);
    void draw(sf::RenderWindow& window);

private:
    sf::CircleShape m_shape;
    sf::Vector2f m_velocity;
};
"
                });

                project.Files.Add(new ProjectFile
                {
                    Path = "Ball.cpp",
                    Content = @"#include ""Ball.hpp""

Ball::Ball(float x, float y, float radius)
    : m_velocity(4.f, 3.f)
{
    m_shape.setRadius(radius);
    m_shape.setFillColor(sf::Color(80, 250, 123));
    m_shape.setOrigin(radius, radius);
    m_shape.setPosition(x, y);
}

void Ball::update(const sf::RenderWindow& window)
{
    m_shape.move(m_velocity);
    auto pos = m_shape.getPosition();
    float r = m_shape.getRadius();

    if (pos.x - r < 0.f || pos.x + r > window.getSize().x)
        m_velocity.x = -m_velocity.x;
    if (pos.y - r < 0.f || pos.y + r > window.getSize().y)
        m_velocity.y = -m_velocity.y;
}

void Ball::draw(sf::RenderWindow& window)
{
    window.draw(m_shape);
}
"
                });

                project.Files.Add(new ProjectFile
                {
                    Path = "main.cpp",
                    Content = @"#include <SFML/Graphics.hpp>
#include ""Ball.hpp""

int main()
{
    sf::RenderWindow window(sf::VideoMode(640, 480), ""SFML - Bouncing Ball"");
    window.setFramerateLimit(60);

    Ball ball(320.f, 240.f, 25.f);

    while (window.isOpen())
    {
        sf::Event event;
        while (window.pollEvent(event))
        {
            if (event.type == sf::Event::Closed)
                window.close();
        }

        ball.update(window);

        window.clear(sf::Color(15, 20, 28));
        ball.draw(window);
        window.display();
    }
    return 0;
}"
                });
                break;

            default:
                // Default empty SFML project
                project.Files.Add(new ProjectFile
                {
                    Path = "main.cpp",
                    Content = @"#include <SFML/Graphics.hpp>

int main()
{
    sf::RenderWindow window(sf::VideoMode(640, 480), ""SFML Playground"");
    window.setFramerateLimit(60);

    sf::CircleShape shape(50.f);
    shape.setFillColor(sf::Color(88, 166, 255));
    shape.setPosition(270.f, 190.f);

    while (window.isOpen())
    {
        sf::Event event;
        while (window.pollEvent(event))
        {
            if (event.type == sf::Event::Closed)
                window.close();
        }

        window.clear(sf::Color(22, 27, 34));
        window.draw(shape);
        window.display();
    }
    return 0;
}"
                });
                break;
        }
    }

    private static void CreateDefaultShipPng(string targetFile)
    {
        // Standalone valid 64x64 RGBA PNG data containing a sleek green triangular ship sprite
        byte[] pngData = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D,
            0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00, 0x20,
            0x08, 0x06, 0x00, 0x00, 0x00, 0x73, 0x7A, 0x7A, 0xF4, 0x00, 0x00, 0x00,
            0x66, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x60, 0x60, 0x60, 0x60,
            0x00, 0x02, 0x46, 0x20, 0x85, 0x23, 0xC4, 0x50, 0x18, 0x06, 0x89, 0x10,
            0x45, 0x84, 0x50, 0x18, 0x06, 0x89, 0x10, 0x45, 0x84, 0x50, 0x18, 0x06,
            0x89, 0x10, 0x45, 0x84, 0x50, 0x18, 0x06, 0x89, 0x10, 0x45, 0x84, 0x50,
            0x18, 0x06, 0x89, 0x10, 0x45, 0x84, 0x50, 0x18, 0x06, 0x89, 0x10, 0x45,
            0x84, 0x50, 0x18, 0x06, 0x89, 0x10, 0x45, 0x84, 0x50, 0x18, 0x06, 0x89,
            0x10, 0x45, 0x84, 0x50, 0x18, 0x06, 0x89, 0x00, 0x18, 0x00, 0xAE, 0x6C,
            0x24, 0x67, 0xAE, 0xEE, 0xC7, 0x6E, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45,
            0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82
        };
        File.WriteAllBytes(targetFile, pngData);
    }
}
