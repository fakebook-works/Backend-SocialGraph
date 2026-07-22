namespace SocialGraph.Api.Tests;

using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Moq;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;
using SocialGraph.Api.Service;

public sealed class HomePostServiceTests
{
    private const long ViewerId = 100;
    private const long FeedAuthorId = 200;
    private const long GroupAuthorId = 201;
    private const long GroupId = 300;

    [Fact]
    public async Task PostDetails_PreservesRankedOrder_Deduplicates_AndDistinguishesGroupPosts()
    {
        await using var context = CreateContext();
        const long feedPostId = 1_000;
        const long groupPostId = 1_001;
        const long mediaId = 1_100;
        const long taggedUserId = 202;
        context.ObjectsTb.AddRange(
            User(FeedAuthorId, "Feed Author"),
            User(GroupAuthorId, "Group Author"),
            User(taggedUserId, "Tagged Friend"),
            Group(GroupId, "Dotnet Vietnam", privacy: 1),
            Post(feedPostId, GraphObjectType.FeedPost, "public feed", privacy: 0),
            Post(groupPostId, GraphObjectType.GroupPost, "member post", privacy: 0),
            Media(mediaId, "https://cdn.example/post.jpg"));
        context.AssociationsTb.AddRange(
            Edge(feedPostId, GraphAssociationType.AuthoredBy, FeedAuthorId),
            Edge(feedPostId, GraphAssociationType.Tagged, taggedUserId),
            Edge(groupPostId, GraphAssociationType.AuthoredBy, GroupAuthorId),
            Edge(groupPostId, GraphAssociationType.PublishedIn, GroupId),
            Edge(groupPostId, GraphAssociationType.Contained, mediaId),
            Edge(ViewerId, GraphAssociationType.Member, GroupId));
        await context.SaveChangesAsync();
        var service = CreateContentService(context);

        var results = await service.GetPostDetailsAsync(
            ViewerId,
            new[] { groupPostId, feedPostId, groupPostId, -1L });

        Assert.Equal(2, results.Count);
        var groupPost = Assert.IsType<GroupPostDetailResult>(results[0]);
        Assert.Equal(groupPostId, groupPost.Id);
        Assert.Equal(GroupId, groupPost.Group.Id);
        Assert.Equal("Dotnet Vietnam", groupPost.Group.Name);
        Assert.False(groupPost.Group.CanJoin);
        Assert.Equal("Group Author", groupPost.Author.Name);
        Assert.Equal("https://cdn.example/post.jpg", Assert.Single(groupPost.Media).Url);

        var feedPost = Assert.IsType<FeedPostDetailResult>(results[1]);
        Assert.Equal(feedPostId, feedPost.Id);
        Assert.Equal("Feed Author", feedPost.Author.Name);
        Assert.Equal("Tagged Friend", Assert.Single(feedPost.TaggedUsers!).Name);
    }

    [Fact]
    public async Task PostDetails_FiltersBlockedAuthorsAndInaccessiblePrivatePosts()
    {
        await using var context = CreateContext();
        const long blockedAuthorId = 210;
        const long privateAuthorId = 211;
        const long blockedPostId = 1_010;
        const long privatePostId = 1_011;
        context.ObjectsTb.AddRange(
            User(blockedAuthorId, "Blocked Author"),
            User(privateAuthorId, "Private Author"),
            Post(blockedPostId, GraphObjectType.FeedPost, "blocked public", privacy: 0),
            Post(privatePostId, GraphObjectType.FeedPost, "private", privacy: 1));
        context.AssociationsTb.AddRange(
            Edge(blockedPostId, GraphAssociationType.AuthoredBy, blockedAuthorId),
            Edge(privatePostId, GraphAssociationType.AuthoredBy, privateAuthorId),
            Edge(ViewerId, GraphAssociationType.Blocked, blockedAuthorId));
        await context.SaveChangesAsync();
        var service = CreateContentService(context);

        var results = await service.GetPostDetailsAsync(ViewerId, new[] { blockedPostId, privatePostId });

        Assert.Empty(results);
    }

