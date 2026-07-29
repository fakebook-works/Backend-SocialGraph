namespace SocialGraph.Api.Service;

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

public interface IAuthenticationContactClient
{
    Task<string?> GetEmailAsync(long userId, CancellationToken cancellationToken = default);
}

public sealed class AuthenticationContactClient(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<AuthenticationContactClient> logger) : IAuthenticationContactClient
{
    private const string AuthenticationSecretHeader = "X-Internal-AuthenticationService-Secret";

    public async Task<string?> GetEmailAsync(long userId, CancellationToken cancellationToken = default)
    {
        if (userId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }

        var baseUrl = configuration["InternalServices:Authentication:BaseUrl"];
        var secret = configuration["InternalServices:Authentication:SharedSecret"];
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(secret))
        {
            logger.LogWarning("Authentication contact lookup is not configured.");
            return null;
        }

        try
        {
            var endpoint = new Uri(
                new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute),
                $"internal/users/{userId.ToString(CultureInfo.InvariantCulture)}/contact");
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.TryAddWithoutValidation(AuthenticationSecretHeader, secret);
            using var response = await httpClientFactory
                .CreateClient("external-services")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Authentication contact lookup returned HTTP {StatusCode}.",
                    (int)response.StatusCode);
                return null;
            }

            var contact = await response.Content.ReadFromJsonAsync<AuthenticationContactResponse>(
                cancellationToken: cancellationToken);
            return contact is not null && contact.UserId == userId && !string.IsNullOrWhiteSpace(contact.Email)
                ? contact.Email.Trim()
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException or JsonException or NotSupportedException)
        {
            logger.LogWarning(exception, "Authentication contact lookup failed.");
            return null;
        }
    }

    private sealed record AuthenticationContactResponse(long UserId, string Email);
}
