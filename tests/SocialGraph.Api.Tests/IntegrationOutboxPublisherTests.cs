namespace SocialGraph.Api.Tests;

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SocialGraph.Api.Database;
using SocialGraph.Api.Infrastructure.Outbox;

public sealed class IntegrationOutboxPublisherTests
{
    private const string EncryptionKey = "integration-outbox-test-key-at-least-32-bytes";

    [Fact]
    public async Task CreateUser_QueuesIndependentServiceEventsAndEncryptsCredentials()
    {
        await using var dbContext = CreateDbContext();
        var store = new PostgresIntegrationOutboxStore(
            dbContext,
            Options.Create(new IntegrationOutboxOptions()));
        var configuration = Configuration();
        var protector = new OutboxPayloadProtector(configuration);
        var context = new DefaultHttpContext();
        context.TraceIdentifier = "trace-id";
        context.Request.Headers["Idempotency-Key"] = "register-request-1";
        var publisher = new IntegrationOutboxPublisher(
            store,
            new HttpContextAccessor { HttpContext = context },
            protector);

        await publisher.CreateUserAsync(123, "a@example.com", "plain-password", "Nguyen A", "2000-01-01", true);
        await publisher.CreateUserAsync(123, "a@example.com", "plain-password", "Nguyen A", "2000-01-01", true);

        var messages = await dbContext.IntegrationOutboxTb.OrderBy(item => item.event_type).ToListAsync();
        Assert.Equal(4, messages.Count);
        Assert.Equal(4, messages.Select(item => item.idempotency_key).Distinct().Count());
        Assert.Contains(messages, item => item.event_type == IntegrationEventType.SearchUpsert);
        Assert.Contains(messages, item => item.event_type == IntegrationEventType.RecommendationUserUpsert);
        Assert.Contains(messages, item => item.event_type == IntegrationEventType.MessagingUserCreate);

        var auth = Assert.Single(messages, item => item.event_type == IntegrationEventType.UserCreate);
        Assert.DoesNotContain("plain-password", auth.payload, StringComparison.Ordinal);
        Assert.DoesNotContain("a@example.com", auth.payload, StringComparison.Ordinal);
        var decrypted = JsonSerializer.Deserialize<UserCreateEvent>(
            protector.Unprotect(auth.payload),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(decrypted);
        Assert.Equal("plain-password", decrypted.Password);
    }

    [Fact]
    public async Task ExplicitIdempotencyKey_DeduplicatesClientRetries()
    {
        await using var dbContext = CreateDbContext();
        var store = new PostgresIntegrationOutboxStore(
            dbContext,
            Options.Create(new IntegrationOutboxOptions()));
        var configuration = Configuration();
        var context = new DefaultHttpContext();
        context.Request.Headers["Idempotency-Key"] = "same-client-operation";
        var publisher = new IntegrationOutboxPublisher(
            store,
            new HttpContextAccessor { HttpContext = context },
            new OutboxPayloadProtector(configuration));

        await publisher.NotifyAsync(1, 2, 4, 1, null);
        await publisher.NotifyAsync(1, 2, 4, 1, null);

        Assert.Equal(1, await dbContext.IntegrationOutboxTb.CountAsync());
    }

    [Fact]
    public async Task RecommendationInteraction_QueuesCanonicalFeedbackEvent()
    {
        await using var dbContext = CreateDbContext();
        var store = new PostgresIntegrationOutboxStore(
            dbContext,
            Options.Create(new IntegrationOutboxOptions()));
        var configuration = Configuration();
        var context = new DefaultHttpContext();
        context.Request.Headers["Idempotency-Key"] = "save-operation";
        var publisher = new IntegrationOutboxPublisher(
            store,
            new HttpContextAccessor { HttpContext = context },
            new OutboxPayloadProtector(configuration));

        await publisher.RecordRecommendationInteractionAsync(123, 456, "SAVE");
        await publisher.RecordRecommendationInteractionAsync(123, 456, "SAVE");

        var message = Assert.Single(await dbContext.IntegrationOutboxTb.ToListAsync());
        Assert.Equal(IntegrationEventType.RecommendationInteraction, message.event_type);
        Assert.Equal(123, message.aggregate_id);
        var payload = JsonSerializer.Deserialize<RecommendationInteractionEvent>(
            message.payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(payload);
        Assert.Equal(123, payload.UserId);
        Assert.Equal(456, payload.TargetId);
        Assert.Equal("SAVE", payload.Action);
    }

    [Fact]
    public async Task MediaLifecycle_QueuesDeduplicatedFinalizeAndDeleteEvents()
    {
        await using var dbContext = CreateDbContext();
        var store = new PostgresIntegrationOutboxStore(dbContext, Options.Create(new IntegrationOutboxOptions()));
        var configuration = Configuration();
        var context = new DefaultHttpContext();
        context.Request.Headers["Idempotency-Key"] = "media-operation";
        var publisher = new IntegrationOutboxPublisher(
            store,
            new HttpContextAccessor { HttpContext = context },
            new OutboxPayloadProtector(configuration));

        await publisher.FinalizeMediaAsync(new[] { "/media/files/a.jpg", "/media/files/a.jpg" });
        await publisher.DeleteMediaAsync(new[] { "/media/files/b.jpg" });

        var messages = await dbContext.IntegrationOutboxTb.OrderBy(item => item.event_type).ToListAsync();
        Assert.Equal(2, messages.Count);
        Assert.Contains(messages, item => item.event_type == IntegrationEventType.MediaFinalize);
        Assert.Contains(messages, item => item.event_type == IntegrationEventType.MediaDelete);
        var finalize = JsonSerializer.Deserialize<MediaLifecycleEvent>(
            Assert.Single(messages, item => item.event_type == IntegrationEventType.MediaFinalize).payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(new[] { "/media/files/a.jpg" }, finalize?.Urls);
    }

    private static IConfiguration Configuration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IntegrationOutbox:PayloadEncryptionKey"] = "",
                ["InternalServices:SocialGraph:SharedSecret"] = EncryptionKey
            })
            .Build();
    }

    private static MyDbContext CreateDbContext()
    {
        return new MyDbContext(new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
    }
}
