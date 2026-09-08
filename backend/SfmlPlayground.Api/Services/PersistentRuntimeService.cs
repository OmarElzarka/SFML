using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using SfmlPlayground.Api.Models;

namespace SfmlPlayground.Api.Services;

/// <summary>
/// Service managing the single persistent SFML 2.6.2 runtime container.
/// Keeps Xvfb, Openbox, x11vnc, and websockify running continuously,
/// provides in-engine fast process replacement (< 1.5s), and local asset caching.
/// </summary>
public class PersistentRuntimeService : IDisposable
{
    private readonly DockerClient _docker;
    private readonly IAssetStorageService _assetStorage;
    private readonly ILogger<PersistentRuntimeService> _logger;
    private readonly string _storageRoot;
    private readonly SemaphoreSlim _containerLock = new(1, 1);
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private bool _containerVerified = false;

    public const string ContainerName = "sfml-runtime";
    public const string ImageName = "sfml-sandbox:latest";
    public const int DefaultDisplayPort = 6080;

    // Single active conceptual game session
    private readonly GameSession _activeSession = new()
    {
        SessionId = "runtime",
        Status = SessionStatus.Ready,
        DisplayPort = DefaultDisplayPort
    };

    public PersistentRuntimeService(
        IConfiguration config,
        IAssetStorageService assetStorage,
        IWebHostEnvironment env,
        ILogger<PersistentRuntimeService> logger)
    {
        _assetStorage = assetStorage;
        _logger = logger;

        var dockerHost = config.GetValue<string>("Docker:Host");
        _docker = string.IsNullOrEmpty(dockerHost)
            ? new DockerClientConfiguration().CreateClient()
            : new DockerClientConfiguration(new Uri(dockerHost)).CreateClient();

        var configWorkspace = config.GetValue<string>("Storage:WorkspacePath");
        _storageRoot = !string.IsNullOrEmpty(configWorkspace)
            ? Path.GetFullPath(configWorkspace)
            : Path.GetFullPath(Path.Combine(env.ContentRootPath, "storage", "workspace"));

        Directory.CreateDirectory(_storageRoot);
        Directory.CreateDirectory(Path.Combine(_storageRoot, "assets"));
        _activeSession.WorkspacePath = _storageRoot;
    }

    public GameSession GetActiveSession() => _activeSession;

    public GameSession GetSession(string? sessionId) => _activeSession;

    public SessionResponse InitializeSession(string baseUrl)
    {
        _activeSession.LastActivityAt = DateTime.UtcNow;
        return _activeSession.ToResponse(baseUrl);
    }

    /// <summary>
    /// Ensures the persistent sfml-runtime container is created, started, and websockify is listening.
    /// </summary>
    public async Task EnsureRuntimeContainerRunningAsync(CancellationToken ct = default)
    {
        if (_containerVerified) return;

        await _containerLock.WaitAsync(ct);
        try
        {
            if (_containerVerified) return;

            var containers = await _docker.Containers.ListContainersAsync(new ContainersListParameters
            {
                All = true
            }, ct);

            var existing = containers.FirstOrDefault(c => c.Names.Any(n => n.TrimStart('/') == ContainerName));

            if (existing == null)
            {
                _logger.LogInformation("Creating persistent SFML runtime container: {Name} on port {Port}", ContainerName, DefaultDisplayPort);

                var normalizedPath = _storageRoot.Replace('\\', '/');

                await _docker.Containers.CreateContainerAsync(new CreateContainerParameters
                {
                    Image = ImageName,
                    Name = ContainerName,
                    HostConfig = new HostConfig
                    {
                        Binds = new List<string> { $"{normalizedPath}:/workspace" },
                        PortBindings = new Dictionary<string, IList<PortBinding>>
                        {
                            ["6080/tcp"] = new List<PortBinding> { new() { HostIP = "127.0.0.1", HostPort = DefaultDisplayPort.ToString() } }
                        },
                        Memory = 2048L * 1024 * 1024,      // 2 GB RAM limit
                        NanoCPUs = 2_000_000_000,          // 2 CPU cores limit
                        PidsLimit = 256,
                        CapDrop = new List<string> { "ALL" },
                        CapAdd = new List<string> { "SYS_PTRACE" }, // For clangd/gdb
                        SecurityOpt = new List<string> { "no-new-privileges:true" }
                    }
                }, ct);

                await _docker.Containers.StartContainerAsync(ContainerName, new ContainerStartParameters(), ct);
                _logger.LogInformation("Persistent runtime container {Name} created and started.", ContainerName);
            }
            else if (existing.State != "running")
            {
                _logger.LogInformation("Starting existing persistent runtime container: {Name}", ContainerName);
                await _docker.Containers.StartContainerAsync(ContainerName, new ContainerStartParameters(), ct);
            }

            // Wait for websockify on port 6080 to respond
            await WaitForPortReadyAsync("127.0.0.1", DefaultDisplayPort, TimeSpan.FromSeconds(10), ct);
            _containerVerified = true;
            _logger.LogInformation("Persistent SFML runtime container verified and ready.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize persistent SFML runtime container");
            throw;
        }
        finally
        {
            _containerLock.Release();
        }
    }

