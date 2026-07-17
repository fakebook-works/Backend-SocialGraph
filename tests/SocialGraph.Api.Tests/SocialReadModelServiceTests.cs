namespace SocialGraph.Api.Tests;

using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Moq;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;
using SocialGraph.Api.Service;

public sealed class SocialReadModelServiceTests
{
    private const long ViewerId = 100;
    private const long TargetUserId = 200;

    [Fact]
    public async Task RelationshipState_BlockSuppressesAllLowerPriorityStates()
    {
        await using var context = CreateContext();
        var objects = new Mock<IObjectService>();
        objects.Setup(item => item.RetrieveObjectAsync(TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(TargetUserId, GraphObjectType.User, "{}"));
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(item => item.HasAssociationAsync(ViewerId, GraphAssociationType.BlockedBy, TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        associations.Setup(item => item.HasAssociationAsync(ViewerId, GraphAssociationType.Friend, TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var service = CreateService(context, objects, associations);

        var state = await service.GetUserRelationshipStateAsync(ViewerId, TargetUserId);

        Assert.NotNull(state);
        Assert.True(state.IsBlockedBy);
        Assert.False(state.IsFriend);
        Assert.False(state.IsFollowing);
        Assert.False(state.FriendRequestSent);
    }

    [Fact]
    public async Task PrivateGroup_AdminCanReadTypedMemberPage()
    {
        await using var context = CreateContext();
        context.ObjectsTb.Add(new Objects
        {
            id = TargetUserId,
            otype = GraphObjectType.User,
            data = UserJson("Member")
        });
        await context.SaveChangesAsync();
        const long groupId = 300;
        var objects = new Mock<IObjectService>();
        objects.Setup(item => item.RetrieveObjectAsync(groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(groupId, GraphObjectType.Group, GroupJson(1)));
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(item => item.HasAssociationAsync(ViewerId, GraphAssociationType.Admin, groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        associations.Setup(item => item.RetrieveAssociationAsync(groupId, GraphAssociationType.HaveMember, null, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(new[] { new AssociationEdgeResult(TargetUserId, 1) }, null));
        var service = CreateService(context, objects, associations);

        var page = await service.GetGroupMembersAsync(ViewerId, groupId, null, 20, false);

        Assert.Equal("Member", Assert.Single(page.Items).Name);
    }

    [Fact]
    public async Task Comments_ReturnTypedAuthorCountsAndViewerReactionState()
    {
        await using var context = CreateContext();
        const long postId = 400;
        const long commentId = 401;
        const long replyId = 402;
        context.ObjectsTb.AddRange(
            new Objects { id = TargetUserId, otype = GraphObjectType.User, data = UserJson("Commenter") },
            new Objects { id = commentId, otype = GraphObjectType.Comment, data = ContentJson("hello") });
        context.AssociationsTb.AddRange(
            Edge(commentId, GraphAssociationType.AuthoredBy, TargetUserId),
            Edge(commentId, GraphAssociationType.LikedBy, 501),
            Edge(commentId, GraphAssociationType.LikedBy, 502),
            Edge(commentId, GraphAssociationType.HaveComment, replyId),
            Edge(ViewerId, GraphAssociationType.Liked, commentId));
        await context.SaveChangesAsync();
        var objects = new Mock<IObjectService>();
        objects.Setup(item => item.RetrieveObjectAsync(postId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(postId, GraphObjectType.FeedPost, ContentJson("post")));
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(item => item.RetrieveAssociationAsync(postId, GraphAssociationType.HaveComment, null, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(new[] { new AssociationEdgeResult(commentId, 1) }, null));
        var content = new Mock<IContentGraphService>();
        content.Setup(item => item.GetPostDetailAsync(ViewerId, postId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FeedPostDetailResult(
                postId,
                GraphObjectType.FeedPost,
                "post",
                0,
                "now",
                new PostAuthorResult(1, "Author", "", false, false),
                Array.Empty<MediaResult>()));
        var service = CreateService(context, objects, associations, content);

        var page = await service.GetCommentsAsync(ViewerId, postId, null, 20);

        var comment = Assert.Single(page.Items);
        Assert.Equal("hello", comment.Content);
        Assert.Equal("Commenter", comment.Author.Name);
        Assert.Equal(2, comment.LikeCount);
        Assert.Equal(1, comment.ReplyCount);
        Assert.True(comment.ViewerHasLiked);
    }

    [Fact]
    public async Task UserPhotos_ReturnsOnlyMediaFromVisibleFeedPosts()
    {
        await using var context = CreateContext();
        const long visibleMediaId = 601;
        const long hiddenMediaId = 602;
        const long visiblePostId = 611;
        const long hiddenPostId = 612;
        context.ObjectsTb.AddRange(
            new Objects { id = visiblePostId, otype = GraphObjectType.FeedPost, data = ContentJson("visible") },
            new Objects { id = hiddenPostId, otype = GraphObjectType.FeedPost, data = ContentJson("hidden") },
            new Objects { id = visibleMediaId, otype = GraphObjectType.Media, data = MediaJson("visible") },
            new Objects { id = hiddenMediaId, otype = GraphObjectType.Media, data = MediaJson("hidden") });
        context.AssociationsTb.AddRange(
            Edge(TargetUserId, GraphAssociationType.Authored, visiblePostId),
            Edge(TargetUserId, GraphAssociationType.Authored, hiddenPostId),
            Edge(visiblePostId, GraphAssociationType.Contained, visibleMediaId),
            Edge(hiddenPostId, GraphAssociationType.Contained, hiddenMediaId));
        await context.SaveChangesAsync();
        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        objects.Setup(item => item.RetrieveObjectAsync(TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(TargetUserId, GraphObjectType.User, UserJson("Owner")));
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        var content = new Mock<IContentGraphService>(MockBehavior.Loose);
        content.Setup(item => item.GetPostDetailsAsync(
                ViewerId,
                It.IsAny<IReadOnlyList<long>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IHomePostResult[] { FeedPost(visiblePostId, TargetUserId) });
        var service = CreateService(context, objects, associations, content);

        var page = await service.GetUserPhotosAsync(ViewerId, TargetUserId, null, 10);

        Assert.Equal(visibleMediaId, Assert.Single(page.Items).Media.Id);
    }

    [Fact]
    public async Task GroupUserPosts_FiltersPublishedPostsByRequestedAuthor()
    {
        await using var context = CreateContext();
        const long groupId = 700;
        const long matchingPostId = 701;
        const long otherPostId = 702;
        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        objects.Setup(item => item.RetrieveObjectAsync(groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(groupId, GraphObjectType.Group, GroupJson(0)));
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(item => item.RetrieveAssociationAsync(groupId, GraphAssociationType.Published, null, 40, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(
                new[]
                {
                    new AssociationEdgeResult(matchingPostId, 2),
                    new AssociationEdgeResult(otherPostId, 1)
                },
                null));
        var content = new Mock<IContentGraphService>(MockBehavior.Loose);
        content.Setup(item => item.GetPostDetailsAsync(
                ViewerId,
                It.IsAny<IReadOnlyList<long>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IHomePostResult[]
            {
                GroupPost(matchingPostId, TargetUserId, groupId),
                GroupPost(otherPostId, TargetUserId + 1, groupId)
            });
        var service = CreateService(context, objects, associations, content);

        var page = await service.GetGroupUserPostsAsync(ViewerId, groupId, TargetUserId, null, 10);

        Assert.Equal(matchingPostId, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task ViewerReelCollections_ReturnOnlyVisibleReels()
    {
        await using var context = CreateContext();
        const long likedReelId = 801;
        const long watchedReelId = 802;
        const long sharedReelId = 803;
        const long shareContainerId = 804;
        context.AssociationsTb.AddRange(
            Edge(ViewerId, GraphAssociationType.Authored, shareContainerId),
            Edge(shareContainerId, GraphAssociationType.Share, sharedReelId));
        await context.SaveChangesAsync();
        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        foreach (var reelId in new[] { likedReelId, watchedReelId, sharedReelId })
        {
            objects.Setup(item => item.RetrieveObjectAsync(reelId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SocialGraphObjectResult(reelId, GraphObjectType.Reel, ContentJson($"reel-{reelId}")));
        }

        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(item => item.RetrieveAssociationAsync(ViewerId, GraphAssociationType.Liked, null, 40, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(new[] { new AssociationEdgeResult(likedReelId, 1) }, null));
        associations.Setup(item => item.RetrieveAssociationAsync(ViewerId, GraphAssociationType.Watched, null, 40, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(new[] { new AssociationEdgeResult(watchedReelId, 1) }, null));
        foreach (var reelId in new[] { likedReelId, watchedReelId, sharedReelId })
        {
            associations.Setup(item => item.RetrieveAssociationAsync(reelId, GraphAssociationType.AuthoredBy, null, 1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AssociationPageResult(new[] { new AssociationEdgeResult(ViewerId, 1) }, null));
        }

        var content = new Mock<IContentGraphService>(MockBehavior.Loose);
        foreach (var reelId in new[] { likedReelId, watchedReelId, sharedReelId })
        {
            content.Setup(item => item.GetContentAsync(reelId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ContentResult(reelId, GraphObjectType.Reel, "reel", 0, "now", ViewerId, Array.Empty<MediaResult>()));
        }

        var service = CreateService(context, objects, associations, content);

        Assert.Equal(likedReelId, Assert.Single((await service.GetLikedReelsAsync(ViewerId, null, 10)).Items).Id);
        Assert.Equal(watchedReelId, Assert.Single((await service.GetWatchedReelsAsync(ViewerId, null, 10)).Items).Id);
        Assert.Equal(sharedReelId, Assert.Single((await service.GetSharedReelsAsync(ViewerId, null, 10)).Items).Id);
    }

    private static SocialReadModelService CreateService(
        MyDbContext context,
        Mock<IObjectService> objects,
        Mock<IAssociationService> associations,
        Mock<IContentGraphService>? content = null) => new(
            context,
            objects.Object,
            associations.Object,
            (content ?? new Mock<IContentGraphService>()).Object);

    private static MyDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MyDbContext(options);
    }

    private static string UserJson(string name) => new JsonObject
    {
        ["name"] = name,
        ["avatar"] = "https://cdn.example/user.jpg",
        ["verify"] = ""
    }.ToJsonString();

    private static string GroupJson(int privacy) => new JsonObject { ["privacy"] = privacy }.ToJsonString();

    private static string ContentJson(string content) => new JsonObject
    {
        ["content"] = content,
        ["create"] = "now",
        ["privacy"] = 0
    }.ToJsonString();

    private static string MediaJson(string value) => new JsonObject
    {
        ["type"] = 0,
        ["url"] = $"https://cdn.example/{value}.jpg"
    }.ToJsonString();

    private static FeedPostDetailResult FeedPost(long id, long authorId) => new(
        id,
        GraphObjectType.FeedPost,
        "post",
        0,
        "now",
        new PostAuthorResult(authorId, "Author", "", false, false),
        Array.Empty<MediaResult>());

    private static GroupPostDetailResult GroupPost(long id, long authorId, long groupId) => new(
        id,
        GraphObjectType.GroupPost,
        "post",
        0,
        "now",
        new PostAuthorResult(authorId, "Author", "", false, false),
        new PostGroupResult(groupId, "Group", "", false),
        Array.Empty<MediaResult>());

    private static Associations Edge(long id1, short atype, long id2) => new()
    {
        id1 = id1,
        atype = atype,
        id2 = id2,
        time = 1
    };
}
