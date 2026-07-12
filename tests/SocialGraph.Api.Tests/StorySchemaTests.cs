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
        Assert.Contains("recordGroupVisit", schema);
        Assert.Contains("createFeedPost", schema);
        Assert.Contains("createNormalStory", schema);
        Assert.Contains("createShareStory", schema);
        Assert.Contains("deleteStory", schema);
        Assert.DoesNotContain("createStory(", schema);
        Assert.DoesNotContain("postCandidates", schema);
    }
}
