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
            Group(300, privacy: 1), Group(301, privacy: 0), Group(302, privacy: 0), Group(303, privacy: 1),
            Post(1_000, GraphObjectType.FeedPost, privacy: 1),
            Post(1_001, GraphObjectType.FeedPost, privacy: 1),
            Post(1_002, GraphObjectType.FeedPost, privacy: 0),
            Post(1_003, GraphObjectType.GroupPost, privacy: 0),
            Post(1_004, GraphObjectType.GroupPost, privacy: 0),
            Post(1_005, GraphObjectType.FeedPost, privacy: 0),
            Post(1_006, GraphObjectType.Reel, privacy: 2),
            Post(1_007, GraphObjectType.Reel, privacy: 1),
            Post(1_008, GraphObjectType.Reel, privacy: 0),
            Post(1_009, GraphObjectType.Reel, privacy: 2),
            Post(1_010, GraphObjectType.Reel, privacy: 0),
            Post(1_011, GraphObjectType.GroupPost, privacy: 3),
            Post(1_012, GraphObjectType.GroupPost, privacy: 0));
        context.AssociationsTb.AddRange(
            Edge(UserId, GraphAssociationType.Friend, 200),
            Edge(UserId, GraphAssociationType.Followed, 201),
            Edge(UserId, GraphAssociationType.Member, 300),
            Edge(UserId, GraphAssociationType.Blocked, 203),
            Authored(200, 1_000), Authored(201, 1_001), Authored(201, 1_002),
            Authored(202, 1_003), Authored(203, 1_004), Authored(204, 1_005),
            Authored(200, 1_006), Authored(201, 1_007), Authored(204, 1_008),
            Authored(201, 1_009), Authored(203, 1_010), Authored(204, 1_011), Authored(204, 1_012),
            AuthoredBy(1_000, 200), AuthoredBy(1_001, 201), AuthoredBy(1_002, 201),
            AuthoredBy(1_003, 202), AuthoredBy(1_004, 203), AuthoredBy(1_005, 204),
            AuthoredBy(1_006, 200), AuthoredBy(1_007, 201), AuthoredBy(1_008, 204),
            AuthoredBy(1_009, 201), AuthoredBy(1_010, 203), AuthoredBy(1_011, 204), AuthoredBy(1_012, 204),
            Edge(300, GraphAssociationType.Published, 1_003),
            Edge(301, GraphAssociationType.Published, 1_004),
            Edge(302, GraphAssociationType.Published, 1_011),
            Edge(303, GraphAssociationType.Published, 1_012));
        await context.SaveChangesAsync();
        var service = new CandidateService(context, Mock.Of<IAssociationService>());

        var ids = await service.GetPostCandidateIdsAsync(UserId, 20);

        Assert.Equal(new long[] { 1_011, 1_008, 1_007, 1_006, 1_005, 1_003, 1_002, 1_001, 1_000 }, ids);
        Assert.DoesNotContain(1_004, ids);
        Assert.DoesNotContain(1_009, ids);
        Assert.DoesNotContain(1_010, ids);
        Assert.DoesNotContain(1_012, ids);
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
        data = PostJson(type, privacy)
    };

    private static string PostJson(short type, int privacy)
    {
        var data = new JsonObject
        {
            ["create"] = DateTimeOffset.UtcNow.ToString("O")
        };
        if (type != GraphObjectType.GroupPost)
        {
            data["privacy"] = privacy;
        }

        return data.ToJsonString();
    }

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
