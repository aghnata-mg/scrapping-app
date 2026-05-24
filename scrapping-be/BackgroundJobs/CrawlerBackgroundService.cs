using scrapping_be.DTOs;
using scrapping_be.Models;
using scrapping_be.Services;

namespace scrapping_be.BackgroundJobs;

/// <summary>
/// Periodically triggers a crawl when a session is active.
/// Interval is controlled by <c>Crawler:ScheduleIntervalMinutes</c> in config.
/// </summary>
public sealed class CrawlerBackgroundService(
    CrawlerService crawlerService,
    SessionManager sessionManager,
    ILogger<CrawlerBackgroundService> logger,
    IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = configuration.GetValue("Crawler:ScheduleIntervalMinutes", 60);
        logger.LogInformation(
            "Crawler background service started. Crawl interval: {Interval} min", intervalMinutes);

        // Initial delay so the app fully starts before the first crawl attempt
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunScheduledCrawlAsync(stoppingToken);

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Crawler background service stopped");
    }

    private async Task RunScheduledCrawlAsync(CancellationToken ct)
    {
        if (sessionManager.Status is not CrawlerSessionStatus.LoggedIn)
        {
            logger.LogDebug("Skipping scheduled crawl — session status: {Status}", sessionManager.Status);
            return;
        }

        logger.LogInformation("Running scheduled crawl");

        try
        {
            var locationId = configuration["Crawler:DefaultLocationId"];
            if (string.IsNullOrWhiteSpace(locationId))
            {
                logger.LogDebug(
                    "Skipping scheduled crawl — Crawler:DefaultLocationId not configured.");
                return;
            }

            var result = await crawlerService.SearchAsync(
                new SearchRequestDto(LocationId: locationId, Page: 1, PageSize: 50));

            logger.LogInformation(
                "Scheduled crawl complete — {Total} total listings found", result.TotalCount);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Graceful shutdown, no action needed
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled crawl failed");
        }
    }
}
