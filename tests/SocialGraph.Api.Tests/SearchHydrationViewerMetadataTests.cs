namespace SocialGraph.Api.Tests;

using Moq;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Infrastructure;
using SocialGraph.Api.Service;
using SocialGraph.Api.SubGraphQL;

public sealed class SearchHydrationViewerMetadataTests
{
    private const long ViewerId = 42;
    private const long FriendId = 84;
    private const long OtherUserId = 126;

    [Fact]
    public async Task UserSearchHydration_DerivesSelfAndFriendFromTrustedViewer()
    {
        var users = new Mock<IUserGraphService>();
        users.Setup(item => item.GetProfileAsync(ViewerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Profile(ViewerId));
        users.Setup(item => item.GetProfileAsync(FriendId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Profile(FriendId));
        users.Setup(item => item.GetProfileAsync(OtherUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Profile(OtherUserId));
        var associations = new Mock<IAssociationService>();
        associations.Setup(item => item.HasAssociationAsync(
                ViewerId,
                GraphAssociationType.Friend,
                FriendId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        associations.Setup(item => item.HasAssociationAsync(
                ViewerId,
                GraphAssociationType.Followed,
                OtherUserId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var trustedCaller = TrustedCaller();
        var query = new Query();

        var self = await query.GetUserSearchResultAsync(
            ViewerId,
            users.Object,
            associations.Object,
            trustedCaller.Object,
            CancellationToken.None);
        var friend = await query.GetUserSearchResultAsync(
            FriendId,
            users.Object,
            associations.Object,
            trustedCaller.Object,
            CancellationToken.None);
        var other = await query.GetUserSearchResultAsync(
            OtherUserId,
            users.Object,
            associations.Object,
            trustedCaller.Object,
            CancellationToken.None);

        Assert.NotNull(self);
        Assert.True(self.ViewerIsSelf);
        Assert.False(self.ViewerIsFriend);
        Assert.False(self.ViewerIsFollowing);
        Assert.NotNull(friend);
        Assert.False(friend.ViewerIsSelf);
        Assert.True(friend.ViewerIsFriend);
        Assert.False(friend.ViewerIsFollowing);
        Assert.NotNull(other);
        Assert.False(other.ViewerIsSelf);
        Assert.False(other.ViewerIsFriend);
        Assert.True(other.ViewerIsFollowing);
        trustedCaller.Verify(item => item.RequireUserId(), Times.Exactly(3));
        associations.Verify(item => item.HasAssociationAsync(
            ViewerId,
            GraphAssociationType.Friend,
            ViewerId,
            It.IsAny<CancellationToken>()), Times.Never);
        associations.Verify(item => item.HasAssociationAsync(
            ViewerId,
            GraphAssociationType.Followed,
            ViewerId,
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public async Task GroupSearchHydration_DerivesMembershipFromMemberOrAdmin(
        bool isMember,
        bool isAdmin,
        bool expected)
    {
        const long groupId = 700;
        var groups = new Mock<IGroupGraphService>();
        groups.Setup(item => item.GetGroupAsync(groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Group(groupId));
        var associations = new Mock<IAssociationService>();
        associations.Setup(item => item.HasAssociationAsync(
                ViewerId,
                GraphAssociationType.Member,
                groupId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(isMember);
        associations.Setup(item => item.HasAssociationAsync(
                ViewerId,
                GraphAssociationType.Admin,
                groupId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(isAdmin);
        var trustedCaller = TrustedCaller();

        var result = await new Query().GetGroupSearchResultAsync(
            groupId,
            groups.Object,
            associations.Object,
            trustedCaller.Object,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(expected, result.ViewerIsMember);
        trustedCaller.Verify(item => item.RequireUserId(), Times.Once);
    }

    [Fact]
    public async Task SearchHydration_StopsBeforeReadingDataWhenTrustedCallerIsRejected()
    {
        var trustedCaller = new Mock<ITrustedCallerAccessor>();
        trustedCaller.Setup(item => item.RequireUserId())
            .Throws(new InvalidOperationException("untrusted"));
        var users = new Mock<IUserGraphService>(MockBehavior.Strict);
        var groups = new Mock<IGroupGraphService>(MockBehavior.Strict);
        var associations = new Mock<IAssociationService>(MockBehavior.Strict);
        var query = new Query();

        await Assert.ThrowsAsync<InvalidOperationException>(() => query.GetUserSearchResultAsync(
            FriendId,
            users.Object,
            associations.Object,
            trustedCaller.Object,
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => query.GetGroupSearchResultAsync(
            700,
            groups.Object,
            associations.Object,
            trustedCaller.Object,
            CancellationToken.None));
    }

    private static Mock<ITrustedCallerAccessor> TrustedCaller()
    {
        var trustedCaller = new Mock<ITrustedCallerAccessor>();
        trustedCaller.Setup(item => item.RequireUserId()).Returns(ViewerId);
        return trustedCaller;
    }

    private static UserProfileResult Profile(long id) => new(
        id,
        $"https://cdn.example/{id}.jpg",
        string.Empty,
        $"User {id}",
        string.Empty,
        1,
        "2000-01-01",
        "Ha Noi",
        0,
        "2026-01-01T00:00:00Z",
        null,
        false,
        0,
        0,
        0);

    private static GroupResult Group(long id) => new(
        id,
        string.Empty,
        string.Empty,
        $"Group {id}",
        string.Empty,
        0,
        "2026-01-01T00:00:00Z",
        0,
        0);
}
