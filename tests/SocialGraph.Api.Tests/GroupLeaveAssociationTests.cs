namespace SocialGraph.Api.Tests;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SocialGraph.Api.Database;
using SocialGraph.Api.Service;
using StackExchange.Redis;

public sealed class GroupLeaveAssociationTests
{
    private const long GroupId = 900;
    private const long LeavingUserId = 101;

    [Fact]
    public async Task MemberLeave_RemovesMembershipInverseAndVisited()
    {
        await using var context = CreateContext(
            Member(LeavingUserId, 10),
            HaveMember(LeavingUserId, 10),
            Edge(LeavingUserId, GraphAssociationType.Visited, GroupId, 20));
        var service = CreateService(context);

        Assert.True(await service.LeaveGroupWithAdminTransferAsync(LeavingUserId, GroupId));

        Assert.Empty(context.AssociationsTb);
    }

    [Fact]
    public async Task AdministratorRemoval_RemovesMembershipInverseAndVisitedOnlyForTheTargetGroup()
    {
        const long removingAdminId = 102;
        const long otherGroupId = 901;
        const long otherUserId = 202;
        await using var context = CreateContext(
            Admin(removingAdminId, 5), HaveAdmin(removingAdminId, 5),
            Member(LeavingUserId, 10), HaveMember(LeavingUserId, 10),
            Edge(LeavingUserId, GraphAssociationType.Visited, GroupId, 20),
            Edge(LeavingUserId, GraphAssociationType.Visited, otherGroupId, 30),
            Edge(otherUserId, GraphAssociationType.Visited, GroupId, 40));
        var service = CreateService(context);

        Assert.True(await service.RemoveGroupMemberByAdminAsync(
            removingAdminId,
            LeavingUserId,
            GroupId));

        Assert.False(await Has(context, LeavingUserId, GraphAssociationType.Member, GroupId));
        Assert.False(await Has(context, GroupId, GraphAssociationType.HaveMember, LeavingUserId));
        Assert.False(await Has(context, LeavingUserId, GraphAssociationType.Visited, GroupId));
        Assert.True(await Has(context, LeavingUserId, GraphAssociationType.Visited, otherGroupId));
        Assert.True(await Has(context, otherUserId, GraphAssociationType.Visited, GroupId));
        Assert.True(await Has(context, removingAdminId, GraphAssociationType.Admin, GroupId));
    }

    [Fact]
    public async Task AdministratorRemoval_FailsClosedWhenTheActorAdminInverseIsMissing()
    {
        const long removingAdminId = 102;
        await using var context = CreateContext(
            Admin(removingAdminId, 5),
            Member(LeavingUserId, 10), HaveMember(LeavingUserId, 10),
            Edge(LeavingUserId, GraphAssociationType.Visited, GroupId, 20));
        var service = CreateService(context);

        Assert.False(await service.RemoveGroupMemberByAdminAsync(
            removingAdminId,
            LeavingUserId,
            GroupId));

        Assert.True(await Has(context, LeavingUserId, GraphAssociationType.Member, GroupId));
        Assert.True(await Has(context, GroupId, GraphAssociationType.HaveMember, LeavingUserId));
        Assert.True(await Has(context, LeavingUserId, GraphAssociationType.Visited, GroupId));
    }

    [Fact]
    public async Task AdministratorRemoval_RejectsAnAdministratorTargetAndPreservesVisited()
    {
        const long removingAdminId = 102;
        await using var context = CreateContext(
            Admin(removingAdminId, 5), HaveAdmin(removingAdminId, 5),
            Member(LeavingUserId, 10), HaveMember(LeavingUserId, 10),
            Admin(LeavingUserId, 10), HaveAdmin(LeavingUserId, 10),
            Edge(LeavingUserId, GraphAssociationType.Visited, GroupId, 20));
        var service = CreateService(context);

        Assert.False(await service.RemoveGroupMemberByAdminAsync(
            removingAdminId,
            LeavingUserId,
            GroupId));

        Assert.True(await Has(context, LeavingUserId, GraphAssociationType.Member, GroupId));
        Assert.True(await Has(context, LeavingUserId, GraphAssociationType.Admin, GroupId));
        Assert.True(await Has(context, LeavingUserId, GraphAssociationType.Visited, GroupId));
    }

