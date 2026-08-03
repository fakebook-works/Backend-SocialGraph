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
    public async Task Public_group_post_projects_the_viewers_pending_join_request()
    {
        await using var context = CreateContext();
        const long groupPostId = 1_002;
        context.ObjectsTb.AddRange(
            User(GroupAuthorId, "Group Author"),
            Group(GroupId, "Pending Group", privacy: 0),
            Post(groupPostId, GraphObjectType.GroupPost, "public group post", privacy: 0));
        context.AssociationsTb.AddRange(
            Edge(groupPostId, GraphAssociationType.AuthoredBy, GroupAuthorId),
            Edge(groupPostId, GraphAssociationType.PublishedIn, GroupId),
            Edge(ViewerId, GraphAssociationType.GroupJoinRequest, GroupId));
        await context.SaveChangesAsync();

        var groupPost = Assert.IsType<GroupPostDetailResult>(
            await CreateContentService(context).GetPostDetailAsync(ViewerId, groupPostId));

        Assert.True(groupPost.Group.CanJoin);
        Assert.True(groupPost.Group.JoinRequestPending);
    }

    [Fact]
    public async Task Public_group_post_does_not_expose_another_users_pending_join_request()
    {
        await using var context = CreateContext();
        const long otherUserId = 101;
        const long groupPostId = 1_003;
        context.ObjectsTb.AddRange(
            User(GroupAuthorId, "Group Author"),
            User(otherUserId, "Other User"),
            Group(GroupId, "Pending Group", privacy: 0),
            Post(groupPostId, GraphObjectType.GroupPost, "public group post", privacy: 0));
        context.AssociationsTb.AddRange(
            Edge(groupPostId, GraphAssociationType.AuthoredBy, GroupAuthorId),
            Edge(groupPostId, GraphAssociationType.PublishedIn, GroupId),
            Edge(otherUserId, GraphAssociationType.GroupJoinRequest, GroupId));
        await context.SaveChangesAsync();

        var groupPost = Assert.IsType<GroupPostDetailResult>(
            await CreateContentService(context).GetPostDetailAsync(ViewerId, groupPostId));

        Assert.True(groupPost.Group.CanJoin);
        Assert.False(groupPost.Group.JoinRequestPending);
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
            Post(reelId, GraphObjectType.Reel, "reel on home", privacy: 2, aspectRatio: 9d / 16d, focalPointX: 0.25d, focalPointY: 0.75d),
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
        Assert.NotNull(visible.AspectRatio);
        Assert.Equal(9d / 16d, visible.AspectRatio.GetValueOrDefault(), precision: 6);
        Assert.Equal(0.25d, visible.FocalPointX);
        Assert.Equal(0.75d, visible.FocalPointY);
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

    [Fact]
    public async Task SharedFeedWrapper_ProjectsVisibleReelPresentationMetadata()
    {
        await using var context = CreateContext();
        const long wrapperId = 2_203;
        const long sourceId = 2_204;
        const long sourceMediaId = 2_205;
        context.ObjectsTb.AddRange(
            User(ViewerId, "Sharer"),
            User(FeedAuthorId, "Reel author"),
            Post(wrapperId, GraphObjectType.FeedPost, "shared reel", privacy: 0),
            Post(sourceId, GraphObjectType.Reel, "cropped reel", privacy: 0, aspectRatio: 9d / 16d, focalPointX: 0.2d, focalPointY: 0.8d),
            Media(sourceMediaId, "https://cdn.example/source-reel.mp4", GraphMediaType.Video));
        context.AssociationsTb.AddRange(
            Edge(wrapperId, GraphAssociationType.AuthoredBy, ViewerId),
            Edge(wrapperId, GraphAssociationType.Share, sourceId),
            Edge(sourceId, GraphAssociationType.AuthoredBy, FeedAuthorId),
            Edge(sourceId, GraphAssociationType.Contained, sourceMediaId));
        await context.SaveChangesAsync();

        var wrapper = Assert.IsType<FeedPostDetailResult>(
            await CreateContentService(context).GetPostDetailAsync(ViewerId, wrapperId));

        Assert.NotNull(wrapper.SharedSource);
        Assert.True(wrapper.SharedSource.IsAvailable);
        Assert.Equal(GraphObjectType.Reel, wrapper.SharedSource.Type);
        Assert.Equal(9d / 16d, wrapper.SharedSource.AspectRatio.GetValueOrDefault(), precision: 6);
        Assert.Equal(0.2d, wrapper.SharedSource.FocalPointX);
        Assert.Equal(0.8d, wrapper.SharedSource.FocalPointY);
    }

    [Theory]
    [InlineData(1, GraphAssociationType.Followed)]
    [InlineData(2, GraphAssociationType.Friend)]
    public async Task SharedFeedWrapper_ProjectsPrivateSourceForAnAuthorizedViewer(
        int sourcePrivacy,
        short relationType)
    {
        await using var context = CreateContext();
        const long wrapperId = 2_205;
        const long sourceId = 2_206;
        context.ObjectsTb.AddRange(
            User(ViewerId, "Authorized viewer"),
            User(FeedAuthorId, "Private source author"),
            Post(wrapperId, GraphObjectType.FeedPost, "wrapper", privacy: 0),
            Post(sourceId, GraphObjectType.FeedPost, "authorized private source", privacy: sourcePrivacy));
        context.AssociationsTb.AddRange(
            Edge(wrapperId, GraphAssociationType.AuthoredBy, ViewerId),
            Edge(wrapperId, GraphAssociationType.Share, sourceId),
            Edge(sourceId, GraphAssociationType.AuthoredBy, FeedAuthorId),
            Edge(ViewerId, relationType, FeedAuthorId));
        await context.SaveChangesAsync();
        var service = CreateContentService(context);

        var wrapper = Assert.IsType<FeedPostDetailResult>(
            await service.GetPostDetailAsync(ViewerId, wrapperId));

        Assert.NotNull(wrapper.SharedSource);
        Assert.True(wrapper.SharedSource.IsAvailable);
        Assert.Equal("authorized private source", wrapper.SharedSource.Content);
        Assert.Equal(sourcePrivacy, wrapper.SharedSource.Privacy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedFeedWrapper_HidesPrivateSourceWithoutCurrentAccess(bool blocked)
    {
        await using var context = CreateContext();
        const long wrapperId = 2_207;
        const long sourceId = 2_208;
        context.ObjectsTb.AddRange(
            User(ViewerId, "Wrapper viewer"),
            User(FeedAuthorId, "Private source author"),
            Post(wrapperId, GraphObjectType.FeedPost, "wrapper", privacy: 0),
            Post(sourceId, GraphObjectType.FeedPost, "friends only", privacy: 2));
        context.AssociationsTb.AddRange(
            Edge(wrapperId, GraphAssociationType.AuthoredBy, ViewerId),
            Edge(wrapperId, GraphAssociationType.Share, sourceId),
            Edge(sourceId, GraphAssociationType.AuthoredBy, FeedAuthorId));
        if (blocked)
        {
            context.AssociationsTb.AddRange(
                Edge(ViewerId, GraphAssociationType.Friend, FeedAuthorId),
                Edge(ViewerId, GraphAssociationType.BlockedBy, FeedAuthorId));
        }
        await context.SaveChangesAsync();
        var service = CreateContentService(context);

        var wrapper = Assert.IsType<FeedPostDetailResult>(
            await service.GetPostDetailAsync(ViewerId, wrapperId));

        Assert.NotNull(wrapper.SharedSource);
        Assert.False(wrapper.SharedSource.IsAvailable);
        Assert.Null(wrapper.SharedSource.Content);
    }

    [Fact]
    public async Task SharedFeedWrapper_HidesReelPresentationMetadataWithoutCurrentAccess()
    {
        await using var context = CreateContext();
        const long wrapperId = 2_209;
        const long sourceId = 2_210;
        context.ObjectsTb.AddRange(
            User(ViewerId, "Wrapper viewer"),
            User(FeedAuthorId, "Private Reel author"),
            Post(wrapperId, GraphObjectType.FeedPost, "wrapper", privacy: 0),
            Post(sourceId, GraphObjectType.Reel, "private crop", privacy: 3, aspectRatio: 9d / 16d, focalPointX: 0.1d, focalPointY: 0.9d));
        context.AssociationsTb.AddRange(
            Edge(wrapperId, GraphAssociationType.AuthoredBy, ViewerId),
            Edge(wrapperId, GraphAssociationType.Share, sourceId),
            Edge(sourceId, GraphAssociationType.AuthoredBy, FeedAuthorId));
        await context.SaveChangesAsync();

        var wrapper = Assert.IsType<FeedPostDetailResult>(
            await CreateContentService(context).GetPostDetailAsync(ViewerId, wrapperId));

        Assert.NotNull(wrapper.SharedSource);
        Assert.False(wrapper.SharedSource.IsAvailable);
        Assert.Null(wrapper.SharedSource.Content);
        Assert.Null(wrapper.SharedSource.AspectRatio);
        Assert.Null(wrapper.SharedSource.FocalPointX);
        Assert.Null(wrapper.SharedSource.FocalPointY);
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

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task SharedGroupPost_ProjectsFullSourceOnlyForCurrentGroupAudience(int groupPrivacy, bool member)
    {
        await using var context = CreateContext();
        const long wrapperId = 2_220;
        const long sourceId = 2_221;
        const long groupId = 2_222;
        context.ObjectsTb.AddRange(
            User(ViewerId, "Viewer"),
            User(FeedAuthorId, "Group author"),
            Post(wrapperId, GraphObjectType.FeedPost, "wrapper", 0),
            Post(sourceId, GraphObjectType.GroupPost, "group source", 0),
            Group(groupId, "Source group", groupPrivacy));
        context.AssociationsTb.AddRange(
            Edge(wrapperId, GraphAssociationType.AuthoredBy, ViewerId),
            Edge(wrapperId, GraphAssociationType.Share, sourceId),
            Edge(sourceId, GraphAssociationType.AuthoredBy, FeedAuthorId),
            Edge(sourceId, GraphAssociationType.PublishedIn, groupId));
        if (member) context.AssociationsTb.Add(Edge(ViewerId, GraphAssociationType.Member, groupId));
        await context.SaveChangesAsync();

        var wrapper = Assert.IsType<FeedPostDetailResult>(
            await CreateContentService(context).GetPostDetailAsync(ViewerId, wrapperId));

        Assert.NotNull(wrapper.SharedSource);
        Assert.True(wrapper.SharedSource.IsAvailable);
        Assert.Equal(GraphObjectType.GroupPost, wrapper.SharedSource.Type);
        Assert.Equal("group source", wrapper.SharedSource.Content);
        Assert.Equal("Group author", wrapper.SharedSource.Author?.Name);
        Assert.Equal(groupPrivacy, wrapper.SharedSource.Privacy);
        Assert.Equal(groupId, wrapper.SharedSource.Group?.Id);
        Assert.Equal(member, wrapper.SharedSource.Group?.ViewerIsMember);
        Assert.False(wrapper.SharedSource.RequiresGroupMembership);
    }

    [Fact]
    public async Task SharedPrivateGroupPost_ReturnsJoinableGroupMetadataButNoProtectedSourceFields()
    {
        await using var context = CreateContext();
        const long wrapperId = 2_223;
        const long sourceId = 2_224;
        const long groupId = 2_225;
        context.ObjectsTb.AddRange(
            User(ViewerId, "Viewer"),
            User(FeedAuthorId, "Hidden author"),
            Post(wrapperId, GraphObjectType.FeedPost, "wrapper", 0),
            Post(sourceId, GraphObjectType.GroupPost, "must stay private", 0),
            Group(groupId, "Private source group", 1));
        context.AssociationsTb.AddRange(
            Edge(wrapperId, GraphAssociationType.AuthoredBy, ViewerId),
            Edge(wrapperId, GraphAssociationType.Share, sourceId),
            Edge(sourceId, GraphAssociationType.AuthoredBy, FeedAuthorId),
            Edge(sourceId, GraphAssociationType.PublishedIn, groupId),
            Edge(FeedAuthorId, GraphAssociationType.Member, groupId));
        await context.SaveChangesAsync();

        var wrapper = Assert.IsType<FeedPostDetailResult>(
            await CreateContentService(context).GetPostDetailAsync(ViewerId, wrapperId));

        Assert.NotNull(wrapper.SharedSource);
        Assert.False(wrapper.SharedSource.IsAvailable);
        Assert.True(wrapper.SharedSource.RequiresGroupMembership);
        Assert.Equal(groupId, wrapper.SharedSource.Group?.Id);
        Assert.Equal(1, wrapper.SharedSource.Group?.MemberCount);
        Assert.Null(wrapper.SharedSource.Content);
        Assert.Null(wrapper.SharedSource.Author);
        Assert.Empty(wrapper.SharedSource.Media);
        Assert.Empty(wrapper.SharedSource.Mentions ?? Array.Empty<MentionUserResult>());
    }

    [Fact]
    public async Task SharedGroup_ProjectsOnlySafeGroupCardMetadata()
    {
        await using var context = CreateContext();
        const long wrapperId = 2_226;
        const long groupId = 2_227;
        context.ObjectsTb.AddRange(
            User(ViewerId, "Viewer"),
            Post(wrapperId, GraphObjectType.FeedPost, "group recommendation", 0),
            Group(groupId, "Shared group", 1));
        context.AssociationsTb.AddRange(
            Edge(wrapperId, GraphAssociationType.AuthoredBy, ViewerId),
            Edge(wrapperId, GraphAssociationType.Share, groupId));
        await context.SaveChangesAsync();

        var wrapper = Assert.IsType<FeedPostDetailResult>(
            await CreateContentService(context).GetPostDetailAsync(ViewerId, wrapperId));

        Assert.NotNull(wrapper.SharedSource);
        Assert.True(wrapper.SharedSource.IsAvailable);
        Assert.Equal(GraphObjectType.Group, wrapper.SharedSource.Type);
        Assert.Equal("Shared group", wrapper.SharedSource.Group?.Name);
        Assert.Null(wrapper.SharedSource.Author);
        Assert.Null(wrapper.SharedSource.Content);
        Assert.Empty(wrapper.SharedSource.Media);
    }

    [Fact]
    public async Task GroupShareWrapper_ProjectsItsCanonicalSharedSource()
    {
        await using var context = CreateContext();
        const long wrapperId = 2_228;
        const long sourceId = 2_229;
        const long destinationGroupId = 2_230;
        context.ObjectsTb.AddRange(
            User(ViewerId, "Viewer"),
            User(FeedAuthorId, "Source author"),
            Group(destinationGroupId, "Destination", 0),
            Post(wrapperId, GraphObjectType.GroupPost, "shared to group", 0),
            Post(sourceId, GraphObjectType.FeedPost, "source", 0));
        context.AssociationsTb.AddRange(
            Edge(wrapperId, GraphAssociationType.AuthoredBy, ViewerId),
            Edge(wrapperId, GraphAssociationType.PublishedIn, destinationGroupId),
            Edge(wrapperId, GraphAssociationType.Share, sourceId),
            Edge(sourceId, GraphAssociationType.AuthoredBy, FeedAuthorId));
        await context.SaveChangesAsync();

        var wrapper = Assert.IsType<GroupPostDetailResult>(
            await CreateContentService(context).GetPostDetailAsync(ViewerId, wrapperId));

        Assert.NotNull(wrapper.SharedSource);
        Assert.True(wrapper.SharedSource.IsAvailable);
        Assert.Equal(sourceId, wrapper.SharedSource.Id);
        Assert.Equal("source", wrapper.SharedSource.Content);
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

    private static Objects Post(long id, short type, string content, int privacy, double? aspectRatio = null, double? focalPointX = null, double? focalPointY = null) => new()
    {
        id = id,
        otype = type,
        data = CreatePostData(content, privacy, aspectRatio, focalPointX, focalPointY)
    };

    private static string CreatePostData(string content, int privacy, double? aspectRatio, double? focalPointX, double? focalPointY)
    {
        var data = new JsonObject
        {
            ["content"] = content,
            ["privacy"] = privacy,
            ["create"] = DateTimeOffset.UtcNow.ToString("O")
        };
        if (aspectRatio is { } value) data["aspectRatio"] = value;
        if (focalPointX is { } x) data["focalPointX"] = x;
        if (focalPointY is { } y) data["focalPointY"] = y;
        return data.ToJsonString();
    }

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
