namespace SocialGraph.Api.Tests;

using GreenDonut;
using Moq;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Infrastructure;
using SocialGraph.Api.Service;
using SocialGraph.Api.SubGraphQL;

public sealed class FederatedUserByIdDataLoaderTests
{
    [Fact]
    public async Task BlockedReferencesResolveToNullWithoutFailingTheBatch()
    {
        var users = new Mock<IUserGraphService>(MockBehavior.Strict);
        users
            .Setup(item => item.GetProfilesForViewerAsync(
                42,
                It.Is<IReadOnlyCollection<long>>(ids => ids.SequenceEqual(new long[] { 100, 200 })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new UserProfileResult(
                    100,
                    "",
                    "",
                    "Visible user",
                    "",
                    0,
                    "",
                    "",
                    0,
                    "",
                    null,
                    false,
                    0,
                    0,
                    0)
            });
        var trustedCaller = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trustedCaller.Setup(item => item.RequireUserId()).Returns(42);

        var loader = new FederatedUserByIdDataLoader(
            users.Object,
            trustedCaller.Object,
            AutoBatchScheduler.Default,
            new DataLoaderOptions());

        var values = await loader.LoadAsync(new long[] { 100, 200 }, CancellationToken.None);

        Assert.NotNull(values[0]);
        Assert.Equal("Visible user", values[0]!.Name);
        Assert.Null(values[1]);
        users.VerifyAll();
        trustedCaller.VerifyAll();
    }
}
