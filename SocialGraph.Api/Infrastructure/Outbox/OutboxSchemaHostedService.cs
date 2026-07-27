namespace SocialGraph.Api.Infrastructure.Outbox;

using Microsoft.Extensions.Options;

public sealed class OutboxSchemaHostedService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IntegrationOutboxOptions _options;
    private readonly ILogger<OutboxSchemaHostedService> _logger;

    public OutboxSchemaHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<IntegrationOutboxOptions> options,
        ILogger<OutboxSchemaHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.EnsureSchemaOnStartup)
        {
            _logger.LogInformation("Integration outbox schema initialization is disabled; migrations own DDL.");
            return;
        }

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
