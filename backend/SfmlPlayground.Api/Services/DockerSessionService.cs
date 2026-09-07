using System.Collections.Concurrent;
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
    private const string ImageName = "sfml-sandbox:latest";
    private const int MaxSessions = 10;
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CompileTimeout = TimeSpan.FromSeconds(30);

    public DockerSessionService(ILogger<DockerSessionService> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;

        // Connect to Docker daemon
        var dockerHost = config.GetValue<string>("Docker:Host");
        _docker = string.IsNullOrEmpty(dockerHost)
            ? new DockerClientConfiguration().CreateClient()
            : new DockerClientConfiguration(new Uri(dockerHost)).CreateClient();
    }

    /// <summary>
    /// Creates a new execution session: starts Docker container, compiles code, runs SFML app.
    /// </summary>
    public async Task<Session> CreateSessionAsync(string sourceCode, CancellationToken ct = default)
    {
        if (_sessions.Count >= MaxSessions)
            throw new InvalidOperationException("Maximum number of concurrent sessions reached. Please try again later.");

        var sessionId = Guid.NewGuid().ToString("N")[..12];
        var session = new Session
        {
            Id = sessionId,
            SourceCode = sourceCode,
            Status = SessionStatus.Starting
        };

        _sessions[sessionId] = session;
        _logger.LogInformation("Session {SessionId}: Created", sessionId);

        try
        {
            await StartContainerAsync(session, ct);
            return session;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session {SessionId}: Failed to start", sessionId);
            session.Status = SessionStatus.Error;
            session.ErrorMessage = $"Failed to start execution environment: {ex.Message}";
            return session;
        }
    }

    private async Task StartContainerAsync(Session session, CancellationToken ct)
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

        // Copy source code into the container
        await CopySourceToContainerAsync(session, ct);

        // Start the container
        await _docker.Containers.StartContainerAsync(session.ContainerId, new ContainerStartParameters(), ct);
        _logger.LogInformation("Session {SessionId}: Container started", session.Id);

        // Monitor container output for compilation result
        await MonitorContainerOutputAsync(session, ct);
    }

    private async Task CopySourceToContainerAsync(Session session, CancellationToken ct)
    {
        // Create a tar archive containing main.cpp
        var sourceBytes = Encoding.UTF8.GetBytes(session.SourceCode);
        var tarBytes = CreateTarArchive("main.cpp", sourceBytes);

        using var stream = new MemoryStream(tarBytes);
        await _docker.Containers.ExtractArchiveToContainerAsync(
            session.ContainerId,
            new ContainerPathStatParameters { Path = "/workspace" },
            stream,
            ct);

        _logger.LogInformation("Session {SessionId}: Source code copied to container", session.Id);
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
    /// Stops a session and cleans up its container.
    /// </summary>
    public async Task StopSessionAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return;

        await StopSessionInternalAsync(session);
        _sessions.TryRemove(sessionId, out _);
    }

    private async Task StopSessionInternalAsync(Session session)
    {
        if (session.Status == SessionStatus.Stopped)
            return;

        session.Status = SessionStatus.Stopping;
        _logger.LogInformation("Session {SessionId}: Stopping", session.Id);

        try
        {
            if (!string.IsNullOrEmpty(session.ContainerId))
            {
                try
                {
                    await _docker.Containers.StopContainerAsync(
                        session.ContainerId,
                        new ContainerStopParameters { WaitBeforeKillSeconds = 3 });
                }
                catch (DockerContainerNotFoundException)
                {
                    // Container already removed (AutoRemove)
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Session {SessionId}: Error stopping container", session.Id);
                    // Try to kill it
                    try
                    {
                        await _docker.Containers.KillContainerAsync(
                            session.ContainerId,
                            new ContainerKillParameters { Signal = "KILL" });
                    }
                    catch { /* Best effort */ }
                }
            }
        }
        finally
        {
            session.Status = SessionStatus.Stopped;
            _logger.LogInformation("Session {SessionId}: Stopped and cleaned up", session.Id);
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

    private static byte[] CreateTarArchive(string fileName, byte[] content)
    {
        using var ms = new MemoryStream();
        // TAR header (512 bytes)
        var header = new byte[512];

        // File name (0-99)
        var nameBytes = Encoding.ASCII.GetBytes(fileName);
        Array.Copy(nameBytes, header, Math.Min(nameBytes.Length, 100));

        // File mode (100-107): 0644
        Encoding.ASCII.GetBytes("0000644\0").CopyTo(header, 100);

        // Owner ID (108-115): 1000
        Encoding.ASCII.GetBytes("0001000\0").CopyTo(header, 108);

        // Group ID (116-123): 1000
        Encoding.ASCII.GetBytes("0001000\0").CopyTo(header, 116);

        // File size in octal (124-135)
        var sizeStr = Convert.ToString(content.Length, 8).PadLeft(11, '0') + "\0";
        Encoding.ASCII.GetBytes(sizeStr).CopyTo(header, 124);

        // Modification time (136-147)
        var mtime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var mtimeStr = Convert.ToString(mtime, 8).PadLeft(11, '0') + "\0";
        Encoding.ASCII.GetBytes(mtimeStr).CopyTo(header, 136);

        // Checksum placeholder (148-155): spaces
        for (var i = 148; i < 156; i++)
            header[i] = (byte)' ';

        // Type flag (156): '0' = regular file
        header[156] = (byte)'0';

        // USTAR indicator (257-262)
        Encoding.ASCII.GetBytes("ustar\0").CopyTo(header, 257);

        // USTAR version (263-264)
        Encoding.ASCII.GetBytes("00").CopyTo(header, 263);

        // Calculate checksum
        var checksum = 0;
        for (var i = 0; i < 512; i++)
            checksum += header[i];
        var checksumStr = Convert.ToString(checksum, 8).PadLeft(6, '0') + "\0 ";
        Encoding.ASCII.GetBytes(checksumStr).CopyTo(header, 148);

        ms.Write(header, 0, 512);
        ms.Write(content, 0, content.Length);

        // Pad to 512 byte boundary
        var padding = 512 - (content.Length % 512);
        if (padding < 512)
            ms.Write(new byte[padding], 0, padding);

        // End of archive marker (two empty blocks)
        ms.Write(new byte[1024], 0, 1024);

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
