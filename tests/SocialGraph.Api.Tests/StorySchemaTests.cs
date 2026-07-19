namespace SocialGraph.Api.Tests;

using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.SubGraphQL;

public sealed class StorySchemaTests
{
    [Fact]
    public async Task Schema_ExposesCanonicalHomeAndStoryOperations()
    {
        var services = new ServiceCollection();
        services
            .AddGraphQLServer()
            .AddQueryType<Query>()
            .AddMutationType<Mutation>()
            .AddType<RecommendationItemResult>()
            .AddTypeExtension<RecommendationItemResolvers>()
            .AddType<FeedPostDetailResult>()
            .AddType<GroupPostDetailResult>()
            .AddType<NormalStoryResult>()
            .AddType<FeedPostShareStoryResult>()
            .AddType<ReelShareStoryResult>()
            .AddType<FeedPostSharedSourceResult>()
            .AddType<ReelSharedSourceResult>();
        await using var provider = services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<IRequestExecutorProvider>();
        var executor = await resolver.GetExecutorAsync();
        var schema = executor.Schema.ToString();

        Assert.Contains("union HomePost = FeedPostDetail | GroupPostDetail", schema);
        Assert.Contains("recommendationItem(postId: ID!): RecommendationItem", schema);
        Assert.Contains("post: HomePost", schema);
        Assert.Contains("union HomeStory = NormalStory | FeedPostShareStory | ReelShareStory", schema);
        Assert.Contains("visitedGroups", schema);
        Assert.Contains("postDetail", schema);
        Assert.Contains("postDetails", schema);
        Assert.Contains("homeStories", schema);
        Assert.Contains("myStories", schema);
        Assert.Contains("hasUnseen", schema);
        Assert.Contains("recordGroupVisit", schema);
        Assert.Contains("incomingFriendRequests", schema);
        Assert.Contains("groupJoinRequests", schema);
        Assert.Contains("userSearchResult(referenceId: ID!): UserSearchResult", schema);
        Assert.Contains("groupSearchResult(referenceId: ID!): GroupSearchResult", schema);
        Assert.Contains("feedPostSearchResult(referenceId: ID!): FeedPostSearchResult", schema);
        Assert.Contains("groupPostSearchResult(referenceId: ID!): GroupPostSearchResult", schema);
        Assert.Contains("reelSearchResult(referenceId: ID!): ReelSearchResult", schema);
        // Fusion executes this field directly against the SocialGraph subgraph.
        // The Gateway composition marks it internal, so it is not public there.
        Assert.Contains("userById(id: Long!): User", schema);
        Assert.Contains("profiles(userIds: [Long!]!): [UserProfileResult!]!", schema);
        Assert.Contains("groups(groupIds: [Long!]!): [GroupResult!]!", schema);
        Assert.Contains("profilePosts", schema);
        Assert.Contains("profileReels", schema);
        Assert.Contains("memberGroups", schema);
        Assert.Contains("adminGroups", schema);
        Assert.Contains("relationshipState", schema);
        Assert.Contains("groupViewerState", schema);
        Assert.Contains("pendingGroupJoins", schema);
        Assert.Contains("groupMembers", schema);
        Assert.Contains("groupAdmins", schema);
        Assert.Contains("groupPosts", schema);
        Assert.Contains("groupUserPosts", schema);
        Assert.Contains("userPhotos", schema);
        Assert.Contains("groupPhotos", schema);
        Assert.Contains("groupUserPhotos", schema);
        Assert.Contains("myFeedPhotoCandidates", schema);
        Assert.Contains("groupPhotoCandidates", schema);
        Assert.DoesNotContain("ownedMedia", schema);
        Assert.Contains("likedReels", schema);
        Assert.Contains("sharedReels", schema);
        Assert.Contains("watchedReels", schema);
        Assert.Contains("comments", schema);
        Assert.Contains("mentions:", schema);
        Assert.Contains("taggedUsers:", schema);
        Assert.Contains("type MentionUserResult", schema);
        Assert.Contains("contentEngagement", schema);
        Assert.Contains("savedContent", schema);
        Assert.Contains("storyViewers", schema);
        Assert.Contains("createFeedPost", schema);
        Assert.Contains("inviteGroupUser", schema);
        Assert.Contains("removeUserAvatar", schema);
        Assert.Contains("removeUserBackground", schema);
        Assert.Contains("removeGroupAvatar", schema);
        Assert.Contains("removeGroupBackground", schema);
        Assert.Contains("content: String", schema);
        Assert.Contains("media: [MediaInput!]", schema);
        Assert.Contains("privacy: Int", schema);
        Assert.Contains("createNormalStory", schema);
        Assert.Contains("createShareStory", schema);
        Assert.Contains("deleteStory", schema);
        Assert.DoesNotContain("createStory(", schema);
        Assert.DoesNotContain("postCandidates", schema);
        Assert.DoesNotContain("addObject(", schema);
        Assert.DoesNotContain("addAssociation(", schema);
        Assert.DoesNotContain("deleteObject(", schema);
        Assert.DoesNotContain("object(id:", schema);
        Assert.DoesNotContain("association(id1:", schema);
    }
}
