namespace SocialGraph.Api.SubGraphQL;

using HotChocolate;
using HotChocolate.ApolloFederation.Resolvers;
using HotChocolate.ApolloFederation.Types;
using HotChocolate.Types;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Infrastructure;
using SocialGraph.Api.Service;

[GraphQLName("User")]
public sealed class FederatedUser
{
    public FederatedUser(
        long id,
        string name,
        string avatar,
        string bio,
        bool isVerified,
        long friendCount,
        long followerCount,
        long followingCount,
        int privacy)
    {
        Id = id;
        Name = name;
        Avatar = avatar;
        Bio = bio;
        IsVerified = isVerified;
        FriendCount = friendCount;
        FollowerCount = followerCount;
        FollowingCount = followingCount;
        Privacy = privacy;
    }

    [Key]
    public long Id { get; }

    public string Name { get; }

    public string Avatar { get; }

    public string Bio { get; }

    public bool IsVerified { get; }

    public long FriendCount { get; }

    public long FollowerCount { get; }

    public long FollowingCount { get; }

    public int Privacy { get; }

    [ReferenceResolver]
    /// <remarks>
    /// Goes through a DataLoader because Fusion calls this once per representation in
    /// _entities. Resolving each one on its own meant two block checks and a profile read
    /// per user — about a hundred and twenty queries to render an inbox of thirty people.
    /// </remarks>
    public static async Task<FederatedUser?> ResolveReferenceAsync(
        long id,
        FederatedUserByIdDataLoader userById,
        CancellationToken cancellationToken)
    {
        return await userById.LoadAsync(id, cancellationToken);
    }

    internal static async Task<FederatedUser?> ResolveForViewerAsync(
        long viewerId,
        long userId,
        IUserGraphService userGraphService,
        IAssociationService associationService,
        CancellationToken cancellationToken)
    {
        if (viewerId != userId &&
            (await associationService.HasAssociationAsync(viewerId, GraphAssociationType.Blocked, userId, cancellationToken) ||
             await associationService.HasAssociationAsync(viewerId, GraphAssociationType.BlockedBy, userId, cancellationToken)))
        {
            return null;
        }

        var profile = await userGraphService.GetProfileAsync(userId, cancellationToken);
        return profile is null
            ? null
            : new FederatedUser(
                profile.Id,
                profile.Name,
                profile.Avatar,
                profile.Bio,
                profile.IsVerified,
                profile.FriendCount,
                profile.FollowerCount,
                profile.FollowingCount,
                profile.Privacy);
    }
}

[GraphQLName("UserSearchResult")]
public sealed record UserSearchHydrationResult(
    [property: GraphQLType(typeof(NonNullType<IdType>))] long ReferenceId,
    FederatedUser User,
    bool ViewerIsSelf,
    bool ViewerIsFriend,
    bool ViewerIsFollowing);

[GraphQLName("GroupSearchResult")]
public sealed record GroupSearchHydrationResult(
    [property: GraphQLType(typeof(NonNullType<IdType>))] long ReferenceId,
    GroupResult Group,
    bool ViewerIsMember);

[GraphQLName("FeedPostSearchResult")]
public sealed record FeedPostSearchHydrationResult(
    [property: GraphQLType(typeof(NonNullType<IdType>))] long ReferenceId,
    FeedPostDetailResult Post);

[GraphQLName("GroupPostSearchResult")]
public sealed record GroupPostSearchHydrationResult(
    [property: GraphQLType(typeof(NonNullType<IdType>))] long ReferenceId,
    GroupPostDetailResult Post);

[GraphQLName("ReelSearchResult")]
public sealed record ReelSearchHydrationResult(
    [property: GraphQLType(typeof(NonNullType<IdType>))] long ReferenceId,
    ContentResult Reel,
    UserSummaryResult Author);
