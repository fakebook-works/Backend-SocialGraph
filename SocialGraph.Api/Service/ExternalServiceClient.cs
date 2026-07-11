namespace SocialGraph.Api.Service;

using System.Globalization;
using System.Net.Http.Json;

public sealed class ExternalServiceClient : IExternalServiceClient
{
    private const string CorrelationHeader = "X-Correlation-ID";
    private const string InternalSecretHeader = "X-Gateway-Secret";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ExternalServiceClient> _logger;

    public ExternalServiceClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ILogger<ExternalServiceClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public Task NotifyAsync(long creatorId, long receiverId, short actionType, long? objectId, object? data, CancellationToken cancellationToken = default)
    {
        return PostConfiguredBestEffortAsync(
            "NotificationServiceCreateNotification",
            new { creatorId, receiverId, actionType, objectId, data },
            cancellationToken);
    }

    public async Task CreateUserAsync(long userId, string email, string password, string name, string birthdate, CancellationToken cancellationToken = default)
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
                dob = birthdate
            },
            correlationId,
            cancellationToken);

        // Authentication owns credentials and is required. The derived projections can be retried.
        await Task.WhenAll(
            UpsertSearchIndexAsync(userId, "user", name, correlationId, cancellationToken),
            EnsureUserEmbeddingAsync(userId, correlationId, cancellationToken));
    }

    public async Task DeleteUserAsync(long userId, CancellationToken cancellationToken = default)
    {
        var correlationId = GetCorrelationId();
        await SendBestEffortAsync(
            "AuthenticationServiceDeleteUser",
            HttpMethod.Post,
            _configuration["ExternalServices:AuthenticationServiceDeleteUser"],
            new { userId },
            correlationId,
            cancellationToken);

        await Task.WhenAll(
            DeleteSearchIndexAsync(userId, correlationId, cancellationToken),
            DeleteUserEmbeddingAsync(userId, correlationId, cancellationToken));
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
            cancellationToken);
    }

    public Task DeletePostEmbeddingAsync(long postId, CancellationToken cancellationToken = default)
    {
        return SendBestEffortAsync(
            "RecommendationServiceDeletePostEmbedding",
            HttpMethod.Delete,
            GetInternalServiceUrl("Recommendation", $"internal/recommendation/posts/{FormatId(postId)}/embedding"),
            null,
            GetCorrelationId(),
            cancellationToken);
    }

    public Task CreateMessengerUserAsync(long userId, CancellationToken cancellationToken = default)
    {
        return PostConfiguredBestEffortAsync("MessengerServiceCreateUser", new { userId }, cancellationToken);
    }

    public Task DeleteMessengerUserAsync(long userId, CancellationToken cancellationToken = default)
    {
        return PostConfiguredBestEffortAsync("MessengerServiceDeleteUser", new { userId }, cancellationToken);
    }

    private Task UpsertSearchIndexAsync(long objectId, string objectType, string text, string correlationId, CancellationToken cancellationToken)
    {
        return SendBestEffortAsync(
            "SearchServiceUpsertIndex",
            HttpMethod.Put,
            GetInternalServiceUrl("Search", $"internal/search/indexes/{FormatId(objectId)}"),
            new { objectType, text },
            correlationId,
            cancellationToken);
    }

    private Task DeleteSearchIndexAsync(long objectId, string correlationId, CancellationToken cancellationToken)
    {
        return SendBestEffortAsync(
            "SearchServiceDeleteIndex",
            HttpMethod.Delete,
            GetInternalServiceUrl("Search", $"internal/search/indexes/{FormatId(objectId)}"),
            null,
            correlationId,
            cancellationToken);
    }

    private Task EnsureUserEmbeddingAsync(long userId, string correlationId, CancellationToken cancellationToken)
    {
        return SendBestEffortAsync(
            "RecommendationServiceEnsureUserEmbedding",
            HttpMethod.Put,
            GetInternalServiceUrl("Recommendation", $"internal/recommendation/users/{FormatId(userId)}/embedding"),
            null,
            correlationId,
            cancellationToken);
    }

    private Task DeleteUserEmbeddingAsync(long userId, string correlationId, CancellationToken cancellationToken)
    {
        return SendBestEffortAsync(
            "RecommendationServiceDeleteUserEmbedding",
            HttpMethod.Delete,
            GetInternalServiceUrl("Recommendation", $"internal/recommendation/users/{FormatId(userId)}/embedding"),
            null,
            correlationId,
            cancellationToken);
    }

    private Task PostConfiguredBestEffortAsync(string key, object payload, CancellationToken cancellationToken)
    {
        return SendBestEffortAsync(
            key,
            HttpMethod.Post,
            _configuration[$"ExternalServices:{key}"],
            payload,
            GetCorrelationId(),
            cancellationToken);
    }

    private async Task SendBestEffortAsync(
        string endpointName,
        HttpMethod method,
        string? url,
        object? payload,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            _logger.LogDebug("External service endpoint {EndpointName} is not configured.", endpointName);
            return;
        }

        try
        {
            using var request = CreateRequest(method, url, payload, correlationId);
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

    private async Task PostRequiredAsync(string key, object payload, string correlationId, CancellationToken cancellationToken)
    {
        var url = _configuration[$"ExternalServices:{key}"];
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ExternalServiceCallException(key, "Required external service endpoint is not configured.");
        }

        try
        {
            using var request = CreateRequest(HttpMethod.Post, url, payload, correlationId);
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

    private HttpRequestMessage CreateRequest(HttpMethod method, string url, object? payload, string correlationId)
    {
        var request = new HttpRequestMessage(method, url);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        var secret = _configuration["Gateway:InternalSharedSecret"] ??
                     _configuration["InternalServices:SharedSecret"];
        if (!string.IsNullOrWhiteSpace(secret))
        {
            request.Headers.TryAddWithoutValidation(InternalSecretHeader, secret);
        }

        request.Headers.TryAddWithoutValidation(CorrelationHeader, correlationId);
        return request;
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
        var incoming = _httpContextAccessor.HttpContext?.Request.Headers[CorrelationHeader].ToString();
        return string.IsNullOrWhiteSpace(incoming) ? Guid.NewGuid().ToString("N") : incoming;
    }

    private static string FormatId(long id)
    {
        return id.ToString(CultureInfo.InvariantCulture);
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
