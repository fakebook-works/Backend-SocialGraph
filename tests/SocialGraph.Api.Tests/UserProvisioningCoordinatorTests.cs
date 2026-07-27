namespace SocialGraph.Api.Tests;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using SocialGraph.Api.Database;
using SocialGraph.Api.Infrastructure.Outbox;
using SocialGraph.Api.Service;

public sealed class UserProvisioningCoordinatorTests
{
    private const string EncryptionKey = "user-provisioning-test-key-at-least-32-bytes";

    [Fact]
    public async Task Completion_QueuesDerivedProjectionsOnlyAfterAuthAcknowledges()
    {
        await using var context = CreateContext();
        var external = new Mock<IExternalServiceClient>(MockBehavior.Strict);
        external.Setup(service => service.CreateSearchIndexAsync(123, "user", "Nguyen A", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        external.Setup(service => service.CreateUserEmbeddingAsync(123, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        external.Setup(service => service.CreateMessengerUserAsync(123, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var users = new Mock<IUserGraphService>(MockBehavior.Strict);
        var store = new Mock<IIntegrationOutboxStore>(MockBehavior.Strict);
        var message = Message();
        store.Setup(item => item.MarkCompletedAsync(message.id, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var coordinator = new UserProvisioningCoordinator(
            context,
            external.Object,
            users.Object,
            Protector());

        await coordinator.CompleteAsync(store.Object, message);

        external.VerifyAll();
        store.VerifyAll();
        users.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TerminalFailure_DeletesSocialProfileAndQueuesDownstreamCompensation()
    {
        await using var context = CreateContext();
        var external = new Mock<IExternalServiceClient>(MockBehavior.Strict);
        var users = new Mock<IUserGraphService>(MockBehavior.Strict);
        users.Setup(service => service.DeleteUserAsync(123, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var store = new Mock<IIntegrationOutboxStore>(MockBehavior.Strict);
        var message = Message();
        store.Setup(item => item.MarkFailedAsync(
                message.id,
                "auth rejected",
                TimeSpan.Zero,
                true,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var coordinator = new UserProvisioningCoordinator(
            context,
            external.Object,
            users.Object,
            Protector());

        await coordinator.CompensateAsync(store.Object, message, "auth rejected");

        users.VerifyAll();
        store.VerifyAll();
        external.VerifyNoOtherCalls();
    }

    private static IntegrationOutboxMessage Message()
    {
        var protector = Protector();
        var payload = JsonSerializer.Serialize(
            new UserCreateEvent(123, "a@example.com", "password", "Nguyen A", "2000-01-01", true),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new IntegrationOutboxMessage
        {
            id = Guid.NewGuid(),
            event_type = IntegrationEventType.UserCreate,
            aggregate_id = 123,
            idempotency_key = "register-123",
            payload = protector.Protect(payload),
            status = IntegrationOutboxStatus.Processing,
            created_at = DateTimeOffset.UtcNow,
            available_at = DateTimeOffset.UtcNow,
            attempts = 1,
            max_attempts = 10
        };
    }

    private static OutboxPayloadProtector Protector() => new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IntegrationOutbox:PayloadEncryptionKey"] = EncryptionKey
            })
            .Build());

    private static MyDbContext CreateContext() => new(
        new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
}
