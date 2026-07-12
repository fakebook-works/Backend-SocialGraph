namespace SocialGraph.Api.Tests;

using HotChocolate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using SocialGraph.Api.Infrastructure;

public sealed class TrustedCallerAccessorTests
{
    private const string SharedSecret = "trusted-caller-test-secret-at-least-32-bytes";

    [Fact]
    public void RequireUserId_AcceptsGatewayAuthenticatedMatchingUser()
    {
        var accessor = CreateAccessor(42, SharedSecret);

        var userId = accessor.RequireUserId(42);

        Assert.Equal(42, userId);
    }

    [Theory]
    [MemberData(nameof(RejectedCallers))]
    public void RequireUserId_RejectsUntrustedOrMismatchedCaller(
        long? headerUserId,
        string providedSecret,
        long requestedUserId,
        string expectedCode)
    {
        var accessor = CreateAccessor(headerUserId, providedSecret);

        var exception = Assert.Throws<GraphQLException>(() => accessor.RequireUserId(requestedUserId));

        Assert.Equal(expectedCode, Assert.Single(exception.Errors).Code);
    }

    public static TheoryData<long?, string, long, string> RejectedCallers => new()
    {
        { 42L, "wrong-secret", 42L, "FORBIDDEN" },
        { null, SharedSecret, 42L, "UNAUTHENTICATED" },
        { 42L, SharedSecret, 99L, "FORBIDDEN" }
    };

    private static TrustedCallerAccessor CreateAccessor(long? userId, string providedSecret)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:InternalSharedSecret"] = SharedSecret
            })
            .Build();
        var context = new DefaultHttpContext();
        context.Request.Headers[InternalApiAuthenticationMiddleware.SecretHeaderName] = providedSecret;
        if (userId is not null)
        {
            context.Request.Headers[TrustedCallerAccessor.UserIdHeaderName] = userId.Value.ToString();
        }

        return new TrustedCallerAccessor(
            new HttpContextAccessor { HttpContext = context },
            configuration);
    }
}
