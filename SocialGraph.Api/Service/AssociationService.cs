namespace SocialGraph.Api.Service;

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using SocialGraph.Api.Database;
using StackExchange.Redis;
using IDbContextTransaction = Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction;

public sealed class AssociationService : IAssociationService
{
    private const string CacheKeyPrefix = "socialgraph:v2:association";

    private readonly MyDbContext _dbContext;
    private readonly IConnectionMultiplexer _redisConnection;
    private readonly IDatabase _redis;
    private readonly ILogger<AssociationService> _logger;
    private readonly bool _cacheEnabled;
    private long _lastRedisWarningTicks;

    public AssociationService(
        MyDbContext dbContext,
        IConnectionMultiplexer redis,
        ILogger<AssociationService> logger,
        IOptions<SocialGraphCacheOptions>? cacheOptions = null)
    {
        _dbContext = dbContext;
        _redisConnection = redis;
        _redis = redis.GetDatabase();
        _logger = logger;
        _cacheEnabled = cacheOptions?.Value.Enabled ?? true;
    }

    public async Task<bool> AddAssociationAsync(
        long id1,
        short atype,
        long id2,
        CancellationToken cancellationToken = default)
    {
        await ValidateAssociationAsync(id1, atype, id2, cancellationToken);
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await using var transaction = await BeginOwnedTransactionAsync(cancellationToken);
        await UpsertAssociationAsync(id1, atype, id2, time, cancellationToken);

        if (TryGetInverseAssociationType(atype, out var inverseAtype) && !IsSameAssociation(id1, atype, id2, id2, inverseAtype, id1))
        {
            await UpsertAssociationAsync(id2, inverseAtype, id1, time, cancellationToken);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        await TryRedisWriteAsync(async () =>
        {
            await AddToCacheOrHydrateAsync(id1, atype, id2, time, cancellationToken);
            if (TryGetInverseAssociationType(atype, out var cacheInverseAtype))
            {
                await AddToCacheOrHydrateAsync(id2, cacheInverseAtype, id1, time, cancellationToken);
            }
        });

        return true;
    }

    public async Task<bool> ApplyMutationsAsync(
        IReadOnlyCollection<AssociationMutation> mutations,
        CancellationToken cancellationToken = default)
    {
        if (mutations.Count == 0)
        {
            return false;
        }

        foreach (var mutation in mutations.Where(item => item.add))
        {
            await ValidateAssociationAsync(mutation.id1, mutation.atype, mutation.id2, cancellationToken);
        }

        var expanded = new Dictionary<(long id1, short atype, long id2), bool>();
        foreach (var mutation in mutations)
        {
            expanded[(mutation.id1, mutation.atype, mutation.id2)] = mutation.add;
            if (TryGetInverseAssociationType(mutation.atype, out var inverseAtype))
            {
                expanded[(mutation.id2, inverseAtype, mutation.id1)] = mutation.add;
            }
        }

        var affected = 0;
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var transaction = await BeginOwnedTransactionAsync(cancellationToken);
        foreach (var mutation in expanded)
        {
            affected += mutation.Value
                ? await UpsertAssociationAsync(
                    mutation.Key.id1,
                    mutation.Key.atype,
                    mutation.Key.id2,
                    time,
                    cancellationToken)
                : await DeleteAssociationRowAsync(
                    mutation.Key.id1,
                    mutation.Key.atype,
                    mutation.Key.id2,
                    cancellationToken);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        await TryRedisWriteAsync(async () =>
        {
            foreach (var key in expanded.Keys.Select(item => (item.id1, item.atype)).Distinct())
            {
                await DeleteAssociationCacheAsync(key.id1, key.atype);
            }
        });

        return affected > 0;
    }

    public Task<bool> HasAssociationAsync(
        long id1,
        short atype,
        long id2,
        CancellationToken cancellationToken = default)
    {
        if (!GraphAssociationRules.IsKnown(atype))
        {
            return Task.FromResult(false);
        }

        return _dbContext.AssociationsTb
            .AsNoTracking()
            .AnyAsync(item => item.id1 == id1 && item.atype == atype && item.id2 == id2, cancellationToken);
    }

    public async Task<bool> DeleteOneAssociationAsync(
        long id1,
        short atype,
        long id2,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginOwnedTransactionAsync(cancellationToken);
        var rows = await DeleteAssociationRowAsync(id1, atype, id2, cancellationToken);

        if (TryGetInverseAssociationType(atype, out var inverseAtype) && !IsSameAssociation(id1, atype, id2, id2, inverseAtype, id1))
        {
            rows += await DeleteAssociationRowAsync(id2, inverseAtype, id1, cancellationToken);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        await TryRedisWriteAsync(async () =>
        {
            await RemoveFromCacheIfLoadedAsync(id1, atype, id2);
            if (TryGetInverseAssociationType(atype, out var cacheInverseAtype))
            {
                await RemoveFromCacheIfLoadedAsync(id2, cacheInverseAtype, id1);
            }
        });

        return rows > 0;
    }

    public async Task<bool> DeleteAllAssociationAsync(
        long id1,
        short atype,
        CancellationToken cancellationToken = default)
    {
        List<long> inverseIds = [];
        if (TryGetInverseAssociationType(atype, out var inverseAtype))
        {
            inverseIds = await _dbContext.AssociationsTb
                .AsNoTracking()
                .Where(item => item.id1 == id1 && item.atype == atype)
                .Select(item => item.id2)
                .ToListAsync(cancellationToken);
        }

        await using var transaction = await BeginOwnedTransactionAsync(cancellationToken);
        var rows = await _dbContext.Database.ExecuteSqlRawAsync(
            "DELETE FROM social_graph.associations WHERE id1 = @id1 AND atype = @atype",
            new object[]
            {
                new NpgsqlParameter("id1", id1),
                new NpgsqlParameter("atype", atype)
            },
            cancellationToken);

        if (TryGetInverseAssociationType(atype, out inverseAtype))
        {
            rows += await _dbContext.Database.ExecuteSqlRawAsync(
                "DELETE FROM social_graph.associations WHERE atype = @atype AND id2 = @id2",
                new object[]
                {
                    new NpgsqlParameter("atype", inverseAtype),
                    new NpgsqlParameter("id2", id1)
                },
                cancellationToken);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        await TryRedisWriteAsync(async () =>
        {
            await DeleteAssociationCacheAsync(id1, atype);
            if (TryGetInverseAssociationType(atype, out var cacheInverseAtype))
            {
                foreach (var inverseId in inverseIds)
                {
                    await RemoveFromCacheIfLoadedAsync(inverseId, cacheInverseAtype, id1);
                }
            }
        });

        return rows > 0;
    }

    public async Task<int> DeleteObjectAssociationsAsync(long objectId, CancellationToken cancellationToken = default)
    {
        var affectedKeys = await _dbContext.AssociationsTb
            .AsNoTracking()
            .Where(item => item.id1 == objectId ||
                (item.id2 == objectId && item.atype != GraphAssociationType.Share))
            .Select(item => new { item.id1, item.atype })
            .Distinct()
            .ToListAsync(cancellationToken);

        var rows = await _dbContext.Database.ExecuteSqlRawAsync(
            "DELETE FROM social_graph.associations WHERE id1 = @id OR (id2 = @id AND atype <> @share)",
            new object[]
            {
                new NpgsqlParameter("id", objectId),
                new NpgsqlParameter("share", GraphAssociationType.Share)
            },
            cancellationToken);

        await TryRedisWriteAsync(async () =>
        {
            foreach (var key in affectedKeys)
            {
                await DeleteAssociationCacheAsync(key.id1, key.atype);
            }
        });

        return rows;
    }

    public async Task<long> CountAssociationAsync(
        long id1,
        short atype,
        CancellationToken cancellationToken = default)
    {
        if (!CanUseRedis())
        {
            return await CountFromPostgresAsync(id1, atype, cancellationToken);
        }

        try
        {
            await EnsureAssociationCacheAsync(id1, atype, cancellationToken);
            return await _redis.SortedSetLengthAsync(AssociationKey(id1, atype));
        }
        catch (RedisException exception)
        {
            LogRedisFallback(exception);
            return await CountFromPostgresAsync(id1, atype, cancellationToken);
        }
    }

    public async Task<AssociationPageResult> RetrieveAssociationAsync(
        long id1,
        short atype,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var skip = ParseCursor(cursor);
        var take = Math.Clamp(limit, 1, 100);

        if (!CanUseRedis())
        {
            return await RetrieveFromPostgresAsync(id1, atype, skip, take, cancellationToken);
        }

        try
        {
            await EnsureAssociationCacheAsync(id1, atype, cancellationToken);

            var key = AssociationKey(id1, atype);
            var entries = await _redis.SortedSetRangeByRankWithScoresAsync(
                key,
                skip,
                skip + take - 1,
                Order.Descending);

            var items = entries
                .Select(entry => new AssociationEdgeResult(ParseId(entry.Element), (long)entry.Score))
                .ToArray();

            var total = await _redis.SortedSetLengthAsync(key);
            var nextOffset = skip + items.Length;
            var nextCursor = nextOffset < total
                ? nextOffset.ToString(CultureInfo.InvariantCulture)
                : null;

            return new AssociationPageResult(items, nextCursor);
        }
        catch (RedisException exception)
        {
            LogRedisFallback(exception);
            return await RetrieveFromPostgresAsync(id1, atype, skip, take, cancellationToken);
        }
    }

    private async Task<int> UpsertAssociationAsync(
        long id1,
        short atype,
        long id2,
        long time,
        CancellationToken cancellationToken)
    {
        return await _dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO social_graph.associations (id1, atype, id2, time)
            VALUES (@id1, @atype, @id2, @time)
            ON CONFLICT (id1, atype, id2) DO UPDATE SET time = EXCLUDED.time
            """,
            new object[]
            {
                new NpgsqlParameter("id1", id1),
                new NpgsqlParameter("atype", atype),
                new NpgsqlParameter("id2", id2),
                new NpgsqlParameter("time", time)
            },
            cancellationToken);
    }

    private async Task<int> DeleteAssociationRowAsync(
        long id1,
        short atype,
        long id2,
        CancellationToken cancellationToken)
    {
        return await _dbContext.Database.ExecuteSqlRawAsync(
            "DELETE FROM social_graph.associations WHERE id1 = @id1 AND atype = @atype AND id2 = @id2",
            new object[]
            {
                new NpgsqlParameter("id1", id1),
                new NpgsqlParameter("atype", atype),
                new NpgsqlParameter("id2", id2)
            },
            cancellationToken);
    }

    private async Task EnsureAssociationCacheAsync(long id1, short atype, CancellationToken cancellationToken)
    {
        if (await _redis.KeyExistsAsync(AssociationMarkerKey(id1, atype)))
        {
            return;
        }

        await HydrateAssociationCacheAsync(id1, atype, cancellationToken);
    }

    private async Task HydrateAssociationCacheAsync(long id1, short atype, CancellationToken cancellationToken)
    {
        var rows = await _dbContext.AssociationsTb
            .AsNoTracking()
            .Where(item => item.id1 == id1 && item.atype == atype)
            .Select(item => new { item.id2, item.time })
            .ToListAsync(cancellationToken);

        var key = AssociationKey(id1, atype);
        await _redis.KeyDeleteAsync(key);

        if (rows.Count > 0)
        {
            var entries = rows
                .Select(item => new SortedSetEntry(ToRedisValue(item.id2), item.time))
                .ToArray();

            await _redis.SortedSetAddAsync(key, entries);
        }

        await _redis.StringSetAsync(AssociationMarkerKey(id1, atype), "1");
    }

    private async Task AddToCacheOrHydrateAsync(
        long id1,
        short atype,
        long id2,
        long time,
        CancellationToken cancellationToken)
    {
        if (await _redis.KeyExistsAsync(AssociationMarkerKey(id1, atype)))
        {
            await _redis.SortedSetAddAsync(AssociationKey(id1, atype), ToRedisValue(id2), time);
            return;
        }

        await HydrateAssociationCacheAsync(id1, atype, cancellationToken);
    }

    private async Task RemoveFromCacheIfLoadedAsync(long id1, short atype, long id2)
    {
        if (!await _redis.KeyExistsAsync(AssociationMarkerKey(id1, atype)))
        {
            return;
        }

        await _redis.SortedSetRemoveAsync(AssociationKey(id1, atype), ToRedisValue(id2));
    }

    private async Task DeleteAssociationCacheAsync(long id1, short atype)
    {
        await _redis.KeyDeleteAsync(new RedisKey[]
        {
            AssociationKey(id1, atype),
            AssociationMarkerKey(id1, atype)
        });
    }

    private static bool TryGetInverseAssociationType(short atype, out short inverseAtype)
    {
        return GraphAssociationRules.TryGetInverse(atype, out inverseAtype);
    }

    private async Task ValidateAssociationAsync(
        long id1,
        short atype,
        long id2,
        CancellationToken cancellationToken)
    {
        if (!GraphAssociationRules.IsKnown(atype))
        {
            throw new ArgumentOutOfRangeException(nameof(atype), $"Unsupported association type: {atype}.");
        }

        if (id1 <= 0 || id2 <= 0 || id1 == id2)
        {
            throw new ArgumentException("Association endpoints must be different positive object IDs.");
        }

        var endpointTypes = await _dbContext.ObjectsTb
            .AsNoTracking()
            .Where(item => item.id == id1 || item.id == id2)
            .Select(item => new { item.id, item.otype })
            .ToDictionaryAsync(item => item.id, item => item.otype, cancellationToken);

        if (!endpointTypes.TryGetValue(id1, out var sourceType) ||
            !endpointTypes.TryGetValue(id2, out var targetType))
        {
            throw new InvalidOperationException("Both association endpoints must exist.");
        }

        if (!GraphAssociationRules.IsValidForObjectTypes(atype, sourceType, targetType))
        {
            throw new InvalidOperationException(
                $"Association type {atype} is invalid between object types {sourceType} and {targetType}.");
        }
    }

    private static bool IsSameAssociation(long id1, short atype, long id2, long otherId1, short otherAtype, long otherId2)
    {
        return id1 == otherId1 && atype == otherAtype && id2 == otherId2;
    }

    private static long ParseCursor(string? cursor)
    {
        return long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) && offset > 0
            ? offset
            : 0;
    }

    private static long ParseId(RedisValue value)
    {
        return long.Parse(value.ToString(), CultureInfo.InvariantCulture);
    }

    private static RedisValue ToRedisValue(long id)
    {
        return id.ToString(CultureInfo.InvariantCulture);
    }

    private static RedisKey AssociationKey(long id1, short atype)
    {
        return $"{CacheKeyPrefix}:{id1.ToString(CultureInfo.InvariantCulture)}:{atype.ToString(CultureInfo.InvariantCulture)}";
    }

    private static RedisKey AssociationMarkerKey(long id1, short atype)
    {
        return $"{AssociationKey(id1, atype)}:cached";
    }

    private async Task TryRedisWriteAsync(Func<Task> operation)
    {
        if (!CanUseRedis())
        {
            return;
        }

        try
        {
            await operation();
        }
        catch (RedisException exception)
        {
            LogRedisFallback(exception);
        }
    }

    private void LogRedisFallback(RedisException exception)
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var previous = Interlocked.Read(ref _lastRedisWarningTicks);
        if (previous != 0 && now - previous < TimeSpan.FromSeconds(30).Ticks)
        {
            return;
        }

        Interlocked.Exchange(ref _lastRedisWarningTicks, now);
        _logger.LogWarning(exception, "Redis is unavailable; SocialGraph association access is using PostgreSQL only.");
    }

    private async Task<IDbContextTransaction?> BeginOwnedTransactionAsync(CancellationToken cancellationToken)
    {
        if (!_dbContext.Database.IsRelational() || _dbContext.Database.CurrentTransaction is not null)
        {
            return null;
        }

        return await _dbContext.Database.BeginTransactionAsync(cancellationToken);
    }

    private bool CanUseRedis() => _cacheEnabled && _redisConnection.IsConnected;

    private Task<long> CountFromPostgresAsync(long id1, short atype, CancellationToken cancellationToken) =>
        _dbContext.AssociationsTb
            .AsNoTracking()
            .LongCountAsync(item => item.id1 == id1 && item.atype == atype, cancellationToken);

    private async Task<AssociationPageResult> RetrieveFromPostgresAsync(
        long id1,
        short atype,
        long skip,
        int take,
        CancellationToken cancellationToken)
    {
        var rows = await _dbContext.AssociationsTb
            .AsNoTracking()
            .Where(item => item.id1 == id1 && item.atype == atype)
            .OrderByDescending(item => item.time)
            .ThenByDescending(item => item.id2)
            .Skip((int)Math.Min(skip, int.MaxValue))
            .Take(take + 1)
            .ToListAsync(cancellationToken);
        var hasNext = rows.Count > take;
        var items = rows.Take(take)
            .Select(item => new AssociationEdgeResult(item.id2, item.time))
            .ToArray();
        return new AssociationPageResult(
            items,
            hasNext ? (skip + items.Length).ToString(CultureInfo.InvariantCulture) : null);
    }
}
