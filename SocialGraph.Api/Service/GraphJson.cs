namespace SocialGraph.Api.Service;

using System.Text.Json;
using System.Text.Json.Nodes;

internal static class GraphJson
{
    public static JsonObject ParseObject(string json)
    {
        return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
    }

    public static string String(JsonObject data, string name, string fallback = "")
    {
        return data.TryGetPropertyValue(name, out var value) && value is not null
            ? value.GetValue<string?>() ?? fallback
            : fallback;
    }

    public static int Int(JsonObject data, string name, int fallback = 0)
    {
        if (!data.TryGetPropertyValue(name, out var value) || value is null)
        {
            return fallback;
        }

        try
        {
            return value.GetValue<int>();
        }
        catch (InvalidOperationException)
        {
            return int.TryParse(value.ToJsonString().Trim('"'), out var parsed) ? parsed : fallback;
        }
    }

    public static string UtcNowString()
    {
        return DateTimeOffset.UtcNow.ToString("O");
    }

    public static string UserJson(string name, bool gender, string birthdate, string location, string? avatar)
    {
        return new JsonObject
        {
            ["avatar"] = avatar ?? "",
            ["name"] = name,
            ["bio"] = $"Xin chao, minh la {name} den tu {location}",
            ["gender"] = gender ? 1 : 0,
            ["birthdate"] = birthdate,
            ["location"] = location,
            ["privacy"] = 0,
            ["create"] = UtcNowString()
        }.ToJsonString();
    }

    public static string GroupJson(string name, string? bio, int privacy, string? avatar)
    {
        return new JsonObject
        {
            ["avatar"] = avatar ?? "",
            ["name"] = name,
            ["bio"] = bio ?? "",
            ["privacy"] = privacy,
            ["create"] = UtcNowString()
        }.ToJsonString();
    }

    public static string PostJson(string content, int privacy)
    {
        return new JsonObject
        {
            ["content"] = content,
            ["privacy"] = privacy,
            ["create"] = UtcNowString()
        }.ToJsonString();
    }

    public static string ContentJson(string content)
    {
        return new JsonObject
        {
            ["content"] = content,
            ["create"] = UtcNowString()
        }.ToJsonString();
    }

    public static string StoryJson(string content, DateTime expire)
    {
        return new JsonObject
        {
            ["content"] = content,
            ["create"] = UtcNowString(),
            ["expire"] = expire.ToUniversalTime().ToString("O")
        }.ToJsonString();
    }

    public static string MediaJson(int type, string url)
    {
        return new JsonObject
        {
            ["type"] = type,
            ["url"] = url
        }.ToJsonString();
    }

    public static string PatchJson(params (string Name, object? Value)[] values)
    {
        var json = new JsonObject();
        foreach (var (name, value) in values)
        {
            if (value is null)
            {
                continue;
            }

            json[name] = JsonValue.Create(value);
        }

        return json.ToJsonString();
    }

    public static IReadOnlyDictionary<string, string> Metadata(JsonElement element)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = property.Value.ToString();
        }

        return result;
    }
}
