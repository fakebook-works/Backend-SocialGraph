namespace SocialGraph.Api.Service;

using System.Text.Json.Nodes;

internal static class GraphJson
{
    public const int CommentEditHistoryLimit = 20;

    internal sealed record CommentEditSnapshot(string Content, string EditedAt);

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

    public static string? NullableString(JsonObject data, string name)
    {
        return data.TryGetPropertyValue(name, out var value) && value is not null
            ? value.GetValue<string?>()
            : null;
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

    public static double? NullableDouble(JsonObject data, string name)
    {
        if (!data.TryGetPropertyValue(name, out var value) || value is null)
        {
            return null;
        }

        try
        {
            var parsed = value.GetValue<double>();
            return double.IsFinite(parsed) ? parsed : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            return double.TryParse(
                value.ToJsonString().Trim('"'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) && double.IsFinite(parsed)
                ? parsed
                : null;
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
            ["avatarSource"] = null,
            ["background"] = "",
            ["name"] = name,
            ["bio"] = $"Xin chao, minh la {name} den tu {location}",
            ["gender"] = gender ? 1 : 0,
            ["birthdate"] = birthdate,
            ["location"] = location,
            ["verify"] = null,
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

    public static bool IsCommentDeleted(JsonObject data)
    {
        // Presence is intentionally fail-closed. A malformed/null tombstone must never make old
        // content visible again after a partial deployment or a failed cleanup retry.
        return data.ContainsKey("deletedAt");
    }

    public static IReadOnlyList<CommentEditSnapshot> CommentEditHistory(JsonObject data)
    {
        if (data["editHistory"] is not JsonArray revisions)
        {
            return Array.Empty<CommentEditSnapshot>();
        }

        var result = new List<CommentEditSnapshot>(Math.Min(revisions.Count, CommentEditHistoryLimit));
        foreach (var revision in revisions.TakeLast(CommentEditHistoryLimit))
        {
            if (revision is not JsonObject item)
            {
                continue;
            }

            try
            {
                var content = String(item, "content");
                var editedAt = NullableString(item, "editedAt");
                if (!string.IsNullOrWhiteSpace(editedAt))
                {
                    result.Add(new CommentEditSnapshot(content, editedAt));
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or FormatException)
            {
                // Ignore a malformed legacy revision without failing the whole comment page.
            }
        }

        return result;
    }

    public static bool ApplyCommentEdit(JsonObject data, string content, string editedAt)
    {
        if (IsCommentDeleted(data))
        {
            return false;
        }

        var currentContent = String(data, "content");
        if (string.Equals(currentContent, content, StringComparison.Ordinal))
        {
            return true;
        }

        // Match Messenger's revision semantics: a historical timestamp is when that
        // version became current, not when it was replaced by the next edit.
        var previousVersionAt = NullableString(data, "editedAt");
        if (string.IsNullOrWhiteSpace(previousVersionAt))
        {
            previousVersionAt = NullableString(data, "create");
        }
        if (string.IsNullOrWhiteSpace(previousVersionAt))
        {
            // Legacy comments should remain editable even if their create timestamp is
            // malformed; use the current server edit time as a conservative fallback.
            previousVersionAt = editedAt;
        }

        var history = CommentEditHistory(data)
            .Append(new CommentEditSnapshot(currentContent, previousVersionAt))
            .TakeLast(CommentEditHistoryLimit)
            .Select(item => (JsonNode)new JsonObject
            {
                ["content"] = item.Content,
                ["editedAt"] = item.EditedAt
            })
            .ToArray();

        data["content"] = content;
        data["editedAt"] = editedAt;
        data["editHistory"] = new JsonArray(history);
        return true;
    }

    public static void ApplyCommentTombstone(JsonObject data, string deletedAt)
    {
        data["content"] = string.Empty;
        data.Remove("editedAt");
        data.Remove("editHistory");
        data["deletedAt"] = deletedAt;
    }

    public static string ReelJson(
        string content,
        int privacy,
        double? aspectRatio,
        double? focalPointX,
        double? focalPointY)
    {
        var data = new JsonObject
        {
            ["content"] = content,
            ["privacy"] = privacy,
            ["create"] = UtcNowString()
        };
        if (aspectRatio is { } value)
        {
            data["aspectRatio"] = value;
        }
        if (focalPointX is { } x)
        {
            data["focalPointX"] = x;
        }
        if (focalPointY is { } y)
        {
            data["focalPointY"] = y;
        }

        return data.ToJsonString();
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

    public static string PatchJsonIncludingNulls(params (string Name, object? Value)[] values)
    {
        var json = new JsonObject();
        foreach (var (name, value) in values)
        {
            json[name] = value is null ? null : JsonValue.Create(value);
        }

        return json.ToJsonString();
    }

}
