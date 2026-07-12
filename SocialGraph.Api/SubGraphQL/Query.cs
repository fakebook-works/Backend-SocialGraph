namespace SocialGraph.Api.SubGraphQL;

using HotChocolate;
using HotChocolate.Types;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Infrastructure;
using SocialGraph.Api.Service;

public class Query
{
    public RecommendationItemResult? GetRecommendationItem(
        [GraphQLType(typeof(NonNullType<IdType>))] long postId)
    {
        return postId > 0 ? new RecommendationItemResult(postId) : null;
    }

    public Task<SocialGraphObjectResult?> GetObjectAsync(
        long id,
        [Service] IObjectService objectService,
        CancellationToken cancellationToken)
    {
        return objectService.RetrieveObjectAsync(id, cancellationToken);
    }

    public Task<AssociationPageResult> GetAssociationAsync(
        long id1,
        short atype,
        string? cursor,
        int limit,
        [Service] IAssociationService associationService,
        CancellationToken cancellationToken)
    {
        return associationService.RetrieveAssociationAsync(id1, atype, cursor, limit, cancellationToken);
    }

    public Task<long> GetAssociationCountAsync(
        long id1,
        short atype,
        [Service] IAssociationService associationService,
        CancellationToken cancellationToken)
    {
        return associationService.CountAssociationAsync(id1, atype, cancellationToken);
    }

    public Task<UserProfileResult?> GetProfileAsync(
        long userId,
        [Service] IUserGraphService userGraphService,
        CancellationToken cancellationToken)
    {
        return userGraphService.GetProfileAsync(userId, cancellationToken);
    }

    public Task<GroupResult?> GetGroupAsync(
        long groupId,
        [Service] IGroupGraphService groupGraphService,
        CancellationToken cancellationToken)
    {
        return groupGraphService.GetGroupAsync(groupId, cancellationToken);
    }

    public Task<VisitedGroupPageResult> GetVisitedGroupsAsync(
        long userId,
        int limit,
        string? cursor,
        [Service] IGroupGraphService groupGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return groupGraphService.GetVisitedGroupsAsync(userId, limit, cursor, cancellationToken);
    }

    public Task<ContentResult?> GetContentAsync(
        long contentId,
        [Service] IContentGraphService contentGraphService,
        CancellationToken cancellationToken)
    {
        return contentGraphService.GetContentAsync(contentId, cancellationToken);
    }

    public Task<IHomePostResult?> GetPostDetailAsync(
        long postId,
        [Service] IContentGraphService contentGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        var viewerId = trustedCaller.RequireUserId();
        return contentGraphService.GetPostDetailAsync(viewerId, postId, cancellationToken);
    }

    public Task<IReadOnlyList<IHomePostResult>> GetPostDetailsAsync(
        IReadOnlyList<long> postIds,
        [Service] IContentGraphService contentGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        if (postIds.Count > ContentGraphService.MaxPostDetailIds)
        {
            throw new GraphQLException(
                ErrorBuilder.New()
                    .SetCode("BAD_USER_INPUT")
                    .SetMessage($"At most {ContentGraphService.MaxPostDetailIds} post IDs can be requested.")
                    .Build());
        }

        var viewerId = trustedCaller.RequireUserId();
        return contentGraphService.GetPostDetailsAsync(viewerId, postIds, cancellationToken);
    }

    public Task<HomeStoryPageResult> GetHomeStoriesAsync(
        long userId,
        int limit,
        string? cursor,
        [Service] IContentGraphService contentGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return contentGraphService.GetHomeStoriesAsync(userId, limit, cursor, cancellationToken);
    }

    public Task<HomeStoryBucketResult?> GetMyStoriesAsync(
        long userId,
        [Service] IContentGraphService contentGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return contentGraphService.GetMyStoriesAsync(userId, cancellationToken);
    }

    public async Task<IReadOnlyList<long>> GetRelationIdsAsync(
        long id1,
        short atype,
        string? cursor,
        int limit,
        [Service] IAssociationService associationService,
        CancellationToken cancellationToken)
    {
        var page = await associationService.RetrieveAssociationAsync(id1, atype, cursor, limit, cancellationToken);
        return page.items.Select(item => item.id2).ToArray();
    }

    public Task<IReadOnlyList<CandidateItemResult>> GetReelCandidatesAsync(
        long userId,
        int limit,
        [Service] ICandidateService candidateService,
        CancellationToken cancellationToken)
    {
        return candidateService.GetReelCandidatesAsync(userId, limit, cancellationToken);
    }
}
