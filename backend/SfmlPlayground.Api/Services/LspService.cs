using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.EntityFrameworkCore;
using SfmlPlayground.Api.Data;
using SfmlPlayground.Api.Models;

namespace SfmlPlayground.Api.Services;

/// <summary>
/// Service managing clangd C++ Language Server container and WebSocket LSP sessions.
/// </summary>
public class LspService : IDisposable
{
    private readonly ILogger<LspService> _logger;
    private readonly IConfiguration _config;
    private readonly PersistentRuntimeService _runtimeService;
    private readonly string _storageRoot;
    public const string LspContainerName = PersistentRuntimeService.ContainerName;

    public LspService(
        PersistentRuntimeService runtimeService,
        IWebHostEnvironment env,
        IConfiguration config,
        ILogger<LspService> logger)
    {
        _runtimeService = runtimeService;
        _logger = logger;
        _config = config;

        var configWorkspace = config.GetValue<string>("Storage:WorkspacePath");
        _storageRoot = !string.IsNullOrEmpty(configWorkspace)
            ? Path.GetFullPath(configWorkspace)
            : Path.GetFullPath(Path.Combine(env.ContentRootPath, "storage", "workspace"));
        Directory.CreateDirectory(_storageRoot);
    }

    /// <summary>
    /// Ensures the persistent sfml-runtime container is created and running.
    /// </summary>
    public async Task EnsureLspContainerRunningAsync(CancellationToken ct = default)
    {
        await _runtimeService.EnsureRuntimeContainerRunningAsync(ct);
    }

    /// <summary>
    /// Prepares the project directory on the host with compile_flags.txt and current files.
    /// </summary>
    public async Task<string> SyncProjectFilesAsync(int projectId, PlaygroundDbContext db, CancellationToken ct = default)
    {
        var projectDir = Path.Combine(_storageRoot, "projects", projectId.ToString());
        Directory.CreateDirectory(projectDir);

        // 1. Write compile_flags.txt so clangd knows where to find SFML 2.6.2 and project headers
        var flagsPath = Path.Combine(projectDir, "compile_flags.txt");
        var flagsContent = $"-std=c++17\n-I/usr/local/include\n-I/workspace/projects/{projectId}\n-DSFML_STATIC=0\n";
        await File.WriteAllTextAsync(flagsPath, flagsContent, ct);

        // 2. Sync all project files from database to disk
        var files = await db.ProjectFiles.Where(f => f.ProjectId == projectId).ToListAsync(ct);
        foreach (var file in files)
        {
            var filePath = Path.Combine(projectDir, file.Path);
            var fileDir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(fileDir))
            {
                Directory.CreateDirectory(fileDir);
            }
            await File.WriteAllTextAsync(filePath, file.Content, ct);
        }

