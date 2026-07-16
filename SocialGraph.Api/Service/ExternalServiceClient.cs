namespace SocialGraph.Api.Service;

using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SocialGraph.Api.Database;
using SocialGraph.Api.Infrastructure.Outbox;

public sealed class ExternalServiceClient : IExternalServiceTransport
{
    private const string CorrelationHeader = "X-Correlation-ID";
    private const string AuthenticationSecretHeader = "X-Internal-AuthenticationService-Secret";
    private const string SearchSecretHeader = "X-Internal-SearchService-Secret";
    private const string RecommendationSecretHeader = "X-Internal-RecommendationService-Secret";
    private const string NotificationSecretHeader = "X-Internal-NotificationService-Secret";
    private const string MessagingSecretHeader = "X-Internal-MessengerService-Secret";
    private const string IdempotencyHeader = "Idempotency-Key";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ExternalServiceClient> _logger;
    private readonly IOutboxPayloadProtector _payloadProtector;

    public ExternalServiceClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ILogger<ExternalServiceClient> logger,
        IOutboxPayloadProtector payloadProtector)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _payloadProtector = payloadProtector;
    }

    public async Task DispatchAsync(
        IntegrationOutboxMessage message,
        CancellationToken cancellationToken = default)
    {
        switch (message.event_type)
        {
            case IntegrationEventType.NotificationCreate:
                {
                    var payload = Deserialize<NotificationCreateEvent>(message.payload);
                    await SendOutboxRequiredAsync(
                        "NotificationServiceCreateNotification",
                        HttpMethod.Post,
                        GetInternalServiceUrl("Notification", "internal/notifications"),
                        new
                        {
                            creatorId = payload.CreatorId,
                            receiverId = payload.ReceiverId,
                            actionType = payload.ActionType,
                            objectId = payload.ObjectId,
                            data = payload.Data
                        },
                        NotificationSecretHeader,
                        "InternalServices:Notification:SharedSecret",
                        message.idempotency_key,
                        cancellationToken);
                    break;
                }
            case IntegrationEventType.UserCreate:
                {
                    var payload = Deserialize<UserCreateEvent>(_payloadProtector.Unprotect(message.payload));
                    await SendOutboxRequiredAsync(
                        "AuthenticationServiceCreateUser",
                        HttpMethod.Post,
                        GetInternalServiceUrl("Authentication", "internal/users") ??
                        _configuration["ExternalServices:AuthenticationServiceCreateUser"],
                        new
                        {
                            userId = payload.UserId,
                            email = payload.Email,
                            password = payload.Password,
                            displayName = payload.Name,
                            dob = payload.Birthdate,
                            gender = payload.Gender
                        },
                        AuthenticationSecretHeader,
                        "InternalServices:Authentication:SharedSecret",
                        message.idempotency_key + "-auth",
                        cancellationToken);
                    break;
                }
            case IntegrationEventType.UserDelete:
                {
                    var payload = Deserialize<UserDeleteEvent>(message.payload);
                    await SendOutboxRequiredAsync(
                        "AuthenticationServiceDeleteUser",
                        HttpMethod.Delete,
                        GetInternalServiceUrl("Authentication", $"internal/users/{FormatId(payload.UserId)}") ??
                        _configuration["ExternalServices:AuthenticationServiceDeleteUser"],
                        null,
                        AuthenticationSecretHeader,
                        "InternalServices:Authentication:SharedSecret",
                        message.idempotency_key + "-auth",
                        cancellationToken,
                        notFoundIsSuccess: true);
                    break;
                }
            case IntegrationEventType.SearchUpsert:
                {
                    var payload = Deserialize<SearchUpsertEvent>(message.payload);
                    await DispatchSearchUpsertAsync(payload, message.idempotency_key, cancellationToken);
                    break;
                }
            case IntegrationEventType.SearchDelete:
                {
                    var payload = Deserialize<SearchDeleteEvent>(message.payload);
                    await DispatchSearchDeleteAsync(payload.ObjectId, message.idempotency_key, cancellationToken);
                    break;
                }
            case IntegrationEventType.RecommendationUserUpsert:
                {
                    var payload = Deserialize<UserEmbeddingEvent>(message.payload);
                    await DispatchUserEmbeddingAsync(payload.UserId, true, message.idempotency_key, cancellationToken);
                    break;
                }
            case IntegrationEventType.RecommendationUserDelete:
                {
                    var payload = Deserialize<UserEmbeddingEvent>(message.payload);
                    await DispatchUserEmbeddingAsync(payload.UserId, false, message.idempotency_key, cancellationToken);
                    break;
                }
            case IntegrationEventType.RecommendationContentUpsert:
                {
                    var payload = Deserialize<ContentEmbeddingEvent>(message.payload);
                    await SendOutboxRequiredAsync(
                        "RecommendationServiceUpsertPostEmbedding",
                        HttpMethod.Put,
                        GetInternalServiceUrl("Recommendation", $"internal/recommendation/posts/{FormatId(payload.ContentId)}/embedding"),
                        new { content = payload.Content, mediaUrls = payload.MediaUrls },
                        RecommendationSecretHeader,
                        "InternalServices:Recommendation:SharedSecret",
                        message.idempotency_key,
                        cancellationToken);
                    break;
                }
            case IntegrationEventType.RecommendationContentDelete:
                {
                    var payload = Deserialize<ContentProjectionDeleteEvent>(message.payload);
                    await SendOutboxRequiredAsync(
                        "RecommendationServiceDeletePostEmbedding",
                        HttpMethod.Delete,
                        GetInternalServiceUrl("Recommendation", $"internal/recommendation/posts/{FormatId(payload.ContentId)}/embedding"),
                        null,
                        RecommendationSecretHeader,
                        "InternalServices:Recommendation:SharedSecret",
                        message.idempotency_key,
                        cancellationToken,
                        notFoundIsSuccess: true);
                    break;
                }
            case IntegrationEventType.RecommendationInteraction:
                {
                    var payload = Deserialize<RecommendationInteractionEvent>(message.payload);
                    await SendOutboxRequiredAsync(
                        "RecommendationServiceRecordInteraction",
                        HttpMethod.Post,
                        GetInternalServiceUrl(
                            "Recommendation",
                            $"internal/recommendation/users/{FormatId(payload.UserId)}/interactions"),
                        new { targetId = payload.TargetId, action = payload.Action },
                        RecommendationSecretHeader,
                        "InternalServices:Recommendation:SharedSecret",
                        message.idempotency_key,
                        cancellationToken);
                    break;
                }
            case IntegrationEventType.MessagingUserCreate:
                {
                    var payload = Deserialize<MessagingUserEvent>(message.payload);
                    await DispatchMessagingUserAsync(payload.UserId, true, message.idempotency_key, cancellationToken);
                    break;
                }
            case IntegrationEventType.MessagingUserDelete:
                {
                    var payload = Deserialize<MessagingUserEvent>(message.payload);
                    await DispatchMessagingUserAsync(payload.UserId, false, message.idempotency_key, cancellationToken);
                    break;
                }
            default:
                throw new PermanentOutboxException($"Unsupported integration event type '{message.event_type}'.");
        }
    }

    public Task NotifyAsync(long creatorId, long receiverId, short actionType, long? objectId, object? data, CancellationToken cancellationToken = default)
    {
        var correlationId = GetCorrelationId();
        var idempotencyKey = BuildIdempotencyKey(
            actionType,
            creatorId,
            receiverId,
            objectId,
            correlationId);
        return SendBestEffortAsync(
            "NotificationServiceCreateNotification",
            HttpMethod.Post,
            GetInternalServiceUrl("Notification", "internal/notifications"),
            new { creatorId, receiverId, actionType, objectId, data },
            correlationId,
            cancellationToken,
            NotificationSecretHeader,
            "InternalServices:Notification:SharedSecret",
            idempotencyKey);
    }

    public async Task CreateUserAsync(
        long userId,
        string email,
        string password,
        string name,
        string birthdate,
        bool gender,
        CancellationToken cancellationToken = default)
    {
        var correlationId = GetCorrelationId();
        await PostRequiredAsync(
            "AuthenticationServiceCreateUser",
            new
            {
                userId,
                email,
                password,
                displayName = name,
                dob = birthdate,
                gender
            },
            correlationId,
            cancellationToken,
            GetInternalServiceUrl("Authentication", "internal/users"));

        // Authentication owns credentials and is required. The derived projections can be retried.
        await Task.WhenAll(
            UpsertSearchIndexAsync(userId, "user", name, correlationId, cancellationToken),
            EnsureUserEmbeddingAsync(userId, correlationId, cancellationToken),
            CreateMessengerUserAsync(userId, cancellationToken));
    }

    public async Task DeleteUserAsync(long userId, CancellationToken cancellationToken = default)
    {
        var correlationId = GetCorrelationId();
        await SendBestEffortAsync(
            "AuthenticationServiceDeleteUser",
            HttpMethod.Delete,
            GetInternalServiceUrl("Authentication", $"internal/users/{FormatId(userId)}") ??
            _configuration["ExternalServices:AuthenticationServiceDeleteUser"],
            null,
            correlationId,
            cancellationToken,
            AuthenticationSecretHeader,
            "InternalServices:Authentication:SharedSecret");

        await Task.WhenAll(
            DeleteSearchIndexAsync(userId, correlationId, cancellationToken),
            DeleteUserEmbeddingAsync(userId, correlationId, cancellationToken),
            DeleteMessengerUserAsync(userId, cancellationToken));
    }

    public Task CreateSearchIndexAsync(long objectId, string objectType, string text, CancellationToken cancellationToken = default)
    {
        return UpsertSearchIndexAsync(objectId, objectType, text, GetCorrelationId(), cancellationToken);
    }

    public Task UpdateSearchIndexAsync(long objectId, string objectType, string text, CancellationToken cancellationToken = default)
    {
        return UpsertSearchIndexAsync(objectId, objectType, text, GetCorrelationId(), cancellationToken);
    }

    public Task DeleteSearchIndexAsync(long objectId, CancellationToken cancellationToken = default)
    {
        return DeleteSearchIndexAsync(objectId, GetCorrelationId(), cancellationToken);
    }

    public Task CreateUserEmbeddingAsync(long userId, CancellationToken cancellationToken = default)
    {
        return EnsureUserEmbeddingAsync(userId, GetCorrelationId(), cancellationToken);
    }

    public Task DeleteUserEmbeddingAsync(long userId, CancellationToken cancellationToken = default)
    {
        return DeleteUserEmbeddingAsync(userId, GetCorrelationId(), cancellationToken);
    }

    public Task CreatePostEmbeddingAsync(long postId, string content, IReadOnlyList<string> mediaUrls, CancellationToken cancellationToken = default)
    {
        return SendBestEffortAsync(
            "RecommendationServiceUpsertPostEmbedding",
            HttpMethod.Put,
            GetInternalServiceUrl("Recommendation", $"internal/recommendation/posts/{FormatId(postId)}/embedding"),
            new { content, mediaUrls },
            GetCorrelationId(),
            cancellationToken,
            RecommendationSecretHeader,
            "InternalServices:Recommendation:SharedSecret");
    }

    public Task DeletePostEmbeddingAsync(long postId, CancellationToken cancellationToken = default)
    {
        return SendBestEffortAsync(
            "RecommendationServiceDeletePostEmbedding",
            HttpMethod.Delete,
            GetInternalServiceUrl("Recommendation", $"internal/recommendation/posts/{FormatId(postId)}/embedding"),
            null,
            GetCorrelationId(),
            cancellationToken,
            RecommendationSecretHeader,
            "InternalServices:Recommendation:SharedSecret");
    }

    public Task RecordRecommendationInteractionAsync(
        long userId,
        long targetId,
        string action,
        CancellationToken cancellationToken = default)
    {
        return SendBestEffortAsync(
            "RecommendationServiceRecordInteraction",
            HttpMethod.Post,
            GetInternalServiceUrl(
                "Recommendation",
                $"internal/recommendation/users/{FormatId(userId)}/interactions"),
            new { targetId, action },
            GetCorrelationId(),
            cancellationToken,
            RecommendationSecretHeader,
            "InternalServices:Recommendation:SharedSecret");
    }

    public Task CreateMessengerUserAsync(long userId, CancellationToken cancellationToken = default)
    {
        return SendBestEffortAsync(
            "MessengerServiceCreateUser",
            HttpMethod.Post,
            GetInternalServiceUrl("Messaging", "internal/users"),
            new { userId },
            GetCorrelationId(),
            cancellationToken,
            MessagingSecretHeader,
            "InternalServices:Messaging:SharedSecret");
    }

    public Task DeleteMessengerUserAsync(long userId, CancellationToken cancellationToken = default)
    {
        return SendBestEffortAsync(
            "MessengerServiceDeleteUser",
            HttpMethod.Delete,
            GetInternalServiceUrl("Messaging", $"internal/users/{FormatId(userId)}"),
            null,
            GetCorrelationId(),
            cancellationToken,
            MessagingSecretHeader,
            "InternalServices:Messaging:SharedSecret");
    }

    private Task UpsertSearchIndexAsync(long objectId, string objectType, string text, string correlationId, CancellationToken cancellationToken)
    {
        return SendBestEffortAsync(
            "SearchServiceUpsertIndex",
            HttpMethod.Put,
            GetInternalServiceUrl("Search", $"internal/search/indexes/{FormatId(objectId)}"),
            new { objectType, text },
            correlationId,
            cancellationToken,
            SearchSecretHeader,
            "InternalServices:Search:SharedSecret");
    }

    private Task DeleteSearchIndexAsync(long objectId, string correlationId, CancellationToken cancellationToken)
    {
        return SendBestEffortAsync(
            "SearchServiceDeleteIndex",
            HttpMethod.Delete,
            GetInternalServiceUrl("Search", $"internal/search/indexes/{FormatId(objectId)}"),
            null,
            correlationId,
            cancellationToken,
            SearchSecretHeader,
            "InternalServices:Search:SharedSecret");
    }

    private Task EnsureUserEmbeddingAsync(long userId, string correlationId, CancellationToken cancellationToken)
    {
        return SendBestEffortAsync(
            "RecommendationServiceEnsureUserEmbedding",
            HttpMethod.Put,
            GetInternalServiceUrl("Recommendation", $"internal/recommendation/users/{FormatId(userId)}/embedding"),
            null,
            correlationId,
            cancellationToken,
            RecommendationSecretHeader,
            "InternalServices:Recommendation:SharedSecret");
    }

    private Task DeleteUserEmbeddingAsync(long userId, string correlationId, CancellationToken cancellationToken)
    {
        return SendBestEffortAsync(
            "RecommendationServiceDeleteUserEmbedding",
            HttpMethod.Delete,
            GetInternalServiceUrl("Recommendation", $"internal/recommendation/users/{FormatId(userId)}/embedding"),
            null,
            correlationId,
            cancellationToken,
            RecommendationSecretHeader,
            "InternalServices:Recommendation:SharedSecret");
    }

    private async Task SendBestEffortAsync(
        string endpointName,
        HttpMethod method,
        string? url,
        object? payload,
        string correlationId,
        CancellationToken cancellationToken,
        string? secretHeader = null,
        string? secretConfigurationKey = null,
        string? idempotencyKey = null)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            _logger.LogDebug("External service endpoint {EndpointName} is not configured.", endpointName);
            return;
        }

        try
        {
            using var request = CreateRequest(
                method,
                url,
                payload,
                correlationId,
                secretHeader,
                secretConfigurationKey,
                idempotencyKey);
            using var response = await _httpClientFactory.CreateClient("external-services").SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("External service {EndpointName} returned {StatusCode}.", endpointName, response.StatusCode);
            }
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "External service {EndpointName} timed out.", endpointName);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or UriFormatException)
        {
            _logger.LogWarning(exception, "External service {EndpointName} call failed.", endpointName);
        }
    }

    private async Task PostRequiredAsync(
        string key,
        object payload,
        string correlationId,
        CancellationToken cancellationToken,
        string? configuredUrl = null)
    {
        var url = configuredUrl ?? _configuration[$"ExternalServices:{key}"];
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ExternalServiceCallException(key, "Required external service endpoint is not configured.");
        }

        try
        {
            using var request = CreateRequest(
                HttpMethod.Post,
                url,
                payload,
                correlationId,
                AuthenticationSecretHeader,
                "InternalServices:Authentication:SharedSecret");
            using var response = await _httpClientFactory.CreateClient("external-services").SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "Required external service {EndpointKey} returned {StatusCode}: {ResponseBody}",
                key,
                response.StatusCode,
                Truncate(body));

            throw new ExternalServiceCallException(
                key,
                $"Required external service returned HTTP {(int)response.StatusCode}.");
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Required external service {EndpointKey} timed out.", key);
            throw new ExternalServiceCallException(key, "Required external service call timed out.", exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or UriFormatException)
        {
            _logger.LogWarning(exception, "Required external service {EndpointKey} call failed.", key);
            throw new ExternalServiceCallException(key, "Required external service call failed.", exception);
        }
    }

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        string url,
        object? payload,
        string correlationId,
        string? secretHeader = null,
        string? secretConfigurationKey = null,
        string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        var secret = secretConfigurationKey is null ? null : _configuration[secretConfigurationKey];
        if (!string.IsNullOrWhiteSpace(secretHeader) && !string.IsNullOrWhiteSpace(secret))
        {
            request.Headers.TryAddWithoutValidation(secretHeader, secret);
        }

        request.Headers.TryAddWithoutValidation(CorrelationHeader, correlationId);
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            request.Headers.TryAddWithoutValidation(IdempotencyHeader, idempotencyKey);
        }
        return request;
    }

    private Task DispatchSearchUpsertAsync(
        SearchUpsertEvent payload,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        return SendOutboxRequiredAsync(
            "SearchServiceUpsertIndex",
            HttpMethod.Put,
            GetInternalServiceUrl("Search", $"internal/search/indexes/{FormatId(payload.ObjectId)}"),
            new { objectType = payload.ObjectType, text = payload.Text },
            SearchSecretHeader,
            "InternalServices:Search:SharedSecret",
            idempotencyKey,
            cancellationToken);
    }

    private Task DispatchSearchDeleteAsync(
        long objectId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        return SendOutboxRequiredAsync(
            "SearchServiceDeleteIndex",
            HttpMethod.Delete,
            GetInternalServiceUrl("Search", $"internal/search/indexes/{FormatId(objectId)}"),
            null,
            SearchSecretHeader,
            "InternalServices:Search:SharedSecret",
            idempotencyKey,
            cancellationToken,
            notFoundIsSuccess: true);
    }

    private Task DispatchUserEmbeddingAsync(
        long userId,
        bool create,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        return SendOutboxRequiredAsync(
            create ? "RecommendationServiceEnsureUserEmbedding" : "RecommendationServiceDeleteUserEmbedding",
            create ? HttpMethod.Put : HttpMethod.Delete,
            GetInternalServiceUrl("Recommendation", $"internal/recommendation/users/{FormatId(userId)}/embedding"),
            null,
            RecommendationSecretHeader,
            "InternalServices:Recommendation:SharedSecret",
            idempotencyKey,
            cancellationToken,
            notFoundIsSuccess: !create);
    }

    private Task DispatchMessagingUserAsync(
        long userId,
        bool create,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        return SendOutboxRequiredAsync(
            create ? "MessengerServiceCreateUser" : "MessengerServiceDeleteUser",
            create ? HttpMethod.Post : HttpMethod.Delete,
            create
                ? GetInternalServiceUrl("Messaging", "internal/users")
                : GetInternalServiceUrl("Messaging", $"internal/users/{FormatId(userId)}"),
            create ? new { userId } : null,
            MessagingSecretHeader,
            "InternalServices:Messaging:SharedSecret",
            idempotencyKey,
            cancellationToken,
            notFoundIsSuccess: !create);
    }

    private async Task SendOutboxRequiredAsync(
        string endpointName,
        HttpMethod method,
        string? url,
        object? payload,
        string secretHeader,
        string secretConfigurationKey,
        string idempotencyKey,
        CancellationToken cancellationToken,
        bool notFoundIsSuccess = false)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new PermanentOutboxException($"{endpointName} endpoint is not configured.");
        }

        var secret = _configuration[secretConfigurationKey];
        if (Encoding.UTF8.GetByteCount(secret ?? string.Empty) < 32)
        {
            throw new PermanentOutboxException($"{endpointName} secret is not configured safely.");
        }

        using var request = CreateRequest(
            method,
            url,
            payload,
            GetCorrelationId(),
            secretHeader,
            secretConfigurationKey,
            idempotencyKey);
        using var response = await _httpClientFactory
            .CreateClient("external-services")
            .SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode ||
            notFoundIsSuccess && response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var message = $"{endpointName} returned HTTP {(int)response.StatusCode}: {Truncate(body)}";
        if ((int)response.StatusCode is 408 or 425 or 429 || (int)response.StatusCode >= 500)
        {
            throw new HttpRequestException(message);
        }

        throw new PermanentOutboxException(message);
    }

    private static T Deserialize<T>(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new PermanentOutboxException($"Outbox payload for {typeof(T).Name} is empty.");
        }
        catch (PermanentOutboxException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new PermanentOutboxException($"Outbox payload for {typeof(T).Name} is invalid.", exception);
        }
    }

    private string? GetInternalServiceUrl(string serviceName, string relativePath)
    {
        var baseUrl = _configuration[$"InternalServices:{serviceName}:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        try
        {
            var normalizedBaseUrl = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
            return new Uri(normalizedBaseUrl, relativePath.TrimStart('/')).ToString();
        }
        catch (UriFormatException exception)
        {
            _logger.LogWarning(exception, "Internal service {ServiceName} base URL is invalid.", serviceName);
            return null;
        }
    }

    private string GetCorrelationId()
    {
        var context = _httpContextAccessor.HttpContext;
        var incoming = context?.Request.Headers[CorrelationHeader].ToString();
        if (!string.IsNullOrWhiteSpace(incoming))
        {
            return incoming;
        }

        return string.IsNullOrWhiteSpace(context?.TraceIdentifier)
            ? Guid.NewGuid().ToString("N")
            : context.TraceIdentifier;
    }

    private static string FormatId(long id)
    {
        return id.ToString(CultureInfo.InvariantCulture);
    }

    private static string BuildIdempotencyKey(
        short actionType,
        long creatorId,
        long receiverId,
        long? objectId,
        string correlationId)
    {
        var source = $"{actionType}:{FormatId(creatorId)}:{FormatId(receiverId)}:{FormatId(objectId ?? 0)}:{correlationId}";
        return "socialgraph-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    private static string Truncate(string value)
    {
        return value.Length <= 500 ? value : value[..500];
    }
}

public sealed class ExternalServiceCallException : Exception
{
    public ExternalServiceCallException(string endpointKey, string message, Exception? innerException = null)
        : base($"{endpointKey}: {message}", innerException)
    {
        EndpointKey = endpointKey;
    }

    public string EndpointKey { get; }
}
