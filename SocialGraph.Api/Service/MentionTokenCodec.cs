namespace SocialGraph.Api.Service;

using System.Text.RegularExpressions;

public static partial class MentionTokenCodec
{
    [GeneratedRegex(@"\[\[mention:([1-9]\d*)\]\]", RegexOptions.CultureInvariant)]
    private static partial Regex MentionPattern();

    public static IReadOnlyList<long> ExtractUserIds(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return Array.Empty<long>();
        }

        var seen = new HashSet<long>();
        var ids = new List<long>();
        foreach (Match match in MentionPattern().Matches(content))
        {
            if (long.TryParse(match.Groups[1].Value, out var userId) && userId > 0 && seen.Add(userId))
            {
                ids.Add(userId);
            }
        }

        return ids;
    }
}
