namespace SocialGraph.Api.Service;

using System.Globalization;
using System.Text.Json;
using SocialGraph.Api.Contracts;

public sealed class BillingClient : IBillingClient
{
    private const double DefaultBoostMultiplier = 1.3;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BillingClient> _logger;

    public BillingClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<BillingClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<IReadOnlyList<EntitlementResult>> GetActiveEntitlementsAsync(long userId, CancellationToken cancellationToken = default)
    {
        var endpoint = _configuration["ExternalServices:BillingServiceGetActiveEntitlements"];
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return Array.Empty<EntitlementResult>();
        }

        var separator = endpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var url = $"{endpoint}{separator}userId={userId.ToString(CultureInfo.InvariantCulture)}";

        try
        {
            using var response = await _httpClientFactory.CreateClient("external-services").GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Billing entitlement request returned {StatusCode}.", response.StatusCode);
                return Array.Empty<EntitlementResult>();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var items = root.ValueKind == JsonValueKind.Array
                ? root
                : root.TryGetProperty("entitlements", out var entitlements) ? entitlements : default;

            if (items.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<EntitlementResult>();
            }

            return items.EnumerateArray().Select(ParseEntitlement).ToArray();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(exception, "Billing entitlement request failed.");
            return Array.Empty<EntitlementResult>();
        }
    }

    public async Task<bool> IsVerifiedAsync(long userId, CancellationToken cancellationToken = default)
    {
        var entitlements = await GetActiveEntitlementsAsync(userId, cancellationToken);
        return entitlements.Any(item => item.Type == BillingEntitlementType.Verified && IsActive(item));
    }

    public async Task<double> GetFeedBoostMultiplierAsync(long userId, CancellationToken cancellationToken = default)
    {
        var entitlements = await GetActiveEntitlementsAsync(userId, cancellationToken);
        var boost = entitlements.FirstOrDefault(item => item.Type == BillingEntitlementType.FeedBoostAuthor && IsActive(item));
        if (boost is null)
        {
            return 1.0;
        }

        return boost.Metadata.TryGetValue("boostMultiplier", out var raw) &&
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var multiplier)
                ? multiplier
                : DefaultBoostMultiplier;
    }

    private static EntitlementResult ParseEntitlement(JsonElement item)
    {
        var type = item.TryGetProperty("type", out var typeElement)
            ? typeElement.GetString() ?? ""
            : "";

        DateTimeOffset? expiresAt = null;
        if (item.TryGetProperty("expiresAt", out var expiresAtElement) &&
            DateTimeOffset.TryParse(expiresAtElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            expiresAt = parsed;
        }

        var metadata = item.TryGetProperty("metadata", out var metadataElement)
            ? GraphJson.Metadata(metadataElement)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        return new EntitlementResult(type, expiresAt, metadata);
    }

    private static bool IsActive(EntitlementResult entitlement)
    {
        return entitlement.ExpiresAt is null || entitlement.ExpiresAt > DateTimeOffset.UtcNow;
    }
}
