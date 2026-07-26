namespace SocialGraph.Api.Service;

using System.Net.Http.Json;
using System.Text.Json.Serialization;
using HotChocolate;
using HotChocolate.Execution;

/// <summary>
/// Raised when a caller references a media URL that the acting user does not own.
/// </summary>
public sealed class MediaOwnershipException : Exception
{
    public MediaOwnershipException(IReadOnlyList<string> urls)
        : base("One or more media URLs are not owned by the acting user.")
    {
        Urls = urls;
    }

    public IReadOnlyList<string> Urls { get; }
}

/// <summary>
/// Validates that client-supplied media URLs belong to the acting user before they are
/// persisted onto graph objects.
/// </summary>
/// <remarks>
/// Media deletion is driven by the URL previously stored on an object: replacing an avatar
/// deletes whatever URL the object carried before. Without this check a user could store
/// another user's media URL and then trigger its permanent deletion by replacing it again.
/// The guard therefore fails closed — if ownership cannot be confirmed the write is refused.
/// </remarks>
public interface IMediaOwnershipGuard
{
    Task EnsureOwnedAsync(long ownerUserId, IEnumerable<string?> urls, CancellationToken cancellationToken = default);
}

public sealed class UploadMediaOwnershipGuard : IMediaOwnershipGuard
{
    private const string CorrelationHeader = "X-Correlation-ID";
    private const string UploadSecretHeader = "X-Internal-UploadService-Secret";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<UploadMediaOwnershipGuard> _logger;

    public UploadMediaOwnershipGuard(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ILogger<UploadMediaOwnershipGuard> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task EnsureOwnedAsync(
        long ownerUserId,
        IEnumerable<string?> urls,
        CancellationToken cancellationToken = default)
    {
        var candidates = urls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0) return;

        var url = GetAuthorizeUrl();
        if (string.IsNullOrWhiteSpace(url))
        {
            _logger.LogError(
                "Upload service base URL is not configured; refusing to persist {Count} media URL(s) for user {UserId}.",
                candidates.Length,
                ownerUserId);
            throw new MediaOwnershipException(candidates);
        }

        AuthorizeMediaResponse? result;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new { ownerUserId, urls = candidates })
            };
            var secret = _configuration["InternalServices:Upload:SharedSecret"];
            if (!string.IsNullOrWhiteSpace(secret))
            {
                request.Headers.TryAddWithoutValidation(UploadSecretHeader, secret);
            }
            request.Headers.TryAddWithoutValidation(CorrelationHeader, GetCorrelationId());

            using var response = await _httpClientFactory
                .CreateClient("external-services")
                .SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Media ownership check returned {StatusCode}; refusing the write.",
                    response.StatusCode);
                throw new MediaOwnershipException(candidates);
            }
            result = await response.Content.ReadFromJsonAsync<AuthorizeMediaResponse>(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            // Fail closed: an unavailable Upload service must not become a way to skip the check.
            _logger.LogWarning(exception, "Media ownership check failed; refusing the write.");
            throw new MediaOwnershipException(candidates);
        }

        if (result is null || !result.Authorized)
        {
            var rejected = result?.UnauthorizedUrls is { Count: > 0 }
                ? result.UnauthorizedUrls
                : candidates;
            _logger.LogWarning(
                "User {UserId} attempted to reference {Count} media URL(s) they do not own.",
                ownerUserId,
                rejected.Count);
            throw new MediaOwnershipException(rejected);
        }
    }

    private string? GetAuthorizeUrl()
    {
        var baseUrl = _configuration["InternalServices:Upload:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        try
        {
            var normalized = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
            return new Uri(normalized, "internal/media/authorize").ToString();
        }
        catch (UriFormatException exception)
        {
            _logger.LogWarning(exception, "Upload service base URL is invalid.");
            return null;
        }
    }

    private string GetCorrelationId()
    {
        var context = _httpContextAccessor.HttpContext;
        var existing = context?.Request.Headers[CorrelationHeader].ToString();
        return string.IsNullOrWhiteSpace(existing) ? Guid.NewGuid().ToString("N") : existing;
    }

    private sealed record AuthorizeMediaResponse(
        [property: JsonPropertyName("authorized")] bool Authorized,
        [property: JsonPropertyName("unauthorizedUrls")] IReadOnlyList<string>? UnauthorizedUrls);
}

/// <summary>
/// Surfaces a rejected media reference as FORBIDDEN without leaking which URLs exist.
/// </summary>
public sealed class MediaOwnershipErrorFilter : IErrorFilter
{
    public IError OnError(IError error) =>
        error.Exception is MediaOwnershipException
            ? ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage("Media must be uploaded by the acting user before it can be referenced.")
                .SetPath(error.Path)
                .Build()
            : error;
}