    [Fact]
    public async Task PostDetails_AllowsPrivateFriendPost_AndRejectsOversizedBatch()
    {
        await using var context = CreateContext();
        const long privatePostId = 1_020;
        context.ObjectsTb.AddRange(
            User(FeedAuthorId, "Friend"),
            Post(privatePostId, GraphObjectType.FeedPost, "friends only", privacy: 1));
        context.AssociationsTb.AddRange(
            Edge(privatePostId, GraphAssociationType.AuthoredBy, FeedAuthorId),
            Edge(ViewerId, GraphAssociationType.Friend, FeedAuthorId));
        await context.SaveChangesAsync();
        var service = CreateContentService(context);

        var visible = await service.GetPostDetailAsync(ViewerId, privatePostId);

        Assert.IsType<FeedPostDetailResult>(visible);
        var oversized = Enumerable.Range(1, ContentGraphService.MaxPostDetailIds + 1)
            .Select(id => (long)id)
            .ToArray();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.GetPostDetailsAsync(ViewerId, oversized));
    }

    [Fact]
    public async Task PostDetails_EnforcesAllFourFeedPrivacyLevelsAtReadTime()
    {
        await using var context = CreateContext();
        const long followerId = 101;
        const long strangerId = 102;
        var postIds = new long[] { 2_000, 2_001, 2_002, 2_003 };
        context.ObjectsTb.AddRange(
            User(FeedAuthorId, "Author"),
            User(ViewerId, "Friend viewer"),
            User(followerId, "Follower viewer"),
            User(strangerId, "Stranger viewer"),
            Post(postIds[0], GraphObjectType.FeedPost, "public", privacy: 0),
            Post(postIds[1], GraphObjectType.FeedPost, "friends and followers", privacy: 1),
            Post(postIds[2], GraphObjectType.FeedPost, "friends", privacy: 2),
            Post(postIds[3], GraphObjectType.FeedPost, "only me", privacy: 3));
        context.AssociationsTb.AddRange(
            postIds.Select(id => Edge(id, GraphAssociationType.AuthoredBy, FeedAuthorId)));
        context.AssociationsTb.AddRange(
            Edge(ViewerId, GraphAssociationType.Friend, FeedAuthorId),
            Edge(followerId, GraphAssociationType.Followed, FeedAuthorId));
        await context.SaveChangesAsync();
        var service = CreateContentService(context);

        var owner = await service.GetPostDetailsAsync(FeedAuthorId, postIds);
        var friend = await service.GetPostDetailsAsync(ViewerId, postIds);
        var follower = await service.GetPostDetailsAsync(followerId, postIds);
        var stranger = await service.GetPostDetailsAsync(strangerId, postIds);

        Assert.Equal(postIds, owner.Select(PostId));
        Assert.Equal(postIds.Take(3), friend.Select(PostId));
        Assert.Equal(postIds.Take(2), follower.Select(PostId));
        Assert.Equal(new[] { postIds[0] }, stranger.Select(PostId));
    }

    [Fact]
    public async Task PostDetails_ProjectsVisibleReelAsItsOwnHomePostType()
    {
        await using var context = CreateContext();
        const long reelId = 2_050;
        const long mediaId = 2_051;
        context.ObjectsTb.AddRange(
            User(FeedAuthorId, "Reel Author"),
            Post(reelId, GraphObjectType.Reel, "reel on home", privacy: 2),
            Media(mediaId, "https://cdn.example/reel.mp4", GraphMediaType.Video));
        context.AssociationsTb.AddRange(
            Edge(reelId, GraphAssociationType.AuthoredBy, FeedAuthorId),
            Edge(reelId, GraphAssociationType.Contained, mediaId),
            Edge(ViewerId, GraphAssociationType.Friend, FeedAuthorId));
        await context.SaveChangesAsync();
        var service = CreateContentService(context);

        var visible = Assert.IsType<ReelDetailResult>(await service.GetPostDetailAsync(ViewerId, reelId));

        Assert.Equal(GraphObjectType.Reel, visible.Type);
        Assert.Equal(2, visible.Privacy);
        Assert.Equal("reel on home", visible.Content);
        Assert.Equal("https://cdn.example/reel.mp4", Assert.Single(visible.Media).Url);
        Assert.Null(await service.GetPostDetailAsync(999, reelId));
    }

    [Fact]
    public async Task PrivateGroupPost_RequiresCurrentMembershipEvenForItsAuthor()
    {
        await using var context = CreateContext();
        const long postId = 2_100;
        context.ObjectsTb.AddRange(
            User(GroupAuthorId, "Former member"),
            Group(GroupId, "Private group", privacy: 1),
            Post(postId, GraphObjectType.GroupPost, "old group post", privacy: 0));
        context.AssociationsTb.AddRange(
            Edge(postId, GraphAssociationType.AuthoredBy, GroupAuthorId),
            Edge(postId, GraphAssociationType.PublishedIn, GroupId));
        await context.SaveChangesAsync();
        var service = CreateContentService(context);

        Assert.Null(await service.GetPostDetailAsync(GroupAuthorId, postId));
    }

    [Fact]
    public async Task SharedFeedWrapper_ProjectsPublicSourceWithAuthorAndMedia()
    {
        await using var context = CreateContext();
        const long wrapperId = 2_200;
        const long sourceId = 2_201;
        const long sourceMediaId = 2_202;
        context.ObjectsTb.AddRange(
            User(ViewerId, "Sharer"),
            User(FeedAuthorId, "Source author"),
            Post(wrapperId, GraphObjectType.FeedPost, "my take", privacy: 0),
            Post(sourceId, GraphObjectType.FeedPost, "public source", privacy: 0),
            Media(sourceMediaId, "https://cdn.example/source.jpg"));
        context.AssociationsTb.AddRange(
            Edge(wrapperId, GraphAssociationType.AuthoredBy, ViewerId),
            Edge(wrapperId, GraphAssociationType.Share, sourceId),
            Edge(sourceId, GraphAssociationType.AuthoredBy, FeedAuthorId),
            Edge(sourceId, GraphAssociationType.Contained, sourceMediaId));
        await context.SaveChangesAsync();
        var service = CreateContentService(context);

        var wrapper = Assert.IsType<FeedPostDetailResult>(
            await service.GetPostDetailAsync(ViewerId, wrapperId));

        Assert.NotNull(wrapper.SharedSource);
        Assert.True(wrapper.SharedSource.IsAvailable);
        Assert.Equal(sourceId, wrapper.SharedSource.Id);
        Assert.Equal("public source", wrapper.SharedSource.Content);
        Assert.Equal("Source author", wrapper.SharedSource.Author?.Name);
        Assert.Equal(0, wrapper.SharedSource.Privacy);
        Assert.False(string.IsNullOrWhiteSpace(wrapper.SharedSource.Create));
        Assert.Equal("https://cdn.example/source.jpg", Assert.Single(wrapper.SharedSource.Media).Url);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SharedFeedWrapper_RemainsVisibleWhenSourceIsPrivateOrDeleted(bool sourceExists)
    {
        await using var context = CreateContext();
        const long wrapperId = 2_210;
        const long sourceId = 2_211;
        context.ObjectsTb.AddRange(
            User(ViewerId, "Sharer"),
            Post(wrapperId, GraphObjectType.FeedPost, "wrapper survives", privacy: 0));
        if (sourceExists)
        {
            context.ObjectsTb.AddRange(
                User(FeedAuthorId, "Source author"),
                Post(sourceId, GraphObjectType.FeedPost, "private now", privacy: 3));
            context.AssociationsTb.Add(Edge(sourceId, GraphAssociationType.AuthoredBy, FeedAuthorId));
        }
        context.AssociationsTb.AddRange(
            Edge(wrapperId, GraphAssociationType.AuthoredBy, ViewerId),
            Edge(wrapperId, GraphAssociationType.Share, sourceId));
        await context.SaveChangesAsync();
        var service = CreateContentService(context);

        var wrapper = Assert.IsType<FeedPostDetailResult>(
            await service.GetPostDetailAsync(ViewerId, wrapperId));

        Assert.Equal("wrapper survives", wrapper.Content);
        Assert.NotNull(wrapper.SharedSource);
        Assert.False(wrapper.SharedSource.IsAvailable);
        Assert.Equal(sourceId, wrapper.SharedSource.Id);
        Assert.Null(wrapper.SharedSource.Content);
    }

    private static long PostId(IHomePostResult post) => post switch
    {
        FeedPostDetailResult feed => feed.Id,
        GroupPostDetailResult group => group.Id,
        _ => 0
    };

    private static ContentGraphService CreateContentService(MyDbContext context) => new(
        context,
        Mock.Of<IObjectService>(),
        Mock.Of<IAssociationService>(),
        Mock.Of<IExternalServiceClient>());

    private static MyDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MyDbContext(options);
    }

    private static Objects User(long id, string name) => new()
    {
        id = id,
        otype = GraphObjectType.User,
        data = new JsonObject
        {
            ["name"] = name,
            ["avatar"] = $"https://cdn.example/{id}.jpg",
            ["privacy"] = 1,
            ["verify"] = ""
        }.ToJsonString()
    };

    private static Objects Group(long id, string name, int privacy) => new()
    {
        id = id,
        otype = GraphObjectType.Group,
        data = new JsonObject
        {
            ["name"] = name,
            ["avatar"] = "https://cdn.example/group.jpg",
            ["privacy"] = privacy
        }.ToJsonString()
    };

    private static Objects Post(long id, short type, string content, int privacy) => new()
    {
        id = id,
        otype = type,
        data = new JsonObject
        {
            ["content"] = content,
            ["privacy"] = privacy,
            ["create"] = DateTimeOffset.UtcNow.ToString("O")
        }.ToJsonString()
    };

    private static Objects Media(long id, string url, int type = GraphMediaType.Photo) => new()
    {
        id = id,
        otype = GraphObjectType.Media,
        data = new JsonObject { ["type"] = type, ["url"] = url }.ToJsonString()
    };

    private static Associations Edge(long id1, short type, long id2, long time = 1) => new()
    {
        id1 = id1,
        atype = type,
        id2 = id2,
        time = time
    };
}
