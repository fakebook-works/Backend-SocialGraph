namespace SocialGraph.Api.SubGraphQL;

using HotChocolate;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Infrastructure;
using SocialGraph.Api.Service;

public class Mutation
{
    public Task<SocialGraphObjectResult> AddObjectAsync(
        short otype,
        string dataJson,
        [Service] IObjectService objectService,
        CancellationToken cancellationToken)
    {
        return objectService.AddObjectAsync(otype, dataJson, cancellationToken);
    }

    public Task<SocialGraphObjectResult?> UpdateObjectAsync(
        long id,
        short otype,
        string patchJson,
        [Service] IObjectService objectService,
        CancellationToken cancellationToken)
    {
        return objectService.UpdateObjectAsync(id, otype, patchJson, cancellationToken);
    }

    public Task<bool> DeleteObjectAsync(
        long id,
        [Service] IObjectService objectService,
        CancellationToken cancellationToken)
    {
        return objectService.DeleteObjectAsync(id, cancellationToken);
    }

    public Task<bool> AddAssociationAsync(
        long id1,
        short atype,
        long id2,
        [Service] IAssociationService associationService,
        CancellationToken cancellationToken)
    {
        return associationService.AddAssociationAsync(id1, atype, id2, cancellationToken);
    }

    public Task<bool> DeleteOneAssociationAsync(
        long id1,
        short atype,
        long id2,
        [Service] IAssociationService associationService,
        CancellationToken cancellationToken)
    {
        return associationService.DeleteOneAssociationAsync(id1, atype, id2, cancellationToken);
    }

    public Task<bool> DeleteAllAssociationAsync(
        long id1,
        short atype,
        [Service] IAssociationService associationService,
        CancellationToken cancellationToken)
    {
        return associationService.DeleteAllAssociationAsync(id1, atype, cancellationToken);
    }

    public Task<CreateUserPayload> CreateUserAsync(
        CreateUserInput input,
        [Service] IUserGraphService userGraphService,
        CancellationToken cancellationToken)
    {
        return userGraphService.CreateUserAsync(input, cancellationToken);
    }

    public Task<UserProfileResult?> UpdateUserAsync(
        UpdateUserInput input,
        [Service] IUserGraphService userGraphService,
        CancellationToken cancellationToken)
    {
        return userGraphService.UpdateUserAsync(input, cancellationToken);
    }

    public Task<bool> DeleteUserAsync(
        long userId,
        [Service] IUserGraphService userGraphService,
        CancellationToken cancellationToken)
    {
        return userGraphService.DeleteUserAsync(userId, cancellationToken);
    }

    public Task<UserProfileResult?> ChangeUserAvatarAsync(
        long userId,
        string avatarUrl,
        string? originalUrl,
        [Service] IUserGraphService userGraphService,
        CancellationToken cancellationToken)
    {
        return userGraphService.ChangeUserAvatarAsync(userId, avatarUrl, originalUrl, cancellationToken);
    }

    public Task<UserProfileResult?> ChangeUserBackgroundAsync(
        long userId,
        string backgroundUrl,
        string? originalUrl,
        [Service] IUserGraphService userGraphService,
        CancellationToken cancellationToken)
    {
        return userGraphService.ChangeUserBackgroundAsync(userId, backgroundUrl, originalUrl, cancellationToken);
    }

    public Task<bool> SendFriendRequestAsync(
        long requesterId,
        long receiverId,
        [Service] IUserGraphService userGraphService,
        CancellationToken cancellationToken)
    {
        return userGraphService.SendFriendRequestAsync(requesterId, receiverId, cancellationToken);
    }

    public Task<bool> AcceptFriendRequestAsync(
        long requesterId,
        long receiverId,
        [Service] IUserGraphService userGraphService,
        CancellationToken cancellationToken)
    {
        return userGraphService.AcceptFriendRequestAsync(requesterId, receiverId, cancellationToken);
    }

