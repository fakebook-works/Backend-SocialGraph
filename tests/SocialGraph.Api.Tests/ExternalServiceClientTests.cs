namespace SocialGraph.Api.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SocialGraph.Api.Infrastructure.Outbox;
using SocialGraph.Api.Service;

public sealed class ExternalServiceClientTests
{
    private const string CorrelationId = "register-correlation";
    private const string SharedSecret = "social-graph-test-secret-at-least-32-bytes";
    private const long UserId = 9_000_000_000_000_001;

    [Fact]
    public async Task CreateUser_CallsAuthFirst_ThenSearchAndRecommendationConcurrently()
    {
        var downstreamStarted = 0;
        var bothDownstreamCallsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CapturingHandler(async (request, cancellationToken) =>
        {
            if (request.Uri.Host == "auth")
            {
                return new HttpResponseMessage(HttpStatusCode.Created);
            }

            if (Interlocked.Increment(ref downstreamStarted) == 2)
            {
                bothDownstreamCallsStarted.TrySetResult();
            }

            await bothDownstreamCallsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = CreateClient(handler);

        await client.CreateUserAsync(
            UserId,
            "a@example.com",
            "secret",
            "Nguyen Van A",
            "2000-01-01",
            true).WaitAsync(TimeSpan.FromSeconds(3));

        var requests = handler.Requests.ToArray();
        Assert.Equal(3, requests.Length);
        Assert.Equal("auth", requests[0].Uri.Host);

        var auth = requests.Single(request => request.Uri.Host == "auth");
        Assert.Equal(HttpMethod.Post, auth.Method);
        Assert.Equal("/internal/users", auth.Uri.AbsolutePath);
        using (var body = JsonDocument.Parse(auth.Body!))
        {
            Assert.Equal(UserId, body.RootElement.GetProperty("userId").GetInt64());
            Assert.Equal("a@example.com", body.RootElement.GetProperty("email").GetString());
            Assert.Equal("secret", body.RootElement.GetProperty("password").GetString());
            Assert.Equal("Nguyen Van A", body.RootElement.GetProperty("displayName").GetString());
            Assert.Equal("2000-01-01", body.RootElement.GetProperty("dob").GetString());
            Assert.True(body.RootElement.GetProperty("gender").GetBoolean());
        }

        var search = requests.Single(request => request.Uri.Host == "search");
        Assert.Equal(HttpMethod.Put, search.Method);
        Assert.Equal($"/internal/search/indexes/{UserId}", search.Uri.AbsolutePath);
        using (var body = JsonDocument.Parse(search.Body!))
        {
            Assert.Equal("user", body.RootElement.GetProperty("objectType").GetString());
            Assert.Equal("Nguyen Van A", body.RootElement.GetProperty("text").GetString());
        }

        var recommendation = requests.Single(request => request.Uri.Host == "recommendation");
        Assert.Equal(HttpMethod.Put, recommendation.Method);
        Assert.Equal($"/internal/recommendation/users/{UserId}/embedding", recommendation.Uri.AbsolutePath);
        Assert.Null(recommendation.Body);

        Assert.All(requests, AssertInternalHeaders);
    }

    [Fact]
    public async Task CreateUser_DoesNotProvisionDerivedServices_WhenAuthFails()
    {
        var handler = new CapturingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<ExternalServiceCallException>(() =>
            client.CreateUserAsync(UserId, "a@example.com", "secret", "Nguyen Van A", "2000-01-01", true));

        Assert.Equal("AuthenticationServiceCreateUser", exception.EndpointKey);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("auth", request.Uri.Host);
    }

    [Fact]
    public async Task CreateUser_RemainsSuccessful_WhenDerivedServicesFail()
    {
        var handler = new CapturingHandler((request, _) => Task.FromResult(
            new HttpResponseMessage(request.Uri.Host == "auth"
                ? HttpStatusCode.Created
                : HttpStatusCode.ServiceUnavailable)));
        var client = CreateClient(handler);

        await client.CreateUserAsync(UserId, "a@example.com", "secret", "Nguyen Van A", "2000-01-01", true);

        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task ProjectionMethods_UseCanonicalIdempotentContracts()
    {
        var handler = new CapturingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var client = CreateClient(handler);
        var postId = UserId + 1;

        await client.UpdateSearchIndexAsync(UserId, "user", "Updated name");
        await client.DeleteSearchIndexAsync(UserId);
        await client.CreateUserEmbeddingAsync(UserId);
        await client.DeleteUserEmbeddingAsync(UserId);
        await client.CreatePostEmbeddingAsync(postId, "Post content", ["https://example.com/photo.jpg"]);
        await client.DeletePostEmbeddingAsync(postId);

        var requests = handler.Requests.ToArray();
        Assert.Contains(requests, request => request.Method == HttpMethod.Put && request.Uri.AbsolutePath == $"/internal/search/indexes/{UserId}");
        Assert.Contains(requests, request => request.Method == HttpMethod.Delete && request.Uri.AbsolutePath == $"/internal/search/indexes/{UserId}");
        Assert.Contains(requests, request => request.Method == HttpMethod.Put && request.Uri.AbsolutePath == $"/internal/recommendation/users/{UserId}/embedding");
        Assert.Contains(requests, request => request.Method == HttpMethod.Delete && request.Uri.AbsolutePath == $"/internal/recommendation/users/{UserId}/embedding");

        var postUpsert = Assert.Single(requests, request =>
            request.Method == HttpMethod.Put &&
            request.Uri.AbsolutePath == $"/internal/recommendation/posts/{postId}/embedding");
        using (var body = JsonDocument.Parse(postUpsert.Body!))
        {
            Assert.Equal("Post content", body.RootElement.GetProperty("content").GetString());
            Assert.Equal("https://example.com/photo.jpg", body.RootElement.GetProperty("mediaUrls")[0].GetString());
        }

        Assert.Contains(requests, request => request.Method == HttpMethod.Delete && request.Uri.AbsolutePath == $"/internal/recommendation/posts/{postId}/embedding");
        Assert.All(requests, AssertInternalHeaders);
    }

    [Fact]
    public async Task Notify_UsesCanonicalActionAndIdempotencyContract()
    {
        var handler = new CapturingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)));
        var client = CreateClient(handler);

        await client.NotifyAsync(UserId, UserId + 1, ExternalNotificationAction.FriendRequest, UserId, null);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/internal/notifications", request.Uri.AbsolutePath);
        Assert.Equal(SharedSecret, Assert.Single(request.Headers["X-Internal-NotificationService-Secret"]));
        Assert.StartsWith("socialgraph-", Assert.Single(request.Headers["Idempotency-Key"]));
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(4, body.RootElement.GetProperty("actionType").GetInt16());
    }

