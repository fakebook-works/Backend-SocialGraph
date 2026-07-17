namespace SocialGraph.Api.Service;

public static class GraphObjectType
{
    public const short User = 0;
    public const short Group = 1;
    public const short FeedPost = 2;
    public const short GroupPost = 3;
    public const short Reel = 4;
    public const short Story = 5;
    public const short Comment = 6;
    public const short Media = 7;
}

public static class GraphMediaType
{
    public const int Photo = 0;
    public const int Video = 1;
    public const int Audio = 2;
    public const int File = 3;
    public const int Link = 4;
}

public static class GraphAssociationType
{
    public const short Friend = 0;
    public const short FriendRequest = 1;
    public const short HaveFriendRequest = 2;
    public const short Followed = 3;
    public const short FollowedBy = 4;
    public const short Blocked = 5;
    public const short BlockedBy = 6;
    public const short Liked = 7;
    public const short LikedBy = 8;
    public const short Authored = 9;
    public const short AuthoredBy = 10;
    public const short Published = 11;
    public const short PublishedIn = 12;
    public const short Member = 13;
    public const short HaveMember = 14;
    public const short Admin = 15;
    public const short HaveAdmin = 16;
    public const short GroupJoinRequest = 17;
    public const short HaveGroupJoinRequest = 18;
    public const short Watched = 19;
    public const short WatchedBy = 20;
    public const short HaveComment = 21;
    public const short Comment = 22;
    public const short Share = 23;
    public const short SharedBy = 24;
    public const short Tagged = 25;
    public const short Mentioned = 26;
    public const short Saved = 27;
    public const short Contained = 28;
    public const short Visited = 29;

    public const short MinValue = Friend;
    public const short MaxValue = Visited;
}

public static class ExternalNotificationAction
{
    public const short Like = 0;
    public const short Comment = 1;
    public const short Tag = 2;
    public const short Mention = 3;
    public const short FriendRequest = 4;
    public const short FriendAccept = 5;
    public const short GroupInvite = 6;
    public const short GroupJoin = 7;
    public const short GroupAccept = 8;
    public const short Share = 9;
}

public static class RecommendationInteractionAction
{
    public const string Like = "LIKE";
    public const string Unlike = "UNLIKE";
    public const string Save = "SAVE";
    public const string Unsave = "UNSAVE";
    public const string Watch = "WATCH";
    public const string Share = "SHARE";
    public const string Comment = "COMMENT";
}