    public Task<bool> FollowUserAsync(long followerId, long targetUserId, [Service] IUserGraphService userGraphService, CancellationToken cancellationToken)
    {
        return userGraphService.FollowUserAsync(followerId, targetUserId, cancellationToken);
    }

    public Task<bool> UnfollowUserAsync(long followerId, long targetUserId, [Service] IUserGraphService userGraphService, CancellationToken cancellationToken)
    {
        return userGraphService.UnfollowUserAsync(followerId, targetUserId, cancellationToken);
    }

    public Task<bool> BlockUserAsync(long blockerId, long blockedUserId, [Service] IUserGraphService userGraphService, CancellationToken cancellationToken)
    {
        return userGraphService.BlockUserAsync(blockerId, blockedUserId, cancellationToken);
    }

    public Task<bool> UnblockUserAsync(long blockerId, long blockedUserId, [Service] IUserGraphService userGraphService, CancellationToken cancellationToken)
    {
        return userGraphService.UnblockUserAsync(blockerId, blockedUserId, cancellationToken);
    }

    public Task<GroupResult> CreateGroupAsync(CreateGroupInput input, [Service] IGroupGraphService groupGraphService, CancellationToken cancellationToken)
    {
        return groupGraphService.CreateGroupAsync(input, cancellationToken);
    }

    public Task<GroupResult?> UpdateGroupAsync(UpdateGroupInput input, [Service] IGroupGraphService groupGraphService, CancellationToken cancellationToken)
    {
        return groupGraphService.UpdateGroupAsync(input, cancellationToken);
    }

    public Task<bool> DeleteGroupAsync(long groupId, [Service] IGroupGraphService groupGraphService, CancellationToken cancellationToken)
    {
        return groupGraphService.DeleteGroupAsync(groupId, cancellationToken);
    }

    public Task<GroupResult?> ChangeGroupAvatarAsync(long groupId, string avatarUrl, [Service] IGroupGraphService groupGraphService, CancellationToken cancellationToken)
    {
        return groupGraphService.ChangeGroupAvatarAsync(groupId, avatarUrl, cancellationToken);
    }

    public Task<GroupResult?> ChangeGroupBackgroundAsync(
        long groupId,
        string backgroundUrl,
        string? originalUrl,
        [Service] IGroupGraphService groupGraphService,
        CancellationToken cancellationToken)
    {
        return groupGraphService.ChangeGroupBackgroundAsync(groupId, backgroundUrl, originalUrl, cancellationToken);
    }

    public Task<bool> RecordGroupVisitAsync(
        long userId,
        long groupId,
        [Service] IGroupGraphService groupGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return groupGraphService.RecordGroupVisitAsync(userId, groupId, cancellationToken);
    }

    public Task<bool> AddGroupMemberAsync(long groupId, long userId, [Service] IGroupGraphService groupGraphService, CancellationToken cancellationToken)
    {
        return groupGraphService.AddMemberAsync(groupId, userId, cancellationToken);
    }

    public Task<bool> RemoveGroupMemberAsync(long groupId, long userId, [Service] IGroupGraphService groupGraphService, CancellationToken cancellationToken)
    {
        return groupGraphService.RemoveMemberAsync(groupId, userId, cancellationToken);
    }

    public Task<bool> AddGroupAdminAsync(long groupId, long userId, [Service] IGroupGraphService groupGraphService, CancellationToken cancellationToken)
    {
        return groupGraphService.AddAdminAsync(groupId, userId, cancellationToken);
    }

    public Task<bool> RemoveGroupAdminAsync(long groupId, long userId, [Service] IGroupGraphService groupGraphService, CancellationToken cancellationToken)
    {
        return groupGraphService.RemoveAdminAsync(groupId, userId, cancellationToken);
    }

    public Task<ContentResult> CreateFeedPostAsync(CreateFeedPostInput input, [Service] IContentGraphService contentGraphService, CancellationToken cancellationToken)
    {
        return contentGraphService.CreateFeedPostAsync(input, cancellationToken);
    }

