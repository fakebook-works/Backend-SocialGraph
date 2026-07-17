namespace SocialGraph.Api.Infrastructure.Outbox;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SocialGraph.Api.Service;

public sealed class IntegrationOutboxPublisher : IExternalServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IIntegrationOutboxStore _outbox;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IOutboxPayloadProtector _payloadProtector;

    public IntegrationOutboxPublisher(
        IIntegrationOutboxStore outbox,
        IHttpContextAccessor httpContextAccessor,
        IOutboxPayloadProtector payloadProtector)
    {
        _outbox = outbox;
        _httpContextAccessor = httpContextAccessor;
        _payloadProtector = payloadProtector;
    }

    public Task NotifyAsync(long creatorId, long receiverId, short actionType, long? objectId, object? data, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(
            IntegrationEventType.NotificationCreate,
            objectId ?? receiverId,
            new NotificationCreateEvent(creatorId, receiverId, actionType, objectId, data),
            cancellationToken);
    }

    public Task CreateUserAsync(
        long userId,
        string email,
        string password,
        string name,
        string birthdate,
        bool gender,
        CancellationToken cancellationToken = default)
    {
        return EnqueueUserCreateAsync(
            userId,
            email,
            password,
            name,
            birthdate,
            gender,
            cancellationToken);
    }

    private async Task EnqueueUserCreateAsync(
        long userId,
        string email,
        string password,
        string name,
        string birthdate,
        bool gender,
        CancellationToken cancellationToken)
    {
        var operationId = GetOperationId();
        await EnqueueAsync(
            IntegrationEventType.UserCreate,
            userId,
            new UserCreateEvent(userId, email, password, name, birthdate, gender),
            cancellationToken,
            protectPayload: true,
            operationId: operationId);
        await EnqueueAsync(
            IntegrationEventType.SearchUpsert,
            userId,
            new SearchUpsertEvent(userId, "user", name),
            cancellationToken,
            operationId: operationId);
        await EnqueueAsync(
            IntegrationEventType.RecommendationUserUpsert,
            userId,
            new UserEmbeddingEvent(userId),
            cancellationToken,
            operationId: operationId);
        await EnqueueAsync(
            IntegrationEventType.MessagingUserCreate,
            userId,
            new MessagingUserEvent(userId),
            cancellationToken,
            operationId: operationId);
    }

    public async Task DeleteUserAsync(long userId, CancellationToken cancellationToken = default)
    {
        var operationId = GetOperationId();
        await EnqueueAsync(
            IntegrationEventType.UserDelete,
            userId,
            new UserDeleteEvent(userId),
            cancellationToken,
            operationId: operationId);
        await EnqueueAsync(
            IntegrationEventType.SearchDelete,
            userId,
            new SearchDeleteEvent(userId),
            cancellationToken,
            operationId: operationId);
        await EnqueueAsync(
            IntegrationEventType.RecommendationUserDelete,
            userId,
            new UserEmbeddingEvent(userId),
            cancellationToken,
            operationId: operationId);
        await EnqueueAsync(
            IntegrationEventType.MessagingUserDelete,
            userId,
            new MessagingUserEvent(userId),
            cancellationToken,
            operationId: operationId);
    }

    public Task CreateSearchIndexAsync(long objectId, string objectType, string text, CancellationToken cancellationToken = default) =>
        UpsertSearchAsync(objectId, objectType, text, cancellationToken);

    public Task UpdateSearchIndexAsync(long objectId, string objectType, string text, CancellationToken cancellationToken = default) =>
        UpsertSearchAsync(objectId, objectType, text, cancellationToken);

    public Task DeleteSearchIndexAsync(long objectId, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(
            IntegrationEventType.SearchDelete,
            objectId,
            new SearchDeleteEvent(objectId),
            cancellationToken);
    }

    public Task CreateUserEmbeddingAsync(long userId, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(
            IntegrationEventType.RecommendationUserUpsert,
            userId,
            new UserEmbeddingEvent(userId),
            cancellationToken);
    }

    public Task DeleteUserEmbeddingAsync(long userId, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(
            IntegrationEventType.RecommendationUserDelete,
            userId,
            new UserEmbeddingEvent(userId),
            cancellationToken);
    }

    public Task CreatePostEmbeddingAsync(long postId, string content, IReadOnlyList<string> mediaUrls, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(
            IntegrationEventType.RecommendationContentUpsert,
            postId,
            new ContentEmbeddingEvent(postId, content, mediaUrls),
            cancellationToken);
    }

    public Task DeletePostEmbeddingAsync(long postId, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(
            IntegrationEventType.RecommendationContentDelete,
            postId,
            new ContentProjectionDeleteEvent(postId),
            cancellationToken);
    }

    public Task RecordRecommendationInteractionAsync(
        long userId,
        long targetId,
        string action,
        CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(
            IntegrationEventType.RecommendationInteraction,
            userId,
            new RecommendationInteractionEvent(userId, targetId, action),
            cancellationToken);
    }

    public Task CreateMessengerUserAsync(long userId, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(
            IntegrationEventType.MessagingUserCreate,
            userId,
            new MessagingUserEvent(userId),
            cancellationToken);
    }

    public Task DeleteMessengerUserAsync(long userId, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(
            IntegrationEventType.MessagingUserDelete,
            userId,
            new MessagingUserEvent(userId),
            cancellationToken);
    }

    public Task FinalizeMediaAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken = default)
    {
        return EnqueueMediaLifecycleAsync(IntegrationEventType.MediaFinalize, urls, cancellationToken);
    }

    public Task DeleteMediaAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken = default)
    {
        return EnqueueMediaLifecycleAsync(IntegrationEventType.MediaDelete, urls, cancellationToken);
    }

    private Task EnqueueMediaLifecycleAsync(
        string eventType,
        IReadOnlyList<string> urls,
        CancellationToken cancellationToken)
    {
        var normalized = urls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return normalized.Length == 0
            ? Task.CompletedTask
            : EnqueueAsync(eventType, null, new MediaLifecycleEvent(normalized), cancellationToken);
    }

    private Task UpsertSearchAsync(
        long objectId,
        string objectType,
        string text,
        CancellationToken cancellationToken)
    {
        return EnqueueAsync(
            IntegrationEventType.SearchUpsert,
            objectId,
            new SearchUpsertEvent(objectId, objectType, text),
            cancellationToken);
    }

    private async Task EnqueueAsync<T>(
        string eventType,
        long? aggregateId,
        T payload,
        CancellationToken cancellationToken,
        bool protectPayload = false,
        string? operationId = null)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var keySource = $"{eventType}:{aggregateId}:{operationId ?? GetOperationId()}:{json}";
        var idempotencyKey = "socialgraph-" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keySource))).ToLowerInvariant();
        await _outbox.EnqueueAsync(
            eventType,
            aggregateId,
            idempotencyKey,
            protectPayload ? _payloadProtector.Protect(json) : json,
            cancellationToken);
    }

    private string GetOperationId()
    {
        var context = _httpContextAccessor.HttpContext;
        var explicitKey = context?.Request.Headers["Idempotency-Key"].ToString();
        if (!string.IsNullOrWhiteSpace(explicitKey))
        {
            return explicitKey;
        }

        return string.IsNullOrWhiteSpace(context?.TraceIdentifier)
            ? Guid.NewGuid().ToString("N")
            : context.TraceIdentifier;
    }
}