    private static ExternalServiceClient CreateClient(CapturingHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:InternalSharedSecret"] = SharedSecret,
                ["InternalServices:Authentication:BaseUrl"] = "http://auth",
                ["InternalServices:Authentication:SharedSecret"] = SharedSecret,
                ["InternalServices:Search:BaseUrl"] = "http://search",
                ["InternalServices:Search:SharedSecret"] = SharedSecret,
                ["InternalServices:Recommendation:BaseUrl"] = "http://recommendation",
                ["InternalServices:Recommendation:SharedSecret"] = SharedSecret,
                ["InternalServices:Notification:BaseUrl"] = "http://notification",
                ["InternalServices:Notification:SharedSecret"] = SharedSecret
            })
            .Build();
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Correlation-ID"] = CorrelationId;

        return new ExternalServiceClient(
            new SingleClientFactory(new HttpClient(handler)),
            configuration,
            new HttpContextAccessor { HttpContext = context },
            NullLogger<ExternalServiceClient>.Instance,
            new OutboxPayloadProtector(configuration));
    }

    private static void AssertInternalHeaders(CapturedRequest request)
    {
        var expectedSecretHeader = request.Uri.Host switch
        {
            "auth" => "X-Internal-AuthenticationService-Secret",
            "search" => "X-Internal-SearchService-Secret",
            "recommendation" => "X-Internal-RecommendationService-Secret",
            "notification" => "X-Internal-NotificationService-Secret",
            _ => throw new Xunit.Sdk.XunitException($"Unexpected internal service host: {request.Uri.Host}")
        };
        Assert.Equal(SharedSecret, Assert.Single(request.Headers[expectedSecretHeader]));
        Assert.Equal(CorrelationId, Assert.Single(request.Headers["X-Correlation-ID"]));
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Uri,
        string? Body,
        IReadOnlyDictionary<string, string[]> Headers);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>> _responseFactory;

        public CapturingHandler(Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public ConcurrentQueue<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var captured = new CapturedRequest(
                request.Method,
                request.RequestUri!,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.ToDictionary(header => header.Key, header => header.Value.ToArray(), StringComparer.OrdinalIgnoreCase));
            Requests.Enqueue(captured);
            return await _responseFactory(captured, cancellationToken);
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SingleClientFactory(HttpClient client)
        {
            _client = client;
        }

        public HttpClient CreateClient(string name)
        {
            return _client;
        }
    }
}
