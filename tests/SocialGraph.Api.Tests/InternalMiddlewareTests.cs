namespace SocialGraph.Api.Tests;

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using SocialGraph.Api.Infrastructure;

public sealed class InternalMiddlewareTests
{
    private const string SharedSecret = "social-graph-test-secret-at-least-32-bytes";

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-secret")]
    public async Task InternalApi_RejectsMissingOrWrongSecret(string? providedSecret)
    {
        var (context, nextCalled) = await InvokeInternalAuthAsync(SharedSecret, providedSecret, "/internal/recommendation/post-candidates");

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("FORBIDDEN", await ReadErrorCodeAsync(context));
    }

    [Fact]
    public async Task InternalApi_FailsClosed_WhenSecretIsNotConfigured()
    {
        var (context, nextCalled) = await InvokeInternalAuthAsync(null, SharedSecret, "/internal/users/1/verify");

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("INTERNAL_AUTH_NOT_CONFIGURED", await ReadErrorCodeAsync(context));
    }

    [Fact]
    public async Task InternalApi_AllowsValidSecret_AndPublicRoutesDoNotRequireIt()
    {
        var (internalContext, internalNextCalled) = await InvokeInternalAuthAsync(SharedSecret, SharedSecret, "/internal/recommendation/post-candidates");
        var (publicContext, publicNextCalled) = await InvokeInternalAuthAsync(null, null, "/graphql");

        Assert.True(internalNextCalled());
        Assert.Equal(StatusCodes.Status204NoContent, internalContext.Response.StatusCode);
        Assert.True(publicNextCalled());
        Assert.Equal(StatusCodes.Status204NoContent, publicContext.Response.StatusCode);
    }

    [Fact]
    public async Task InternalApi_AcceptsDedicatedSocialGraphServiceSecret()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InternalServices:SocialGraph:SharedSecret"] = SharedSecret
            })
            .Build();
        var context = new DefaultHttpContext();
        context.Request.Path = "/internal/recommendation/post-candidates";
        context.Request.Headers["X-Internal-SocialGraphService-Secret"] = SharedSecret;
        var called = false;
        var middleware = new InternalApiAuthenticationMiddleware(
            _ =>
            {
                called = true;
                return Task.CompletedTask;
            },
            configuration);

        await middleware.InvokeAsync(context);

        Assert.True(called);
    }

    [Fact]
    public async Task CorrelationMiddleware_PreservesIncomingId_AndGeneratesMissingId()
    {
        var incomingContext = new DefaultHttpContext();
        incomingContext.Request.Headers[CorrelationIdMiddleware.HeaderName] = "existing-id";
        var incomingMiddleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);
        await incomingMiddleware.InvokeAsync(incomingContext);

        var generatedContext = new DefaultHttpContext();
        var generatedMiddleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);
        await generatedMiddleware.InvokeAsync(generatedContext);

        Assert.Equal("existing-id", incomingContext.TraceIdentifier);
        Assert.Equal("existing-id", incomingContext.Response.Headers[CorrelationIdMiddleware.HeaderName]);
        Assert.False(string.IsNullOrWhiteSpace(generatedContext.TraceIdentifier));
        Assert.Equal(generatedContext.TraceIdentifier, generatedContext.Response.Headers[CorrelationIdMiddleware.HeaderName]);
    }

    private static async Task<(DefaultHttpContext Context, Func<bool> NextCalled)> InvokeInternalAuthAsync(
        string? expectedSecret,
        string? providedSecret,
        string path)
    {
        var values = new Dictionary<string, string?>();
        if (expectedSecret is not null)
        {
            values["Gateway:InternalSharedSecret"] = expectedSecret;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        if (providedSecret is not null)
        {
            context.Request.Headers[InternalApiAuthenticationMiddleware.SecretHeaderName] = providedSecret;
        }

        var called = false;
        var middleware = new InternalApiAuthenticationMiddleware(
            _ =>
            {
                called = true;
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            configuration);

        await middleware.InvokeAsync(context);
        return (context, () => called);
    }

    private static async Task<string> ReadErrorCodeAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        return body.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }
}