        return projectDir;
    }

    /// <summary>
    /// Handles an active WebSocket connection by bridging it to an isolated clangd process inside the LSP container.
    /// </summary>
    public async Task HandleWebSocketAsync(
        int projectId,
        WebSocket webSocket,
        PlaygroundDbContext db,
        CancellationToken ct)
    {
        _logger.LogInformation("Starting LSP session for project {ProjectId}", projectId);

        try
        {
            await EnsureLspContainerRunningAsync(ct);
            await SyncProjectFilesAsync(projectId, db, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize LSP environment for project {ProjectId}", projectId);
            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.InternalServerError, "LSP setup failed", ct);
            }
            return;
        }

        // Launch clangd inside the running container via docker exec
        var containerWorkspaceDir = $"/workspace/projects/{projectId}";
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = $"exec -i {LspContainerName} clangd --background-index --compile-commands-dir={containerWorkspaceDir}",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process == null)
            {
                throw new InvalidOperationException("Failed to start clangd process via docker exec.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch clangd process for project {ProjectId}", projectId);
            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.InternalServerError, "Failed to start clangd", ct);
            }
            return;
        }

        using (process)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var linkedToken = cts.Token;

            // Log stderr in background
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!linkedToken.IsCancellationRequested)
                    {
                        var line = await process.StandardError.ReadLineAsync(linkedToken);
                        if (line == null) break;
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            _logger.LogDebug("[clangd-stderr:{ProjectId}] {Line}", projectId, line);
                        }
                    }
                }
                catch { }
            }, linkedToken);

            // Task 1: clangd stdout -> WebSocket
            var stdoutToWsTask = Task.Run(async () =>
            {
                var stdoutStream = process.StandardOutput.BaseStream;
                try
                {
                    while (!linkedToken.IsCancellationRequested && webSocket.State == WebSocketState.Open)
                    {
                        // Read Content-Length header
                        var headerLine = await ReadLineAsync(stdoutStream, linkedToken);
                        if (headerLine == null) break;

                        if (headerLine.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        {
                            var lengthStr = headerLine.Substring("Content-Length:".Length).Trim();
                            if (int.TryParse(lengthStr, out int contentLength) && contentLength > 0)
                            {
                                // Consume empty line \r\n separating header and body
                                await ReadLineAsync(stdoutStream, linkedToken);

                                // Read exact JSON payload
                                var payload = new byte[contentLength];
                                int totalRead = 0;
                                while (totalRead < contentLength)
                                {
                                    int read = await stdoutStream.ReadAsync(payload.AsMemory(totalRead, contentLength - totalRead), linkedToken);
                                    if (read == 0) break;
                                    totalRead += read;
                                }

                                if (totalRead == contentLength && webSocket.State == WebSocketState.Open)
                                {
                                    await webSocket.SendAsync(
                                        new ArraySegment<byte>(payload),
                                        WebSocketMessageType.Text,
                                        true,
                                        linkedToken);
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "LSP stdout -> WebSocket loop ended for project {ProjectId}", projectId);
                }
                finally
                {
                    cts.Cancel();
                }
            }, linkedToken);

            // Task 2: WebSocket -> clangd stdin
            var wsToStdinTask = Task.Run(async () =>
            {
                var buffer = new byte[64 * 1024];
                var stdinStream = process.StandardInput.BaseStream;

                try
                {
                    while (!linkedToken.IsCancellationRequested && webSocket.State == WebSocketState.Open)
                    {
                        var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), linkedToken);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", linkedToken);
                            break;
                        }

                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            using var ms = new MemoryStream();
                            ms.Write(buffer, 0, result.Count);

                            while (!result.EndOfMessage)
                            {
                                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), linkedToken);
                                ms.Write(buffer, 0, result.Count);
                            }

                            var messageBytes = ms.ToArray();
                            var messageStr = Encoding.UTF8.GetString(messageBytes).Trim();

                            if (!string.IsNullOrEmpty(messageStr))
                            {
                                byte[] bodyBytes;
                                if (messageStr.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                {
                                    bodyBytes = messageBytes;
                                }
                                else
                                {
                                    // Wrap standard JSON message with LSP header
                                    var jsonBytes = Encoding.UTF8.GetBytes(messageStr);
                                    var header = $"Content-Length: {jsonBytes.Length}\r\n\r\n";
                                    var headerBytes = Encoding.ASCII.GetBytes(header);

                                    bodyBytes = new byte[headerBytes.Length + jsonBytes.Length];
                                    Buffer.BlockCopy(headerBytes, 0, bodyBytes, 0, headerBytes.Length);
                                    Buffer.BlockCopy(jsonBytes, 0, bodyBytes, headerBytes.Length, jsonBytes.Length);
                                }

                                await stdinStream.WriteAsync(bodyBytes, linkedToken);
                                await stdinStream.FlushAsync(linkedToken);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "WebSocket -> LSP stdin loop ended for project {ProjectId}", projectId);
                }
                finally
                {
                    cts.Cancel();
                }
            }, linkedToken);

            await Task.WhenAny(stdoutToWsTask, wsToStdinTask);
            cts.Cancel();

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch { }

            _logger.LogInformation("LSP session finished for project {ProjectId}", projectId);
        }
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new byte[1];

        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, 1), ct);
            if (read == 0)
            {
                return sb.Length > 0 ? sb.ToString() : null;
            }

            char c = (char)buffer[0];
            if (c == '\n')
            {
                return sb.ToString().TrimEnd('\r');
            }

            sb.Append(c);
        }
    }

    public void Dispose()
    {
    }
}
