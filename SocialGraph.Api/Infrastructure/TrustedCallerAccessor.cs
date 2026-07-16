namespace SocialGraph.Api.Infrastructure;

using System.Globalization;
using HotChocolate;

public interface ITrustedCallerAccessor
{
    long RequireUserId();
    long RequireUserId(long requestedUserId);
}

public sealed class TrustedCallerAccessor : ITrustedCallerAccessor
{
    public const string UserIdHeaderName = "X-User-Id";

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IConfiguration _configuration;

    public TrustedCallerAccessor(
        IHttpContextAccessor httpContextAccessor,
        IConfiguration configuration)
    {
        _httpContextAccessor = httpContextAccessor;
        _configuration = configuration;
    }

    public long RequireUserId()
    {
        var context = _httpContextAccessor.HttpContext ??
            throw Error("UNAUTHENTICATED", "Trusted caller context is unavailable.");

        var authentication = InternalCallerAuthentication.Validate(_configuration, context.Request.Headers);
        if (authentication == InternalAuthenticationResult.NotConfigured)
        {
            throw Error(
                "INTERNAL_AUTH_NOT_CONFIGURED",
                "Internal service authentication is not configured.");
        }

        if (authentication != InternalAuthenticationResult.Valid)
        {
            throw Error("FORBIDDEN", "Trusted Gateway authentication failed.");
        }

        var rawUserId = context.Request.Headers[UserIdHeaderName].ToString();
        if (!long.TryParse(rawUserId, NumberStyles.None, CultureInfo.InvariantCulture, out var userId) || userId <= 0)
        {
            throw Error("UNAUTHENTICATED", "A valid trusted user ID is required.");
        }

        return userId;
    }

    public long RequireUserId(long requestedUserId)
    {
        var userId = RequireUserId();
        if (requestedUserId != userId)
        {
            throw Error("FORBIDDEN", "The requested user does not match the authenticated user.");
        }

        return userId;
    }

    private static GraphQLException Error(string code, string message)
    {
        return new GraphQLException(
            ErrorBuilder.New()
                .SetCode(code)
                .SetMessage(message)
                .Build());
    }
}
