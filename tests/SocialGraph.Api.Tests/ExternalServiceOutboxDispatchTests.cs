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

    private static ExternalServiceClient CreateClient(
        CapturingHandler handler,
        IConfiguration? configuration = null,
        IOutboxPayloadProtector? protector = null)
    {
        configuration ??= Configuration();
        protector ??= new OutboxPayloadProtector(configuration);
        return new ExternalServiceClient(
            new SingleClientFactory(new HttpClient(handler)),
            configuration,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            NullLogger<ExternalServiceClient>.Instance,
            protector);
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
                ["InternalServices:Messaging:SharedSecret"] = SharedSecret
            })
            .Build();
    }

    private static IntegrationOutboxMessage Message(string eventType, string payload)
    {
        return new IntegrationOutboxMessage
        {
            id = Guid.NewGuid(),
            event_type = eventType,
            idempotency_key = "stable-outbox-key",
            payload = payload,
            created_at = DateTimeOffset.UtcNow,
            available_at = DateTimeOffset.UtcNow,
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

        public HttpClient CreateClient(string name) => _client;
    }
}