    public Task<ContentResult> CreateGroupPostAsync(CreateGroupPostInput input, [Service] IContentGraphService contentGraphService, CancellationToken cancellationToken)
    {
        return contentGraphService.CreateGroupPostAsync(input, cancellationToken);
    }

    public Task<ContentResult?> UpdatePostAsync(UpdatePostInput input, [Service] IContentGraphService contentGraphService, CancellationToken cancellationToken)
    {
        return contentGraphService.UpdatePostAsync(input, cancellationToken);
    }

    public Task<bool> DeleteContentAsync(long contentId, [Service] IContentGraphService contentGraphService, CancellationToken cancellationToken)
    {
        return contentGraphService.DeleteContentAsync(contentId, cancellationToken);
    }

    public Task<ContentResult> CreateCommentAsync(CreateCommentInput input, [Service] IContentGraphService contentGraphService, CancellationToken cancellationToken)
    {
        return contentGraphService.CreateCommentAsync(input, cancellationToken);
    }

    public Task<NormalStoryResult> CreateNormalStoryAsync(
        CreateNormalStoryInput input,
        [Service] IContentGraphService contentGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(input.AuthorId);
        return contentGraphService.CreateNormalStoryAsync(input, cancellationToken);
    }

    public Task<IHomeStoryResult> CreateShareStoryAsync(
        CreateShareStoryInput input,
        [Service] IContentGraphService contentGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(input.AuthorId);
        return contentGraphService.CreateShareStoryAsync(input, cancellationToken);
    }

    public Task<DeleteStoryPayload> DeleteStoryAsync(
        DeleteStoryInput input,
        [Service] IContentGraphService contentGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(input.AuthorId);
        return contentGraphService.DeleteStoryAsync(input, cancellationToken);
    }

    public Task<ContentResult> CreateReelAsync(CreateReelInput input, [Service] IContentGraphService contentGraphService, CancellationToken cancellationToken)
    {
        return contentGraphService.CreateReelAsync(input, cancellationToken);
    }

    public Task<ContentResult> SharePostAsync(SharePostInput input, [Service] IContentGraphService contentGraphService, CancellationToken cancellationToken)
    {
        return contentGraphService.SharePostAsync(input, cancellationToken);
    }

    public Task<bool> LikeAsync(long userId, long targetId, [Service] IContentGraphService contentGraphService, CancellationToken cancellationToken)
    {
        return contentGraphService.LikeAsync(userId, targetId, cancellationToken);
    }

    public Task<bool> UnlikeAsync(long userId, long targetId, [Service] IContentGraphService contentGraphService, CancellationToken cancellationToken)
    {
        return contentGraphService.UnlikeAsync(userId, targetId, cancellationToken);
    }

    public Task<bool> SaveAsync(long userId, long targetId, [Service] IContentGraphService contentGraphService, CancellationToken cancellationToken)
    {
        return contentGraphService.SaveAsync(userId, targetId, cancellationToken);
    }

    public Task<bool> UnsaveAsync(long userId, long targetId, [Service] IContentGraphService contentGraphService, CancellationToken cancellationToken)
    {
        return contentGraphService.UnsaveAsync(userId, targetId, cancellationToken);
    }

    public Task<bool> WatchAsync(long userId, long targetId, [Service] IContentGraphService contentGraphService, CancellationToken cancellationToken)
    {
        return contentGraphService.WatchAsync(userId, targetId, cancellationToken);
    }

    public Task<bool> TagAsync(long postId, long userId, [Service] IContentGraphService contentGraphService, CancellationToken cancellationToken)
    {
        return contentGraphService.TagAsync(postId, userId, cancellationToken);
    }

    public Task<bool> MentionAsync(long sourceId, long userId, [Service] IContentGraphService contentGraphService, CancellationToken cancellationToken)
    {
        return contentGraphService.MentionAsync(sourceId, userId, cancellationToken);
    }
}
