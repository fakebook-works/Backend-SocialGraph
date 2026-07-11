namespace SocialGraph.Api.Service;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SocialGraph.Api.Database;
using SocialGraph.Api.Utils;
using StackExchange.Redis;

public sealed class ObjectService : IObjectService
{
    private readonly MyDbContext _dbContext;
    private readonly IDatabase _redis;

    public ObjectService(MyDbContext dbContext, IConnectionMultiplexer redis)
    {
        _dbContext = dbContext;
        _redis = redis.GetDatabase();
    }

    public async Task<SocialGraphObjectResult> AddObjectAsync(
        short otype,
        string dataJson,
        CancellationToken cancellationToken = default)
    {
        var normalizedData = ObjectTypeRules.NormalizeObjectJson(otype, dataJson);
        var id = IdGeneratorUtil.GenerateId();

        await _dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO social_graph.objects (id, otype, data) VALUES (@id, @otype, @data)",
            new object[]
            {
                new NpgsqlParameter("id", id),
                new NpgsqlParameter("otype", otype),
                new NpgsqlParameter("data", normalizedData) { NpgsqlDbType = NpgsqlDbType.Jsonb }
            },
            cancellationToken);

        var result = new SocialGraphObjectResult(id, otype, normalizedData);
        await CacheObjectAsync(result);
        return result;
    }

    public async Task<SocialGraphObjectResult?> UpdateObjectAsync(
        long id,
        short otype,
        string patchJson,
        CancellationToken cancellationToken = default)
    {
        var patch = ObjectTypeRules.FilterPatch(otype, patchJson);
        if (patch.Count == 0)
        {
            var current = await RetrieveObjectAsync(id, cancellationToken);
            return current?.otype == otype ? current : null;
        }

        return await UpdateObjectDataAsync(id, otype, patch, cancellationToken);
    }

    public async Task<SocialGraphObjectResult?> UpdateSystemObjectAsync(
        long id,
        short otype,
        string patchJson,
        CancellationToken cancellationToken = default)
    {
        if (!ObjectTypeRules.IsKnownObjectType(otype))
        {
            throw new ArgumentOutOfRangeException(nameof(otype), $"Unsupported object type: {otype}");
        }

        var patch = JsonNode.Parse(patchJson) as JsonObject
            ?? throw new ArgumentException("JSON value must be an object.", nameof(patchJson));

        return patch.Count == 0
            ? await RetrieveObjectAsync(id, cancellationToken)
            : await UpdateObjectDataAsync(id, otype, patch, cancellationToken);
    }

    private async Task<SocialGraphObjectResult?> UpdateObjectDataAsync(
        long id,
        short otype,
        JsonObject patch,
        CancellationToken cancellationToken)
    {
        var patchData = patch.ToJsonString();
        var rows = await _dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE social_graph.objects SET data = data || @patch WHERE id = @id AND otype = @otype",
            new object[]
            {
                new NpgsqlParameter("patch", patchData) { NpgsqlDbType = NpgsqlDbType.Jsonb },
                new NpgsqlParameter("id", id),
                new NpgsqlParameter("otype", otype)
            },
            cancellationToken);

        if (rows == 0)
        {
            return null;
        }

        await PatchCachedObjectAsync(id, patch);
        return await RetrieveObjectAsync(id, cancellationToken);
    }

    public async Task<bool> DeleteObjectAsync(long id, CancellationToken cancellationToken = default)
    {
        var rows = await _dbContext.Database.ExecuteSqlRawAsync(
            "DELETE FROM social_graph.objects WHERE id = @id",
            new object[] { new NpgsqlParameter("id", id) },
            cancellationToken);

        if (rows > 0)
        {
            await _redis.KeyDeleteAsync(ObjectKey(id));
        }

        return rows > 0;
    }

    public async Task<SocialGraphObjectResult?> RetrieveObjectAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var cached = await TryGetCachedObjectAsync(id);
        if (cached is not null)
        {
            return cached;
        }

        var entity = await _dbContext.ObjectsTb
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.id == id, cancellationToken);

        if (entity is null)
        {
            return null;
        }

        var result = new SocialGraphObjectResult(entity.id, entity.otype, entity.data);
        await CacheObjectAsync(result);
        return result;
    }

    private async Task CacheObjectAsync(SocialGraphObjectResult item)
    {
        var payload = new JsonObject
        {
            ["otype"] = item.otype,
            ["data"] = JsonNode.Parse(item.data)
        }.ToJsonString();

        await _redis.ExecuteAsync("JSON.SET", ObjectKey(item.id), "$", payload);
    }

    private async Task PatchCachedObjectAsync(long id, JsonObject patch)
    {
        var key = ObjectKey(id);
        if (!await _redis.KeyExistsAsync(key))
        {
            return;
        }

        foreach (var item in patch)
        {
            await _redis.ExecuteAsync("JSON.SET", key, $"$.data.{item.Key}", ToJson(item.Value));
        }
    }

    private async Task<SocialGraphObjectResult?> TryGetCachedObjectAsync(long id)
    {
        var key = ObjectKey(id);
        var result = await _redis.ExecuteAsync("JSON.GET", key);
        if (result.IsNull)
        {
            return null;
        }

        var json = result.ToString();
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var otype = root.GetProperty("otype").GetInt16();
            var data = root.GetProperty("data").GetRawText();
            return new SocialGraphObjectResult(id, otype, data);
        }
        catch (JsonException)
        {
            await _redis.KeyDeleteAsync(key);
            return null;
        }
    }

    private static string ObjectKey(long id)
    {
        return id.ToString(CultureInfo.InvariantCulture);
    }

    private static string ToJson(JsonNode? node)
    {
        return node?.ToJsonString() ?? "null";
    }
}
