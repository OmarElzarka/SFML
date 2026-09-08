using System.Net.WebSockets;
using SfmlPlayground.Api.Models;

namespace SfmlPlayground.Api.Services;

/// <summary>
/// Handles HTTP and WebSocket reverse-proxying between the student's browser and the internal container's websockify server.
/// Enables seamless HTTPS (port 443) and WSS connectivity without exposing internal VNC ports publicly or triggering mixed-content errors.
/// </summary>
public class VncProxyService
{
    private readonly DockerSessionService _sessionService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<VncProxyService> _logger;
    private readonly string _runnerHost;

    public VncProxyService(
        DockerSessionService sessionService,
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<VncProxyService> logger)
    {
        _sessionService = sessionService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _runnerHost = config.GetValue<string>("Runner:InternalHost") ?? "127.0.0.1";
    }

    public async Task HandleProxyRequestAsync(string sessionId, string? restPath, HttpContext context)
    {
        var session = _sessionService.GetSession(sessionId);
        if (session == null || session.Status != SessionStatus.Running || session.DisplayPort <= 0)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync($"Execution session '{sessionId}' is not active or running.");
            return;
        }

        var port = session.DisplayPort;
        var subPath = (restPath ?? string.Empty).TrimStart('/');

        // ─── 1. WebSocket Proxy (RFB VNC Stream) ──────────────────────────────
        if (context.WebSockets.IsWebSocketRequest)
        {
            await HandleWebSocketProxyAsync(sessionId, port, subPath, context);
            return;
        }

        // ─── 2. HTTP Static Assets Proxy (noVNC HTML/CSS/JS) ─────────────────
        await HandleHttpProxyAsync(port, subPath, context);
    }

    private async Task HandleWebSocketProxyAsync(string sessionId, int port, string subPath, HttpContext context)
    {
        var ct = context.RequestAborted;
        _logger.LogInformation("Session {SessionId}: Upgrading VNC WebSocket proxy on port {Port} (path: {Path})", sessionId, port, subPath);

        WebSocket clientWs;
        try
        {
            clientWs = await context.WebSockets.AcceptWebSocketAsync("binary");
        }
        catch
        {
            clientWs = await context.WebSockets.AcceptWebSocketAsync();
        }

        using (clientWs)
        {
            using var targetWs = new ClientWebSocket();
            targetWs.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            // Request binary subprotocol if supported
            targetWs.Options.AddSubProtocol("binary");

            var targetUri = new Uri($"ws://{_runnerHost}:{port}/{subPath}");

            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(TimeSpan.FromSeconds(10));
                await targetWs.ConnectAsync(targetUri, connectCts.Token);
                _logger.LogInformation("Session {SessionId}: Connected VNC proxy to target {TargetUri}", sessionId, targetUri);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session {SessionId}: Failed to connect VNC proxy to target {TargetUri}", sessionId, targetUri);
                if (clientWs.State == WebSocketState.Open)
                {
                    await clientWs.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "VNC container unavailable", ct);
                }
                return;
            }

            // Bidirectional proxy copy loop
            var clientToTarget = RelayWebSocketAsync(clientWs, targetWs, "client->target", ct);
            var targetToClient = RelayWebSocketAsync(targetWs, clientWs, "target->client", ct);

            await Task.WhenAny(clientToTarget, targetToClient);

            try
            {
                if (clientWs.State == WebSocketState.Open)
                    await clientWs.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Session ended", CancellationToken.None);
                if (targetWs.State == WebSocketState.Open)
                    await targetWs.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Session ended", CancellationToken.None);
            }
            catch { }
        }
    }

    private static async Task RelayWebSocketAsync(WebSocket source, WebSocket destination, string direction, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && source.State == WebSocketState.Open && destination.State == WebSocketState.Open)
            {
                var result = await source.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await destination.CloseOutputAsync(
                        result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                        result.CloseStatusDescription ?? string.Empty,
                        ct);
                    break;
                }

                await destination.SendAsync(
                    new ArraySegment<byte>(buffer, 0, result.Count),
                    result.MessageType,
                    result.EndOfMessage,
                    ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    private async Task HandleHttpProxyAsync(int port, string subPath, HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(subPath))
        {
            subPath = "vnc_lite.html";
        }

        var client = _httpClientFactory.CreateClient("VncHttpClient");
        var queryString = context.Request.QueryString.Value ?? string.Empty;
        var targetUrl = $"http://{_runnerHost}:{port}/{subPath}{queryString}";

        try
        {
            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, targetUrl);
            using var responseMessage = await client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

            context.Response.StatusCode = (int)responseMessage.StatusCode;

            if (responseMessage.Content.Headers.ContentType != null)
            {
                context.Response.ContentType = responseMessage.Content.Headers.ContentType.ToString();
            }
            else
            {
                // Fallback content types based on extension
                if (subPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                    context.Response.ContentType = "text/html; charset=utf-8";
                else if (subPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                    context.Response.ContentType = "application/javascript";
                else if (subPath.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
                    context.Response.ContentType = "text/css";
                else if (subPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    context.Response.ContentType = "image/png";
                else if (subPath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                    context.Response.ContentType = "image/svg+xml";
            }

            await responseMessage.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to proxy HTTP request to {TargetUrl}", targetUrl);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsync("Error connecting to VNC display backend.");
        }
    }
}
