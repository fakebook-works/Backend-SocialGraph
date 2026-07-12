namespace SocialGraph.Api.Tests;

using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Moq;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;
using SocialGraph.Api.Service;

public sealed class StoryServiceTests
{
    private const long ViewerId = 100;
    private const long StoryAuthorId = 200;

    [Fact]
    public async Task HomeStories_RechecksSharedPostPrivacyOnEveryRead()
    {
        await using var context = CreateContext();
        var storyId = 1_000L;
        var postId = 2_000L;
        context.ObjectsTb.AddRange(
            User(StoryAuthorId, "Story Author"),
            new Objects { id = storyId, otype = GraphObjectType.Story, data = ActiveStoryJson("shared") },
            new Objects { id = postId, otype = GraphObjectType.FeedPost, data = PostJson("private source", 1) });
        context.AssociationsTb.AddRange(
            Edge(StoryAuthorId, GraphAssociationType.Authored, storyId),
            Edge(storyId, GraphAssociationType.Share, postId));
        await context.SaveChangesAsync();
        var associations = VisibleAuthorAssociations();
        var service = CreateService(context, associations);

        var hidden = await service.GetHomeStoriesAsync(ViewerId, 20, null);

        Assert.Empty(hidden.Items);

        var source = await context.ObjectsTb.FindAsync(postId);
        source!.data = PostJson("public source", 0);
        await context.SaveChangesAsync();

        var visible = await service.GetHomeStoriesAsync(ViewerId, 20, null);

        var bucket = Assert.Single(visible.Items);
        var story = Assert.IsType<FeedPostShareStoryResult>(Assert.Single(bucket.Stories));
        Assert.Equal(postId, story.SharedSource.Id);
        Assert.Equal("public source", story.SharedSource.Content);
    }

    [Fact]
    public async Task HomeStories_FiltersExpiredStoriesWithoutDeletingDuringRead()
    {
        await using var context = CreateContext();
        var storyId = 1_001L;
        context.ObjectsTb.AddRange(
            User(StoryAuthorId, "Story Author"),
            new Objects { id = storyId, otype = GraphObjectType.Story, data = ExpiredStoryJson("expired") });
        context.AssociationsTb.Add(Edge(StoryAuthorId, GraphAssociationType.Authored, storyId));
        await context.SaveChangesAsync();
        var associations = VisibleAuthorAssociations();
        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        var service = CreateService(context, associations, objects);

        var result = await service.GetHomeStoriesAsync(ViewerId, 20, null);

        Assert.Empty(result.Items);
        objects.Verify(item => item.DeleteObjectAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        associations.Verify(item => item.DeleteObjectAssociationsAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CleanupExpiredStories_DeletesTemporaryMediaButPreservesOwnedMedia()
    {
        await using var context = CreateContext();
        const long temporaryStoryId = 1_010;
        const long temporaryMediaId = 1_011;
        const long legacyStoryId = 1_020;
        const long ownedMediaId = 1_021;
        context.ObjectsTb.AddRange(
            new Objects { id = temporaryStoryId, otype = GraphObjectType.Story, data = ExpiredStoryJson("temporary") },
            new Objects { id = temporaryMediaId, otype = GraphObjectType.Media, data = MediaJson(0, "temporary.jpg") },
            new Objects { id = legacyStoryId, otype = GraphObjectType.Story, data = ExpiredStoryJson("legacy") },
            new Objects { id = ownedMediaId, otype = GraphObjectType.Media, data = MediaJson(0, "owned.jpg") });
        context.AssociationsTb.AddRange(
            Edge(temporaryStoryId, GraphAssociationType.Contained, temporaryMediaId),
            Edge(legacyStoryId, GraphAssociationType.Contained, ownedMediaId),
            Edge(StoryAuthorId, GraphAssociationType.Owned, ownedMediaId));
        await context.SaveChangesAsync();

        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations
            .Setup(item => item.RetrieveAssociationAsync(
                temporaryStoryId,
                GraphAssociationType.Contained,
                null,
                100,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(temporaryMediaId));
        associations
            .Setup(item => item.RetrieveAssociationAsync(
                legacyStoryId,
                GraphAssociationType.Contained,
                null,
                100,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(ownedMediaId));
        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        objects
            .Setup(item => item.DeleteObjectAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var service = CreateService(context, associations, objects);

        var deleted = await service.CleanupExpiredStoriesAsync(10);

        Assert.Equal(2, deleted);
        objects.Verify(item => item.DeleteObjectAsync(temporaryMediaId, It.IsAny<CancellationToken>()), Times.Once);
        objects.Verify(item => item.DeleteObjectAsync(ownedMediaId, It.IsAny<CancellationToken>()), Times.Never);
        objects.Verify(item => item.DeleteObjectAsync(temporaryStoryId, It.IsAny<CancellationToken>()), Times.Once);
        objects.Verify(item => item.DeleteObjectAsync(legacyStoryId, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static MyDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MyDbContext(options);
    }

    private static ContentGraphService CreateService(
        MyDbContext context,
        Mock<IAssociationService> associations,
        Mock<IObjectService>? objects = null)
    {
        return new ContentGraphService(
            context,
            (objects ?? new Mock<IObjectService>(MockBehavior.Loose)).Object,
            associations.Object,
            Mock.Of<IExternalServiceClient>());
    }

    private static Mock<IAssociationService> VisibleAuthorAssociations()
    {
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations
            .Setup(item => item.RetrieveAssociationAsync(
                ViewerId,
                GraphAssociationType.Friend,
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(StoryAuthorId));
        associations
            .Setup(item => item.RetrieveAssociationAsync(
                ViewerId,
                GraphAssociationType.Followed,
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page());
        return associations;
    }

    private static Objects User(long id, string name) => new()
    {
        id = id,
        otype = GraphObjectType.User,
        data = UserJson(name)
    };

    private static Associations Edge(long id1, short type, long id2) => new()
    {
        id1 = id1,
        atype = type,
        id2 = id2,
        time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };

    private static AssociationPageResult Page(params long[] ids) => new(
        ids.Select(id => new AssociationEdgeResult(id, 1)).ToArray(),
        null);

    private static string ActiveStoryJson(string content) => StoryJson(
        content,
        DateTimeOffset.UtcNow.AddMinutes(-5),
        DateTimeOffset.UtcNow.AddHours(23));

    private static string ExpiredStoryJson(string content) => StoryJson(
        content,
        DateTimeOffset.UtcNow.AddDays(-2),
        DateTimeOffset.UtcNow.AddDays(-1));

    private static string StoryJson(string content, DateTimeOffset created, DateTimeOffset expires) =>
        new JsonObject
        {
            ["content"] = content,
            ["create"] = created.ToString("O"),
            ["expire"] = expires.ToString("O")
        }.ToJsonString();

    private static string PostJson(string content, int privacy) => new JsonObject
    {
        ["content"] = content,
        ["privacy"] = privacy,
        ["create"] = DateTimeOffset.UtcNow.ToString("O")
    }.ToJsonString();

    private static string MediaJson(int type, string url) => new JsonObject
    {
        ["type"] = type,
        ["url"] = url
    }.ToJsonString();

    private static string UserJson(string name) => new JsonObject
    {
        ["name"] = name,
        ["avatar"] = "",
        ["verify"] = ""
    }.ToJsonString();
}