    [Fact]
    public async Task OneOfMultipleAdminsLeavesWithoutPromotingAnotherMember()
    {
        const long remainingAdminId = 202;
        await using var context = CreateContext(
            Member(LeavingUserId, 10), HaveMember(LeavingUserId, 10),
            Admin(LeavingUserId, 10), HaveAdmin(LeavingUserId, 10),
            Member(remainingAdminId, 20), HaveMember(remainingAdminId, 20),
            Admin(remainingAdminId, 20), HaveAdmin(remainingAdminId, 20),
            Edge(LeavingUserId, GraphAssociationType.Visited, GroupId, 30));
        var service = CreateService(context);

        Assert.True(await service.LeaveGroupWithAdminTransferAsync(LeavingUserId, GroupId));

        Assert.False(await Has(context, LeavingUserId, GraphAssociationType.Member, GroupId));
        Assert.False(await Has(context, LeavingUserId, GraphAssociationType.Admin, GroupId));
        Assert.False(await Has(context, LeavingUserId, GraphAssociationType.Visited, GroupId));
        Assert.True(await Has(context, remainingAdminId, GraphAssociationType.Member, GroupId));
        Assert.True(await Has(context, remainingAdminId, GraphAssociationType.Admin, GroupId));
        Assert.Equal(1, await context.AssociationsTb.CountAsync(item => item.atype == GraphAssociationType.HaveAdmin));
    }

    [Fact]
    public async Task SoleAdminLeave_PromotesTheEarliestCurrentMemberAndIgnoresPendingRequests()
    {
        const long earliestMemberId = 303;
        const long laterMemberId = 304;
        const long pendingUserId = 302;
        await using var context = CreateContext(
            Member(LeavingUserId, 10), HaveMember(LeavingUserId, 10),
            Admin(LeavingUserId, 10), HaveAdmin(LeavingUserId, 10),
            Member(earliestMemberId, 20), HaveMember(earliestMemberId, 20),
            Member(laterMemberId, 30), HaveMember(laterMemberId, 30),
            Edge(pendingUserId, GraphAssociationType.GroupJoinRequest, GroupId, 1),
            Edge(GroupId, GraphAssociationType.HaveGroupJoinRequest, pendingUserId, 1),
            Edge(LeavingUserId, GraphAssociationType.Visited, GroupId, 40));
        var service = CreateService(context);

        Assert.True(await service.LeaveGroupWithAdminTransferAsync(LeavingUserId, GroupId));

        Assert.True(await Has(context, earliestMemberId, GraphAssociationType.Admin, GroupId));
        Assert.True(await Has(context, GroupId, GraphAssociationType.HaveAdmin, earliestMemberId));
        Assert.True(await Has(context, earliestMemberId, GraphAssociationType.Member, GroupId));
        Assert.False(await Has(context, laterMemberId, GraphAssociationType.Admin, GroupId));
        Assert.False(await Has(context, pendingUserId, GraphAssociationType.Admin, GroupId));
        Assert.False(await Has(context, LeavingUserId, GraphAssociationType.Member, GroupId));
        Assert.False(await Has(context, LeavingUserId, GraphAssociationType.Admin, GroupId));
        Assert.False(await Has(context, LeavingUserId, GraphAssociationType.Visited, GroupId));
    }

    [Fact]
    public async Task SoleAdminLeave_UsesUserIdAsTheDeterministicTieBreaker()
    {
        const long lowerId = 402;
        const long higherId = 403;
        await using var context = CreateContext(
            Member(LeavingUserId, 10), HaveMember(LeavingUserId, 10),
            Admin(LeavingUserId, 10), HaveAdmin(LeavingUserId, 10),
            Member(higherId, 20), HaveMember(higherId, 20),
            Member(lowerId, 20), HaveMember(lowerId, 20));
        var service = CreateService(context);

        Assert.True(await service.LeaveGroupWithAdminTransferAsync(LeavingUserId, GroupId));

        Assert.True(await Has(context, lowerId, GraphAssociationType.Admin, GroupId));
        Assert.False(await Has(context, higherId, GraphAssociationType.Admin, GroupId));
    }

