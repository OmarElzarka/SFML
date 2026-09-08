using System.Collections.Concurrent;
using System.Formats.Tar;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using SfmlPlayground.Api.Models;

namespace SfmlPlayground.Api.Services;

/// <summary>
/// Manages Docker container lifecycle for SFML execution sessions.
/// </summary>
public class DockerSessionService : IDisposable
{
    private readonly DockerClient _docker;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly ILogger<DockerSessionService> _logger;
    private readonly IConfiguration _config;
    private readonly IAssetStorageService _assetStorage;
    private const string ImageName = "sfml-sandbox:latest";
    private const int MaxSessions = 50;
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CompileTimeout = TimeSpan.FromSeconds(30);

    public static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".psd",
        ".wav", ".ogg", ".flac",
        ".ttf", ".otf"
    };

    public const long MaxFileSizeBytes = 10 * 1024 * 1024;    // 10 MB
    public const long MaxTotalSizeBytes = 20 * 1024 * 1024;   // 20 MB
    public const int MaxAssetCount = 20;

    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "main.cpp", "app", "compile.sh", "entrypoint.sh", "rc.xml"
    };

    private readonly PersistentRuntimeService _runtimeService;

    public DockerSessionService(
        ILogger<DockerSessionService> logger,
        IConfiguration config,
        IAssetStorageService assetStorage,
        PersistentRuntimeService runtimeService)
    {
        _logger = logger;
        _config = config;
        _assetStorage = assetStorage;
        _runtimeService = runtimeService;

        // Connect to Docker daemon
        var dockerHost = config.GetValue<string>("Docker:Host");
        _docker = string.IsNullOrEmpty(dockerHost)
            ? new DockerClientConfiguration().CreateClient()
            : new DockerClientConfiguration(new Uri(dockerHost)).CreateClient();
    }

    private void EnsureSessionCapacity()
    {
        if (_sessions.Count >= MaxSessions)
        {
            // Prune any stopped, error, or idle ready sessions
            var removable = _sessions.Values
                .Where(s => s.Status == SessionStatus.Stopped ||
                            s.Status == SessionStatus.Error ||
                            (s.Status == SessionStatus.Ready && DateTime.UtcNow - s.CreatedAt > TimeSpan.FromMinutes(1)))
                .ToList();

            foreach (var s in removable)
            {
                _sessions.TryRemove(s.Id, out _);
            }

            if (_sessions.Count >= MaxSessions)
                throw new InvalidOperationException("Maximum number of concurrent sessions reached. Please try again later.");
        }
    }

    /// <summary>
    /// Initializes an empty session and its workspace directory without starting Docker yet.
    /// </summary>
    public Session InitializeSession(string? preferredId = null)
    {
        EnsureSessionCapacity();

        var sessionId = !string.IsNullOrWhiteSpace(preferredId) ? preferredId : "runtime";
        var workspacePath = Path.Combine(Path.GetTempPath(), "sfml-sessions", sessionId, "workspace");
        Directory.CreateDirectory(workspacePath);

        var session = _sessions.GetOrAdd(sessionId, id => new Session
        {
            Id = id,
            Status = SessionStatus.Ready,
            WorkspacePath = workspacePath,
            DisplayPort = PersistentRuntimeService.DefaultDisplayPort
        });

        session.Status = SessionStatus.Ready;
        session.LastHeartbeat = DateTime.UtcNow;
        session.DisplayPort = PersistentRuntimeService.DefaultDisplayPort;
        return session;
    }

    /// <summary>
    /// Executes source code using the persistent SFML runtime container.
    /// </summary>
    public async Task<Session> CreateSessionAsync(string sourceCode, string? existingSessionId = null, CancellationToken ct = default)
    {
        var sessionId = !string.IsNullOrWhiteSpace(existingSessionId) ? existingSessionId : "runtime";
        var session = _sessions.GetOrAdd(sessionId, id => new Session
        {
            Id = id,
            DisplayPort = PersistentRuntimeService.DefaultDisplayPort
        });

        session.SourceCode = sourceCode;
        session.LastHeartbeat = DateTime.UtcNow;
        session.Status = SessionStatus.Compiling;
        session.DisplayPort = PersistentRuntimeService.DefaultDisplayPort;

        var dummyFile = new ProjectFile { Path = "main.cpp", Content = sourceCode };
        var assetList = new List<ProjectAsset>();
        lock (session.Assets)
        {
            foreach (var a in session.Assets)
            {
                var filePath = Path.Combine(session.WorkspacePath, "assets", a.Name);
                if (File.Exists(filePath))
                {
                    assetList.Add(new ProjectAsset { FileName = a.Name, Size = a.Size, StoragePath = filePath });
                }
            }
        }

        try
        {
            var gameSession = await _runtimeService.RunProjectAsync(0, new[] { dummyFile }, assetList, ct);
            session.Status = gameSession.Status;
            session.CompilerOutput = gameSession.CompilerOutput;
            session.RuntimeOutput = gameSession.RuntimeOutput;
            session.ErrorMessage = gameSession.ErrorMessage;
            return session;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session {SessionId}: Execution failed", session.Id);
            session.Status = SessionStatus.Error;
            session.ErrorMessage = $"Execution failed: {ex.Message}";
            return session;
        }
    }

    /// <summary>
    /// Executes a multi-file project using the persistent SFML runtime container.
    /// </summary>
    public async Task<Session> CreateProjectSessionAsync(
        IEnumerable<ProjectFile> files,
        IEnumerable<ProjectAsset> assets,
        string? existingSessionId = null,
        CancellationToken ct = default)
    {
        var fileList = files.ToList();
        var mainFile = fileList.FirstOrDefault(f => string.Equals(f.Path, "main.cpp", StringComparison.OrdinalIgnoreCase))
            ?? fileList.FirstOrDefault(f => f.Path.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase))
            ?? fileList.FirstOrDefault();
        var sourceCode = mainFile?.Content ?? string.Empty;

        var sessionId = !string.IsNullOrWhiteSpace(existingSessionId) ? existingSessionId : "runtime";
        var session = _sessions.GetOrAdd(sessionId, id => new Session
        {
            Id = id,
            DisplayPort = PersistentRuntimeService.DefaultDisplayPort
        });

        session.SourceCode = sourceCode;
        session.LastHeartbeat = DateTime.UtcNow;
        session.Status = SessionStatus.Compiling;
        session.DisplayPort = PersistentRuntimeService.DefaultDisplayPort;

        try
        {
            var gameSession = await _runtimeService.RunProjectAsync(0, fileList, assets, ct);
            session.Status = gameSession.Status;
            session.CompilerOutput = gameSession.CompilerOutput;
            session.RuntimeOutput = gameSession.RuntimeOutput;
            session.ErrorMessage = gameSession.ErrorMessage;
            return session;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session {SessionId}: Project execution failed", session.Id);
            session.Status = SessionStatus.Error;
            session.ErrorMessage = $"Execution failed: {ex.Message}";
            return session;
        }
    }

    private async Task StartContainerAsync(
        Session session,
        Func<Session, CancellationToken, Task> copyWorkspaceAsync,
        CancellationToken ct)
    {
        session.Status = SessionStatus.Compiling;

        // Find a free port for websockify
        var port = FindFreePort();
        session.DisplayPort = port;

        _logger.LogInformation("Session {SessionId}: Starting container on port {Port}", session.Id, port);

        // Create the container
        var createResponse = await _docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = ImageName,
            Name = $"sfml-session-{session.Id}",
            HostConfig = new HostConfig
            {
                // Resource limits
                Memory = 512 * 1024 * 1024,       // 512 MB
                NanoCPUs = 500_000_000,            // 0.5 CPU
                PidsLimit = 64,
                // Read-only root filesystem with tmpfs for writable areas
                ReadonlyRootfs = false, // We need writable /workspace for compilation
                Tmpfs = new Dictionary<string, string>
                {
                    { "/tmp", "rw,noexec,nosuid,size=64m" }
                },
                // Port mapping: container 6080 -> host dynamic port
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    {
                        "6080/tcp",
                        new List<PortBinding> { new() { HostPort = port.ToString() } }
                    }
                },
                // Security: drop all capabilities, add only what's needed
                CapDrop = new List<string> { "ALL" },
                CapAdd = new List<string> { "SYS_PTRACE" }, // Needed for Xvfb
                // No privileged mode
                Privileged = false,
                // Auto-remove on stop
                AutoRemove = true,
                // Security options
                SecurityOpt = new List<string> { "no-new-privileges" }
            },
            ExposedPorts = new Dictionary<string, EmptyStruct>
            {
                { "6080/tcp", default }
            },
            Env = new List<string>
            {
                "DISPLAY=:99"
            }
        }, ct);

        session.ContainerId = createResponse.ID;
        _logger.LogInformation("Session {SessionId}: Container created {ContainerId}", session.Id, session.ContainerId[..12]);

        // Copy source code / project files into the container
        await copyWorkspaceAsync(session, ct);

        // Start the container
        await _docker.Containers.StartContainerAsync(session.ContainerId, new ContainerStartParameters(), ct);
        _logger.LogInformation("Session {SessionId}: Container started", session.Id);

        // Monitor container output for compilation result
        await MonitorContainerOutputAsync(session, ct);
    }

    private async Task CopySourceToContainerAsync(Session session, CancellationToken ct)
    {
        // Write main.cpp to the session workspace
        if (!string.IsNullOrEmpty(session.WorkspacePath))
        {
            var mainCppPath = Path.Combine(session.WorkspacePath, "main.cpp");
            await File.WriteAllTextAsync(mainCppPath, session.SourceCode, Encoding.UTF8, ct);
        }

        // Create a tar archive containing main.cpp and all uploaded assets
        var tarBytes = CreateWorkspaceTarArchive(session.WorkspacePath, session.SourceCode);

        using var stream = new MemoryStream(tarBytes);
        await _docker.Containers.ExtractArchiveToContainerAsync(
            session.ContainerId,
            new ContainerPathStatParameters { Path = "/workspace" },
            stream,
            ct);

        _logger.LogInformation("Session {SessionId}: Source code and {AssetCount} asset(s) copied to container",
            session.Id, session.Assets.Count);
    }

    private async Task CopyProjectToContainerAsync(
        Session session,
        List<ProjectFile> files,
        List<ProjectAsset> assets,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(session.WorkspacePath))
        {
            if (Directory.Exists(session.WorkspacePath))
            {
                try { Directory.Delete(session.WorkspacePath, true); } catch { }
            }
            Directory.CreateDirectory(session.WorkspacePath);

            foreach (var file in files)
            {
                var normalized = file.Path.Replace('\\', '/').TrimStart('/');
                var fullFilePath = Path.Combine(session.WorkspacePath, normalized.Replace('/', Path.DirectorySeparatorChar));
                var parentDir = Path.GetDirectoryName(fullFilePath);
                if (!string.IsNullOrEmpty(parentDir))
                    Directory.CreateDirectory(parentDir);
                await File.WriteAllTextAsync(fullFilePath, file.Content, Encoding.UTF8, ct);
            }

            var assetsDir = Path.Combine(session.WorkspacePath, "assets");
            Directory.CreateDirectory(assetsDir);

            foreach (var asset in assets)
            {
                var targetInAssets = Path.Combine(assetsDir, asset.FileName);
                var targetInRoot = Path.Combine(session.WorkspacePath, asset.FileName);
                try
                {
                    await _assetStorage.MaterializeAssetToFileAsync(asset.StoragePath, targetInAssets, ct);
                    if (!File.Exists(targetInRoot))
                    {
                        File.Copy(targetInAssets, targetInRoot, true);
                    }

                    var fileInfo = new FileInfo(targetInAssets);
                    lock (session.Assets)
                    {
                        session.Assets.RemoveAll(a => string.Equals(a.Name, asset.FileName, StringComparison.OrdinalIgnoreCase));
                        session.Assets.Add(new SessionAsset(asset.FileName, fileInfo.Length, DateTime.UtcNow));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Session {SessionId}: Failed to materialize asset {Asset}", session.Id, asset.FileName);
                }
            }
        }

        var tarBytes = await CreateProjectTarArchiveAsync(files, assets, ct);
        using var stream = new MemoryStream(tarBytes);
        await _docker.Containers.ExtractArchiveToContainerAsync(
            session.ContainerId,
            new ContainerPathStatParameters { Path = "/workspace" },
            stream,
            ct);

        _logger.LogInformation("Session {SessionId}: Project copied to container ({FileCount} files, {AssetCount} assets)",
            session.Id, files.Count, assets.Count);
    }

    private async Task<byte[]> CreateProjectTarArchiveAsync(
        List<ProjectFile> files,
        List<ProjectAsset> assets,
        CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using (var writer = new TarWriter(ms, TarEntryFormat.Ustar, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entryPath = file.Path.Replace('\\', '/').TrimStart('/');
                var contentBytes = Encoding.UTF8.GetBytes(file.Content);
                var entry = new UstarTarEntry(TarEntryType.RegularFile, entryPath)
                {
                    DataStream = new MemoryStream(contentBytes),
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
                };
                writer.WriteEntry(entry);
            }

            foreach (var asset in assets)
            {
                byte[]? assetBytes = null;
                try
                {
                    assetBytes = await _assetStorage.GetAssetBytesAsync(asset.StoragePath, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Session: Failed to get asset bytes for {Asset}", asset.FileName);
                }

                if (assetBytes != null)
                {
                    var assetEntry1 = new UstarTarEntry(TarEntryType.RegularFile, $"assets/{asset.FileName}")
                    {
                        DataStream = new MemoryStream(assetBytes),
                        Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
                    };
                    writer.WriteEntry(assetEntry1);

                    var assetEntry2 = new UstarTarEntry(TarEntryType.RegularFile, asset.FileName)
                    {
                        DataStream = new MemoryStream(assetBytes),
                        Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
                    };
                    writer.WriteEntry(assetEntry2);
                }
            }
        }
        return ms.ToArray();
    }

    private async Task MonitorContainerOutputAsync(Session session, CancellationToken ct)
    {
        var output = new StringBuilder();
        var compileSuccess = false;
        var displayReady = false;

        using var logCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        logCts.CancelAfter(SessionTimeout);

        try
        {
            var logStream = await _docker.Containers.GetContainerLogsAsync(
                session.ContainerId,
                false,
                new ContainerLogsParameters
                {
                    ShowStdout = true,
                    ShowStderr = true,
                    Follow = true
                },
                logCts.Token);

            // MultiplexedStream requires ReadOutputAsync to properly demux stdout/stderr
            var buffer = new byte[8192];

            // Read with timeout for compilation phase
            using var compileCts = CancellationTokenSource.CreateLinkedTokenSource(logCts.Token);
            compileCts.CancelAfter(CompileTimeout);

            var lineBuffer = new StringBuilder();

            try
            {
                while (true)
                {
                    var result = await logStream.ReadOutputAsync(buffer, 0, buffer.Length, compileCts.Token);
                    if (result.EOF) break;
                    if (result.Count == 0) continue;

                    var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    lineBuffer.Append(text);

                    // Process complete lines
                    var content = lineBuffer.ToString();
                    var lines = content.Split('\n');

                    // Process all complete lines (keep the last incomplete one in buffer)
                    for (int i = 0; i < lines.Length - 1; i++)
                    {
                        var line = lines[i].TrimEnd('\r');
                        output.AppendLine(line);
                        _logger.LogDebug("Session {SessionId}: {Line}", session.Id, line);

                        if (line.Contains("COMPILE_ERROR"))
                        {
                            session.Status = SessionStatus.CompileError;
                            // Continue reading briefly to get the error output
                            var errorOutput = await ReadRemainingOutputAsync(logStream, buffer, compileCts.Token);
                            session.CompilerOutput = errorOutput;
                            _logger.LogInformation("Session {SessionId}: Compilation failed", session.Id);
                            logStream.Dispose();
                            return;
                        }

                        if (line.Contains("COMPILE_SUCCESS"))
                        {
                            compileSuccess = true;
                            session.CompilerOutput = "Compilation successful.";
                        }

                        if (line.Contains("DISPLAY_READY"))
                        {
                            displayReady = true;
                        }

                        if (line.Contains("APP_STARTED"))
                        {
                            if (compileSuccess && displayReady)
                            {
                                session.Status = SessionStatus.Running;
                                _logger.LogInformation("Session {SessionId}: Application running, display on port {Port}",
                                    session.Id, session.DisplayPort);
                                _ = Task.Run(async () => await MonitorRuntimeAsync(session));
                            }
                            logStream.Dispose();
                            return;
                        }
                    }

                    // Keep the last incomplete line in the buffer
                    lineBuffer.Clear();
                    lineBuffer.Append(lines[^1]);
                }
            }
            catch (OperationCanceledException) when (compileCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                session.Status = SessionStatus.Error;
                session.ErrorMessage = "Compilation timed out.";
                session.CompilerOutput = output.ToString();
                await StopSessionInternalAsync(session);
            }
            finally
            {
                logStream.Dispose();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Session {SessionId}: Error monitoring container", session.Id);
            session.Status = SessionStatus.Error;
            session.ErrorMessage = $"Container monitoring error: {ex.Message}";
        }
    }

    private async Task MonitorRuntimeAsync(Session session)
    {
        try
        {
            var waitResponse = await _docker.Containers.WaitContainerAsync(session.ContainerId);
            if (session.Status == SessionStatus.Running)
            {
                if (waitResponse.StatusCode != 0)
                {
                    session.Status = SessionStatus.Error;
                    session.ErrorMessage = $"Application terminated (Exit code: {waitResponse.StatusCode})";
                    _logger.LogWarning("Session {SessionId}: Application exited with code {ExitCode}", session.Id, waitResponse.StatusCode);
                }
                else
                {
                    session.Status = SessionStatus.Stopped;
                    _logger.LogInformation("Session {SessionId}: Application exited normally", session.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Session {SessionId}: Runtime monitoring ended", session.Id);
        }
    }

    private static async Task<string> ReadRemainingOutputAsync(MultiplexedStream stream, byte[] buffer, CancellationToken ct)
    {
        var sb = new StringBuilder();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            while (true)
            {
                var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cts.Token);
                if (result.EOF || result.Count == 0) break;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
        }
        catch (OperationCanceledException)
        {
            // Expected - we just want what's available
        }
        return sb.ToString();
    }

    /// <summary>
    /// Gets a session by ID.
    /// </summary>
    public Session? GetSession(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        if (session != null)
            session.LastHeartbeat = DateTime.UtcNow;
        return session;
    }

    /// <summary>
    /// Stops the running game process in the persistent runtime.
    /// </summary>
    public async Task StopSessionAsync(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            await _runtimeService.StopProcessAsync();
            session.Status = SessionStatus.Stopped;
        }
    }

    private async Task StopSessionInternalAsync(Session session)
    {
        if (session.Status == SessionStatus.Stopped)
            return;

        await _runtimeService.StopProcessAsync();
        session.Status = SessionStatus.Stopped;
    }

    private async Task StopContainerInternalAsync(string containerId)
    {
        try
        {
            await _docker.Containers.StopContainerAsync(
                containerId,
                new ContainerStopParameters { WaitBeforeKillSeconds = 3 });
        }
        catch (DockerContainerNotFoundException)
        {
            // Container already removed (AutoRemove)
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping container {ContainerId}", containerId);
            try
            {
                await _docker.Containers.KillContainerAsync(
                    containerId,
                    new ContainerKillParameters { Signal = "KILL" });
            }
            catch { /* Best effort */ }
        }
    }

    private void PurgeWorkspaceDirectory(Session session)
    {
        if (string.IsNullOrEmpty(session.WorkspacePath))
            return;

        try
        {
            var sessionDir = Directory.GetParent(session.WorkspacePath)?.FullName;
            if (!string.IsNullOrEmpty(sessionDir) && Directory.Exists(sessionDir))
            {
                Directory.Delete(sessionDir, recursive: true);
                _logger.LogInformation("Session {SessionId}: Purged session workspace at {Dir}", session.Id, sessionDir);
            }
            else if (Directory.Exists(session.WorkspacePath))
            {
                Directory.Delete(session.WorkspacePath, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session {SessionId}: Failed to delete workspace directory {Path}", session.Id, session.WorkspacePath);
        }
    }

    /// <summary>
    /// Adds an asset to a session workspace.
    /// </summary>
    public async Task<SessionAsset> AddAssetAsync(string sessionId, string fileName, Stream fileStream, long fileSize, CancellationToken ct = default)
    {
        var session = GetSession(sessionId)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' not found.");

        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("Asset filename cannot be empty.", nameof(fileName));

        var safeName = Path.GetFileName(fileName).Trim();
        if (string.IsNullOrWhiteSpace(safeName))
            throw new ArgumentException("Asset filename cannot be empty.", nameof(fileName));

        if (safeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"Filename contains invalid characters: '{safeName}'.", nameof(fileName));

        if (safeName.Contains("..") || safeName.Contains('/') || safeName.Contains('\\'))
            throw new ArgumentException("Path traversal is not allowed.", nameof(fileName));

        if (safeName.StartsWith('.'))
            throw new ArgumentException("Hidden files are not allowed.", nameof(fileName));

        if (ReservedFileNames.Contains(safeName))
            throw new ArgumentException($"Filename '{safeName}' is reserved.", nameof(fileName));

        var ext = Path.GetExtension(safeName);
        if (string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext))
            throw new ArgumentException($"Unsupported file type: '{ext}'. Allowed extensions: {string.Join(", ", AllowedExtensions)}");

        if (fileSize > MaxFileSizeBytes)
            throw new ArgumentException($"Asset exceeds the 10 MB limit ({fileSize / (1024.0 * 1024.0):F2} MB).");

        lock (session.Assets)
        {
            var existingIndex = session.Assets.FindIndex(a => string.Equals(a.Name, safeName, StringComparison.OrdinalIgnoreCase));
            if (existingIndex < 0 && session.Assets.Count >= MaxAssetCount)
                throw new InvalidOperationException($"Maximum asset limit reached ({MaxAssetCount} files).");

            var currentTotal = session.Assets
                .Where(a => !string.Equals(a.Name, safeName, StringComparison.OrdinalIgnoreCase))
                .Sum(a => a.Size);

            if (currentTotal + fileSize > MaxTotalSizeBytes)
                throw new InvalidOperationException($"Total asset size would exceed the 20 MB limit ({(currentTotal + fileSize) / (1024.0 * 1024.0):F2} MB).");
        }

        if (string.IsNullOrEmpty(session.WorkspacePath))
        {
            session.WorkspacePath = Path.Combine(Path.GetTempPath(), "sfml-sessions", session.Id, "workspace");
        }
        Directory.CreateDirectory(session.WorkspacePath);

        var targetPath = Path.Combine(session.WorkspacePath, safeName);
        using (var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await fileStream.CopyToAsync(fs, ct);
        }

        var actualSize = new FileInfo(targetPath).Length;
        var asset = new SessionAsset(safeName, actualSize, DateTime.UtcNow);

        lock (session.Assets)
        {
            var idx = session.Assets.FindIndex(a => string.Equals(a.Name, safeName, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                session.Assets[idx] = asset;
            else
                session.Assets.Add(asset);
        }

        _logger.LogInformation("Session {SessionId}: Saved asset {Asset} ({Size} bytes)", session.Id, safeName, actualSize);

        // If container is currently running, live-inject asset into /workspace
        if (session.Status == SessionStatus.Running && !string.IsNullOrEmpty(session.ContainerId))
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(targetPath, ct);
                var tarBytes = CreateSingleFileTar(safeName, bytes);
                using var tarStream = new MemoryStream(tarBytes);
                await _docker.Containers.ExtractArchiveToContainerAsync(
                    session.ContainerId,
                    new ContainerPathStatParameters { Path = "/workspace" },
                    tarStream,
                    ct);
                _logger.LogInformation("Session {SessionId}: Live-injected {Asset} into running container", session.Id, safeName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Session {SessionId}: Live injection failed for {Asset}", session.Id, safeName);
            }
        }

        return asset;
    }

    /// <summary>
    /// Deletes an asset from a session workspace.
    /// </summary>
    public bool DeleteAsset(string sessionId, string assetName)
    {
        var session = GetSession(sessionId);
        if (session == null) return false;

        var safeName = Path.GetFileName(assetName);
        bool removed;
        lock (session.Assets)
        {
            var idx = session.Assets.FindIndex(a => string.Equals(a.Name, safeName, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                session.Assets.RemoveAt(idx);
                removed = true;
            }
            else
            {
                removed = false;
            }
        }

        if (!string.IsNullOrEmpty(session.WorkspacePath))
        {
            var targetPath = Path.Combine(session.WorkspacePath, safeName);
            if (File.Exists(targetPath))
            {
                try
                {
                    File.Delete(targetPath);
                    _logger.LogInformation("Session {SessionId}: Deleted asset {Asset} from workspace", session.Id, safeName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Session {SessionId}: Failed to delete asset file {File}", session.Id, targetPath);
                }
            }
        }

        return removed;
    }

    /// <summary>
    /// Gets all assets for a session.
    /// </summary>
    public IReadOnlyList<SessionAsset> GetAssets(string sessionId)
    {
        var session = GetSession(sessionId)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' not found.");

        lock (session.Assets)
        {
            return session.Assets.ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// Cleans up expired sessions (no heartbeat for too long).
    /// </summary>
    public async Task CleanupExpiredSessionsAsync()
    {
        var expired = _sessions.Values
            .Where(s => DateTime.UtcNow - s.LastHeartbeat > SessionTimeout
                || DateTime.UtcNow - s.CreatedAt > SessionTimeout)
            .ToList();

        foreach (var session in expired)
        {
            _logger.LogInformation("Session {SessionId}: Expired, cleaning up", session.Id);
            try
            {
                await StopSessionInternalAsync(session);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session {SessionId}: Error during cleanup", session.Id);
            }
            _sessions.TryRemove(session.Id, out _);
        }
    }

    /// <summary>
    /// Gets all active sessions (for monitoring).
    /// </summary>
    public IReadOnlyCollection<Session> GetActiveSessions() =>
        _sessions.Values.ToList().AsReadOnly();

    private static int FindFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static byte[] CreateWorkspaceTarArchive(string workspacePath, string sourceCode)
    {
        using var ms = new MemoryStream();
        using (var writer = new TarWriter(ms, TarEntryFormat.Ustar, leaveOpen: true))
        {
            // Add main.cpp
            var sourceBytes = Encoding.UTF8.GetBytes(sourceCode);
            var mainEntry = new UstarTarEntry(TarEntryType.RegularFile, "main.cpp")
            {
                DataStream = new MemoryStream(sourceBytes),
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
            };
            writer.WriteEntry(mainEntry);

            // Add all assets in workspacePath if exists
            if (!string.IsNullOrEmpty(workspacePath) && Directory.Exists(workspacePath))
            {
                foreach (var file in Directory.GetFiles(workspacePath))
                {
                    var fileName = Path.GetFileName(file);
                    if (string.Equals(fileName, "main.cpp", StringComparison.OrdinalIgnoreCase))
                        continue; // Already added as primary source

                    var bytes = File.ReadAllBytes(file);
                    var entry = new UstarTarEntry(TarEntryType.RegularFile, fileName)
                    {
                        DataStream = new MemoryStream(bytes),
                        Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
                    };
                    writer.WriteEntry(entry);
                }
            }
        }
        return ms.ToArray();
    }

    private static byte[] CreateSingleFileTar(string fileName, byte[] content)
    {
        using var ms = new MemoryStream();
        using (var writer = new TarWriter(ms, TarEntryFormat.Ustar, leaveOpen: true))
        {
            var entry = new UstarTarEntry(TarEntryType.RegularFile, fileName)
            {
                DataStream = new MemoryStream(content),
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
            };
            writer.WriteEntry(entry);
        }
        return ms.ToArray();
    }

    public void Dispose()
    {
        // Cleanup all sessions on shutdown
        foreach (var session in _sessions.Values)
        {
            try
            {
                StopSessionInternalAsync(session).GetAwaiter().GetResult();
            }
            catch { /* Best effort on shutdown */ }
        }
        _docker.Dispose();
    }
}
