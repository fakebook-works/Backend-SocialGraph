namespace SocialGraph.Api.SubGraphQL;

using HotChocolate;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Infrastructure;
using SocialGraph.Api.Service;

public class Mutation
{
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
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(input.Id);
        return userGraphService.UpdateUserAsync(input, cancellationToken);
    }

    public Task<bool> DeleteUserAsync(
        long userId,
        [Service] IUserGraphService userGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return userGraphService.DeleteUserAsync(userId, cancellationToken);
    }

    public Task<UserProfileResult?> ChangeUserAvatarAsync(
        long userId,
        string avatarUrl,
        string? originalUrl,
        int? privacy,
        [Service] IUserGraphService userGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return userGraphService.ChangeUserAvatarAsync(userId, avatarUrl, originalUrl, privacy ?? 0, cancellationToken);
    }

    public Task<UserProfileResult?> ChangeUserBackgroundAsync(
        long userId,
        string backgroundUrl,
        string? originalUrl,
        int? privacy,
        [Service] IUserGraphService userGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return userGraphService.ChangeUserBackgroundAsync(userId, backgroundUrl, originalUrl, privacy ?? 0, cancellationToken);
    }

    public Task<UserProfileResult?> RemoveUserAvatarAsync(
        long userId,
        [Service] IUserGraphService userGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return userGraphService.ChangeUserAvatarAsync(userId, string.Empty, null, cancellationToken);
    }

    public Task<UserProfileResult?> RemoveUserBackgroundAsync(
        long userId,
        [Service] IUserGraphService userGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return userGraphService.ChangeUserBackgroundAsync(userId, string.Empty, null, cancellationToken);
    }

    public Task<bool> SendFriendRequestAsync(
        long requesterId,
        long receiverId,
        [Service] IUserGraphService userGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(requesterId);
        return userGraphService.SendFriendRequestAsync(requesterId, receiverId, cancellationToken);
    }

    public Task<bool> CancelFriendRequestAsync(
        long requesterId,
        long receiverId,
        [Service] IUserGraphService userGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(requesterId);
        return userGraphService.CancelFriendRequestAsync(requesterId, receiverId, cancellationToken);
    }

    public Task<bool> AcceptFriendRequestAsync(
        long requesterId,
        long receiverId,
        [Service] IUserGraphService userGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(receiverId);
        return userGraphService.AcceptFriendRequestAsync(requesterId, receiverId, cancellationToken);
    }

    public Task<bool> RejectFriendRequestAsync(
        long requesterId,
        long receiverId,
        [Service] IUserGraphService userGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(receiverId);
        return userGraphService.RejectFriendRequestAsync(requesterId, receiverId, cancellationToken);
    }

    public Task<bool> UnfriendAsync(
        long userId,
        long friendId,
        [Service] IUserGraphService userGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return userGraphService.UnfriendAsync(userId, friendId, cancellationToken);
    }

    public Task<bool> FollowUserAsync(long followerId, long targetUserId, [Service] IUserGraphService userGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(followerId);
        return userGraphService.FollowUserAsync(followerId, targetUserId, cancellationToken);
    }

    public Task<bool> UnfollowUserAsync(long followerId, long targetUserId, [Service] IUserGraphService userGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(followerId);
        return userGraphService.UnfollowUserAsync(followerId, targetUserId, cancellationToken);
    }

    public Task<bool> BlockUserAsync(long blockerId, long blockedUserId, [Service] IUserGraphService userGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(blockerId);
        return userGraphService.BlockUserAsync(blockerId, blockedUserId, cancellationToken);
    }

    public Task<bool> UnblockUserAsync(long blockerId, long blockedUserId, [Service] IUserGraphService userGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(blockerId);
        return userGraphService.UnblockUserAsync(blockerId, blockedUserId, cancellationToken);
    }

    public Task<GroupResult> CreateGroupAsync(CreateGroupInput input, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(input.CreatorId);
        return groupGraphService.CreateGroupAsync(input, cancellationToken);
    }

    public async Task<GroupResult?> UpdateGroupAsync(UpdateGroupInput input, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        await RequireGroupAdminAsync(trustedCaller.RequireUserId(), input.Id, groupGraphService, cancellationToken);
        return await groupGraphService.UpdateGroupAsync(input, cancellationToken);
    }

    public async Task<bool> DeleteGroupAsync(long groupId, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        await RequireGroupAdminAsync(trustedCaller.RequireUserId(), groupId, groupGraphService, cancellationToken);
        return await groupGraphService.DeleteGroupAsync(groupId, cancellationToken);
    }

    public async Task<GroupResult?> ChangeGroupAvatarAsync(long groupId, string avatarUrl, string? originalUrl, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        var actorId = trustedCaller.RequireUserId();
        await RequireGroupAdminAsync(actorId, groupId, groupGraphService, cancellationToken);
        return await groupGraphService.ChangeGroupAvatarAsync(actorId, groupId, avatarUrl, originalUrl, cancellationToken);
    }

    public async Task<GroupResult?> ChangeGroupBackgroundAsync(
        long groupId,
        string backgroundUrl,
        string? originalUrl,
        [Service] IGroupGraphService groupGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        var actorId = trustedCaller.RequireUserId();
        await RequireGroupAdminAsync(actorId, groupId, groupGraphService, cancellationToken);
        return await groupGraphService.ChangeGroupBackgroundAsync(actorId, groupId, backgroundUrl, originalUrl, cancellationToken);
    }

    public async Task<GroupResult?> RemoveGroupAvatarAsync(
        long groupId,
        [Service] IGroupGraphService groupGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        var actorId = trustedCaller.RequireUserId();
        await RequireGroupAdminAsync(actorId, groupId, groupGraphService, cancellationToken);
        return await groupGraphService.ChangeGroupAvatarAsync(actorId, groupId, string.Empty, null, cancellationToken);
    }

    public async Task<GroupResult?> RemoveGroupBackgroundAsync(
        long groupId,
        [Service] IGroupGraphService groupGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        var actorId = trustedCaller.RequireUserId();
        await RequireGroupAdminAsync(actorId, groupId, groupGraphService, cancellationToken);
        return await groupGraphService.ChangeGroupBackgroundAsync(actorId, groupId, string.Empty, null, cancellationToken);
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

    public Task<bool> RequestJoinGroupAsync(long userId, long groupId, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return groupGraphService.RequestJoinAsync(userId, groupId, cancellationToken);
    }

    public Task<bool> CancelJoinGroupRequestAsync(long userId, long groupId, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return groupGraphService.CancelJoinRequestAsync(userId, groupId, cancellationToken);
    }

    public Task<bool> LeaveGroupAsync(long userId, long groupId, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return groupGraphService.LeaveGroupAsync(userId, groupId, cancellationToken);
    }

    public Task<bool> ApproveGroupJoinRequestAsync(long groupId, long userId, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        return groupGraphService.ApproveJoinRequestAsync(trustedCaller.RequireUserId(), groupId, userId, cancellationToken);
    }

    public Task<bool> RejectGroupJoinRequestAsync(long groupId, long userId, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        return groupGraphService.RejectJoinRequestAsync(trustedCaller.RequireUserId(), groupId, userId, cancellationToken);
    }

    public async Task<bool> InviteGroupUserAsync(
        long groupId,
        long userId,
        [Service] IGroupGraphService groupGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        var adminId = trustedCaller.RequireUserId();
        await RequireGroupAdminAsync(adminId, groupId, groupGraphService, cancellationToken);
        return await groupGraphService.InviteUserAsync(adminId, groupId, userId, cancellationToken);
    }

    public async Task<bool> AddGroupMemberAsync(long groupId, long userId, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        await RequireGroupAdminAsync(trustedCaller.RequireUserId(), groupId, groupGraphService, cancellationToken);
        return await groupGraphService.AddMemberAsync(groupId, userId, cancellationToken);
    }

    public async Task<bool> RemoveGroupMemberAsync(long groupId, long userId, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        await RequireGroupAdminAsync(trustedCaller.RequireUserId(), groupId, groupGraphService, cancellationToken);
        return await groupGraphService.RemoveMemberAsync(groupId, userId, cancellationToken);
    }

    public async Task<bool> AddGroupAdminAsync(long groupId, long userId, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        await RequireGroupAdminAsync(trustedCaller.RequireUserId(), groupId, groupGraphService, cancellationToken);
        return await groupGraphService.AddAdminAsync(groupId, userId, cancellationToken);
    }

    public async Task<bool> RemoveGroupAdminAsync(long groupId, long userId, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        await RequireGroupAdminAsync(trustedCaller.RequireUserId(), groupId, groupGraphService, cancellationToken);
        return await groupGraphService.RemoveAdminAsync(groupId, userId, cancellationToken);
    }

    public Task<ContentResult> CreateFeedPostAsync(
        CreateFeedPostInput input,
        [Service] IContentGraphService contentGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        var actorId = trustedCaller.RequireUserId();
        return contentGraphService.CreateFeedPostAsync(input with { AuthorId = actorId }, cancellationToken);
    }

    public async Task<ContentResult> CreateGroupPostAsync(CreateGroupPostInput input, [Service] IContentGraphService contentGraphService, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        var actorId = trustedCaller.RequireUserId();
        if (!await groupGraphService.IsParticipantAsync(actorId, input.GroupId, cancellationToken))
        {
            throw Forbidden("Only group members and administrators can publish group posts.");
        }

        return await contentGraphService.CreateGroupPostAsync(input with { AuthorId = actorId }, cancellationToken);
    }

    public async Task<ContentResult?> UpdatePostAsync(UpdatePostInput input, [Service] IContentGraphService contentGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        await RequireContentAuthorAsync(trustedCaller.RequireUserId(), input.Id, contentGraphService, cancellationToken);
        return await contentGraphService.UpdatePostAsync(input, cancellationToken);
    }

    public async Task<bool> DeleteContentAsync(long contentId, [Service] IContentGraphService contentGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        await RequireContentAuthorAsync(trustedCaller.RequireUserId(), contentId, contentGraphService, cancellationToken);
        return await contentGraphService.DeleteContentAsync(contentId, cancellationToken);
    }

    public async Task<ContentResult> CreateCommentAsync(CreateCommentInput input, [Service] IContentGraphService contentGraphService, [Service] ISocialReadModelService readModels, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        var actorId = trustedCaller.RequireUserId();
        if (!await readModels.CanCommentTargetAsync(actorId, input.TargetId, cancellationToken))
        {
            throw Forbidden("The target is unavailable or not visible to the current user.");
        }

        return await contentGraphService.CreateCommentAsync(input with { AuthorId = actorId }, cancellationToken);
    }

    public Task<NormalStoryResult> CreateNormalStoryAsync(
        CreateNormalStoryInput input,
        [Service] IContentGraphService contentGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        var actorId = trustedCaller.RequireUserId();
        return contentGraphService.CreateNormalStoryAsync(input with { AuthorId = actorId }, cancellationToken);
    }

    public async Task<IHomeStoryResult> CreateShareStoryAsync(
        CreateShareStoryInput input,
        [Service] IContentGraphService contentGraphService,
        [Service] ISocialReadModelService readModels,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        var actorId = trustedCaller.RequireUserId();
        if (!await readModels.CanShareTargetAsync(actorId, input.SharedSourceId, cancellationToken))
        {
            throw Forbidden("Only visible public feed posts and reels can be shared to a story.");
        }

        return await contentGraphService.CreateShareStoryAsync(input with { AuthorId = actorId }, cancellationToken);
    }

    public Task<DeleteStoryPayload> DeleteStoryAsync(
        DeleteStoryInput input,
        [Service] IContentGraphService contentGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        var actorId = trustedCaller.RequireUserId();
        return contentGraphService.DeleteStoryAsync(input with { AuthorId = actorId }, cancellationToken);
    }

    public Task<ContentResult> CreateReelAsync(CreateReelInput input, [Service] IContentGraphService contentGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        var actorId = trustedCaller.RequireUserId();
        return contentGraphService.CreateReelAsync(input with { AuthorId = actorId }, cancellationToken);
    }

    public async Task<ContentResult> SharePostAsync(SharePostInput input, [Service] IContentGraphService contentGraphService, [Service] ISocialReadModelService readModels, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        var actorId = trustedCaller.RequireUserId();
        if (!await readModels.CanShareTargetAsync(actorId, input.SourceId, cancellationToken))
        {
            throw Forbidden("Only visible public feed posts and reels can be shared.");
        }

        return await contentGraphService.SharePostAsync(input with { AuthorId = actorId }, cancellationToken);
    }

    public async Task<bool> LikeAsync(long userId, long targetId, [Service] IContentGraphService contentGraphService, [Service] ISocialReadModelService readModels, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        if (!await readModels.CanViewTargetAsync(userId, targetId, cancellationToken))
        {
            throw Forbidden("The target is unavailable or not visible to the current user.");
        }

        return await contentGraphService.LikeAsync(userId, targetId, cancellationToken);
    }

    public Task<bool> UnlikeAsync(long userId, long targetId, [Service] IContentGraphService contentGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return contentGraphService.UnlikeAsync(userId, targetId, cancellationToken);
    }

    public async Task<bool> SaveAsync(long userId, long targetId, [Service] IContentGraphService contentGraphService, [Service] ISocialReadModelService readModels, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        if (!await readModels.CanSaveTargetAsync(userId, targetId, cancellationToken))
        {
            throw Forbidden("The target is unavailable or not visible to the current user.");
        }

        return await contentGraphService.SaveAsync(userId, targetId, cancellationToken);
    }

    public Task<bool> UnsaveAsync(long userId, long targetId, [Service] IContentGraphService contentGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return contentGraphService.UnsaveAsync(userId, targetId, cancellationToken);
    }

    public async Task<bool> WatchAsync(long userId, long targetId, [Service] IContentGraphService contentGraphService, [Service] ISocialReadModelService readModels, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        if (!await readModels.CanWatchTargetAsync(userId, targetId, cancellationToken))
        {
            throw Forbidden("The target is unavailable or not visible to the current user.");
        }

        return await contentGraphService.WatchAsync(userId, targetId, cancellationToken);
    }

    public async Task<bool> TagAsync(long postId, long userId, [Service] IContentGraphService contentGraphService, [Service] ISocialReadModelService readModels, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        var viewerId = trustedCaller.RequireUserId();
        await RequireContentAuthorAsync(viewerId, postId, contentGraphService, cancellationToken);
        var relationship = await readModels.GetUserRelationshipStateAsync(viewerId, userId, cancellationToken);
        if (relationship is null || relationship.IsBlocked || relationship.IsBlockedBy)
        {
            throw Forbidden("The tagged user is unavailable.");
        }

        return await contentGraphService.TagAsync(postId, userId, cancellationToken);
    }

    public async Task<bool> MentionAsync(long sourceId, long userId, [Service] IContentGraphService contentGraphService, [Service] ISocialReadModelService readModels, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        var viewerId = trustedCaller.RequireUserId();
        await RequireContentAuthorAsync(viewerId, sourceId, contentGraphService, cancellationToken);
        var relationship = await readModels.GetUserRelationshipStateAsync(viewerId, userId, cancellationToken);
        if (relationship is null || relationship.IsBlocked || relationship.IsBlockedBy)
        {
            throw Forbidden("The mentioned user is unavailable.");
        }

        return await contentGraphService.MentionAsync(sourceId, userId, cancellationToken);
    }

    private static async Task RequireGroupAdminAsync(
        long viewerId,
        long groupId,
        IGroupGraphService groupGraphService,
        CancellationToken cancellationToken)
    {
        if (!await groupGraphService.IsAdminAsync(viewerId, groupId, cancellationToken))
        {
            throw Forbidden("Group administrator permission is required.");
        }
    }

    private static async Task RequireContentAuthorAsync(
        long viewerId,
        long contentId,
        IContentGraphService contentGraphService,
        CancellationToken cancellationToken)
    {
        if (!await contentGraphService.IsAuthorAsync(viewerId, contentId, cancellationToken))
        {
            throw Forbidden("Only the content author can perform this operation.");
        }
    }

    private static GraphQLException Forbidden(string message)
    {
        return new GraphQLException(
            ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage(message)
                .Build());
    }
}
