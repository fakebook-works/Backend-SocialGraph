namespace SocialGraph.Api.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SocialGraph.Api.Database;
using SocialGraph.Api.Infrastructure.Outbox;

public sealed class IntegrationOutboxProcessorTests
{
    [Fact]
    public async Task Success_MarksMessageCompleted()
    {
        var store = new Mock<IIntegrationOutboxStore>(MockBehavior.Strict);
        var dispatcher = new Mock<IIntegrationOutboxDispatcher>(MockBehavior.Strict);
        var message = Message(attempts: 1, maxAttempts: 3);
        dispatcher.Setup(item => item.DispatchAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(item => item.MarkCompletedAsync(message.id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await Processor().ProcessAsync(store.Object, dispatcher.Object, message);

        store.VerifyAll();
        dispatcher.VerifyAll();
    }

    [Fact]
    public async Task TransientFailure_SchedulesExponentialRetry()
    {
        var store = new Mock<IIntegrationOutboxStore>(MockBehavior.Strict);
        var dispatcher = new Mock<IIntegrationOutboxDispatcher>(MockBehavior.Strict);
        var message = Message(attempts: 2, maxAttempts: 3);
        dispatcher
            .Setup(item => item.DispatchAsync(message, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("temporary"));
        store
            .Setup(item => item.MarkFailedAsync(
                message.id,
                "temporary",
                It.Is<TimeSpan>(delay => delay >= TimeSpan.FromSeconds(4) && delay < TimeSpan.FromSeconds(5)),
                false,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await Processor().ProcessAsync(store.Object, dispatcher.Object, message);

        store.VerifyAll();
    }

    [Fact]
    public async Task ExhaustedTransientFailure_MovesMessageToDeadLetter()
    {
        var store = new Mock<IIntegrationOutboxStore>(MockBehavior.Strict);
        var dispatcher = new Mock<IIntegrationOutboxDispatcher>(MockBehavior.Strict);
        var message = Message(attempts: 3, maxAttempts: 3);
        dispatcher
            .Setup(item => item.DispatchAsync(message, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("still down"));
        store
            .Setup(item => item.MarkFailedAsync(
                message.id,
                "still down",
                TimeSpan.Zero,
                true,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await Processor().ProcessAsync(store.Object, dispatcher.Object, message);

        store.VerifyAll();
    }

    [Fact]
    public async Task PermanentFailure_MovesMessageDirectlyToDeadLetter()
    {
        var store = new Mock<IIntegrationOutboxStore>(MockBehavior.Strict);
        var dispatcher = new Mock<IIntegrationOutboxDispatcher>(MockBehavior.Strict);
        var message = Message(attempts: 1, maxAttempts: 10);
        dispatcher
            .Setup(item => item.DispatchAsync(message, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PermanentOutboxException("bad contract"));
        store
            .Setup(item => item.MarkFailedAsync(
                message.id,
                "bad contract",
                TimeSpan.Zero,
                true,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await Processor().ProcessAsync(store.Object, dispatcher.Object, message);

        store.VerifyAll();
    }

    [Fact]
    public async Task Shutdown_ReleasesClaimedMessage()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var store = new Mock<IIntegrationOutboxStore>(MockBehavior.Strict);
        var dispatcher = new Mock<IIntegrationOutboxDispatcher>(MockBehavior.Strict);
        var message = Message(attempts: 1, maxAttempts: 3);
        dispatcher
            .Setup(item => item.DispatchAsync(message, cancellation.Token))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        store
            .Setup(item => item.ReleaseAsync(message.id, CancellationToken.None))
            .Returns(Task.CompletedTask);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Processor().ProcessAsync(store.Object, dispatcher.Object, message, cancellation.Token));

        store.VerifyAll();
    }

    private static IntegrationOutboxMessageProcessor Processor()
    {
        return new IntegrationOutboxMessageProcessor(
            Options.Create(new IntegrationOutboxOptions
            {
                BaseDelaySeconds = 2,
                MaxDelayMinutes = 15
            }),
            NullLogger<IntegrationOutboxMessageProcessor>.Instance);
    }

    private static IntegrationOutboxMessage Message(int attempts, int maxAttempts)
    {
        return new IntegrationOutboxMessage
        {
            id = Guid.NewGuid(),
            event_type = "test.v1",
            idempotency_key = "test-key",
            payload = "{}",
            created_at = DateTimeOffset.UtcNow,
            available_at = DateTimeOffset.UtcNow,
            attempts = attempts,
            max_attempts = maxAttempts,
            status = IntegrationOutboxStatus.Processing
        };
    }
}
