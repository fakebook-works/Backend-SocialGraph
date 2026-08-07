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
        CancellationToken cancellationToken = default,
        IUserProvisioningCoordinator? userProvisioning = null)
    {
        try
        {
            await dispatcher.DispatchAsync(message, cancellationToken);
            if (message.event_type == IntegrationEventType.UserCreate && userProvisioning is not null)
            {
                await userProvisioning.CompleteAsync(store, message, cancellationToken);
            }
            else
            {
                await store.MarkCompletedAsync(message.id, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await store.ReleaseAsync(message.id, CancellationToken.None);
            throw;
        }
        catch (PermanentOutboxException exception) when (IsDurableAfterParentCommit(message))
        {
            // The parent row has already committed. Even a contract/ownership failure must
            // remain durable: a rolling deployment, offline reconciliation, or repaired
            // ownership record can make the exact attach/detach valid later. Dropping it
            // would permanently leak a reference or delete a committed parent's lease.
            var delay = CalculateBackoff(message);
            await store.MarkFailedAsync(
                message.id,
                exception.Message,
                delay,
                deadLetter: false,
                CancellationToken.None);
            _logger.LogError(
                exception,
                "Durable post-commit event {EventId} ({EventType}) hit a permanent-class failure; retry remains scheduled at {AvailableAt}.",
                message.id,
                message.event_type,
                DateTimeOffset.UtcNow + delay);
        }
        catch (PermanentOutboxException exception)
        {
            await DeadLetterAsync(store, message, exception.Message, userProvisioning);
            _logger.LogError(
                exception,
                "Integration event {EventId} ({EventType}) moved to dead-letter after a permanent failure.",
                message.id,
                message.event_type);
        }
        catch (Exception exception)
        {
            // Media lifecycle and erasure work describes parent state that has already
            // committed. It keeps a durable, capped-backoff repair row beyond the generic
            // attempt budget; losing it would leak storage or retain erased projections.
            var durableAfterParentCommit = IsDurableAfterParentCommit(message);
            var deadLetter = !durableAfterParentCommit && message.attempts >= message.max_attempts;
            var delay = deadLetter ? TimeSpan.Zero : CalculateBackoff(message);
            if (deadLetter)
            {
                await DeadLetterAsync(store, message, exception.Message, userProvisioning);
            }
            else
            {
                await store.MarkFailedAsync(
                    message.id,
                    exception.Message,
                    delay,
                    deadLetter: false,
                    CancellationToken.None);
            }
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

    private static bool IsDurableAfterParentCommit(IntegrationOutboxMessage message) =>
        message.event_type is
            IntegrationEventType.MediaFinalize or
            IntegrationEventType.MediaDelete or
            IntegrationEventType.UserDelete or
            IntegrationEventType.SearchDelete or
            IntegrationEventType.RecommendationUserDelete or
            IntegrationEventType.RecommendationContentDelete or
            IntegrationEventType.MessagingUserDelete;

    private static Task DeadLetterAsync(
        IIntegrationOutboxStore store,
        IntegrationOutboxMessage message,
        string error,
        IUserProvisioningCoordinator? userProvisioning)
    {
        return message.event_type == IntegrationEventType.UserCreate && userProvisioning is not null
            ? userProvisioning.CompensateAsync(store, message, error, CancellationToken.None)
            : store.MarkFailedAsync(
                message.id,
                error,
                TimeSpan.Zero,
                deadLetter: true,
                CancellationToken.None);
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