    [Fact]
    public async Task SoleAdminWithoutSuccessor_IsRejectedWithoutChangingAnyAssociation()
    {
        await using var context = CreateContext(
            Member(LeavingUserId, 10), HaveMember(LeavingUserId, 10),
            Admin(LeavingUserId, 10), HaveAdmin(LeavingUserId, 10),
            Edge(LeavingUserId, GraphAssociationType.Visited, GroupId, 20));
        var before = await context.AssociationsTb.AsNoTracking()
            .OrderBy(item => item.atype)
            .Select(item => new { item.id1, item.atype, item.id2, item.time })
            .ToArrayAsync();
        var service = CreateService(context);

        Assert.False(await service.LeaveGroupWithAdminTransferAsync(LeavingUserId, GroupId));

        var after = await context.AssociationsTb.AsNoTracking()
            .OrderBy(item => item.atype)
            .Select(item => new { item.id1, item.atype, item.id2, item.time })
            .ToArrayAsync();
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task AdminDemotion_RemovesOnlyTheRoleAndPreservesMembership()
    {
        const long remainingAdminId = 202;
        await using var context = CreateContext(
            Member(LeavingUserId, 10), HaveMember(LeavingUserId, 10),
            Admin(LeavingUserId, 10), HaveAdmin(LeavingUserId, 10),
            Member(remainingAdminId, 20), HaveMember(remainingAdminId, 20),
            Admin(remainingAdminId, 20), HaveAdmin(remainingAdminId, 20));
        var service = CreateService(context);

        Assert.True(await service.DemoteGroupAdminAsync(LeavingUserId, GroupId));

        Assert.True(await Has(context, LeavingUserId, GraphAssociationType.Member, GroupId));
        Assert.True(await Has(context, GroupId, GraphAssociationType.HaveMember, LeavingUserId));
        Assert.False(await Has(context, LeavingUserId, GraphAssociationType.Admin, GroupId));
        Assert.False(await Has(context, GroupId, GraphAssociationType.HaveAdmin, LeavingUserId));
        Assert.True(await Has(context, remainingAdminId, GraphAssociationType.Admin, GroupId));
    }

    [Fact]
    public async Task SequentialAdminDemotions_CannotRemoveTheLastAdministrator()
    {
        const long secondAdminId = 202;
        await using var context = CreateContext(
            Member(LeavingUserId, 10), HaveMember(LeavingUserId, 10),
            Admin(LeavingUserId, 10), HaveAdmin(LeavingUserId, 10),
            Member(secondAdminId, 20), HaveMember(secondAdminId, 20),
            Admin(secondAdminId, 20), HaveAdmin(secondAdminId, 20));
        var service = CreateService(context);

        Assert.True(await service.DemoteGroupAdminAsync(LeavingUserId, GroupId));
        Assert.False(await service.DemoteGroupAdminAsync(secondAdminId, GroupId));

        Assert.Equal(1, await context.AssociationsTb.CountAsync(item =>
            item.id1 == GroupId && item.atype == GraphAssociationType.HaveAdmin));
        Assert.True(await Has(context, secondAdminId, GraphAssociationType.Admin, GroupId));
    }

    [Fact]
    public async Task AdminDemotion_RejectsCorruptAdminWithoutMembership()
    {
        const long remainingAdminId = 202;
        await using var context = CreateContext(
            Admin(LeavingUserId, 10), HaveAdmin(LeavingUserId, 10),
            Member(remainingAdminId, 20), HaveMember(remainingAdminId, 20),
            Admin(remainingAdminId, 20), HaveAdmin(remainingAdminId, 20));
        var service = CreateService(context);

        Assert.False(await service.DemoteGroupAdminAsync(LeavingUserId, GroupId));

        Assert.True(await Has(context, LeavingUserId, GraphAssociationType.Admin, GroupId));
        Assert.Equal(2, await context.AssociationsTb.CountAsync(item =>
            item.id1 == GroupId && item.atype == GraphAssociationType.HaveAdmin));
    }

    private static AssociationService CreateService(MyDbContext context)
    {
        var database = new Mock<IDatabase>();
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(item => item.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);
        return new AssociationService(
            context,
            redis.Object,
            NullLogger<AssociationService>.Instance,
            Options.Create(new SocialGraphCacheOptions { Mode = "off" }));
    }

    private static MyDbContext CreateContext(params Associations[] associations)
    {
        var context = new MyDbContext(new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
        context.AssociationsTb.AddRange(associations);
        context.SaveChanges();
        return context;
    }

    private static Task<bool> Has(MyDbContext context, long id1, short atype, long id2) =>
        context.AssociationsTb.AnyAsync(item => item.id1 == id1 && item.atype == atype && item.id2 == id2);

    private static Associations Member(long userId, long time) => Edge(userId, GraphAssociationType.Member, GroupId, time);
    private static Associations HaveMember(long userId, long time) => Edge(GroupId, GraphAssociationType.HaveMember, userId, time);
    private static Associations Admin(long userId, long time) => Edge(userId, GraphAssociationType.Admin, GroupId, time);
    private static Associations HaveAdmin(long userId, long time) => Edge(GroupId, GraphAssociationType.HaveAdmin, userId, time);
    private static Associations Edge(long id1, short atype, long id2, long time) => new() { id1 = id1, atype = atype, id2 = id2, time = time };
}
