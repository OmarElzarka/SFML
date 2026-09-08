namespace SfmlPlayground.Api.Services;

/// <summary>
/// Local filesystem implementation of IAssetStorageService for development and on-premise environments.
/// </summary>
public class LocalFileAssetStorageService : IAssetStorageService
{
    private readonly string _baseStoragePath;
    private readonly ILogger<LocalFileAssetStorageService> _logger;

    public LocalFileAssetStorageService(IWebHostEnvironment env, ILogger<LocalFileAssetStorageService> logger)
    {
        _logger = logger;
        _baseStoragePath = Path.Combine(env.ContentRootPath, "storage", "projects");
        Directory.CreateDirectory(_baseStoragePath);
    }

    public async Task<string> UploadAssetAsync(int projectId, string fileName, Stream content, string? contentType, CancellationToken ct = default)
    {
        var cleanFileName = Path.GetFileName(fileName);
        var projectDir = Path.Combine(_baseStoragePath, projectId.ToString(), "assets");
        Directory.CreateDirectory(projectDir);

        var physicalPath = Path.Combine(projectDir, cleanFileName);
        using (var fs = new FileStream(physicalPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await content.CopyToAsync(fs, ct);
        }

        _logger.LogDebug("Stored local asset at {PhysicalPath}", physicalPath);
        return physicalPath;
    }

    public Task<Stream?> GetAssetStreamAsync(string storagePath, CancellationToken ct = default)
    {
        if (!File.Exists(storagePath))
            return Task.FromResult<Stream?>(null);

        var stream = new FileStream(storagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult<Stream?>(stream);
    }

    public async Task<byte[]?> GetAssetBytesAsync(string storagePath, CancellationToken ct = default)
    {
        if (!File.Exists(storagePath))
            return null;

        return await File.ReadAllBytesAsync(storagePath, ct);
    }

    public Task<bool> DeleteAssetAsync(string storagePath, CancellationToken ct = default)
    {
        if (File.Exists(storagePath))
        {
            try
            {
                File.Delete(storagePath);
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete local asset at {StoragePath}", storagePath);
                return Task.FromResult(false);
            }
        }
        return Task.FromResult(false);
    }

    public Task<bool> AssetExistsAsync(string storagePath, CancellationToken ct = default)
    {
        return Task.FromResult(File.Exists(storagePath));
    }

    public async Task MaterializeAssetToFileAsync(string storagePath, string targetFilePath, CancellationToken ct = default)
    {
        var targetDir = Path.GetDirectoryName(targetFilePath);
        if (!string.IsNullOrEmpty(targetDir))
            Directory.CreateDirectory(targetDir);

        if (File.Exists(storagePath))
        {
            if (string.Equals(Path.GetFullPath(storagePath), Path.GetFullPath(targetFilePath), StringComparison.OrdinalIgnoreCase))
                return;

            File.Copy(storagePath, targetFilePath, overwrite: true);
        }
        else
        {
            throw new FileNotFoundException($"Source asset file '{storagePath}' not found.");
        }
    }
}
