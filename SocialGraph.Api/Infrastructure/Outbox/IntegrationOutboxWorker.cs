namespace SocialGraph.Api.Infrastructure.Outbox;

using Microsoft.Extensions.Options;
using SocialGraph.Api.Database;

public sealed class IntegrationOutboxWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IntegrationOutboxOptions _options;
    private readonly ILogger<IntegrationOutboxWorker> _logger;
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public IntegrationOutboxWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<IntegrationOutboxOptions> options,
        ILogger<IntegrationOutboxWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollMilliseconds = Math.Clamp(_options.PollMilliseconds, 100, 60_000);
        var maxIdlePollMilliseconds = Math.Clamp(
            _options.MaxIdlePollMilliseconds,
            pollMilliseconds,
            60_000);
        var idlePollMilliseconds = pollMilliseconds;
        var lockTimeout = TimeSpan.FromMinutes(Math.Clamp(_options.LockTimeoutMinutes, 1, 120));
        var lastCleanup = DateTimeOffset.MinValue;
        var schemaInitialized = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IIntegrationOutboxStore>();
                if (!schemaInitialized)
                {
                    await store.EnsureSchemaAsync(stoppingToken);
                    schemaInitialized = true;
                }

                var messages = await store.ClaimAsync(
                    _workerId,
                    Math.Clamp(_options.BatchSize, 1, 100),
                    lockTimeout,
                    stoppingToken);

                if (messages.Count > 0)
                {
                    var dispatcher = scope.ServiceProvider.GetRequiredService<IIntegrationOutboxDispatcher>();
                    var processor = scope.ServiceProvider.GetRequiredService<IIntegrationOutboxMessageProcessor>();
                    foreach (var message in messages)
                    {
                        await processor.ProcessAsync(store, dispatcher, message, stoppingToken);
                        processed++;
                    }
                }

                if (DateTimeOffset.UtcNow - lastCleanup > TimeSpan.FromHours(1))
                {
                    var retention = TimeSpan.FromDays(Math.Clamp(_options.CompletedRetentionDays, 1, 365));
                    await store.DeleteCompletedBeforeAsync(DateTimeOffset.UtcNow - retention, stoppingToken);
                    lastCleanup = DateTimeOffset.UtcNow;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                schemaInitialized = false;
                _logger.LogError(exception, "Integration outbox worker iteration failed.");
            }

            if (processed == 0)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(idlePollMilliseconds), stoppingToken);
                    idlePollMilliseconds = Math.Min(maxIdlePollMilliseconds, idlePollMilliseconds * 2);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
            else
            {
                idlePollMilliseconds = pollMilliseconds;
            }
        }
    }

}
