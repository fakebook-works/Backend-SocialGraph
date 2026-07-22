namespace SocialGraph.Api.Service;

using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ObjectTypeRules
{
    private static readonly IReadOnlyDictionary<short, ISet<string>> MutableFields = new Dictionary<short, ISet<string>>
    {
        [GraphObjectType.User] = new HashSet<string>(StringComparer.Ordinal) { "avatar", "background", "name", "bio", "gender", "birthdate", "location", "privacy" },
        [GraphObjectType.Group] = new HashSet<string>(StringComparer.Ordinal) { "avatar", "background", "name", "bio", "privacy" },
        [GraphObjectType.FeedPost] = new HashSet<string>(StringComparer.Ordinal) { "content", "privacy" },
        [GraphObjectType.GroupPost] = new HashSet<string>(StringComparer.Ordinal) { "content" },
        [GraphObjectType.Reel] = new HashSet<string>(StringComparer.Ordinal) { "content", "privacy" },
        [GraphObjectType.Story] = new HashSet<string>(StringComparer.Ordinal),
        [GraphObjectType.Comment] = new HashSet<string>(StringComparer.Ordinal),
        [GraphObjectType.Media] = new HashSet<string>(StringComparer.Ordinal)
    };

    public static bool IsKnownObjectType(short otype)
    {
        return MutableFields.ContainsKey(otype);
    }

    public static string NormalizeObjectJson(short otype, string dataJson)
    {
        if (!IsKnownObjectType(otype))
        {
            throw new ArgumentOutOfRangeException(nameof(otype), $"Unsupported object type: {otype}");
        }

        return ParseJsonObject(dataJson, nameof(dataJson)).ToJsonString();
    }

    public static JsonObject FilterPatch(short otype, string patchJson)
    {
        if (!MutableFields.TryGetValue(otype, out var mutableFields))
        {
            throw new ArgumentOutOfRangeException(nameof(otype), $"Unsupported object type: {otype}");
        }

        var patch = ParseJsonObject(patchJson, nameof(patchJson));
        var filtered = new JsonObject();

        foreach (var item in patch)
        {
            if (mutableFields.Contains(item.Key))
            {
                filtered[item.Key] = item.Value?.DeepClone();
            }
        }

        return filtered;
    }

    private static JsonObject ParseJsonObject(string json, string argumentName)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject
                ?? throw new ArgumentException("JSON value must be an object.", argumentName);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("JSON value is invalid.", argumentName, exception);
        }
    }
}