    /// <summary>
    /// Executes a project: fast workspace sync, process termination, compilation, and execution on DISPLAY=:99.
    /// </summary>
    public async Task<GameSession> RunProjectAsync(
        int projectId,
        IEnumerable<ProjectFile> files,
        IEnumerable<ProjectAsset> assets,
        CancellationToken ct = default)
    {
        await _runLock.WaitAsync(ct);
        try
        {
            await EnsureRuntimeContainerRunningAsync(ct);

            _activeSession.ProjectId = projectId;
            _activeSession.Status = SessionStatus.Compiling;
            _activeSession.ErrorMessage = null;
            _activeSession.CompilerOutput = "Compiling project sources...\n";
            _activeSession.LastActivityAt = DateTime.UtcNow;

            // 1. Materialize / sync project files & assets on host disk
            await SyncWorkspaceAsync(files, assets, ct);

            // 2. Terminate any previous game process
            await StopProcessInternalAsync(ct);

            // 3. Compile code inside container
            var (compileExit, compileOutput) = await RunExecAsync(
                new[] { "/opt/compile.sh", "/workspace/main.cpp", "/workspace/app" },
                "/workspace",
                ct);

            _activeSession.CompilerOutput = compileOutput;

            if (compileExit != 0)
            {
                _logger.LogWarning("Project {ProjectId}: Compilation failed (exit code {ExitCode})", projectId, compileExit);
                _activeSession.Status = SessionStatus.CompileError;
                _activeSession.ErrorMessage = "Compilation failed.";
                return _activeSession;
            }

            _logger.LogInformation("Project {ProjectId}: Compilation succeeded. Starting game process.", projectId);

            // 4. Run game process on DISPLAY=:99
            var (runExit, runOutput) = await RunExecAsync(
                new[] { "/opt/run-app.sh", "/workspace/app" },
                "/workspace",
                ct);

            _activeSession.RuntimeOutput = runOutput;
            _activeSession.Status = SessionStatus.Running;
            return _activeSession;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Project {ProjectId}: Failed during execution run", projectId);
            _activeSession.Status = SessionStatus.Error;
            _activeSession.ErrorMessage = $"Execution failed: {ex.Message}";
            return _activeSession;
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>
    /// Executes standalone source code (backward compatibility).
    /// </summary>
    public async Task<GameSession> RunSourceCodeAsync(string sourceCode, CancellationToken ct = default)
    {
        var dummyFile = new ProjectFile
        {
            Path = "main.cpp",
            Content = sourceCode
        };
        return await RunProjectAsync(0, new[] { dummyFile }, Array.Empty<ProjectAsset>(), ct);
    }

    /// <summary>
    /// Stops the currently running game process without killing the container or graphical services.
    /// </summary>
    public async Task<GameSession> StopProcessAsync(CancellationToken ct = default)
    {
        await _runLock.WaitAsync(ct);
        try
        {
            await StopProcessInternalAsync(ct);
            _activeSession.Status = SessionStatus.Stopped;
            _activeSession.LastActivityAt = DateTime.UtcNow;
            return _activeSession;
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task StopProcessInternalAsync(CancellationToken ct)
    {
        try
        {
            await RunExecAsync(new[] { "/opt/stop-app.sh", "/workspace/app" }, "/workspace", ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error while stopping previous game process");
        }
    }

    /// <summary>
    /// Synchronizes project files and cached assets to the host workspace folder.
    /// Unchanged assets are never re-downloaded from Azure Blob Storage.
    /// </summary>
    private async Task SyncWorkspaceAsync(
        IEnumerable<ProjectFile> files,
        IEnumerable<ProjectAsset> assets,
        CancellationToken ct)
    {
        var fileList = files.ToList();

        // 1. Sync source files
        var activePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in fileList)
        {
            var relativePath = file.Path.TrimStart('/', '\\');
            activePaths.Add(relativePath);

            var diskPath = Path.Combine(_storageRoot, relativePath);
            var dir = Path.GetDirectoryName(diskPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Only write if file doesn't exist or content changed
            if (!File.Exists(diskPath) || await File.ReadAllTextAsync(diskPath, ct) != file.Content)
            {
                await File.WriteAllTextAsync(diskPath, file.Content, Encoding.UTF8, ct);
            }
        }

        // Clean up obsolete .cpp/.hpp files in workspace root
        foreach (var existingFile in Directory.GetFiles(_storageRoot, "*.*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(existingFile);
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            if ((ext == ".cpp" || ext == ".hpp" || ext == ".h") && !activePaths.Contains(fileName))
            {
                try { File.Delete(existingFile); } catch { }
            }
        }

        // 2. Sync assets (with local disk caching)
        var assetsDir = Path.Combine(_storageRoot, "assets");
        Directory.CreateDirectory(assetsDir);

        var activeAssetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets)
        {
            var cleanName = Path.GetFileName(asset.FileName);
            activeAssetNames.Add(cleanName);

            var assetDiskPath = Path.Combine(assetsDir, cleanName);

            // Local cache check: if file exists with matching size, reuse local copy!
            if (File.Exists(assetDiskPath) && new FileInfo(assetDiskPath).Length == asset.Size)
            {
                continue; // Cache hit! Zero network latency
            }

            // Download from storage (Azure Blob or local fallback)
            await using var stream = await _assetStorage.GetAssetStreamAsync(asset.StoragePath, ct);
            if (stream != null)
            {
                await using var fs = new FileStream(assetDiskPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await stream.CopyToAsync(fs, ct);
            }
        }

        // Clean up obsolete assets
        foreach (var existingAsset in Directory.GetFiles(assetsDir, "*.*", SearchOption.TopDirectoryOnly))
        {
            var assetName = Path.GetFileName(existingAsset);
            if (!activeAssetNames.Contains(assetName))
            {
                try { File.Delete(existingAsset); } catch { }
            }
        }
    }

    /// <summary>
    /// Executes a command inside the persistent container via Docker Exec.
    /// </summary>
    public async Task<(int ExitCode, string Output)> RunExecAsync(
        string[] cmd,
        string? workingDir = null,
        CancellationToken ct = default)
    {
        var exec = await _docker.Exec.ExecCreateContainerAsync(ContainerName, new ContainerExecCreateParameters
        {
            AttachStdout = true,
            AttachStderr = true,
            WorkingDir = workingDir ?? "/workspace",
            Cmd = cmd
        }, ct);

        using var stream = await _docker.Exec.StartAndAttachContainerExecAsync(exec.ID, false, ct);
        var (stdout, stderr) = await stream.ReadOutputToEndAsync(ct);
        var inspect = await _docker.Exec.InspectContainerExecAsync(exec.ID, ct);

        var output = (stdout + "\n" + stderr).Trim();
        return ((int)inspect.ExitCode, output);
    }

    private static async Task WaitForPortReadyAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout && !ct.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(host, port, ct);
                if (client.Connected) return;
            }
            catch
            {
                await Task.Delay(100, ct);
            }
        }
    }

    public void Dispose()
    {
        _containerLock.Dispose();
        _runLock.Dispose();
        _docker.Dispose();
    }
}
