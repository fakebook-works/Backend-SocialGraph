namespace SocialGraph.Api.Tests;

using System.Text.Json.Nodes;
using HotChocolate;
using Microsoft.EntityFrameworkCore;
using Moq;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;
using SocialGraph.Api.Infrastructure;
using SocialGraph.Api.Service;
using SocialGraph.Api.SubGraphQL;

public sealed class GroupShortcutServiceTests
{
    private const long UserId = 100;

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task CreateGroup_RejectsPrivacyOutsidePublicAndPrivate(int privacy)
    {
        await using var context = CreateContext();
        var objects = new Mock<IObjectService>(MockBehavior.Strict);
        var service = CreateService(context, objects);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.CreateGroupAsync(
            new CreateGroupInput(UserId, "Invalid privacy", null, privacy)));

        objects.Verify(item => item.AddObjectAsync(
            It.IsAny<short>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task UpdateGroup_RejectsPrivacyOutsidePublicAndPrivate(int privacy)
    {
        await using var context = CreateContext();
        var objects = new Mock<IObjectService>(MockBehavior.Strict);
        var service = CreateService(context, objects);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.UpdateGroupAsync(
            UserId,
            new UpdateGroupInput(300, null, null, null, null, privacy)));

        objects.Verify(item => item.UpdateObjectAsync(
            It.IsAny<long>(),
            It.IsAny<short>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task VisitedGroups_UsesStableKeysetCursorAcrossPages()
    {
        await using var context = CreateContext();
        context.ObjectsTb.AddRange(
            Group(301, "Newest"),
            Group(302, "Same timestamp, larger id"),
            Group(303, "Same timestamp, smaller id"),
            Group(304, "Oldest"));
        context.AssociationsTb.AddRange(
            Edge(UserId, GraphAssociationType.Visited, 301, 3_000),
            Edge(UserId, GraphAssociationType.Visited, 302, 2_000),
            Edge(UserId, GraphAssociationType.Visited, 303, 2_000),
            Edge(UserId, GraphAssociationType.Visited, 304, 1_000));
        await context.SaveChangesAsync();
        var service = CreateService(context);

        var first = await service.GetVisitedGroupsAsync(UserId, 2, null);
        var second = await service.GetVisitedGroupsAsync(UserId, 2, first.EndCursor);

        Assert.Equal(new long[] { 301, 303 }, first.Items.Select(item => item.Id));
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(3_000).UtcDateTime,
            DateTimeOffset.Parse(first.Items[0].VisitedAt).UtcDateTime);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(2_000).UtcDateTime,
            DateTimeOffset.Parse(first.Items[1].VisitedAt).UtcDateTime);
        Assert.True(first.HasNextPage);
        Assert.False(string.IsNullOrWhiteSpace(first.EndCursor));
        Assert.Equal(new long[] { 302, 304 }, second.Items.Select(item => item.Id));
        Assert.False(second.HasNextPage);
        Assert.Empty(first.Items.Select(item => item.Id).Intersect(second.Items.Select(item => item.Id)));
    }

    [Fact]
    public async Task VisitedGroups_HidesPrivateGroupUnlessViewerIsMember()
    {
        await using var context = CreateContext();
        context.ObjectsTb.AddRange(
            Group(310, "Public", privacy: 0),
            Group(311, "Private hidden", privacy: 1),
            Group(312, "Private member", privacy: 1));
        context.AssociationsTb.AddRange(
            Edge(UserId, GraphAssociationType.Visited, 310, 3_000),
            Edge(UserId, GraphAssociationType.Visited, 311, 2_000),
            Edge(UserId, GraphAssociationType.Visited, 312, 1_000),
            Edge(UserId, GraphAssociationType.Member, 312, 4_000));
        await context.SaveChangesAsync();
        var service = CreateService(context);

        var page = await service.GetVisitedGroupsAsync(UserId, 10, null);

        Assert.Equal(new long[] { 310, 312 }, page.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task GroupSuggestions_RanksGroupsJoinedByFriendsAndIncludesPrivateMetadata()
    {
        await using var context = CreateContext();
        const long firstFriendId = 101;
        const long secondFriendId = 102;
        const long blockedFriendId = 103;
        const long thirdFriendId = 104;
        const long fourthFriendId = 105;
        var now = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
        var yesterdayStart = new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);
        context.ObjectsTb.AddRange(
            User(firstFriendId, "An"),
            User(secondFriendId, "Binh"),
            User(blockedFriendId, "Blocked"),
            User(thirdFriendId, "Chi"),
            User(fourthFriendId, "Dung"),
            Group(350, "Public suggestion", privacy: 0),
            Group(351, "Private suggestion", privacy: 1),
            Group(352, "Already joined", privacy: 0),
            Group(353, "Pending request", privacy: 1),
            Group(354, "Blocked source", privacy: 1),
            GroupPost(500),
            GroupPost(501),
            GroupPost(502),
            GroupPost(503));
        context.AssociationsTb.AddRange(
            Edge(UserId, GraphAssociationType.Friend, firstFriendId, 10_000),
            Edge(UserId, GraphAssociationType.Friend, secondFriendId, 9_000),
            Edge(UserId, GraphAssociationType.Friend, thirdFriendId, 8_900),
            Edge(UserId, GraphAssociationType.Friend, fourthFriendId, 8_800),
            // A stale Friend edge must never let a blocked account influence suggestions.
            Edge(UserId, GraphAssociationType.Friend, blockedFriendId, 8_000),
            Edge(UserId, GraphAssociationType.Blocked, blockedFriendId, 8_100),
            Edge(firstFriendId, GraphAssociationType.Member, 350, 7_000),
            Edge(firstFriendId, GraphAssociationType.Member, 351, 7_100),
            Edge(firstFriendId, GraphAssociationType.Admin, 351, 7_200),
            Edge(secondFriendId, GraphAssociationType.Member, 351, 7_300),
            Edge(thirdFriendId, GraphAssociationType.Member, 351, 7_250),
            Edge(fourthFriendId, GraphAssociationType.Member, 351, 7_225),
            Edge(firstFriendId, GraphAssociationType.Member, 352, 7_400),
            Edge(firstFriendId, GraphAssociationType.Member, 353, 7_500),
            Edge(blockedFriendId, GraphAssociationType.Member, 354, 7_600),
            Edge(UserId, GraphAssociationType.Member, 352, 7_700),
            Edge(UserId, GraphAssociationType.GroupJoinRequest, 353, 7_800),
            Edge(350, GraphAssociationType.HaveMember, firstFriendId, 7_000),
            Edge(351, GraphAssociationType.HaveMember, firstFriendId, 7_100),
            Edge(351, GraphAssociationType.HaveMember, secondFriendId, 7_300),
            Edge(351, GraphAssociationType.HaveMember, thirdFriendId, 7_250),
            Edge(351, GraphAssociationType.HaveMember, fourthFriendId, 7_225),
            Edge(351, GraphAssociationType.HaveAdmin, firstFriendId, 7_200),
            Edge(351, GraphAssociationType.Published, 500, yesterdayStart.ToUnixTimeMilliseconds()),
            Edge(351, GraphAssociationType.Published, 501, yesterdayStart.AddDays(1).AddMilliseconds(-1).ToUnixTimeMilliseconds()),
            Edge(351, GraphAssociationType.Published, 502, yesterdayStart.AddMilliseconds(-1).ToUnixTimeMilliseconds()),
            Edge(351, GraphAssociationType.Published, 503, yesterdayStart.AddDays(1).ToUnixTimeMilliseconds()));
        await context.SaveChangesAsync();
        var service = CreateService(context, timeProvider: new FixedTimeProvider(now));

        var suggestions = await service.GetGroupSuggestionsAsync(UserId, 10);

        Assert.Equal(new long[] { 351, 350 }, suggestions.Select(item => item.Group.Id));
        var privateSuggestion = suggestions[0];
        Assert.Equal(1, privateSuggestion.Group.Privacy);
        Assert.Equal(4, privateSuggestion.Group.MemberCount);
        Assert.Equal(1, privateSuggestion.Group.AdminCount);
        Assert.Equal(4, privateSuggestion.FriendMemberCount);
        Assert.Equal(3, privateSuggestion.FriendMembers.Count);
        Assert.Equal(3, privateSuggestion.FriendMembers.Select(item => item.Id).Distinct().Count());
        Assert.All(privateSuggestion.FriendMembers, friend => Assert.False(string.IsNullOrWhiteSpace(friend.Name)));
        Assert.Equal(2, privateSuggestion.YesterdayPostCount);
        Assert.Equal(0, suggestions[1].Group.Privacy);
        Assert.DoesNotContain(suggestions, item => item.Group.Id is 352 or 353 or 354);
    }

    [Fact]
    public async Task GroupSuggestions_DerivesViewerFromTrustedCaller()
    {
        var groups = new Mock<IGroupGraphService>(MockBehavior.Strict);
        groups.Setup(item => item.GetGroupSuggestionsAsync(
                UserId,
                12,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GroupSuggestionResult>());
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(UserId);

        var result = await new Query().GetGroupSuggestionsAsync(
            12,
            groups.Object,
            trusted.Object,
            CancellationToken.None);

        Assert.Empty(result);
        groups.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task GroupSuggestions_RejectsUntrustedCallerBeforeReadingGroups()
    {
        var groups = new Mock<IGroupGraphService>(MockBehavior.Strict);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Throws(new GraphQLException("untrusted"));

        await Assert.ThrowsAsync<GraphQLException>(() => new Query().GetGroupSuggestionsAsync(
            12,
            groups.Object,
            trusted.Object,
            CancellationToken.None));

        groups.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GroupJoinRequests_UsesTrustedAdminAndTypedBoundedReadModel()
    {
        const long groupId = 355;
        var expected = new UserSummaryPageResult(
            new[] { new UserSummaryResult(101, "Pending", "", false) },
            null,
            false);
        var groups = new Mock<IGroupGraphService>(MockBehavior.Strict);
        groups.Setup(item => item.IsAdminAsync(UserId, groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var reads = new Mock<ISocialReadModelService>(MockBehavior.Strict);
        reads.Setup(item => item.GetGroupJoinRequestsAsync(
                UserId,
                groupId,
                null,
                50,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(UserId);

        var result = await new Query().GetGroupJoinRequestsAsync(
            groupId,
            null,
            500,
            reads.Object,
            groups.Object,
            trusted.Object,
            CancellationToken.None);

        Assert.Same(expected, result);
        reads.VerifyAll();
        groups.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task GroupJoinRequests_RejectsAuthenticatedNonAdminBeforeReadingEdges()
    {
        const long groupId = 356;
        var groups = new Mock<IGroupGraphService>(MockBehavior.Strict);
        groups.Setup(item => item.IsAdminAsync(UserId, groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var reads = new Mock<ISocialReadModelService>(MockBehavior.Strict);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(UserId);

        var exception = await Assert.ThrowsAsync<GraphQLException>(() => new Query().GetGroupJoinRequestsAsync(
            groupId,
            null,
            50,
            reads.Object,
            groups.Object,
            trusted.Object,
            CancellationToken.None));

        Assert.Equal("FORBIDDEN", exception.Errors.Single().Code);
        reads.VerifyNoOtherCalls();
        groups.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task RecordGroupVisit_UpsertsVisitedAssociationForVisibleGroup()
    {
        await using var context = CreateContext();
        var objects = new Mock<IObjectService>();
        objects
            .Setup(item => item.RetrieveObjectAsync(320, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(320, GraphObjectType.Group, GroupJson("Public", 0)));
        var associations = new Mock<IAssociationService>();
        associations
            .Setup(item => item.AddAssociationAsync(
                UserId,
                GraphAssociationType.Visited,
                320,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var service = CreateService(context, objects, associations);

        var recorded = await service.RecordGroupVisitAsync(UserId, 320);

        Assert.True(recorded);
        associations.VerifyAll();
    }

    [Fact]
    public async Task LeaveGroup_DelegatesToTheAtomicAssociationOperation()
    {
        await using var context = CreateContext();
        const long groupId = 321;
        var associations = new Mock<IAssociationService>(MockBehavior.Strict);
        associations.Setup(item => item.LeaveGroupWithAdminTransferAsync(
                UserId,
                groupId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var service = CreateService(context, associations: associations);

        Assert.True(await service.LeaveGroupAsync(UserId, groupId));
        associations.VerifyAll();
    }

    [Fact]
    public async Task LeaveGroup_PropagatesAnAtomicLeaveRejection()
    {
        await using var context = CreateContext();
        const long groupId = 322;
        var associations = new Mock<IAssociationService>(MockBehavior.Strict);
        associations.Setup(item => item.LeaveGroupWithAdminTransferAsync(
                UserId,
                groupId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var service = CreateService(context, associations: associations);

        Assert.False(await service.LeaveGroupAsync(UserId, groupId));
        associations.VerifyAll();
    }

    [Fact]
    public async Task RemoveGroupMember_DelegatesTrustedAdministratorAndVisitedCleanupToTheAtomicOperation()
    {
        await using var context = CreateContext();
        const long groupId = 324;
        const long targetUserId = 325;
        var associations = new Mock<IAssociationService>(MockBehavior.Strict);
        associations.Setup(item => item.RemoveGroupMemberByAdminAsync(
                UserId,
                targetUserId,
                groupId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var service = CreateService(context, associations: associations);

        Assert.True(await service.RemoveMemberAsync(UserId, groupId, targetUserId));
        associations.VerifyAll();
    }

    [Fact]
    public async Task RemoveGroupMemberMutation_ForwardsOnlyTheTrustedAdministratorActor()
    {
        const long groupId = 326;
        const long targetUserId = 327;
        var groups = new Mock<IGroupGraphService>(MockBehavior.Strict);
        groups.Setup(item => item.IsAdminAsync(UserId, groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        groups.Setup(item => item.RemoveMemberAsync(
                UserId,
                groupId,
                targetUserId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(UserId);

        Assert.True(await new Mutation().RemoveGroupMemberAsync(
            groupId,
            targetUserId,
            groups.Object,
            trusted.Object,
            CancellationToken.None));

        groups.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task LeaveGroup_RejectsAnUntrustedCallerBeforeTouchingTheGraph()
    {
        const long groupId = 323;
        var groups = new Mock<IGroupGraphService>(MockBehavior.Strict);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId(UserId)).Throws(new GraphQLException("untrusted"));

        await Assert.ThrowsAsync<GraphQLException>(() => new Mutation().LeaveGroupAsync(
            UserId,
            groupId,
            groups.Object,
            trusted.Object,
            CancellationToken.None));

        groups.Verify(item => item.LeaveGroupAsync(
            It.IsAny<long>(),
            It.IsAny<long>(),
            It.IsAny<CancellationToken>()), Times.Never);
        trusted.VerifyAll();
    }

    [Fact]
    public async Task PrivateGroupJoin_CreatesPendingEdgeAndNotifiesAdministrators()
    {
        await using var context = CreateContext();
        var objects = new Mock<IObjectService>();
        objects.Setup(item => item.RetrieveObjectAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(UserId, GraphObjectType.User, "{}"));
        objects.Setup(item => item.RetrieveObjectAsync(330, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(330, GraphObjectType.Group, GroupJson("Private", 1)));
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(item => item.AddAssociationAsync(UserId, GraphAssociationType.GroupJoinRequest, 330, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        associations.Setup(item => item.RetrieveAssociationAsync(330, GraphAssociationType.HaveAdmin, null, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(new[] { new AssociationEdgeResult(200, 1) }, null));
        var external = new Mock<IExternalServiceClient>(MockBehavior.Loose);
        var service = CreateService(context, objects, associations, external.Object);

        var result = await service.RequestJoinAsync(UserId, 330);

        Assert.True(result);
        associations.Verify(item => item.AddAssociationAsync(UserId, GraphAssociationType.GroupJoinRequest, 330, It.IsAny<CancellationToken>()), Times.Once);
        external.Verify(item => item.NotifyAsync(UserId, 200, ExternalNotificationAction.GroupJoin, 330, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublicGroupJoin_CreatesPendingEdgeAndWaitsForApproval()
    {
        await using var context = CreateContext();
        var objects = new Mock<IObjectService>();
        objects.Setup(item => item.RetrieveObjectAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(UserId, GraphObjectType.User, "{}"));
        objects.Setup(item => item.RetrieveObjectAsync(331, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(331, GraphObjectType.Group, GroupJson("Public", 0)));
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(item => item.AddAssociationAsync(UserId, GraphAssociationType.GroupJoinRequest, 331, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        associations.Setup(item => item.RetrieveAssociationAsync(331, GraphAssociationType.HaveAdmin, null, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(new[] { new AssociationEdgeResult(200, 1) }, null));
        var external = new Mock<IExternalServiceClient>(MockBehavior.Loose);
        var service = CreateService(context, objects, associations, external.Object);

        var result = await service.RequestJoinAsync(UserId, 331);

        Assert.True(result);
        associations.Verify(item => item.AddAssociationAsync(UserId, GraphAssociationType.GroupJoinRequest, 331, It.IsAny<CancellationToken>()), Times.Once);
        associations.Verify(item => item.ApplyMutationsAsync(It.IsAny<IReadOnlyCollection<AssociationMutation>>(), It.IsAny<CancellationToken>()), Times.Never);
        external.Verify(item => item.NotifyAsync(UserId, 200, ExternalNotificationAction.GroupJoin, 331, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GroupInvite_RequiresCurrentParticipantAndFriendAndQueuesCanonicalNotification()
    {
        await using var context = CreateContext();
        const long inviterId = 200;
        const long groupId = 340;
        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        objects.Setup(item => item.RetrieveObjectAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(UserId, GraphObjectType.User, "{}"));
        objects.Setup(item => item.RetrieveObjectAsync(groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(groupId, GraphObjectType.Group, GroupJson("Invite", 1)));
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(item => item.HasAssociationAsync(inviterId, GraphAssociationType.Member, groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        associations.Setup(item => item.HasAssociationAsync(inviterId, GraphAssociationType.Friend, UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var external = new Mock<IExternalServiceClient>(MockBehavior.Loose);
        var service = CreateService(context, objects, associations, external.Object);

        var invited = await service.InviteUserAsync(inviterId, groupId, UserId);

        Assert.True(invited);
        external.Verify(item => item.NotifyAsync(
            inviterId,
            UserId,
            ExternalNotificationAction.GroupInvite,
            groupId,
            null,
            It.IsAny<CancellationToken>()), Times.Once);

        associations.Setup(item => item.HasAssociationAsync(201, GraphAssociationType.Member, groupId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        associations.Setup(item => item.HasAssociationAsync(201, GraphAssociationType.Admin, groupId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        Assert.False(await service.InviteUserAsync(201, groupId, UserId));

        associations.Setup(item => item.HasAssociationAsync(202, GraphAssociationType.Member, groupId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        associations.Setup(item => item.HasAssociationAsync(202, GraphAssociationType.Friend, UserId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        Assert.False(await service.InviteUserAsync(202, groupId, UserId));
    }

    [Fact]
    public async Task DeleteGroup_RejectsAdministratorWhileAnotherParticipantExists()
    {
        await using var context = CreateContext();
        const long groupId = 370;
        const long otherMemberId = 371;
        context.AssociationsTb.AddRange(
            Edge(UserId, GraphAssociationType.Member, groupId, 1),
            Edge(UserId, GraphAssociationType.Admin, groupId, 1),
            Edge(groupId, GraphAssociationType.HaveMember, UserId, 1),
            Edge(groupId, GraphAssociationType.HaveAdmin, UserId, 1),
            Edge(otherMemberId, GraphAssociationType.Member, groupId, 2),
            Edge(groupId, GraphAssociationType.HaveMember, otherMemberId, 2));
        await context.SaveChangesAsync();
        var objects = new Mock<IObjectService>(MockBehavior.Strict);
        var associations = new Mock<IAssociationService>(MockBehavior.Strict);
        var service = CreateService(context, objects, associations);

        Assert.False(await service.DeleteGroupAsync(UserId, groupId));

        objects.VerifyNoOtherCalls();
        associations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DeleteGroup_FailsClosedWhenAnotherForwardMembershipEdgeHasNoInverse()
    {
        await using var context = CreateContext();
        const long groupId = 374;
        const long otherMemberId = 375;
        context.AssociationsTb.AddRange(
            Edge(UserId, GraphAssociationType.Member, groupId, 1),
            Edge(UserId, GraphAssociationType.Admin, groupId, 1),
            Edge(groupId, GraphAssociationType.HaveMember, UserId, 1),
            Edge(groupId, GraphAssociationType.HaveAdmin, UserId, 1),
            Edge(otherMemberId, GraphAssociationType.Member, groupId, 2));
        await context.SaveChangesAsync();
        var objects = new Mock<IObjectService>(MockBehavior.Strict);
        var associations = new Mock<IAssociationService>(MockBehavior.Strict);
        var service = CreateService(context, objects, associations);

        Assert.False(await service.DeleteGroupAsync(UserId, groupId));

        objects.VerifyNoOtherCalls();
        associations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DeleteGroup_AllowsOnlyTheFinalAdministratorParticipant()
    {
        await using var context = CreateContext();
        const long groupId = 372;
        context.AssociationsTb.AddRange(
            Edge(UserId, GraphAssociationType.Member, groupId, 1),
            Edge(UserId, GraphAssociationType.Admin, groupId, 1),
            Edge(groupId, GraphAssociationType.HaveMember, UserId, 1),
            Edge(groupId, GraphAssociationType.HaveAdmin, UserId, 1));
        await context.SaveChangesAsync();
        var objects = new Mock<IObjectService>(MockBehavior.Strict);
        objects.Setup(item => item.RetrieveObjectAsync(groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(groupId, GraphObjectType.Group, GroupJson("Final", 1)));
        objects.Setup(item => item.DeleteObjectAsync(groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var associations = new Mock<IAssociationService>(MockBehavior.Strict);
        associations.Setup(item => item.DeleteObjectAssociationsAsync(groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(4);
        var external = new Mock<IExternalServiceClient>(MockBehavior.Loose);
        var service = CreateService(context, objects, associations, external.Object);

        Assert.True(await service.DeleteGroupAsync(UserId, groupId));

        objects.VerifyAll();
        associations.VerifyAll();
        external.Verify(item => item.DeleteSearchIndexAsync(groupId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteGroup_TerminalTransactionQueuesDescendantCommentMediaBeforePostCleanup()
    {
        await using var context = CreateContext();
        const long groupId = 380;
        const long postId = 381;
        const long commentId = 382;
        const long replyId = 383;
        const long postMediaId = 384;
        const long commentMediaId = 385;
        const long replyMediaId = 386;
        context.ObjectsTb.AddRange(
            new Objects { id = commentId, otype = GraphObjectType.Comment, data = "{}" },
            new Objects { id = replyId, otype = GraphObjectType.Comment, data = "{}" },
            new Objects { id = postMediaId, otype = GraphObjectType.Media, data = GraphJson.MediaJson(0, "/media/files/post.avif") },
            new Objects { id = commentMediaId, otype = GraphObjectType.Media, data = GraphJson.MediaJson(0, "/media/files/comment.avif") },
            new Objects { id = replyMediaId, otype = GraphObjectType.Media, data = GraphJson.MediaJson(0, "/media/files/reply.avif") });
        context.AssociationsTb.AddRange(
            Edge(UserId, GraphAssociationType.Member, groupId, 1),
            Edge(UserId, GraphAssociationType.Admin, groupId, 1),
            Edge(groupId, GraphAssociationType.HaveMember, UserId, 1),
            Edge(groupId, GraphAssociationType.HaveAdmin, UserId, 1),
            Edge(groupId, GraphAssociationType.Published, postId, 2),
            Edge(postId, GraphAssociationType.HaveComment, commentId, 3),
            Edge(commentId, GraphAssociationType.HaveComment, replyId, 4),
            Edge(postId, GraphAssociationType.Contained, postMediaId, 5),
            Edge(commentId, GraphAssociationType.Contained, commentMediaId, 6),
            Edge(replyId, GraphAssociationType.Contained, replyMediaId, 7));
        await context.SaveChangesAsync();

        var objects = new Mock<IObjectService>(MockBehavior.Strict);
        objects.Setup(item => item.RetrieveObjectAsync(groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(groupId, GraphObjectType.Group, GroupJson("Terminal", 1)));
        objects.Setup(item => item.DeleteObjectAsync(groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var associations = new Mock<IAssociationService>(MockBehavior.Strict);
        associations.Setup(item => item.DeleteObjectAssociationsAsync(groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(4);
        var external = new Mock<IExternalServiceClient>(MockBehavior.Loose);
        var content = new Mock<IContentGraphService>(MockBehavior.Strict);
        content.Setup(item => item.DeleteContentAsync(postId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var userGraph = new UserGraphService(
            objects.Object,
            associations.Object,
            external.Object,
            context);
        var service = new GroupGraphService(
            context,
            objects.Object,
            associations.Object,
            external.Object,
            new BlockVisibilityService(context),
            userGraph,
            TimeProvider.System,
            content.Object);

        Assert.True(await service.DeleteGroupAsync(UserId, groupId));

        external.Verify(item => item.DeleteMediaAsync(
            It.Is<IReadOnlyList<MediaLifecycleReference>>(references =>
                references.Any(reference => reference == MediaLifecycleReferences.ForMedia(postMediaId, "/media/files/post.avif")) &&
                references.Any(reference => reference == MediaLifecycleReferences.ForMedia(commentMediaId, "/media/files/comment.avif")) &&
                references.Any(reference => reference == MediaLifecycleReferences.ForMedia(replyMediaId, "/media/files/reply.avif"))),
            null,
            It.IsAny<CancellationToken>()), Times.Once);
        content.VerifyAll();
    }

    [Fact]
    public async Task DeleteGroupMutation_ForwardsOnlyTheTrustedAdministratorActor()
    {
        const long groupId = 373;
        var groups = new Mock<IGroupGraphService>(MockBehavior.Strict);
        groups.Setup(item => item.IsAdminAsync(UserId, groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        groups.Setup(item => item.DeleteGroupAsync(UserId, groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(UserId);

        Assert.True(await new Mutation().DeleteGroupAsync(
            groupId,
            groups.Object,
            trusted.Object,
            CancellationToken.None));

        groups.VerifyAll();
        trusted.VerifyAll();
    }

    private static GroupGraphService CreateService(
        MyDbContext context,
        Mock<IObjectService>? objects = null,
        Mock<IAssociationService>? associations = null,
        IExternalServiceClient? external = null,
        TimeProvider? timeProvider = null)
    {
        var objectService = (objects ?? new Mock<IObjectService>()).Object;
        var associationService = (associations ?? new Mock<IAssociationService>()).Object;
        var externalService = external ?? Mock.Of<IExternalServiceClient>();
        var userGraphService = new UserGraphService(
            objectService,
            associationService,
            externalService,
            context);
        return new GroupGraphService(
            context,
            objectService,
            associationService,
            externalService,
            new BlockVisibilityService(context),
            userGraphService,
            timeProvider ?? TimeProvider.System);
    }

    private static MyDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MyDbContext(options);
    }

    private static Objects Group(long id, string name, int privacy = 0) => new()
    {
        id = id,
        otype = GraphObjectType.Group,
        data = GroupJson(name, privacy)
    };

    private static Objects User(long id, string name) => new()
    {
        id = id,
        otype = GraphObjectType.User,
        data = new JsonObject
        {
            ["avatar"] = $"https://cdn.example/{id}.jpg",
            ["name"] = name,
            ["privacy"] = 0
        }.ToJsonString()
    };

    private static Objects GroupPost(long id) => new()
    {
        id = id,
        otype = GraphObjectType.GroupPost,
        data = "{}"
    };

    private static string GroupJson(string name, int privacy) => new JsonObject
    {
        ["name"] = name,
        ["avatar"] = $"https://cdn.example/{name}.jpg",
        ["privacy"] = privacy
    }.ToJsonString();

    private static Associations Edge(long id1, short type, long id2, long time) => new()
    {
        id1 = id1,
        atype = type,
        id2 = id2,
        time = time
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
