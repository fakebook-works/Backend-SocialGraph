namespace SocialGraph.Api.SubGraphQL;

using HotChocolate.Types;
using SocialGraph.Api.Contracts;

[ExtendObjectType(typeof(RecommendationItemResult))]
public sealed class RecommendationItemResolvers
{
    public async Task<IHomePostResult?> GetPostAsync(
        [Parent] RecommendationItemResult item,
        HomePostByIdDataLoader postById,
        CancellationToken cancellationToken) =>
        await postById.LoadAsync(item.PostId, cancellationToken);
}
