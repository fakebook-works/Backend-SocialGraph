namespace SocialGraph.Api.Tests;

using Moq;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Infrastructure;
using SocialGraph.Api.Service;
using SocialGraph.Api.SubGraphQL;

public sealed class AdvancedMutationContractTests
{
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
    public async Task InviteGroupUser_RequiresTrustedAdministrator()
    {
        const long adminId = 100;
        const long groupId = 200;
        const long userId = 300;
        var groups = new Mock<IGroupGraphService>(MockBehavior.Strict);
        groups.Setup(item => item.IsAdminAsync(adminId, groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        groups.Setup(item => item.InviteUserAsync(adminId, groupId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(adminId);

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
}
