namespace SocialGraph.Api.Infrastructure.Outbox;

using SocialGraph.Api.Database;

public sealed class IntegrationOutboxDispatcher : IIntegrationOutboxDispatcher
{
    private readonly IExternalServiceTransport _transport;

    public IntegrationOutboxDispatcher(IExternalServiceTransport transport)
    {
        _transport = transport;
    }

    public Task DispatchAsync(IntegrationOutboxMessage message, CancellationToken cancellationToken = default) =>
        _transport.DispatchAsync(message, cancellationToken);
}

