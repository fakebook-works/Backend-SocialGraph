namespace SocialGraph.Api.Service;

public interface IExternalServiceClient
{
    Task NotifyAsync(long creatorId, long receiverId, short actionType, long? objectId, object? data, CancellationToken cancellationToken = default);
    Task CreateUserAsync(long userId, string email, string password, string name, string birthdate, CancellationToken cancellationToken = default);
    Task DeleteUserAsync(long userId, CancellationToken cancellationToken = default);
    Task CreateSearchIndexAsync(long objectId, string objectType, string text, CancellationToken cancellationToken = default);
    Task UpdateSearchIndexAsync(long objectId, string objectType, string text, CancellationToken cancellationToken = default);
    Task DeleteSearchIndexAsync(long objectId, CancellationToken cancellationToken = default);
    Task CreateUserEmbeddingAsync(long userId, CancellationToken cancellationToken = default);
    Task DeleteUserEmbeddingAsync(long userId, CancellationToken cancellationToken = default);
    Task CreatePostEmbeddingAsync(long postId, string content, IReadOnlyList<string> mediaUrls, CancellationToken cancellationToken = default);
    Task DeletePostEmbeddingAsync(long postId, CancellationToken cancellationToken = default);
    Task CreateMessengerUserAsync(long userId, CancellationToken cancellationToken = default);
    Task DeleteMessengerUserAsync(long userId, CancellationToken cancellationToken = default);
}
