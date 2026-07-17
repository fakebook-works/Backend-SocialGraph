namespace SocialGraph.Api.Service;

public static class GraphAssociationRules
{
    private static readonly IReadOnlyDictionary<short, short> InverseTypes = new Dictionary<short, short>
    {
        [GraphAssociationType.Friend] = GraphAssociationType.Friend,
        [GraphAssociationType.FriendRequest] = GraphAssociationType.HaveFriendRequest,
        [GraphAssociationType.HaveFriendRequest] = GraphAssociationType.FriendRequest,
        [GraphAssociationType.Followed] = GraphAssociationType.FollowedBy,
        [GraphAssociationType.FollowedBy] = GraphAssociationType.Followed,
        [GraphAssociationType.Blocked] = GraphAssociationType.BlockedBy,
        [GraphAssociationType.BlockedBy] = GraphAssociationType.Blocked,
        [GraphAssociationType.Liked] = GraphAssociationType.LikedBy,
        [GraphAssociationType.LikedBy] = GraphAssociationType.Liked,
        [GraphAssociationType.Authored] = GraphAssociationType.AuthoredBy,
        [GraphAssociationType.AuthoredBy] = GraphAssociationType.Authored,
        [GraphAssociationType.Published] = GraphAssociationType.PublishedIn,
        [GraphAssociationType.PublishedIn] = GraphAssociationType.Published,
        [GraphAssociationType.Member] = GraphAssociationType.HaveMember,
        [GraphAssociationType.HaveMember] = GraphAssociationType.Member,
        [GraphAssociationType.Admin] = GraphAssociationType.HaveAdmin,
        [GraphAssociationType.HaveAdmin] = GraphAssociationType.Admin,
        [GraphAssociationType.GroupJoinRequest] = GraphAssociationType.HaveGroupJoinRequest,
        [GraphAssociationType.HaveGroupJoinRequest] = GraphAssociationType.GroupJoinRequest,
        [GraphAssociationType.Watched] = GraphAssociationType.WatchedBy,
        [GraphAssociationType.WatchedBy] = GraphAssociationType.Watched,
        [GraphAssociationType.HaveComment] = GraphAssociationType.Comment,
        [GraphAssociationType.Comment] = GraphAssociationType.HaveComment,
        [GraphAssociationType.Share] = GraphAssociationType.SharedBy,
        [GraphAssociationType.SharedBy] = GraphAssociationType.Share
    };

    public static bool IsKnown(short associationType) =>
        associationType is >= GraphAssociationType.MinValue and <= GraphAssociationType.MaxValue;

    public static bool TryGetInverse(short associationType, out short inverseType) =>
        InverseTypes.TryGetValue(associationType, out inverseType);

    public static bool IsValidForObjectTypes(short associationType, short sourceType, short targetType)
    {
        return associationType switch
        {
            GraphAssociationType.Friend or
            GraphAssociationType.FriendRequest or
            GraphAssociationType.HaveFriendRequest or
            GraphAssociationType.Followed or
            GraphAssociationType.FollowedBy or
            GraphAssociationType.Blocked or
            GraphAssociationType.BlockedBy => Is(sourceType, GraphObjectType.User) && Is(targetType, GraphObjectType.User),

            GraphAssociationType.Liked =>
                Is(sourceType, GraphObjectType.User) && IsReactable(targetType),
            GraphAssociationType.LikedBy =>
                IsReactable(sourceType) && Is(targetType, GraphObjectType.User),

            GraphAssociationType.Authored =>
                Is(sourceType, GraphObjectType.User) && IsAuthoredContent(targetType),
            GraphAssociationType.AuthoredBy =>
                IsAuthoredContent(sourceType) && Is(targetType, GraphObjectType.User),

            GraphAssociationType.Published =>
                Is(sourceType, GraphObjectType.Group) && Is(targetType, GraphObjectType.GroupPost),
            GraphAssociationType.PublishedIn =>
                Is(sourceType, GraphObjectType.GroupPost) && Is(targetType, GraphObjectType.Group),

            GraphAssociationType.Member or GraphAssociationType.Admin or GraphAssociationType.GroupJoinRequest =>
                Is(sourceType, GraphObjectType.User) && Is(targetType, GraphObjectType.Group),
            GraphAssociationType.HaveMember or GraphAssociationType.HaveAdmin or GraphAssociationType.HaveGroupJoinRequest =>
                Is(sourceType, GraphObjectType.Group) && Is(targetType, GraphObjectType.User),

            GraphAssociationType.Watched =>
                Is(sourceType, GraphObjectType.User) && IsWatchable(targetType),
            GraphAssociationType.WatchedBy =>
                IsWatchable(sourceType) && Is(targetType, GraphObjectType.User),

            GraphAssociationType.HaveComment =>
                IsCommentTarget(sourceType) && Is(targetType, GraphObjectType.Comment),
            GraphAssociationType.Comment =>
                Is(sourceType, GraphObjectType.Comment) && IsCommentTarget(targetType),

            GraphAssociationType.Share => IsShareContainer(sourceType) && IsShareTarget(targetType),
            GraphAssociationType.SharedBy => IsShareTarget(sourceType) && IsShareContainer(targetType),
            GraphAssociationType.Tagged =>
                Is(sourceType, GraphObjectType.FeedPost) && Is(targetType, GraphObjectType.User),
            GraphAssociationType.Mentioned =>
                IsMentionSource(sourceType) && Is(targetType, GraphObjectType.User),
            GraphAssociationType.Saved =>
                Is(sourceType, GraphObjectType.User) && IsSaveable(targetType),
            GraphAssociationType.Contained =>
                IsMediaContainer(sourceType) && Is(targetType, GraphObjectType.Media),
            GraphAssociationType.Visited =>
                Is(sourceType, GraphObjectType.User) && Is(targetType, GraphObjectType.Group),
            _ => false
        };
    }

    private static bool Is(short actual, short expected) => actual == expected;
    private static bool IsReactable(short value) => value is GraphObjectType.FeedPost or GraphObjectType.GroupPost or GraphObjectType.Reel or GraphObjectType.Story or GraphObjectType.Comment;
    private static bool IsAuthoredContent(short value) => value is GraphObjectType.FeedPost or GraphObjectType.GroupPost or GraphObjectType.Reel or GraphObjectType.Story or GraphObjectType.Comment;
    private static bool IsWatchable(short value) => value is GraphObjectType.Reel or GraphObjectType.Story;
    private static bool IsCommentTarget(short value) => value is GraphObjectType.FeedPost or GraphObjectType.GroupPost or GraphObjectType.Reel or GraphObjectType.Comment;
    private static bool IsShareContainer(short value) => value is GraphObjectType.FeedPost or GraphObjectType.Story;
    private static bool IsShareTarget(short value) => value is GraphObjectType.FeedPost or GraphObjectType.Reel;
    private static bool IsMentionSource(short value) => value is GraphObjectType.FeedPost or GraphObjectType.GroupPost or GraphObjectType.Reel or GraphObjectType.Story or GraphObjectType.Comment;
    private static bool IsSaveable(short value) => value is GraphObjectType.FeedPost or GraphObjectType.GroupPost or GraphObjectType.Reel;
    private static bool IsMediaContainer(short value) => value is GraphObjectType.FeedPost or GraphObjectType.GroupPost or GraphObjectType.Reel or GraphObjectType.Story;
}
