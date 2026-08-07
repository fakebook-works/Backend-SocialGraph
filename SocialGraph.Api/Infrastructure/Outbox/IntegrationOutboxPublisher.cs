namespace SocialGraph.Api.Infrastructure.Outbox;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SocialGraph.Api.Service;

public sealed class IntegrationOutboxPublisher : IExternalServiceClient
{
    private const int MaxMediaLifecycleBatchSize = 512;
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
        if (string.IsNullOrWhiteSpace(content) && mediaUrls.Count == 0)
        {
            // Text-less shared/placeholder posts are valid graph content but have no
            // recommendation representation. A delete also clears a stale embedding on edit.
            return DeletePostEmbeddingAsync(postId, cancellationToken);
        }

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

    public Task FinalizeMediaAsync(IReadOnlyList<MediaLifecycleReference> references, long? ownerUserId, CancellationToken cancellationToken = default)
    {
        return EnqueueMediaLifecycleAsync(IntegrationEventType.MediaFinalize, references, ownerUserId, cancellationToken);
    }

    public Task<DateTimeOffset> GetMediaOperationTimeAsync(CancellationToken cancellationToken = default) =>
        _outbox.GetCurrentTimeAsync(cancellationToken);

    public Task FinalizeMediaAsync(
        IReadOnlyList<MediaLifecycleReference> references,
        long? ownerUserId,
        DateTimeOffset operationAt,
        CancellationToken cancellationToken = default)
    {
        return EnqueueMediaLifecycleAsync(
            IntegrationEventType.MediaFinalize,
            references,
            ownerUserId,
            operationAt,
            cancellationToken);
    }

    public Task DeleteMediaAsync(IReadOnlyList<MediaLifecycleReference> references, long? ownerUserId, CancellationToken cancellationToken = default)
    {
        return EnqueueMediaLifecycleAsync(IntegrationEventType.MediaDelete, references, ownerUserId, cancellationToken);
    }

    public Task DeleteMediaAsync(
        IReadOnlyList<MediaLifecycleReference> references,
        long? ownerUserId,
        DateTimeOffset operationAt,
        CancellationToken cancellationToken = default)
    {
        return EnqueueMediaLifecycleAsync(
            IntegrationEventType.MediaDelete,
            references,
            ownerUserId,
            operationAt,
            cancellationToken);
    }

    private async Task EnqueueMediaLifecycleAsync(
        string eventType,
        IReadOnlyList<MediaLifecycleReference> references,
        long? ownerUserId,
        CancellationToken cancellationToken)
    {
        var operationAt = await _outbox.GetCurrentTimeAsync(cancellationToken);
        await EnqueueMediaLifecycleAsync(
            eventType,
            references,
            ownerUserId,
            operationAt,
            cancellationToken);
    }

    private async Task EnqueueMediaLifecycleAsync(
        string eventType,
        IReadOnlyList<MediaLifecycleReference> references,
        long? ownerUserId,
        DateTimeOffset operationAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(references);
        if (references.Count > MaxMediaLifecycleBatchSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(references),
                $"A media lifecycle event supports at most {MaxMediaLifecycleBatchSize} references.");
        }
        if (references.Any(reference =>
                reference is null ||
                string.IsNullOrWhiteSpace(reference.Url) ||
                string.IsNullOrWhiteSpace(reference.ReferenceId)))
        {
            throw new ArgumentException("A media lifecycle batch contains an invalid parent reference.", nameof(references));
        }

        var normalized = references
            .DistinctBy(
                reference => $"{reference.ReferenceId}\n{reference.Url}",
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized
            .GroupBy(reference => reference.ReferenceId, StringComparer.Ordinal)
            .Any(group => group.Select(reference => reference.Url).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any()))
        {
            throw new InvalidOperationException("One media parent reference cannot attach to multiple URLs in the same lifecycle batch.");
        }
        if (normalized.Length == 0)
        {
            return;
        }

        var idempotencyMaterial = JsonSerializer.Serialize(
            new { references = normalized, ownerUserId, operationAt },
            JsonOptions);
        await EnqueueAsync(
            eventType,
            null,
            new MediaLifecycleEvent(
                OwnerUserId: ownerUserId,
                References: normalized,
                OperationAt: operationAt),
            cancellationToken,
            idempotencyMaterial: idempotencyMaterial);
    }

    private Task UpsertSearchAsync(
        long objectId,
        string objectType,
        string text,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            // Search rejects empty text. Treat it as "not indexable" and clear a previous
            // projection instead of creating a permanent dead letter.
            return DeleteSearchIndexAsync(objectId, cancellationToken);
        }

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
        string? operationId = null,
        string? idempotencyMaterial = null)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var keySource = $"{eventType}:{aggregateId}:{operationId ?? GetOperationId()}:{idempotencyMaterial ?? json}";
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
