namespace SocialGraph.Api.Service;

public sealed class StoryCleanupBackgroundService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<StoryCleanupBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = Math.Clamp(
            configuration.GetValue<int?>("StoryCleanup:IntervalMinutes") ?? 15,
            1,
            1_440);
        var batchSize = Math.Clamp(
            configuration.GetValue<int?>("StoryCleanup:BatchSize") ?? 100,
            1,
            500);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var contentService = scope.ServiceProvider.GetRequiredService<IContentGraphService>();
                var deleted = await contentService.CleanupExpiredStoriesAsync(batchSize, stoppingToken);
                if (deleted > 0)
                {
                    logger.LogInformation("Deleted {StoryCount} expired stories.", deleted);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Expired story cleanup failed.");
            }
        }
    }
}
