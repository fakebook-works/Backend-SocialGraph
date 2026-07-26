namespace SocialGraph.Api.Service;

public interface IExternalServiceClient
{
    Task NotifyAsync(long creatorId, long receiverId, short actionType, long? objectId, object? data, CancellationToken cancellationToken = default);
    Task CreateUserAsync(
        long userId,
        string email,
        string password,
        string name,
        string birthdate,
        bool gender,
        CancellationToken cancellationToken = default);
    Task DeleteUserAsync(long userId, CancellationToken cancellationToken = default);
    Task CreateSearchIndexAsync(long objectId, string objectType, string text, CancellationToken cancellationToken = default);
    Task UpdateSearchIndexAsync(long objectId, string objectType, string text, CancellationToken cancellationToken = default);
    Task DeleteSearchIndexAsync(long objectId, CancellationToken cancellationToken = default);
    Task CreateUserEmbeddingAsync(long userId, CancellationToken cancellationToken = default);
    Task DeleteUserEmbeddingAsync(long userId, CancellationToken cancellationToken = default);
    Task CreatePostEmbeddingAsync(long postId, string content, IReadOnlyList<string> mediaUrls, CancellationToken cancellationToken = default);
    Task DeletePostEmbeddingAsync(long postId, CancellationToken cancellationToken = default);
    Task RecordRecommendationInteractionAsync(long userId, long targetId, string action, CancellationToken cancellationToken = default);
    Task CreateMessengerUserAsync(long userId, CancellationToken cancellationToken = default);
    Task DeleteMessengerUserAsync(long userId, CancellationToken cancellationToken = default);
    /// <param name="ownerUserId">
    /// When supplied, the Upload service only acts on assets owned by this user. Callers pass it
    /// wherever the acting user is necessarily the media owner (avatars, covers, authored content)
    /// so a stored URL can never be used to finalize or delete somebody else's asset.
    /// </param>
    Task FinalizeMediaAsync(IReadOnlyList<string> urls, long? ownerUserId, CancellationToken cancellationToken = default);
    Task DeleteMediaAsync(IReadOnlyList<string> urls, long? ownerUserId, CancellationToken cancellationToken = default);
}
