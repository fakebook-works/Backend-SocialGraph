namespace SocialGraph.Api.Tests;

using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

public sealed class DistributedNonceValidationTests
{
    private const string Secret = "distributed-nonce-test-secret-at-least-32-bytes";
    private const string Nonce = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task SharedNonceStoreRejectsReplayAcrossValidatorInstances()
    {
        var store = new FakeDistributedNonceStore();
        var first = Validator(store);
        var second = Validator(store);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var firstResult = await first.ValidateAsync(Request(timestamp), Secret, CancellationToken.None);
        var replayResult = await second.ValidateAsync(Request(timestamp), Secret, CancellationToken.None);

        Assert.Equal(InternalSignatureValidationResult.Valid, firstResult);
        Assert.Equal(InternalSignatureValidationResult.Invalid, replayResult);
    }

    [Fact]
    public async Task RedisOutageFailsClosedAfterCryptographicValidation()
    {
        var store = new FakeDistributedNonceStore { Available = false };

        var result = await Validator(store).ValidateAsync(
            Request(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            Secret,
            CancellationToken.None);

        Assert.Equal(InternalSignatureValidationResult.Unavailable, result);
        Assert.Equal(1, store.Claims);
    }

    [Fact]
    public async Task InvalidSignatureDoesNotPoisonTheDistributedNonce()
    {
        var store = new FakeDistributedNonceStore();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var invalid = Request(timestamp);
        invalid.Headers[InternalRequestSigning.SignatureHeader] = new string('0', 64);

        var invalidResult = await Validator(store).ValidateAsync(invalid, Secret, CancellationToken.None);
        var validResult = await Validator(store).ValidateAsync(Request(timestamp), Secret, CancellationToken.None);

        Assert.Equal(InternalSignatureValidationResult.Invalid, invalidResult);
        Assert.Equal(InternalSignatureValidationResult.Valid, validResult);
        Assert.Equal(1, store.Claims);
    }

    private static InternalSignatureValidator Validator(IInternalNonceStore store)
    {
        var configuration = new ConfigurationBuilder().Build();
        return new InternalSignatureValidator(
            Options.Create(new InternalRequestSigningOptions()),
            TimeProvider.System,
            store,
            new InternalRequestSigningTarget(configuration, "test", "X-Test", "social-graph"));
    }

    private static HttpRequest Request(long timestamp)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/internal/test";
        context.Request.Body = new MemoryStream();
        context.Request.Headers[InternalRequestSigning.TimestampHeader] = timestamp.ToString(CultureInfo.InvariantCulture);
        context.Request.Headers[InternalRequestSigning.NonceHeader] = Nonce;
        context.Request.Headers[InternalRequestSigning.SignatureHeader] = Convert.ToHexString(
            InternalRequestSigning.Sign(Secret, HttpMethods.Post, "/internal/test", timestamp, Nonce, [])).ToLowerInvariant();
        return context.Request;
    }

    private sealed class FakeDistributedNonceStore : IInternalNonceStore
    {
        private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
        public bool Available { get; init; } = true;
        public int Claims { get; private set; }

        public Task<InternalNonceClaimResult> TryClaimAsync(
            string audience,
            string nonce,
            TimeSpan retention,
            CancellationToken cancellationToken)
        {
            Claims++;
            if (!Available) return Task.FromResult(InternalNonceClaimResult.Unavailable);
            return Task.FromResult(_keys.Add($"{audience}:{nonce}")
                ? InternalNonceClaimResult.Claimed
                : InternalNonceClaimResult.Duplicate);
        }

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(Available);
    }
}
