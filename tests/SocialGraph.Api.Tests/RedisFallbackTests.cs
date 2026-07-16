namespace SocialGraph.Api.Tests;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SocialGraph.Api.Database;
using SocialGraph.Api.Service;
using StackExchange.Redis;

public sealed class RedisFallbackTests
{
    [Fact]
    public async Task ObjectRead_FallsBackToPostgres_WhenRedisIsOffline()
    {
        await using var context = CreateContext();
        context.ObjectsTb.Add(new Objects { id = 101, otype = GraphObjectType.User, data = "{\"name\":\"Fallback\"}" });
        await context.SaveChangesAsync();
        var redis = new Mock<IDatabase>();
        redis.Setup(item => item.ExecuteAsync("JSON.GET", It.IsAny<object[]>()))
            .ThrowsAsync(Offline());
        var service = new ObjectService(
            context,
            Multiplexer(redis.Object).Object,
            NullLogger<ObjectService>.Instance);

        var result = await service.RetrieveObjectAsync(101);

        Assert.NotNull(result);
        Assert.Equal(GraphObjectType.User, result.otype);
    }

    [Fact]
    public async Task AssociationRead_FallsBackToPostgres_WhenRedisIsOffline()
    {
        await using var context = CreateContext();
        context.AssociationsTb.AddRange(
            new Associations { id1 = 101, atype = GraphAssociationType.Friend, id2 = 102, time = 2 },
            new Associations { id1 = 101, atype = GraphAssociationType.Friend, id2 = 103, time = 1 });
        await context.SaveChangesAsync();
        var redis = new Mock<IDatabase>();
        redis.Setup(item => item.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(Offline());
        var service = new AssociationService(
            context,
            Multiplexer(redis.Object).Object,
            NullLogger<AssociationService>.Instance);

        var page = await service.RetrieveAssociationAsync(101, GraphAssociationType.Friend, null, 10);

        Assert.Equal(new long[] { 102, 103 }, page.items.Select(item => item.id2));
    }

    private static Mock<IConnectionMultiplexer> Multiplexer(IDatabase database)
    {
        var multiplexer = new Mock<IConnectionMultiplexer>();
        multiplexer.Setup(item => item.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database);
        return multiplexer;
    }

    private static RedisConnectionException Offline() =>
        new(ConnectionFailureType.UnableToConnect, "Redis is offline for the fallback test.");

    private static MyDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MyDbContext(options);
    }
}

