namespace SocialGraph.Api.Tests;

using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Moq;
using SocialGraph.Api.Database;
using SocialGraph.Api.Service;

public sealed class CandidateServiceTests
{
    private const long UserId = 100;

    [Fact]
    public async Task PostCandidateIds_CombinesSocialAndPublicSources_WithPrivacyAndBlockFiltering()
    {
        await using var context = CreateContext();
        context.ObjectsTb.AddRange(
            User(200), User(201), User(202), User(203), User(204),
            Group(300, privacy: 1), Group(301, privacy: 0),
            Post(1_000, GraphObjectType.FeedPost, privacy: 1),
            Post(1_001, GraphObjectType.FeedPost, privacy: 1),
            Post(1_002, GraphObjectType.FeedPost, privacy: 0),
            Post(1_003, GraphObjectType.GroupPost, privacy: 0),
            Post(1_004, GraphObjectType.GroupPost, privacy: 0),
            Post(1_005, GraphObjectType.FeedPost, privacy: 0));
        context.AssociationsTb.AddRange(
            Edge(UserId, GraphAssociationType.Friend, 200),
            Edge(UserId, GraphAssociationType.Followed, 201),
            Edge(UserId, GraphAssociationType.Member, 300),
            Edge(UserId, GraphAssociationType.Blocked, 203),
            Authored(200, 1_000), Authored(201, 1_001), Authored(201, 1_002),
            Authored(202, 1_003), Authored(203, 1_004), Authored(204, 1_005),
            AuthoredBy(1_000, 200), AuthoredBy(1_001, 201), AuthoredBy(1_002, 201),
            AuthoredBy(1_003, 202), AuthoredBy(1_004, 203), AuthoredBy(1_005, 204),
            Edge(300, GraphAssociationType.Published, 1_003),
            Edge(301, GraphAssociationType.Published, 1_004));
        await context.SaveChangesAsync();
        var service = new CandidateService(context, Mock.Of<IAssociationService>());

        var ids = await service.GetPostCandidateIdsAsync(UserId, 20);

        Assert.Equal(new long[] { 1_005, 1_003, 1_002, 1_000 }, ids);
        Assert.DoesNotContain(1_001, ids);
        Assert.DoesNotContain(1_004, ids);
    }

    [Fact]
    public async Task PostCandidateIds_DoesNotTruncateBlockListAtAssociationPageSize()
    {
        await using var context = CreateContext();
        const long blockedAuthorId = 999;
        const long blockedPostId = 2_000;
        context.ObjectsTb.AddRange(User(blockedAuthorId), Post(blockedPostId, GraphObjectType.FeedPost, privacy: 0));
        context.AssociationsTb.AddRange(
            Enumerable.Range(1, 101)
                .Select(index => Edge(UserId, GraphAssociationType.Blocked, 10_000 + index)));
        context.AssociationsTb.AddRange(
            Edge(UserId, GraphAssociationType.Blocked, blockedAuthorId),
            Authored(blockedAuthorId, blockedPostId),
            AuthoredBy(blockedPostId, blockedAuthorId));
        await context.SaveChangesAsync();
        var service = new CandidateService(context, Mock.Of<IAssociationService>());

        var ids = await service.GetPostCandidateIdsAsync(UserId, 20);

        Assert.DoesNotContain(blockedPostId, ids);
    }

    private static MyDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MyDbContext(options);
    }

    private static Objects User(long id) => new()
    {
        id = id,
        otype = GraphObjectType.User,
        data = "{}"
    };

    private static Objects Group(long id, int privacy) => new()
    {
        id = id,
        otype = GraphObjectType.Group,
        data = new JsonObject { ["privacy"] = privacy }.ToJsonString()
    };

    private static Objects Post(long id, short type, int privacy) => new()
    {
        id = id,
        otype = type,
        data = new JsonObject
        {
            ["privacy"] = privacy,
            ["create"] = DateTimeOffset.UtcNow.ToString("O")
        }.ToJsonString()
    };

    private static Associations Authored(long authorId, long postId) =>
        Edge(authorId, GraphAssociationType.Authored, postId);

    private static Associations AuthoredBy(long postId, long authorId) =>
        Edge(postId, GraphAssociationType.AuthoredBy, authorId);

    private static Associations Edge(long id1, short type, long id2) => new()
    {
        id1 = id1,
        atype = type,
        id2 = id2,
        time = id2
    };
}
