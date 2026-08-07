namespace SocialGraph.Api.Service;

using System.Globalization;
using SocialGraph.Api.Contracts;

/// <summary>
/// Identifies one durable SocialGraph parent of an Upload asset. The URL locates the asset;
/// <see cref="ReferenceId"/> identifies the exact graph object or profile slot that keeps it alive.
/// </summary>
public sealed record MediaLifecycleReference(string Url, string ReferenceId);

/// <summary>
/// Centralizes the wire-stable reference identifiers understood by Upload Server. These values
/// are server-derived and never accepted from GraphQL input.
/// </summary>
public static class MediaLifecycleReferences
{
    private const string Prefix = "socialgraph";

    public static MediaLifecycleReference ForMedia(long mediaId, string url) =>
        Create(url, $"{Prefix}:media:{PositiveId(mediaId, nameof(mediaId))}");

    public static MediaLifecycleReference ForUserAvatar(long userId, string url) =>
        Create(url, $"{Prefix}:user:{PositiveId(userId, nameof(userId))}:avatar");

    public static MediaLifecycleReference ForUserBackground(long userId, string url) =>
        Create(url, $"{Prefix}:user:{PositiveId(userId, nameof(userId))}:background");

    public static MediaLifecycleReference ForGroupAvatar(long groupId, string url) =>
        Create(url, $"{Prefix}:group:{PositiveId(groupId, nameof(groupId))}:avatar");

    public static MediaLifecycleReference ForGroupBackground(long groupId, string url) =>
        Create(url, $"{Prefix}:group:{PositiveId(groupId, nameof(groupId))}:background");

    public static IReadOnlyList<MediaLifecycleReference> ForMedia(
        IEnumerable<MediaResult> media) =>
        media
            .Select(item => ForMedia(item.Id, item.Url))
            .ToArray();

    private static MediaLifecycleReference Create(string url, string referenceId)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("A media lifecycle reference requires a URL.", nameof(url));
        }

        return new MediaLifecycleReference(url, referenceId);
    }

    private static string PositiveId(long id, string parameterName)
    {
        if (id <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "A media lifecycle parent ID must be positive.");
        }

        return id.ToString(CultureInfo.InvariantCulture);
    }
}
