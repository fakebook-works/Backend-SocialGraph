namespace SocialGraph.Api.Tests;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SocialGraph.Api.Database;
using SocialGraph.Api.Service;
using StackExchange.Redis;

/// <summary>
/// Hydration used to read an entire association bucket and push all of it into a sorted set
/// with no limit, so the first page for an account with a million followers pulled a million
/// rows across the network, and nothing written to Redis ever expired.
/// </summary>
public sealed class AssociationCacheBoundsTests
{
    private const long OwnerId = 101;

    [Fact]
    public async Task A_bucket_larger_than_the_cap_is_served_from_postgres_and_never_cached()
    {
        await using var context = CreateContext(edges: 12);
        var redis = new RecordingRedis();
        var service = CreateService(context, redis, maxCachedEntries: 5);

        var page = await service.RetrieveAssociationAsync(OwnerId, GraphAssociationType.Friend, null, 10);

        // The whole page still comes back — bypassing the cache must not change the contract.
        Assert.Equal(10, page.items.Count);
        Assert.NotNull(page.nextCursor);
        Assert.Empty(redis.SortedSetAdds);
        Assert.Equal("0", redis.MarkerValue);
    }

    [Fact]
    public async Task An_oversized_bucket_is_not_measured_again_on_every_read()
    {
        await using var context = CreateContext(edges: 12);
        var redis = new RecordingRedis();
        var service = CreateService(context, redis, maxCachedEntries: 5);

        await service.RetrieveAssociationAsync(OwnerId, GraphAssociationType.Friend, null, 10);
        await service.RetrieveAssociationAsync(OwnerId, GraphAssociationType.Friend, null, 10);

        // The marker is written once; the second read short-circuits on it.
        Assert.Equal(1, redis.MarkerWrites);
    }

    [Fact]
    public async Task A_bucket_within_the_cap_is_cached_with_a_lifetime()
    {
        await using var context = CreateContext(edges: 3);
        var redis = new RecordingRedis();
        var service = CreateService(context, redis, maxCachedEntries: 5);

        await service.RetrieveAssociationAsync(OwnerId, GraphAssociationType.Friend, null, 10);

        Assert.NotEmpty(redis.SortedSetAdds);
        Assert.Equal("1", redis.MarkerValue);
        // Both the set and its marker must expire, or Redis grows for the life of the process
        // and a stale entry stays authoritative because reads prefer the cache.
        Assert.True(redis.MarkerHasExpiry, "The marker was written without a lifetime.");
        Assert.True(redis.SetTtlApplied, "The sorted set was written without a lifetime.");
    }

    private static AssociationService CreateService(
        MyDbContext context,
        RecordingRedis redis,
        int maxCachedEntries) =>
        new(
            context,
            Multiplexer(redis).Object,
            NullLogger<AssociationService>.Instance,
            Options.Create(new SocialGraphCacheOptions
            {
                MaxCachedAssociationEntries = maxCachedEntries,
                EntryTtlMinutes = 30
            }));

    private static MyDbContext CreateContext(int edges)
    {
        var context = new MyDbContext(new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
        for (var index = 1; index <= edges; index++)
        {
            context.AssociationsTb.Add(new Associations
            {
                id1 = OwnerId,
                atype = GraphAssociationType.Friend,
                id2 = 1_000 + index,
                time = index
            });
        }
        context.SaveChanges();
        return context;
    }

    private static Mock<IConnectionMultiplexer> Multiplexer(RecordingRedis redis)
    {
        var multiplexer = new Mock<IConnectionMultiplexer>();
        multiplexer.Setup(item => item.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(redis.Database);
        multiplexer.SetupGet(item => item.IsConnected).Returns(true);
        return multiplexer;
    }

    /// <summary>Records what the service asks Redis to do, and answers as an empty cache would.</summary>
    private sealed class RecordingRedis
    {
        private readonly Mock<IDatabase> _database = new();

        public RecordingRedis()
        {
            _database
                .Setup(item => item.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(() => MarkerValue is null ? RedisValue.Null : MarkerValue);
            _database
                .Setup(item => item.StringSetAsync(
                    It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<Expiration>(),
                    It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>()))
                .Callback((RedisKey _, RedisValue value, Expiration expiry, ValueCondition _, CommandFlags _) =>
                {
                    MarkerValue = value.ToString();
                    MarkerHasExpiry = !expiry.Equals(default(Expiration));
                    MarkerWrites++;
                })
                .ReturnsAsync(true);
            _database
                .Setup(item => item.SortedSetAddAsync(
                    It.IsAny<RedisKey>(), It.IsAny<SortedSetEntry[]>(),
                    It.IsAny<SortedSetWhen>(), It.IsAny<CommandFlags>()))
                .Callback((RedisKey _, SortedSetEntry[] entries, SortedSetWhen _, CommandFlags _) =>
                    SortedSetAdds.Add(entries.Length))
                .ReturnsAsync(0L);
            _database
                .Setup(item => item.KeyExpireAsync(
                    It.IsAny<RedisKey>(), It.IsAny<TimeSpan?>(), It.IsAny<ExpireWhen>(), It.IsAny<CommandFlags>()))
                .Callback(() => SetTtlApplied = true)
                .ReturnsAsync(true);
            _database
                .Setup(item => item.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .Callback(() => KeyDeletes++)
                .ReturnsAsync(true);
            _database
                .Setup(item => item.SortedSetRangeByRankWithScoresAsync(
                    It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<long>(),
                    It.IsAny<Order>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(Array.Empty<SortedSetEntry>());
            _database
                .Setup(item => item.SortedSetLengthAsync(
                    It.IsAny<RedisKey>(), It.IsAny<double>(), It.IsAny<double>(),
                    It.IsAny<Exclude>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(0L);
        }

        public IDatabase Database => _database.Object;

        public List<int> SortedSetAdds { get; } = [];

        public string? MarkerValue { get; private set; }

        public bool MarkerHasExpiry { get; private set; }

        public int MarkerWrites { get; private set; }

        public bool SetTtlApplied { get; private set; }

        public int KeyDeletes { get; private set; }
    }
}
