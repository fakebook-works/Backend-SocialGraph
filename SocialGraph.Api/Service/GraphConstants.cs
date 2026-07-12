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
    public const short Followed = 1;
    public const short FollowedBy = 2;
    public const short Liked = 3;
    public const short LikedBy = 4;
    public const short Authored = 5;
    public const short AuthoredBy = 6;
    public const short Comment = 7;
    public const short Share = 8;
    public const short Published = 9;
    public const short PublishedIn = 10;
    public const short Tagged = 11;
    public const short TaggedIn = 12;
    public const short Member = 13;
    public const short HaveMember = 14;
    public const short Admin = 15;
    public const short HaveAdmin = 16;
    public const short Watched = 17;
    public const short WatchedBy = 18;
    public const short Saved = 19;
    public const short Contained = 20;
    public const short Mentioned = 21;
    public const short Owned = 22;
    public const short Blocked = 23;
    public const short BlockedBy = 24;
    public const short Visited = 25;
}

public static class ExternalNotificationAction
{
    public const short FriendRequest = 0;
    public const short FriendAccept = 1;
    public const short GroupInvite = 2;
    public const short GroupJoin = 3;
    public const short GroupAccept = 4;
    public const short Comment = 5;
    public const short Like = 6;
    public const short Mention = 7;
    public const short Tag = 8;
}
