using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace SfmlPlayground.Api.Services;

/// <summary>
/// Production Azure Blob Storage implementation of IAssetStorageService.
/// Organizes project assets as: projects/{projectId}/assets/{fileName}
/// </summary>
public class AzureBlobAssetStorageService : IAssetStorageService
{
    private readonly BlobContainerClient _containerClient;
    private readonly ILogger<AzureBlobAssetStorageService> _logger;
    private const string DefaultContainerName = "sfml-assets";

    public AzureBlobAssetStorageService(IConfiguration config, ILogger<AzureBlobAssetStorageService> logger)
    {
        _logger = logger;

        var connectionString = config.GetValue<string>("AzureStorage:ConnectionString")
            ?? config.GetConnectionString("AzureStorage")
            ?? Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Azure Storage connection string is not configured.");

        var containerName = config.GetValue<string>("AzureStorage:ContainerName") ?? DefaultContainerName;

        var blobServiceClient = new BlobServiceClient(connectionString);
        _containerClient = blobServiceClient.GetBlobContainerClient(containerName);

        // Ensure container exists
        try
        {
            _containerClient.CreateIfNotExists(PublicAccessType.None);
            _logger.LogInformation("Azure Blob Storage container '{Container}' initialized", containerName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not verify/create Azure Blob Storage container '{Container}' on startup", containerName);
        }
    }

    /// <summary>
    /// Alternate constructor taking a pre-configured BlobContainerClient (useful for testing or DI with TokenCredential).
    /// </summary>
    public AzureBlobAssetStorageService(BlobContainerClient containerClient, ILogger<AzureBlobAssetStorageService> logger)
    {
        _containerClient = containerClient;
        _logger = logger;
    }

    private static string NormalizeBlobPath(int projectId, string fileName)
    {
        var cleanFileName = Path.GetFileName(fileName).Trim();
        if (string.IsNullOrWhiteSpace(cleanFileName) || cleanFileName.Contains(".."))
            throw new ArgumentException("Invalid asset filename or path traversal detected.", nameof(fileName));

        return $"projects/{projectId}/assets/{cleanFileName}";
    }

    public async Task<string> UploadAssetAsync(int projectId, string fileName, Stream content, string? contentType, CancellationToken ct = default)
    {
        var blobPath = NormalizeBlobPath(projectId, fileName);
        var blobClient = _containerClient.GetBlobClient(blobPath);

        var headers = new BlobHttpHeaders();
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            headers.ContentType = contentType;
        }

        if (content.CanSeek)
            content.Position = 0;

        await blobClient.UploadAsync(content, new BlobUploadOptions { HttpHeaders = headers }, ct);
        _logger.LogInformation("Uploaded asset to Azure Blob: {BlobPath}", blobPath);

        return blobPath;
    }

    public async Task<Stream?> GetAssetStreamAsync(string storagePath, CancellationToken ct = default)
    {
        var blobClient = ResolveBlobClient(storagePath);
        if (!await blobClient.ExistsAsync(ct))
            return null;

        var download = await blobClient.DownloadStreamingAsync(cancellationToken: ct);
        return download.Value.Content;
    }

    public async Task<byte[]?> GetAssetBytesAsync(string storagePath, CancellationToken ct = default)
    {
        var blobClient = ResolveBlobClient(storagePath);
        if (!await blobClient.ExistsAsync(ct))
            return null;

        var download = await blobClient.DownloadContentAsync(ct);
        return download.Value.Content.ToArray();
    }

    public async Task<bool> DeleteAssetAsync(string storagePath, CancellationToken ct = default)
    {
        try
        {
            var blobClient = ResolveBlobClient(storagePath);
            var response = await blobClient.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);
            _logger.LogInformation("Deleted asset from Azure Blob: {StoragePath} (Deleted: {Deleted})", storagePath, response.Value);
            return response.Value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete Azure Blob asset: {StoragePath}", storagePath);
            return false;
        }
    }

    public async Task<bool> AssetExistsAsync(string storagePath, CancellationToken ct = default)
    {
        var blobClient = ResolveBlobClient(storagePath);
        return await blobClient.ExistsAsync(ct);
    }

    public async Task MaterializeAssetToFileAsync(string storagePath, string targetFilePath, CancellationToken ct = default)
    {
        var targetDir = Path.GetDirectoryName(targetFilePath);
        if (!string.IsNullOrEmpty(targetDir))
            Directory.CreateDirectory(targetDir);

        var blobClient = ResolveBlobClient(storagePath);
        if (!await blobClient.ExistsAsync(ct))
            throw new FileNotFoundException($"Azure Blob asset '{storagePath}' does not exist.");

        await blobClient.DownloadToAsync(targetFilePath, ct);
        _logger.LogDebug("Materialized Azure Blob {BlobPath} to {TargetFilePath}", storagePath, targetFilePath);
    }

    private BlobClient ResolveBlobClient(string storagePath)
    {
        var relative = storagePath.Replace('\\', '/');
        // If storagePath is a full URI or contains container prefix, extract relative blob name
        if (Uri.TryCreate(relative, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.TrimStart('/').Split('/', 2);
            relative = segments.Length > 1 ? segments[1] : segments[0];
        }

        return _containerClient.GetBlobClient(relative);
    }
}
