namespace SocialGraph.Api.Tests;

using Microsoft.EntityFrameworkCore;
using Moq;
using SocialGraph.Api.Database;
using SocialGraph.Api.Infrastructure;
using StackExchange.Redis;

public sealed class HealthProbeTests
{
    [Fact]
    public async Task Readiness_RemainsReady_WhenRedisUsesPostgresFallback()
    {
        var options = new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var context = new MyDbContext(options);
        var redis = new Mock<IConnectionMultiplexer>();
        redis.SetupGet(item => item.IsConnected).Returns(false);

        var result = await HealthProbe.CheckReadinessAsync(context, redis.Object);

        Assert.True(result.Ready);
        Assert.Equal("available", result.PostgreSql);
        Assert.Equal("postgres-fallback", result.Redis);
    }
}

