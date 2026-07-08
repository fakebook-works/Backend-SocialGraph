namespace SocialGraph.Api.Service;

using System.Net.Http.Json;

public sealed class ExternalServiceClient : IExternalServiceClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ExternalServiceClient> _logger;

    public ExternalServiceClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<ExternalServiceClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public Task NotifyAsync(long creatorId, long receiverId, short actionType, long? objectId, object? data, CancellationToken cancellationToken = default)
    {
        return PostAsync("NotificationServiceCreateNotification", new { creatorId, receiverId, actionType, objectId, data }, cancellationToken);
    }

    public async Task CreateUserAsync(long userId, string email, string password, string name, CancellationToken cancellationToken = default)
    {
        await PostAsync("AuthenticationServiceCreateUser", new { userId, email, password }, cancellationToken);
        await CreateMessengerUserAsync(userId, cancellationToken);
        await CreateSearchIndexAsync(userId, "user", name, cancellationToken);
        await CreateUserEmbeddingAsync(userId, cancellationToken);
    }

    public async Task DeleteUserAsync(long userId, CancellationToken cancellationToken = default)
    {
        await PostAsync("AuthenticationServiceDeleteUser", new { userId }, cancellationToken);
        await DeleteMessengerUserAsync(userId, cancellationToken);
        await DeleteSearchIndexAsync(userId, cancellationToken);
        await DeleteUserEmbeddingAsync(userId, cancellationToken);
    }

    public Task CreateSearchIndexAsync(long objectId, string objectType, string text, CancellationToken cancellationToken = default)
    {
        return PostAsync("SearchServiceCreateIndex", new { objectId, objectType, text }, cancellationToken);
    }

    public Task UpdateSearchIndexAsync(long objectId, string objectType, string text, CancellationToken cancellationToken = default)
    {
        return PostAsync("SearchServiceUpdateIndex", new { objectId, objectType, text }, cancellationToken);
    }

    public Task DeleteSearchIndexAsync(long objectId, CancellationToken cancellationToken = default)
    {
        return PostAsync("SearchServiceDeleteIndex", new { objectId }, cancellationToken);
    }

    public Task CreateUserEmbeddingAsync(long userId, CancellationToken cancellationToken = default)
    {
        return PostAsync("RecommendServiceCreateUserEmbedding", new { userId }, cancellationToken);
    }

    public Task DeleteUserEmbeddingAsync(long userId, CancellationToken cancellationToken = default)
    {
        return PostAsync("RecommendServiceDeleteUserEmbedding", new { userId }, cancellationToken);
    }

    public Task CreatePostEmbeddingAsync(long postId, string content, IReadOnlyList<string> mediaUrls, CancellationToken cancellationToken = default)
    {
        return PostAsync("RecommendServiceCreatePostEmbedding", new { postId, content, mediaUrls }, cancellationToken);
    }

    public Task DeletePostEmbeddingAsync(long postId, CancellationToken cancellationToken = default)
    {
        return PostAsync("RecommendServiceDeletePostEmbedding", new { postId }, cancellationToken);
    }

    public Task CreateMessengerUserAsync(long userId, CancellationToken cancellationToken = default)
    {
        return PostAsync("MessengerServiceCreateUser", new { userId }, cancellationToken);
    }

    public Task DeleteMessengerUserAsync(long userId, CancellationToken cancellationToken = default)
    {
        return PostAsync("MessengerServiceDeleteUser", new { userId }, cancellationToken);
    }

    private async Task PostAsync(string key, object payload, CancellationToken cancellationToken)
    {
        var url = _configuration[$"ExternalServices:{key}"];
        if (string.IsNullOrWhiteSpace(url))
        {
            _logger.LogDebug("External service endpoint {EndpointKey} is not configured.", key);
            return;
        }

        try
        {
            var response = await _httpClientFactory.CreateClient("external-services").PostAsJsonAsync(url, payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("External service {EndpointKey} returned {StatusCode}.", key, response.StatusCode);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(exception, "External service {EndpointKey} call failed.", key);
        }
    }
}
