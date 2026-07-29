namespace SocialGraph.Api.Tests;

using HotChocolate;
using Moq;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Infrastructure;
using SocialGraph.Api.Service;
using SocialGraph.Api.SubGraphQL;

public sealed class ProfileTargetReadSecurityTests
{
    private const long ViewerId = 401;
    private const long TargetUserId = 402;

    [Fact]
    public async Task ProfileFriends_UsesTrustedViewerAndTargetOnlyAsResourceIdentifier()
    {
        var users = new Mock<IUserGraphService>(MockBehavior.Strict);
        users.Setup(item => item.GetProfileAsync(TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Profile(TargetUserId));
        users.Setup(item => item.GetProfileFriendsForViewerAsync(
                TargetUserId,
                ViewerId,
                25,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FriendProfileWithMutualCountResult>());
        var block = new Mock<IBlockVisibilityService>(MockBehavior.Strict);
        block.Setup(item => item.IsBlockedEitherDirectionAsync(ViewerId, TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(ViewerId);

        var result = await new Query().GetProfileFriendsAsync(
            TargetUserId,
            25,
            users.Object,
            block.Object,
            trusted.Object,
            CancellationToken.None);

        Assert.Empty(result);
        users.VerifyAll();
        block.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task ProfileFriends_ReturnsNoDataWhenEitherBlockDirectionApplies()
    {
        var users = new Mock<IUserGraphService>(MockBehavior.Strict);
        var block = new Mock<IBlockVisibilityService>(MockBehavior.Strict);
        block.Setup(item => item.IsBlockedEitherDirectionAsync(ViewerId, TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(ViewerId);

        var result = await new Query().GetProfileFriendsAsync(
            TargetUserId,
            25,
            users.Object,
            block.Object,
            trusted.Object,
            CancellationToken.None);

        Assert.Empty(result);
        users.Verify(item => item.GetProfileFriendsForViewerAsync(
            It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProfileContact_ReturnsOnlyTheTargetEmailAfterVisibilityChecks()
    {
        var users = new Mock<IUserGraphService>(MockBehavior.Strict);
        users.Setup(item => item.GetProfileAsync(TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Profile(TargetUserId));
        var contacts = new Mock<IAuthenticationContactClient>(MockBehavior.Strict);
        contacts.Setup(item => item.GetEmailAsync(TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("target@example.com");
        var block = new Mock<IBlockVisibilityService>(MockBehavior.Strict);
        block.Setup(item => item.IsBlockedEitherDirectionAsync(ViewerId, TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(ViewerId);

        var result = await new Query().GetProfileContactAsync(
            TargetUserId,
            users.Object,
            contacts.Object,
            block.Object,
            trusted.Object,
            CancellationToken.None);

        Assert.Equal("target@example.com", result?.Email);
        users.VerifyAll();
        contacts.VerifyAll();
        block.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task ProfileContact_DoesNotCallAuthenticationWhenEitherBlockDirectionApplies()
    {
        var users = new Mock<IUserGraphService>(MockBehavior.Strict);
        var contacts = new Mock<IAuthenticationContactClient>(MockBehavior.Strict);
        var block = new Mock<IBlockVisibilityService>(MockBehavior.Strict);
        block.Setup(item => item.IsBlockedEitherDirectionAsync(ViewerId, TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(ViewerId);

        var result = await new Query().GetProfileContactAsync(
            TargetUserId,
            users.Object,
            contacts.Object,
            block.Object,
            trusted.Object,
            CancellationToken.None);

        Assert.Null(result);
        contacts.Verify(item => item.GetEmailAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        users.Verify(item => item.GetProfileAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        block.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task ProfileContact_DoesNotCallAuthenticationWhenCanonicalProfileIsMissing()
    {
        var users = new Mock<IUserGraphService>(MockBehavior.Strict);
        users.Setup(item => item.GetProfileAsync(TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserProfileResult?)null);
        var contacts = new Mock<IAuthenticationContactClient>(MockBehavior.Strict);
        var block = new Mock<IBlockVisibilityService>(MockBehavior.Strict);
        block.Setup(item => item.IsBlockedEitherDirectionAsync(ViewerId, TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(ViewerId);

        var result = await new Query().GetProfileContactAsync(
            TargetUserId,
            users.Object,
            contacts.Object,
            block.Object,
            trusted.Object,
            CancellationToken.None);

        Assert.Null(result);
        contacts.Verify(item => item.GetEmailAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        users.VerifyAll();
        block.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task ProfileAvatarSource_ReturnsOnlyAVisibleSourceOwnedByTheProfile()
    {
        const long contentId = 9_000_000_000_000_111;
        const long mediaId = 9_000_000_000_000_112;
        var users = new Mock<IUserGraphService>(MockBehavior.Strict);
        users.Setup(item => item.GetAvatarSourceAsync(TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileAvatarSourceResult(contentId, mediaId));
        var content = new Mock<IContentGraphService>(MockBehavior.Strict);
        content.Setup(item => item.GetPostDetailAsync(ViewerId, contentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FeedPostDetailResult(
                contentId,
                GraphObjectType.FeedPost,
                "avatar",
                0,
                "2026-01-01T00:00:00Z",
                new PostAuthorResult(TargetUserId, "Target", string.Empty, false, false),
                new[] { new MediaResult(mediaId, GraphMediaType.Photo, "/media/avatar.jpg") }));
        var block = new Mock<IBlockVisibilityService>(MockBehavior.Strict);
        block.Setup(item => item.IsBlockedEitherDirectionAsync(ViewerId, TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(ViewerId);

        var result = await new Query().GetProfileAvatarSourceAsync(
            TargetUserId,
            users.Object,
            content.Object,
            block.Object,
            trusted.Object,
            CancellationToken.None);

        Assert.Equal(contentId, result?.ContentId);
        Assert.Equal(mediaId, result?.MediaId);
        users.VerifyAll();
        content.VerifyAll();
        block.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task ProfileAvatarSource_FailsClosedWhenBlockedOrSourceIsNotVisible()
    {
        const long contentId = 701;
        const long mediaId = 702;
        var blockedUsers = new Mock<IUserGraphService>(MockBehavior.Strict);
        var blockedContent = new Mock<IContentGraphService>(MockBehavior.Strict);
        var blockedVisibility = new Mock<IBlockVisibilityService>(MockBehavior.Strict);
        blockedVisibility.Setup(item => item.IsBlockedEitherDirectionAsync(ViewerId, TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(ViewerId);

        var blockedResult = await new Query().GetProfileAvatarSourceAsync(
            TargetUserId,
            blockedUsers.Object,
            blockedContent.Object,
            blockedVisibility.Object,
            trusted.Object,
            CancellationToken.None);
        Assert.Null(blockedResult);
        blockedUsers.Verify(item => item.GetAvatarSourceAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        blockedContent.Verify(item => item.GetPostDetailAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);

        var users = new Mock<IUserGraphService>(MockBehavior.Strict);
        users.Setup(item => item.GetAvatarSourceAsync(TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileAvatarSourceResult(contentId, mediaId));
        var content = new Mock<IContentGraphService>(MockBehavior.Strict);
        content.Setup(item => item.GetPostDetailAsync(ViewerId, contentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IHomePostResult?)null);
        var visible = new Mock<IBlockVisibilityService>(MockBehavior.Strict);
        visible.Setup(item => item.IsBlockedEitherDirectionAsync(ViewerId, TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var hiddenResult = await new Query().GetProfileAvatarSourceAsync(
            TargetUserId,
            users.Object,
            content.Object,
            visible.Object,
            trusted.Object,
            CancellationToken.None);
        Assert.Null(hiddenResult);
    }

    [Fact]
    public async Task ProfileTargetReads_RejectAnUntrustedCallerBeforeReadingData()
    {
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Throws(new GraphQLException("untrusted"));

        await Assert.ThrowsAsync<GraphQLException>(() => new Query().GetProfileFriendsAsync(
            TargetUserId,
            25,
            Mock.Of<IUserGraphService>(),
            Mock.Of<IBlockVisibilityService>(),
            trusted.Object,
            CancellationToken.None));
    }

    private static UserProfileResult Profile(long id) => new(
        id,
        string.Empty,
        string.Empty,
        "Target",
        string.Empty,
        1,
        "2000-01-01",
        string.Empty,
        0,
        "2026-01-01T00:00:00Z",
        null,
        false,
        0,
        0,
        0);
}
