using SfmlPlayground.Api.Services;

namespace SfmlPlayground.Api.Services;

/// <summary>
/// Background service that periodically cleans up expired sessions.
/// </summary>
public class SessionCleanupService : BackgroundService
{
    private readonly DockerSessionService _sessionService;
    private readonly ILogger<SessionCleanupService> _logger;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(30);

    public SessionCleanupService(DockerSessionService sessionService, ILogger<SessionCleanupService> logger)
    {
        _sessionService = sessionService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Session cleanup service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CleanupInterval, stoppingToken);
                await _sessionService.CleanupExpiredSessionsAsync();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during session cleanup");
            }
        }

        _logger.LogInformation("Session cleanup service stopped");
    }
}
