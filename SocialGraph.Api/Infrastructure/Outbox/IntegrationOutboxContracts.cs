namespace SocialGraph.Api.Infrastructure.Outbox;

using SocialGraph.Api.Database;

public static class IntegrationEventType
{
    public const string NotificationCreate = "notification.create.v1";
    public const string UserCreate = "user.create.v1";
    public const string UserDelete = "user.delete.v1";
    public const string SearchUpsert = "search.upsert.v1";
    public const string SearchDelete = "search.delete.v1";
    public const string RecommendationUserUpsert = "recommendation.user-upsert.v1";
    public const string RecommendationUserDelete = "recommendation.user-delete.v1";
    public const string RecommendationContentUpsert = "recommendation.content-upsert.v1";
    public const string RecommendationContentDelete = "recommendation.content-delete.v1";
    public const string RecommendationInteraction = "recommendation.interaction.v1";
    public const string MessagingUserCreate = "messaging.user-create.v1";
    public const string MessagingUserDelete = "messaging.user-delete.v1";
    public const string MediaFinalize = "media.finalize.v1";
    public const string MediaDelete = "media.delete.v1";
}

public static class IntegrationOutboxStatus
{
    public const short Pending = 0;
    public const short Processing = 1;
    public const short Completed = 2;
    public const short DeadLetter = 3;
}

public sealed class IntegrationOutboxOptions
{
    public const string SectionName = "IntegrationOutbox";

    public int PollMilliseconds { get; set; } = 500;
    public int MaxIdlePollMilliseconds { get; set; } = 2_000;
    public int BatchSize { get; set; } = 20;
    public int MaxAttempts { get; set; } = 10;
    public int BaseDelaySeconds { get; set; } = 2;
    public int MaxDelayMinutes { get; set; } = 15;
    public int LockTimeoutMinutes { get; set; } = 5;
    public int CompletedRetentionDays { get; set; } = 7;
}

public sealed record NotificationCreateEvent(
    long CreatorId,
    long ReceiverId,
    short ActionType,
    long? ObjectId,
    object? Data);

public sealed record UserCreateEvent(
    long UserId,
    string Email,
    string Password,
    string Name,
    string Birthdate,
    bool Gender);

public sealed record UserDeleteEvent(long UserId);

public sealed record SearchUpsertEvent(long ObjectId, string ObjectType, string Text);

public sealed record SearchDeleteEvent(long ObjectId);

public sealed record UserEmbeddingEvent(long UserId);

public sealed record ContentEmbeddingEvent(long ContentId, string Content, IReadOnlyList<string> MediaUrls);

public sealed record ContentProjectionDeleteEvent(long ContentId);

public sealed record RecommendationInteractionEvent(long UserId, long TargetId, string Action);

public sealed record MessagingUserEvent(long UserId);

public sealed record MediaLifecycleEvent(IReadOnlyList<string> Urls);

public interface IIntegrationOutboxStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken = default);
    Task<IntegrationOutboxMessage> EnqueueAsync(
        string eventType,
        long? aggregateId,
        string idempotencyKey,
        string payload,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IntegrationOutboxMessage>> ClaimAsync(
        string workerId,
        int batchSize,
        TimeSpan lockTimeout,
        CancellationToken cancellationToken = default);
    Task MarkCompletedAsync(Guid id, CancellationToken cancellationToken = default);
    Task MarkFailedAsync(Guid id, string error, TimeSpan delay, bool deadLetter, CancellationToken cancellationToken = default);
    Task ReleaseAsync(Guid id, CancellationToken cancellationToken = default);
    Task DeleteCompletedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IntegrationOutboxMessage>> ListDeadLettersAsync(
        int limit,
        CancellationToken cancellationToken = default);
    Task<bool> RequeueDeadLetterAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IIntegrationOutboxDispatcher
{
    Task DispatchAsync(IntegrationOutboxMessage message, CancellationToken cancellationToken = default);
}

public interface IIntegrationOutboxMessageProcessor
{
    Task ProcessAsync(
        IIntegrationOutboxStore store,
        IIntegrationOutboxDispatcher dispatcher,
        IntegrationOutboxMessage message,
        CancellationToken cancellationToken = default);
}

public interface IExternalServiceTransport
{
    Task DispatchAsync(IntegrationOutboxMessage message, CancellationToken cancellationToken = default);
}

public sealed class PermanentOutboxException : Exception
{
    public PermanentOutboxException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
