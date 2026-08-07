namespace SocialGraph.Api.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SocialGraph.Api.Database;
using SocialGraph.Api.Infrastructure.Outbox;
using SocialGraph.Api.Service;

public sealed class ExternalServiceOutboxDispatchTests
{
    private const string SharedSecret = "outbox-transport-test-secret-at-least-32-bytes";

    [Fact]
    public async Task NotificationDispatch_ForwardsStableIdempotencyAndServiceSecret()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var client = CreateClient(handler);
        var message = Message(
            IntegrationEventType.NotificationCreate,
            JsonSerializer.Serialize(new NotificationCreateEvent(1, 2, 4, 1, null)));

        await client.DispatchAsync(message);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/internal/notifications", request.Uri.AbsolutePath);
        Assert.Equal(message.idempotency_key, Assert.Single(request.Headers["Idempotency-Key"]));
        Assert.Equal(SharedSecret, Assert.Single(request.Headers["X-Internal-NotificationService-Secret"]));
    }

    [Fact]
    public async Task TransientHttpFailure_IsRetryable()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var client = CreateClient(handler);
        var message = Message(
            IntegrationEventType.SearchDelete,
            JsonSerializer.Serialize(new SearchDeleteEvent(123)));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.DispatchAsync(message));
    }

    [Fact]
    public async Task InvalidContractFailure_IsPermanent()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var client = CreateClient(handler);
        var message = Message(
            IntegrationEventType.SearchUpsert,
            JsonSerializer.Serialize(new SearchUpsertEvent(123, "user", "Name")));

        await Assert.ThrowsAsync<PermanentOutboxException>(() => client.DispatchAsync(message));
    }

    [Fact]
    public async Task EmptySearchProjection_IsDispatchedAsIdempotentDelete()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = CreateClient(handler);
        var message = Message(
            IntegrationEventType.SearchUpsert,
            JsonSerializer.Serialize(new SearchUpsertEvent(123, "feedPost", "  ")));

        await client.DispatchAsync(message);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("/internal/search/indexes/123", request.Uri.AbsolutePath);
    }

    [Fact]
    public async Task EmptyRecommendationProjection_IsDispatchedAsIdempotentDelete()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = CreateClient(handler);
        var message = Message(
            IntegrationEventType.RecommendationContentUpsert,
            JsonSerializer.Serialize(new ContentEmbeddingEvent(123, "", Array.Empty<string>())));

        await client.DispatchAsync(message);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("/internal/recommendation/posts/123/embedding", request.Uri.AbsolutePath);
    }

    [Fact]
    public async Task RecommendationContentProjection_UsesDedicatedBoundedLongRunningClient()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var (client, factory) = CreateClientWithFactory(handler);
        var message = Message(
            IntegrationEventType.RecommendationContentUpsert,
            JsonSerializer.Serialize(new ContentEmbeddingEvent(123, "embedding text", Array.Empty<string>())));

        await client.DispatchAsync(message);

        Assert.Equal("recommendation-content", Assert.Single(factory.RequestedNames));
    }

    [Fact]
    public async Task AuthUserCreate_DecryptsCredentialsAndDispatchesOnlyAuthEvent()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var configuration = Configuration();
        var protector = new OutboxPayloadProtector(configuration);
        var client = CreateClient(handler, configuration, protector);
        var payload = JsonSerializer.Serialize(new UserCreateEvent(
            123,
            "a@example.com",
            "secret-password",
            "Nguyen A",
            "2000-01-01",
            true));
        var message = Message(IntegrationEventType.UserCreate, protector.Protect(payload));

        await client.DispatchAsync(message);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("auth", request.Uri.Host);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("secret-password", body.RootElement.GetProperty("password").GetString());
    }

    [Fact]
    public async Task RecommendationInteractionDispatch_ForwardsBodySecretAndStableIdempotency()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = CreateClient(handler);
        var message = Message(
            IntegrationEventType.RecommendationInteraction,
            JsonSerializer.Serialize(new RecommendationInteractionEvent(123, 456, "SAVE")));

        await client.DispatchAsync(message);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/internal/recommendation/users/123/interactions", request.Uri.AbsolutePath);
        Assert.Equal(message.idempotency_key, Assert.Single(request.Headers["Idempotency-Key"]));
        Assert.Equal(SharedSecret, Assert.Single(request.Headers["X-Internal-RecommendationService-Secret"]));
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(456, body.RootElement.GetProperty("targetId").GetInt64());
        Assert.Equal("SAVE", body.RootElement.GetProperty("action").GetString());
    }

    [Fact]
    public async Task MediaFinalizeDispatch_UsesUploadInternalContract()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"finalized\":1}")
        });
        var client = CreateClient(handler);
        var message = Message(
            IntegrationEventType.MediaFinalize,
            JsonSerializer.Serialize(new MediaLifecycleEvent(new[] { "/media/files/a.jpg" })));

        await client.DispatchAsync(message);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("upload", request.Uri.Host);
        Assert.Equal("/internal/media/finalize", request.Uri.AbsolutePath);
        Assert.Equal(SharedSecret, Assert.Single(request.Headers["X-Internal-UploadService-Secret"]));
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("/media/files/a.jpg", body.RootElement.GetProperty("urls")[0].GetString());
    }

    [Fact]
    public async Task MediaFinalize_IncompleteAcknowledgement_IsRetryable()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"finalized\":0}")
        });
        var client = CreateClient(handler);
        var message = Message(
            IntegrationEventType.MediaFinalize,
            JsonSerializer.Serialize(new MediaLifecycleEvent(new[] { "/media/files/a.jpg" })));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.DispatchAsync(message));
    }

    [Fact]
    public async Task MediaFinalizeDispatch_SendsStableReferenceAndOperationTime()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"finalized\":1,\"stale\":0}")
        });
        var client = CreateClient(handler);
        var operationAt = DateTimeOffset.Parse("2026-08-07T12:34:56Z");
        var reference = new MediaLifecycleReference(
            "/media/files/a.jpg",
            "socialgraph:user:42:avatar");
        var message = Message(
            IntegrationEventType.MediaFinalize,
            JsonSerializer.Serialize(new MediaLifecycleEvent(
                OwnerUserId: 42,
                References: new[] { reference },
                OperationAt: operationAt)));

        await client.DispatchAsync(message);

        var request = Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.False(body.RootElement.TryGetProperty("urls", out _));
        Assert.Equal(42, body.RootElement.GetProperty("ownerUserId").GetInt64());
        Assert.Equal(operationAt, body.RootElement.GetProperty("operationAt").GetDateTimeOffset());
        var sentReference = Assert.Single(body.RootElement.GetProperty("references").EnumerateArray());
        Assert.Equal(reference.Url, sentReference.GetProperty("url").GetString());
        Assert.Equal(reference.ReferenceId, sentReference.GetProperty("referenceId").GetString());
    }

    [Theory]
    [InlineData(IntegrationEventType.MediaFinalize)]
    [InlineData(IntegrationEventType.MediaDelete)]
    public async Task ExactMediaDispatch_MissingOperationTimeFailsBeforeNetwork(string eventType)
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = CreateClient(handler);
        var message = Message(
            eventType,
            JsonSerializer.Serialize(new MediaLifecycleEvent(
                OwnerUserId: 42,
                References:
                [
                    new MediaLifecycleReference(
                        "/media/files/a.jpg",
                        "socialgraph:user:42:avatar")
                ])));

        await Assert.ThrowsAsync<PermanentOutboxException>(() => client.DispatchAsync(message));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RetriedExactFinalize_RenewsOriginalReservationBeforeFinalize()
    {
        var handler = new CapturingHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.Uri.AbsolutePath.EndsWith("/authorize", StringComparison.Ordinal)
                ? "{\"authorized\":true,\"unauthorizedUrls\":[],\"exactReferences\":true,\"lifecycleVersion\":3,\"referenceCount\":1}"
                : "{\"finalized\":1,\"stale\":0}")
        });
        var client = CreateClient(handler);
        var operationAt = DateTimeOffset.Parse("2026-08-07T12:34:56Z");
        var message = Message(
            IntegrationEventType.MediaFinalize,
            JsonSerializer.Serialize(new MediaLifecycleEvent(
                OwnerUserId: 42,
                References:
                [
                    new MediaLifecycleReference(
                        "/media/files/a.jpg",
                        "socialgraph:user:42:avatar")
                ],
                OperationAt: operationAt)),
            attempts: 2);

        await client.DispatchAsync(message);

        Assert.Equal(
            ["/internal/media/authorize", "/internal/media/finalize"],
            handler.Requests.Select(request => request.Uri.AbsolutePath));
        Assert.All(handler.Requests, request =>
        {
            using var body = JsonDocument.Parse(request.Body!);
            Assert.Equal(operationAt, body.RootElement.GetProperty("operationAt").GetDateTimeOffset());
        });
    }

    [Fact]
    public async Task MediaDeleteDispatch_RequiresCompleteReferenceAcknowledgement()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"detached\":0,\"stale\":0}")
        });
        var client = CreateClient(handler);
        var reference = MediaLifecycleReferences.ForMedia(100, "/media/files/a.jpg");
        var message = Message(
            IntegrationEventType.MediaDelete,
            JsonSerializer.Serialize(new MediaLifecycleEvent(
                References: new[] { reference },
                OperationAt: DateTimeOffset.UtcNow)));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.DispatchAsync(message));
    }

    [Fact]
    public async Task LegacyMediaDeleteDispatch_RequiresCompleteScheduledAcknowledgement()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"scheduled\":0}")
        });
        var client = CreateClient(handler);
        var message = Message(
            IntegrationEventType.MediaDelete,
            JsonSerializer.Serialize(new MediaLifecycleEvent(
                Urls: new[] { "/media/files/a.jpg" })));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.DispatchAsync(message));
    }

    private static ExternalServiceClient CreateClient(
        CapturingHandler handler,
        IConfiguration? configuration = null,
        IOutboxPayloadProtector? protector = null)
        => CreateClientWithFactory(handler, configuration, protector).Client;

    private static (ExternalServiceClient Client, SingleClientFactory Factory) CreateClientWithFactory(
        CapturingHandler handler,
        IConfiguration? configuration = null,
        IOutboxPayloadProtector? protector = null)
    {
        configuration ??= Configuration();
        protector ??= new OutboxPayloadProtector(configuration);
        var factory = new SingleClientFactory(new HttpClient(handler));
        var client = new ExternalServiceClient(
            factory,
            configuration,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            NullLogger<ExternalServiceClient>.Instance,
            protector);
        return (client, factory);
    }

    private static IConfiguration Configuration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IntegrationOutbox:PayloadEncryptionKey"] = SharedSecret,
                ["InternalServices:Authentication:BaseUrl"] = "http://auth",
                ["InternalServices:Authentication:SharedSecret"] = SharedSecret,
                ["InternalServices:Search:BaseUrl"] = "http://search",
                ["InternalServices:Search:SharedSecret"] = SharedSecret,
                ["InternalServices:Recommendation:BaseUrl"] = "http://recommendation",
                ["InternalServices:Recommendation:SharedSecret"] = SharedSecret,
                ["InternalServices:Notification:BaseUrl"] = "http://notification",
                ["InternalServices:Notification:SharedSecret"] = SharedSecret,
                ["InternalServices:Messaging:BaseUrl"] = "http://messaging",
                ["InternalServices:Messaging:SharedSecret"] = SharedSecret,
                ["InternalServices:Upload:BaseUrl"] = "http://upload",
                ["InternalServices:Upload:SharedSecret"] = SharedSecret
            })
            .Build();
    }

    private static IntegrationOutboxMessage Message(string eventType, string payload, int attempts = 0)
    {
        return new IntegrationOutboxMessage
        {
            id = Guid.NewGuid(),
            event_type = eventType,
            idempotency_key = "stable-outbox-key",
            payload = payload,
            created_at = DateTimeOffset.UtcNow,
            available_at = DateTimeOffset.UtcNow,
            attempts = attempts,
            max_attempts = 10,
            status = IntegrationOutboxStatus.Processing
        };
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Uri,
        string? Body,
        IReadOnlyDictionary<string, string[]> Headers);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<CapturedRequest, HttpResponseMessage> _responseFactory;

        public CapturingHandler(Func<CapturedRequest, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public ConcurrentQueue<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var captured = new CapturedRequest(
                request.Method,
                request.RequestUri!,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.ToDictionary(
                    header => header.Key,
                    header => header.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase));
            Requests.Enqueue(captured);
            return _responseFactory(captured);
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SingleClientFactory(HttpClient client)
        {
            _client = client;
        }

        public ConcurrentQueue<string> RequestedNames { get; } = new();

        public HttpClient CreateClient(string name)
        {
            RequestedNames.Enqueue(name);
            return _client;
        }
    }
}
