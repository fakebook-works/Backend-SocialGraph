namespace SocialGraph.Api.Tests;

using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.SubGraphQL;

public sealed class StorySchemaTests
{
    [Fact]
    public async Task Schema_ExposesFinalStoryUnionAndDeprecatedCompatibilityMutation()
    {
        var services = new ServiceCollection();
        services
            .AddGraphQLServer()
            .AddQueryType<Query>()
            .AddMutationType<Mutation>()
            .AddType<NormalStoryResult>()
            .AddType<FeedPostShareStoryResult>()
            .AddType<ReelShareStoryResult>()
            .AddType<FeedPostSharedSourceResult>()
            .AddType<ReelSharedSourceResult>();
        await using var provider = services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<IRequestExecutorProvider>();
        var executor = await resolver.GetExecutorAsync();
        var schema = executor.Schema.ToString();

        Assert.Contains("union HomeStory = NormalStory | FeedPostShareStory | ReelShareStory", schema);
        Assert.Contains("createNormalStory", schema);
        Assert.Contains("createShareStory", schema);
        Assert.Contains("createStory", schema);
        Assert.Contains("@deprecated(reason: \"Use createNormalStory.\")", schema);
    }
}
