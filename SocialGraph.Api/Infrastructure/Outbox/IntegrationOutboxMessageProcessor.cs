namespace SocialGraph.Api.Infrastructure.Outbox;

using Microsoft.Extensions.Options;
using SocialGraph.Api.Database;

public sealed class IntegrationOutboxMessageProcessor : IIntegrationOutboxMessageProcessor
{
    private readonly IntegrationOutboxOptions _options;
    private readonly ILogger<IntegrationOutboxMessageProcessor> _logger;

    public IntegrationOutboxMessageProcessor(
        IOptions<IntegrationOutboxOptions> options,
        ILogger<IntegrationOutboxMessageProcessor> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task ProcessAsync(
        IIntegrationOutboxStore store,
        IIntegrationOutboxDispatcher dispatcher,
        IntegrationOutboxMessage message,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await dispatcher.DispatchAsync(message, cancellationToken);
            await store.MarkCompletedAsync(message.id, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await store.ReleaseAsync(message.id, CancellationToken.None);
            throw;
        }
        catch (PermanentOutboxException exception)
        {
            await store.MarkFailedAsync(
                message.id,
                exception.Message,
                TimeSpan.Zero,
                deadLetter: true,
                CancellationToken.None);
            _logger.LogError(
                exception,
                "Integration event {EventId} ({EventType}) moved to dead-letter after a permanent failure.",
                message.id,
                message.event_type);
        }
        catch (Exception exception)
        {
            var deadLetter = message.attempts >= message.max_attempts;
            var delay = deadLetter ? TimeSpan.Zero : CalculateBackoff(message);
            await store.MarkFailedAsync(
                message.id,
                exception.Message,
                delay,
                deadLetter,
                CancellationToken.None);
            if (deadLetter)
            {
                _logger.LogError(
                    exception,
                    "Integration event {EventId} ({EventType}) exhausted {Attempts} attempts and moved to dead-letter.",
                    message.id,
                    message.event_type,
                    message.attempts);
            }
            else
            {
                _logger.LogWarning(
                    exception,
                    "Integration event {EventId} ({EventType}) failed attempt {Attempts}; retrying at {AvailableAt}.",
                    message.id,
                    message.event_type,
                    message.attempts,
                    DateTimeOffset.UtcNow + delay);
            }
        }
    }

    private TimeSpan CalculateBackoff(IntegrationOutboxMessage message)
    {
        var baseSeconds = Math.Clamp(_options.BaseDelaySeconds, 1, 300);
        var maxSeconds = Math.Clamp(_options.MaxDelayMinutes, 1, 1440) * 60d;
        var exponent = Math.Clamp(message.attempts - 1, 0, 20);
        var seconds = Math.Min(maxSeconds, baseSeconds * Math.Pow(2, exponent));
        var jitterMilliseconds = Math.Abs(message.id.GetHashCode() % 1000);
        return TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(jitterMilliseconds);
    }
}
