namespace SocialGraph.Api.Tests;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SocialGraph.Api.Database;
using SocialGraph.Api.Infrastructure.Outbox;

public sealed class IntegrationOutboxStoreTests
{
    [Fact]
    public async Task Enqueue_DeduplicatesByIdempotencyKey()
    {
        await using var dbContext = CreateDbContext();
        var store = CreateStore(dbContext);

        var first = await store.EnqueueAsync("test.v1", 42, "same-key", "{\"value\":1}");
        var second = await store.EnqueueAsync("test.v1", 42, "same-key", "{\"value\":1}");

        Assert.Equal(first.id, second.id);
        Assert.Equal(1, await dbContext.IntegrationOutboxTb.CountAsync());
    }

    [Fact]
    public async Task ClaimRetryDeadLetterAndReplay_TransitionsDurably()
    {
        await using var dbContext = CreateDbContext();
        var store = CreateStore(dbContext, maxAttempts: 2);
        var queued = await store.EnqueueAsync("test.v1", 42, "retry-key", "{}");

        var firstClaim = Assert.Single(await store.ClaimAsync("worker-a", 10, TimeSpan.FromMinutes(5)));
        Assert.Equal(1, firstClaim.attempts);
        Assert.Equal(IntegrationOutboxStatus.Processing, firstClaim.status);

        await store.MarkFailedAsync(queued.id, "temporary", TimeSpan.Zero, deadLetter: false);
        var secondClaim = Assert.Single(await store.ClaimAsync("worker-b", 10, TimeSpan.FromMinutes(5)));
        Assert.Equal(2, secondClaim.attempts);

        await store.MarkFailedAsync(queued.id, "poison", TimeSpan.Zero, deadLetter: true);
        var deadLetter = Assert.Single(await store.ListDeadLettersAsync(10));
        Assert.Equal("poison", deadLetter.last_error);

        Assert.True(await store.RequeueDeadLetterAsync(queued.id));
        var replay = Assert.Single(await store.ClaimAsync("worker-c", 10, TimeSpan.FromMinutes(5)));
        Assert.Equal(1, replay.attempts);
        await store.MarkCompletedAsync(replay.id);

        var completed = await dbContext.IntegrationOutboxTb.SingleAsync();
        Assert.Equal(IntegrationOutboxStatus.Completed, completed.status);
        Assert.NotNull(completed.completed_at);
        Assert.Empty(await store.ListDeadLettersAsync(10));
    }

    [Fact]
    public async Task Claim_RecoversStaleProcessingLock()
    {
        await using var dbContext = CreateDbContext();
        var store = CreateStore(dbContext);
        var queued = await store.EnqueueAsync("test.v1", 42, "stale-key", "{}");
        queued.status = IntegrationOutboxStatus.Processing;
        queued.locked_at = DateTimeOffset.UtcNow.AddMinutes(-10);
        queued.locked_by = "dead-worker";
        await dbContext.SaveChangesAsync();

        var claimed = Assert.Single(await store.ClaimAsync("new-worker", 10, TimeSpan.FromMinutes(5)));

        Assert.Equal("new-worker", claimed.locked_by);
        Assert.Equal(1, claimed.attempts);
    }

    private static MyDbContext CreateDbContext()
    {
        return new MyDbContext(new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
    }

    private static PostgresIntegrationOutboxStore CreateStore(MyDbContext dbContext, int maxAttempts = 10)
    {
        return new PostgresIntegrationOutboxStore(
            dbContext,
            Options.Create(new IntegrationOutboxOptions { MaxAttempts = maxAttempts }));
    }
}
