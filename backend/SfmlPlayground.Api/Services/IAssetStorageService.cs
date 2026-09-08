namespace SfmlPlayground.Api.Services;

/// <summary>
/// Abstraction for storing, retrieving, and materializing project assets (local filesystem or Azure Blob Storage).
/// </summary>
public interface IAssetStorageService
{
    /// <summary>
    /// Uploads an asset for a project and returns its storage identifier/path.
    /// </summary>
    Task<string> UploadAssetAsync(int projectId, string fileName, Stream content, string? contentType, CancellationToken ct = default);

    /// <summary>
    /// Gets a readable stream of the asset content.
    /// </summary>
    Task<Stream?> GetAssetStreamAsync(string storagePath, CancellationToken ct = default);

    /// <summary>
    /// Gets all bytes of the asset content.
    /// </summary>
    Task<byte[]?> GetAssetBytesAsync(string storagePath, CancellationToken ct = default);

    /// <summary>
    /// Deletes the asset from storage.
    /// </summary>
    Task<bool> DeleteAssetAsync(string storagePath, CancellationToken ct = default);

    /// <summary>
    /// Checks if the asset exists in storage.
    /// </summary>
    Task<bool> AssetExistsAsync(string storagePath, CancellationToken ct = default);

    /// <summary>
    /// Downloads/copies the asset directly to a local target file path.
    /// </summary>
    Task MaterializeAssetToFileAsync(string storagePath, string targetFilePath, CancellationToken ct = default);
}
