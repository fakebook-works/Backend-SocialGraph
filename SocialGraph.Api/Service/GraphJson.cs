namespace SocialGraph.Api.Service;

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

    public static string UserJson(string name, bool gender, string birthdate, string location)
    {
        return new JsonObject
        {
            ["avatar"] = "",
            ["background"] = "",
            ["name"] = name,
            ["bio"] = $"Xin chao, minh la {name} den tu {location}",
            ["gender"] = gender ? 1 : 0,
            ["birthdate"] = birthdate,
            ["location"] = location,
            ["verify"] = "",
            ["privacy"] = 0,
            ["create"] = UtcNowString()
        }.ToJsonString();
    }

    public static string GroupJson(string name, string? bio, int privacy, string? avatar, string? background)
    {
        return new JsonObject
        {
            ["avatar"] = avatar ?? "",
            ["background"] = background ?? "",
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

    public static string GroupPostJson(string content)
    {
        return new JsonObject
        {
            ["content"] = content,
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

    public static string StoryJson(string content)
    {
        var createdAt = DateTimeOffset.UtcNow;
        return new JsonObject
        {
            ["content"] = content,
            ["create"] = createdAt.ToString("O"),
            ["expire"] = createdAt.AddDays(1).ToString("O")
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

}
