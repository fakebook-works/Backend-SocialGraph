namespace SocialGraph.Api.Infrastructure.Outbox;

public sealed class OutboxSchemaHostedService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxSchemaHostedService> _logger;

    public OutboxSchemaHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<OutboxSchemaHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IIntegrationOutboxStore>();
            await store.EnsureSchemaAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Integration outbox schema initialization failed at startup; the worker will retry in the background.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
