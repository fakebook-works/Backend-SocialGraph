namespace SocialGraph.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SocialGraph.Api.Service;

public sealed class AuthenticationContactClientTests
{
    private const long UserId = 9_000_000_000_000_001;
    private const string SharedSecret = "auth-target-test-secret-at-least-32-bytes";

    [Fact]
    public async Task GetEmail_UsesTheRegisteredInternalClientAndMinimalContract()
    {
        var handler = new RecordingHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { userId = UserId, email = "target@example.com" })
        });
        var factory = new RecordingClientFactory(new HttpClient(handler));
        var client = CreateClient(factory);

        var email = await client.GetEmailAsync(UserId);

        Assert.Equal("target@example.com", email);
        Assert.Equal("external-services", factory.RequestedName);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"/internal/users/{UserId}/contact", request.Path);
        Assert.Equal(SharedSecret, request.AuthenticationSecret);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task GetEmail_FailsClosedWithoutInventingContactData(HttpStatusCode statusCode)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(statusCode));
        var client = CreateClient(new RecordingClientFactory(new HttpClient(handler)));

        var email = await client.GetEmailAsync(UserId);

        Assert.Null(email);
    }

    [Fact]
    public async Task GetEmail_RejectsAMismatchedAuthenticationIdentity()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { userId = UserId + 1, email = "wrong@example.com" })
        });
        var client = CreateClient(new RecordingClientFactory(new HttpClient(handler)));

        var email = await client.GetEmailAsync(UserId);

        Assert.Null(email);
    }

    [Fact]
    public async Task GetEmail_DegradesToNoContactWhenAuthenticationReturnsMalformedJson()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not-json")
        });
        var client = CreateClient(new RecordingClientFactory(new HttpClient(handler)));

        var email = await client.GetEmailAsync(UserId);

        Assert.Null(email);
    }

    private static AuthenticationContactClient CreateClient(IHttpClientFactory factory)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InternalServices:Authentication:BaseUrl"] = "http://auth",
                ["InternalServices:Authentication:SharedSecret"] = SharedSecret
            })
            .Build();
        return new AuthenticationContactClient(
            factory,
            configuration,
            NullLogger<AuthenticationContactClient>.Instance);
    }

    private sealed record CapturedRequest(HttpMethod Method, string Path, string? AuthenticationSecret);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri?.AbsolutePath ?? string.Empty,
                request.Headers.TryGetValues("X-Internal-AuthenticationService-Secret", out var values)
                    ? values.SingleOrDefault()
                    : null));
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class RecordingClientFactory(HttpClient client) : IHttpClientFactory
    {
        public string? RequestedName { get; private set; }

        public HttpClient CreateClient(string name)
        {
            RequestedName = name;
            return client;
        }
    }
}
