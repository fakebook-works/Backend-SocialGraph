namespace SocialGraph.Api.SubGraphQL;

using HotChocolate.Types;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Infrastructure;
using SocialGraph.Api.Service;

[ExtendObjectType(typeof(ReelRecommendationItemResult))]
public sealed class ReelRecommendationItemResolvers
{
    public async Task<ContentResult?> GetReelAsync(
        [Parent] ReelRecommendationItemResult item,
        [Service] IContentGraphService contentGraphService,
        [Service] ISocialReadModelService readModels,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        var viewerId = trustedCaller.RequireUserId();
        var reel = await contentGraphService.GetContentAsync(item.ReelId, cancellationToken);
        if (reel is null || reel.Type != GraphObjectType.Reel)
        {
            return null;
        }

        if (!await readModels.CanViewTargetAsync(viewerId, item.ReelId, cancellationToken))
        {
            return null;
        }

        return reel;
    }
}

