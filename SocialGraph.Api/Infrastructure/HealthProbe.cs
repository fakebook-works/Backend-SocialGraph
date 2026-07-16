namespace SocialGraph.Api.Infrastructure;

using SocialGraph.Api.Database;
using StackExchange.Redis;

public sealed record ServiceReadinessResult(
    bool Ready,
    string PostgreSql,
    string Redis);

public static class HealthProbe
{
    public static async Task<ServiceReadinessResult> CheckReadinessAsync(
        MyDbContext dbContext,
        IConnectionMultiplexer redis,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await dbContext.Database.CanConnectAsync(cancellationToken))
            {
                return new ServiceReadinessResult(false, "unavailable", RedisStatus(redis));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ServiceReadinessResult(false, "unavailable", RedisStatus(redis));
        }

        return new ServiceReadinessResult(true, "available", RedisStatus(redis));
    }

    private static string RedisStatus(IConnectionMultiplexer redis) =>
        redis.IsConnected ? "available" : "postgres-fallback";
}

