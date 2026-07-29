namespace SocialGraph.Api.Tests;

using HotChocolate;
using Moq;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Infrastructure;
using SocialGraph.Api.Service;
using SocialGraph.Api.SubGraphQL;

public sealed class ProfilePostsQueryTests
{
    private const long ViewerId = 401;
    private const long TargetUserId = 402;

    [Fact]
    public async Task ProfilePosts_ReturnsVisibleFeedPostsAndReelsInAuthoredOrder()
    {
        const long feedId = 1_001;
        const long reelId = 1_002;
        const long hiddenId = 1_003;
        var associations = new Mock<IAssociationService>(MockBehavior.Strict);
        associations.Setup(item => item.RetrieveAssociationAsync(
                ViewerId,
                GraphAssociationType.Authored,
                null,
                20,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(
                [
                    new AssociationEdgeResult(feedId, 3),
                    new AssociationEdgeResult(reelId, 2),
                    new AssociationEdgeResult(hiddenId, 1),
                ],
                null));
        var content = new Mock<IContentGraphService>(MockBehavior.Strict);
        content.Setup(item => item.GetPostDetailsAsync(
                ViewerId,
                It.Is<IReadOnlyList<long>>(ids => ids.SequenceEqual(new[] { feedId, reelId, hiddenId })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new FeedPostDetailResult(
                    feedId,
                    GraphObjectType.FeedPost,
                    "feed",
                    2,
                    "2026-07-15T12:00:00Z",
                    new PostAuthorResult(ViewerId, "Owner", string.Empty, false, false),
                    Array.Empty<MediaResult>()),
                new ReelDetailResult(
                    reelId,
                    GraphObjectType.Reel,
                    "reel",
                    1,
                    "2026-07-12T12:00:00Z",
                    9d / 16d,
                    0.5d,
                    0.5d,
                    new PostAuthorResult(ViewerId, "Owner", string.Empty, false, false),
                    Array.Empty<MediaResult>()),
            ]);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(ViewerId);

        var result = await new Query().GetProfilePostsAsync(
            ViewerId,
            5,
            null,
            content.Object,
            associations.Object,
            trusted.Object,
            CancellationToken.None);

        Assert.Collection(
            result.Items,
            item => Assert.IsType<FeedPostDetailResult>(item),
            item => Assert.IsType<ReelDetailResult>(item));
        Assert.False(result.HasNextPage);
        Assert.Null(result.EndCursor);
        associations.VerifyAll();
        content.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task ProfilePosts_BlockedTargetReturnsNoContentBeforeHydration()
    {
        var associations = new Mock<IAssociationService>(MockBehavior.Strict);
        associations.Setup(item => item.HasAssociationAsync(
                ViewerId,
                GraphAssociationType.Blocked,
                TargetUserId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var content = new Mock<IContentGraphService>(MockBehavior.Strict);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(ViewerId);

        var result = await new Query().GetProfilePostsAsync(
            TargetUserId,
            25,
            null,
            content.Object,
            associations.Object,
            trusted.Object,
            CancellationToken.None);

        Assert.Empty(result.Items);
        content.Verify(item => item.GetPostDetailsAsync(
            It.IsAny<long>(), It.IsAny<IReadOnlyList<long>>(), It.IsAny<CancellationToken>()), Times.Never);
        associations.Verify(item => item.RetrieveAssociationAsync(
            It.IsAny<long>(), It.IsAny<short>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        associations.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task ProfilePosts_RejectsAnUntrustedCallerBeforeReadingTheGraph()
    {
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Throws(new GraphQLException("untrusted"));
        var content = new Mock<IContentGraphService>(MockBehavior.Strict);
        var associations = new Mock<IAssociationService>(MockBehavior.Strict);

        await Assert.ThrowsAsync<GraphQLException>(() => new Query().GetProfilePostsAsync(
            TargetUserId,
            25,
            null,
            content.Object,
            associations.Object,
            trusted.Object,
            CancellationToken.None));

        content.VerifyNoOtherCalls();
        associations.VerifyNoOtherCalls();
    }
}
