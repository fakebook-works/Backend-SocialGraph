namespace SocialGraph.Api.Tests;

using HotChocolate;
using Moq;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Infrastructure;
using SocialGraph.Api.Service;
using SocialGraph.Api.SubGraphQL;

public sealed class AdvancedMutationContractTests
{
    [Fact]
    public async Task SharePostToGroup_UsesTrustedActorAndRequiresDestinationMembership()
    {
        const long actorId = 100;
        const long spoofedAuthorId = 101;
        const long sourceId = 200;
        const long groupId = 300;
        var content = new Mock<IContentGraphService>(MockBehavior.Strict);
        content.Setup(item => item.ResolveCanonicalShareSourceIdAsync(sourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sourceId);
        var reads = new Mock<ISocialReadModelService>(MockBehavior.Strict);
        reads.Setup(item => item.CanShareTargetAsync(actorId, sourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var groups = new Mock<IGroupGraphService>(MockBehavior.Strict);
        groups.Setup(item => item.IsParticipantAsync(actorId, groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(actorId);

        var exception = await Assert.ThrowsAsync<GraphQLException>(() => new Mutation().SharePostAsync(
            new SharePostInput(spoofedAuthorId, sourceId, "share", 0, groupId),
            content.Object,
            reads.Object,
            groups.Object,
            trusted.Object,
            CancellationToken.None));

        Assert.Equal("FORBIDDEN", exception.Errors.Single().Code);
        content.Verify(item => item.SharePostAsync(It.IsAny<SharePostInput>(), It.IsAny<CancellationToken>()), Times.Never);
        content.VerifyAll();
        reads.VerifyAll();
        groups.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task SharePostToGroup_ForwardsOnlyTrustedActorAndCanonicalSource()
    {
        const long actorId = 100;
        const long sourceId = 200;
        const long canonicalSourceId = 201;
        const long groupId = 300;
        var expected = new ContentResult(400, GraphObjectType.GroupPost, "share", 0, "now", actorId, Array.Empty<MediaResult>());
        var content = new Mock<IContentGraphService>(MockBehavior.Strict);
        content.Setup(item => item.ResolveCanonicalShareSourceIdAsync(sourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canonicalSourceId);
        content.Setup(item => item.SharePostAsync(
                It.Is<SharePostInput>(input => input.AuthorId == actorId && input.SourceId == canonicalSourceId && input.DestinationGroupId == groupId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var reads = new Mock<ISocialReadModelService>(MockBehavior.Strict);
        reads.Setup(item => item.CanShareTargetAsync(actorId, canonicalSourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var groups = new Mock<IGroupGraphService>(MockBehavior.Strict);
        groups.Setup(item => item.IsParticipantAsync(actorId, groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(actorId);

        var result = await new Mutation().SharePostAsync(
            new SharePostInput(999, sourceId, "share", 0, groupId),
            content.Object,
            reads.Object,
            groups.Object,
            trusted.Object,
            CancellationToken.None);

        Assert.Same(expected, result);
        content.VerifyAll();
        reads.VerifyAll();
        groups.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task DeleteContent_AllowsGroupAdministratorThroughTheCanonicalPolicy()
    {
        const long adminId = 100;
        const long postId = 200;
        var content = new Mock<IContentGraphService>(MockBehavior.Strict);
        content.Setup(item => item.CanDeleteContentAsync(adminId, postId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        content.Setup(item => item.DeleteContentAsync(postId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(adminId);

        var deleted = await new Mutation().DeleteContentAsync(
            postId,
            content.Object,
            trusted.Object,
            CancellationToken.None);

        Assert.True(deleted);
        content.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task DeleteContent_RejectsCallerOutsideTheCanonicalPolicy()
    {
        const long viewerId = 100;
        const long postId = 200;
        var content = new Mock<IContentGraphService>(MockBehavior.Strict);
        content.Setup(item => item.CanDeleteContentAsync(viewerId, postId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(viewerId);

        var exception = await Assert.ThrowsAsync<GraphQLException>(() => new Mutation().DeleteContentAsync(
            postId,
            content.Object,
            trusted.Object,
            CancellationToken.None));

        Assert.Equal("FORBIDDEN", exception.Errors.Single().Code);
        content.Verify(item => item.DeleteContentAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateComment_RejectsANonAuthorBeforeTheDomainWrite()
    {
        const long viewerId = 100;
        const long commentId = 200;
        var content = new Mock<IContentGraphService>(MockBehavior.Strict);
        content.Setup(item => item.IsAuthorAsync(viewerId, commentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(viewerId);

        var exception = await Assert.ThrowsAsync<GraphQLException>(() => new Mutation().UpdateCommentAsync(
            new UpdateCommentInput(commentId, "spoofed edit"),
            content.Object,
            trusted.Object,
            CancellationToken.None));

        Assert.Equal("FORBIDDEN", exception.Errors.Single().Code);
        content.Verify(item => item.UpdateCommentAsync(It.IsAny<UpdateCommentInput>(), It.IsAny<CancellationToken>()), Times.Never);
        content.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task UpdateComment_UsesTheTrustedCallerAndForwardsOnlyAfterOwnershipPasses()
    {
        const long viewerId = 100;
        const long commentId = 200;
        var input = new UpdateCommentInput(commentId, "edited");
        var expected = new ContentResult(commentId, GraphObjectType.Comment, "edited", 0, "now", viewerId, []);
        var content = new Mock<IContentGraphService>(MockBehavior.Strict);
        content.Setup(item => item.IsAuthorAsync(viewerId, commentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        content.Setup(item => item.UpdateCommentAsync(input, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(viewerId);

        var result = await new Mutation().UpdateCommentAsync(input, content.Object, trusted.Object, CancellationToken.None);

        Assert.Same(expected, result);
        content.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task ChangeUserAvatar_UsesTrustedOwnerAndForwardsExactSourcePair()
    {
        const long userId = 100;
        const long contentId = 9_000_000_000_000_121;
        const long mediaId = 9_000_000_000_000_122;
        var users = new Mock<IUserGraphService>(MockBehavior.Strict);
        users.Setup(item => item.ChangeUserAvatarAsync(
                userId,
                "/media/cropped.jpg",
                null,
                0,
                contentId,
                mediaId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserProfileResult?)null);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId(userId)).Returns(userId);

        await new Mutation().ChangeUserAvatarAsync(
            userId,
            "/media/cropped.jpg",
            null,
            0,
            contentId,
            mediaId,
            users.Object,
            trusted.Object,
            CancellationToken.None);

        users.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task RemoveUserAvatar_UsesTrustedOwnerAndEmptyUrlSemantics()
    {
        const long userId = 100;
        var users = new Mock<IUserGraphService>(MockBehavior.Strict);
        users.Setup(item => item.ChangeUserAvatarAsync(userId, string.Empty, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserProfileResult?)null);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId(userId)).Returns(userId);

        await new Mutation().RemoveUserAvatarAsync(userId, users.Object, trusted.Object, CancellationToken.None);

        users.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task InviteGroupUser_RequiresTrustedCurrentParticipant()
    {
        const long inviterId = 100;
        const long groupId = 200;
        const long userId = 300;
        var groups = new Mock<IGroupGraphService>(MockBehavior.Strict);
        groups.Setup(item => item.IsParticipantAsync(inviterId, groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        groups.Setup(item => item.InviteUserAsync(inviterId, groupId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(inviterId);

        var result = await new Mutation().InviteGroupUserAsync(
            groupId,
            userId,
            groups.Object,
            trusted.Object,
            CancellationToken.None);

        Assert.True(result);
        groups.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task InviteGroupUser_RejectsAuthenticatedNonParticipantBeforeDispatch()
    {
        const long outsiderId = 101;
        const long groupId = 200;
        const long userId = 300;
        var groups = new Mock<IGroupGraphService>(MockBehavior.Strict);
        groups.Setup(item => item.IsParticipantAsync(outsiderId, groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(outsiderId);

        await Assert.ThrowsAsync<GraphQLException>(() => new Mutation().InviteGroupUserAsync(
            groupId,
            userId,
            groups.Object,
            trusted.Object,
            CancellationToken.None));

        groups.Verify(item => item.InviteUserAsync(
            It.IsAny<long>(),
            It.IsAny<long>(),
            It.IsAny<long>(),
            It.IsAny<CancellationToken>()), Times.Never);
        groups.VerifyAll();
        trusted.VerifyAll();
    }
}
