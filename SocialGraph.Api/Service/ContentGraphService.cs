namespace SocialGraph.Api.Service;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;

public sealed class ContentGraphService : IContentGraphService
{
    public const int MaxPostDetailIds = 100;
    public const double MinReelAspectRatio = 9d / 16d;
    public const double MaxReelAspectRatio = 16d / 9d;
    private const double ReelPresentationEpsilon = 0.000001d;

    /// <summary>Matches the recursion bound used by the visibility checks in SocialReadModelService.</summary>
    private const int MaxCommentChainDepth = 20;

    private readonly MyDbContext _dbContext;
    private readonly IObjectService _objectService;
    private readonly IAssociationService _associationService;
    private readonly IExternalServiceClient _externalServiceClient;
    private readonly IBlockVisibilityService _blockVisibility;

    private readonly IMediaOwnershipGuard? _mediaOwnershipGuard;

    public ContentGraphService(
        MyDbContext dbContext,
        IObjectService objectService,
        IAssociationService associationService,
        IExternalServiceClient externalServiceClient,
        IMediaOwnershipGuard? mediaOwnershipGuard = null,
        IBlockVisibilityService? blockVisibility = null)
    {
        _dbContext = dbContext;
        _objectService = objectService;
        _associationService = associationService;
        _externalServiceClient = externalServiceClient;
        _mediaOwnershipGuard = mediaOwnershipGuard;
        _blockVisibility = blockVisibility ?? new BlockVisibilityService(dbContext);
    }

    /// <summary>
    /// Refuses client-supplied media URLs that <paramref name="ownerUserId"/> does not own, so a
    /// caller cannot attach — and subsequently destroy — media belonging to another user.
    /// </summary>
    public async Task<ContentResult> CreateFeedPostAsync(CreateFeedPostInput input, CancellationToken cancellationToken = default)
    {
        if (input.Privacy is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Feed privacy must be between 0 and 3.");
        }

        await EnsureReferencesAllowedAsync(
            input.AuthorId,
            NormalizeUserIds(input.TaggedUserIds).Concat(MentionUserIds(input.Content)),
            cancellationToken);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        SocialGraphObjectResult? post = null;
        MediaReservation? mediaReservation = null;
        var committed = false;
        var commitAttempted = false;
        try
        {
            await LockAuthorForContentCreationAsync(input.AuthorId, cancellationToken);
            post = await _objectService.AddObjectAsync(GraphObjectType.FeedPost, GraphJson.PostJson(input.Content, input.Privacy), cancellationToken);
            var media = await AttachMediaAsync(post.id, input.Media, cancellationToken);
            await _associationService.AddAssociationAsync(input.AuthorId, GraphAssociationType.Authored, post.id, cancellationToken);
            foreach (var userId in NormalizeUserIds(input.TaggedUserIds))
            {
                if (!await AddUserReferenceAsync(
                        post.id,
                        userId,
                        input.AuthorId,
                        GraphObjectType.FeedPost,
                        0,
                        GraphAssociationType.Tagged,
                        ExternalNotificationAction.Tag,
                        cancellationToken))
                {
                    throw new InvalidOperationException($"Unable to tag user {userId}.");
                }
            }

            foreach (var userId in MentionUserIds(input.Content))
            {
                if (!await AddUserReferenceAsync(
                        post.id,
                        userId,
                        input.AuthorId,
                        GraphObjectType.FeedPost,
                        0,
                        GraphAssociationType.Mentioned,
                        ExternalNotificationAction.Mention,
                        cancellationToken))
                {
                    throw new InvalidOperationException($"Unable to mention user {userId}.");
                }
            }

            await _externalServiceClient.CreateSearchIndexAsync(post.id, "feedPost", input.Content, cancellationToken);
            await _externalServiceClient.CreatePostEmbeddingAsync(post.id, input.Content, media.Select(item => item.Url).ToArray(), cancellationToken);
            mediaReservation = await ReserveAndQueueMediaAsync(
                MediaLifecycleReferences.ForMedia(media),
                input.AuthorId,
                cancellationToken);
            if (transaction is not null)
            {
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken);
            }
            committed = true;

            return await BuildContentResultAsync(post, input.AuthorId, media, cancellationToken);
        }
        catch
        {
            // Objects and edges are written through Redis before the transaction commits, so
            // rolling back without invalidating left content that does not exist in the cache,
            // and the read paths prefer the cache over PostgreSQL.
            if (!committed && !commitAttempted)
            {
                await RollbackAndInvalidateAsync(transaction, post?.id);
                await CancelReservationBestEffortAsync(mediaReservation);
            }
            throw;
        }
    }

    public async Task<ContentResult> CreateGroupPostAsync(CreateGroupPostInput input, CancellationToken cancellationToken = default)
    {
        var taggedUserIds = NormalizeUserIds(input.TaggedUserIds);
        var mentionedUserIds = MentionUserIds(input.Content);
        var referencedUserIds = taggedUserIds.Concat(mentionedUserIds).Distinct().ToArray();
        await EnsureReferencesAllowedAsync(input.AuthorId, referencedUserIds, cancellationToken);
        await EnsureGroupReferencesAllowedAsync(input.AuthorId, input.GroupId, referencedUserIds, cancellationToken);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        SocialGraphObjectResult? post = null;
        MediaReservation? mediaReservation = null;
        var committed = false;
        var commitAttempted = false;
        try
        {
            await LockAuthorForContentCreationAsync(input.AuthorId, cancellationToken);
            await AcquireGroupLifecycleLockAsync(input.GroupId, cancellationToken);
            if (!await LockCurrentGroupParticipationAsync(input.AuthorId, input.GroupId, cancellationToken))
            {
                throw new InvalidOperationException("Only current group members and administrators can publish group posts.");
            }

            post = await _objectService.AddObjectAsync(GraphObjectType.GroupPost, GraphJson.GroupPostJson(input.Content), cancellationToken);
            var media = await AttachMediaAsync(post.id, input.Media, cancellationToken);
            await _associationService.AddAssociationAsync(input.AuthorId, GraphAssociationType.Authored, post.id, cancellationToken);
            await _associationService.AddAssociationAsync(input.GroupId, GraphAssociationType.Published, post.id, cancellationToken);
            foreach (var userId in taggedUserIds)
            {
                if (!await AddUserReferenceAsync(
                        post.id,
                        userId,
                        input.AuthorId,
                        GraphObjectType.GroupPost,
                        input.GroupId,
                        GraphAssociationType.Tagged,
                        ExternalNotificationAction.Tag,
                        cancellationToken,
                        groupReferenceAlreadyValidated: true))
                {
                    throw new InvalidOperationException("Unable to tag the selected account.");
                }
            }

            foreach (var userId in mentionedUserIds)
            {
                if (!await AddUserReferenceAsync(
                        post.id,
                        userId,
                        input.AuthorId,
                        GraphObjectType.GroupPost,
                        input.GroupId,
                        GraphAssociationType.Mentioned,
                        ExternalNotificationAction.Mention,
                        cancellationToken,
                        groupReferenceAlreadyValidated: true))
                {
                    throw new InvalidOperationException("Unable to mention the selected account.");
                }
            }

            await _externalServiceClient.CreateSearchIndexAsync(post.id, "groupPost", input.Content, cancellationToken);
            await _externalServiceClient.CreatePostEmbeddingAsync(post.id, input.Content, media.Select(item => item.Url).ToArray(), cancellationToken);
            mediaReservation = await ReserveAndQueueMediaAsync(
                MediaLifecycleReferences.ForMedia(media),
                input.AuthorId,
                cancellationToken);
            if (transaction is not null)
            {
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken);
            }
            committed = true;

            return await BuildContentResultAsync(post, input.AuthorId, media, cancellationToken);
        }
        catch
        {
            // Objects and edges are written through Redis before the transaction commits, so
            // rolling back without invalidating left content that does not exist in the cache,
            // and the read paths prefer the cache over PostgreSQL.
            if (!committed && !commitAttempted)
            {
                await RollbackAndInvalidateAsync(transaction, post?.id);
                await CancelReservationBestEffortAsync(mediaReservation);
            }
            throw;
        }
    }

    public async Task<ContentResult?> UpdatePostAsync(UpdatePostInput input, CancellationToken cancellationToken = default)
    {
        var preflight = await _objectService.RetrieveObjectAsync(input.Id, cancellationToken);
        if (preflight?.otype is not (GraphObjectType.FeedPost or GraphObjectType.GroupPost or GraphObjectType.Reel))
        {
            return null;
        }

        if (preflight.otype is GraphObjectType.FeedPost or GraphObjectType.Reel && input.Privacy is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Feed post and reel privacy must be between 0 and 3.");
        }

        var authorId = await GetAuthorIdAsync(input.Id, cancellationToken);
        if (input.Content is not null)
        {
            await EnsureReferencesAllowedAsync(authorId, MentionUserIds(input.Content), cancellationToken);
            if (preflight.otype == GraphObjectType.GroupPost)
            {
                var groupId = await GetPublishedGroupIdAsync(input.Id, cancellationToken);
                await EnsureGroupReferencesAllowedAsync(
                    authorId,
                    groupId,
                    MentionUserIds(input.Content),
                    cancellationToken);
            }
        }
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        MediaReservation? mediaReservation = null;
        var committed = false;
        var commitAttempted = false;
        try
        {
            // Serialize replacement of a parent slot. Without this lock two concurrent
            // updates can each attach a different URL under a different Media id while
            // only one version remains visible in the post.
            var locked = await LockObjectRowAsync(input.Id, cancellationToken, preflight);
            if (locked?.otype is not (GraphObjectType.FeedPost or GraphObjectType.GroupPost or GraphObjectType.Reel))
            {
                if (transaction is not null)
                {
                    commitAttempted = true;
                    await transaction.CommitAsync(cancellationToken);
                }
                return null;
            }

            var currentData = GraphJson.ParseObject(locked.data);
            var post = await _objectService.UpdateObjectAsync(
                input.Id,
                locked.otype,
                GraphJson.PatchJson(
                    ("content", input.Content),
                    ("privacy", locked.otype is GraphObjectType.FeedPost or GraphObjectType.Reel ? input.Privacy : null)),
                cancellationToken);
            if (post is null)
            {
                if (transaction is not null)
                {
                    commitAttempted = true;
                    await transaction.CommitAsync(cancellationToken);
                }
                return null;
            }

            IReadOnlyList<MediaResult> media;
            if (input.Media is null)
            {
                media = await GetMediaAsync(input.Id, cancellationToken);
            }
            else
            {
                var existingMediaIds = await GetContainedMediaIdsAsync(input.Id, cancellationToken);
                foreach (var mediaId in existingMediaIds)
                {
                    await _associationService.DeleteOneAssociationAsync(
                        input.Id,
                        GraphAssociationType.Contained,
                        mediaId,
                        cancellationToken);
                }

                media = await AttachMediaAsync(input.Id, input.Media, cancellationToken);
                mediaReservation = await ReserveAndQueueMediaAsync(
                    MediaLifecycleReferences.ForMedia(media),
                    authorId,
                    cancellationToken);
                await DeleteOrphanMediaAsync(existingMediaIds, cancellationToken);
            }

            var content = input.Content ?? GraphJson.String(currentData, "content");
            if (input.Content is not null)
            {
                await SyncMentionAssociationsAsync(input.Id, authorId, content, cancellationToken);
                await _externalServiceClient.UpdateSearchIndexAsync(
                    input.Id,
                    locked.otype switch
                    {
                        GraphObjectType.FeedPost => "feedPost",
                        GraphObjectType.GroupPost => "groupPost",
                        GraphObjectType.Reel => "reel",
                        _ => throw new InvalidOperationException("Unsupported content type.")
                    },
                    content,
                    cancellationToken);
            }

            if (input.Content is not null || input.Media is not null)
            {
                await _externalServiceClient.CreatePostEmbeddingAsync(
                    input.Id,
                    content,
                    media.Select(item => item.Url).ToArray(),
                    cancellationToken);
            }

            if (transaction is not null)
            {
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken);
            }
            committed = true;
            return await BuildContentResultAsync(post, authorId, media, cancellationToken);
        }
        catch
        {
            if (!committed && !commitAttempted)
            {
                await RollbackAndInvalidateAsync(transaction, input.Id);
                await CancelReservationBestEffortAsync(mediaReservation);
            }
            throw;
        }
    }

    public async Task<ContentResult?> UpdateCommentAsync(
        UpdateCommentInput input,
        CancellationToken cancellationToken = default)
    {
        var preflight = await _objectService.RetrieveObjectAsync(input.Id, cancellationToken);
        if (preflight?.otype != GraphObjectType.Comment)
        {
            return null;
        }

        if (input.Media is not null && (input.Media.Type != 0 || string.IsNullOrWhiteSpace(input.Media.Url)))
        {
            throw new ArgumentException("Comment media must be one image with a valid URL.", nameof(input));
        }

        var currentData = GraphJson.ParseObject(preflight.data);
        if (GraphJson.IsCommentDeleted(currentData))
        {
            return null;
        }
        var authorId = await GetAuthorIdAsync(input.Id, cancellationToken);
        if (input.Content is not null)
        {
            await EnsureReferencesAllowedAsync(authorId, MentionUserIds(input.Content), cancellationToken);
        }

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        MediaReservation? mediaReservation = null;
        var committed = false;
        var commitAttempted = false;
        try
        {
            var locked = await LockObjectRowAsync(input.Id, cancellationToken, preflight);
            if (locked?.otype != GraphObjectType.Comment ||
                GraphJson.IsCommentDeleted(GraphJson.ParseObject(locked.data)))
            {
                if (transaction is not null)
                {
                    commitAttempted = true;
                    await transaction.CommitAsync(cancellationToken);
                }
                return null;
            }

            var comment = input.Content is null
                ? new SocialGraphObjectResult(locked.id, locked.otype, locked.data)
                : await MutateCommentObjectAsync(
                    input.Id,
                    data => GraphJson.ApplyCommentEdit(data, input.Content, GraphJson.UtcNowString()),
                    cancellationToken,
                    () => SyncMentionAssociationsAsync(input.Id, authorId, input.Content, cancellationToken));
            if (comment is null)
            {
                if (transaction is not null)
                {
                    commitAttempted = true;
                    await transaction.CommitAsync(cancellationToken);
                }
                return null;
            }

            IReadOnlyList<MediaResult> media;
            if (input.Media is null && !input.ClearMedia)
            {
                media = await GetMediaAsync(input.Id, cancellationToken);
            }
            else
            {
                var existingMediaIds = await GetContainedMediaIdsAsync(input.Id, cancellationToken);
                foreach (var mediaId in existingMediaIds)
                {
                    await _associationService.DeleteOneAssociationAsync(
                        input.Id,
                        GraphAssociationType.Contained,
                        mediaId,
                        cancellationToken);
                }

                media = await AttachSingleMediaAsync(input.Id, input.Media, cancellationToken);
                mediaReservation = await ReserveAndQueueMediaAsync(
                    MediaLifecycleReferences.ForMedia(media),
                    authorId,
                    cancellationToken);
                await DeleteOrphanMediaAsync(existingMediaIds, cancellationToken);
            }

            if (transaction is not null)
            {
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken);
            }
            committed = true;
            return await BuildContentResultAsync(comment, authorId, media, cancellationToken);
        }
        catch
        {
            if (!committed && !commitAttempted)
            {
                await RollbackAndInvalidateAsync(transaction, input.Id);
                await CancelReservationBestEffortAsync(mediaReservation);
            }
            throw;
        }
    }

    /// <summary>
    /// Walks a comment up to the post, group post or reel it belongs to, following the chain for
    /// replies. Returns 0 when the chain cannot be resolved.
    /// </summary>
    public async Task<long> ResolveRootPostIdAsync(long contentId, CancellationToken cancellationToken = default)
    {
        var currentId = contentId;
        for (var depth = 0; depth < MaxCommentChainDepth; depth++)
        {
            var current = await _objectService.RetrieveObjectAsync(currentId, cancellationToken);
            if (current is null)
            {
                return 0;
            }

            if (current.otype is GraphObjectType.FeedPost or GraphObjectType.GroupPost or GraphObjectType.Reel)
            {
                return current.id;
            }

            if (current.otype != GraphObjectType.Comment)
            {
                return 0;
            }

            var parent = await _associationService.RetrieveAssociationAsync(
                currentId,
                GraphAssociationType.Comment,
                null,
                1,
                cancellationToken);
            var parentId = parent.items.FirstOrDefault()?.id2 ?? 0;
            if (parentId <= 0)
            {
                return 0;
            }

            currentId = parentId;
        }

        return 0;
    }

    public async Task<bool> DeleteContentAsync(long contentId, CancellationToken cancellationToken = default)
    {
        var preflight = await _objectService.RetrieveObjectAsync(contentId, cancellationToken);
        if (preflight is null)
        {
            return false;
        }

        if (preflight.otype == GraphObjectType.Comment)
        {
            return await DeleteCommentAsync(contentId, cancellationToken);
        }

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        try
        {
            var item = await LockObjectRowAsync(contentId, cancellationToken, preflight);
            if (item is null)
            {
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
                return false;
            }

            var descendantCommentIds = await GetDescendantCommentIdsAsync(contentId, cancellationToken);
            var mediaByComment = new Dictionary<long, IReadOnlyList<long>>();
            foreach (var commentId in descendantCommentIds)
            {
                mediaByComment[commentId] = await GetContainedMediaIdsAsync(commentId, cancellationToken);
            }
            var mediaIds = (await GetContainedMediaIdsAsync(contentId, cancellationToken))
                .Concat(mediaByComment.Values.SelectMany(value => value))
                .Distinct()
                .ToArray();
            await _associationService.DeleteObjectAssociationsAsync(contentId, cancellationToken);
            foreach (var comment in mediaByComment)
            {
                foreach (var mediaId in comment.Value)
                {
                    await _associationService.DeleteOneAssociationAsync(
                        comment.Key,
                        GraphAssociationType.Contained,
                        mediaId,
                        cancellationToken);
                }
            }
            var deleted = await _objectService.DeleteObjectAsync(contentId, cancellationToken);
            if (deleted)
            {
                // The exact detach rows commit with the parent deletion. A process crash can
                // no longer leave an invisible post holding permanent media references.
                await DeleteOrphanMediaAsync(mediaIds, cancellationToken, contentId);
            }
            if (deleted && (item.otype == GraphObjectType.FeedPost ||
                            item.otype == GraphObjectType.GroupPost ||
                            item.otype == GraphObjectType.Reel))
            {
                await _externalServiceClient.DeletePostEmbeddingAsync(contentId, cancellationToken);
                await _externalServiceClient.DeleteSearchIndexAsync(contentId, cancellationToken);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            return deleted;
        }
        catch
        {
            await RollbackAndInvalidateAsync(transaction, contentId);
            throw;
        }
    }

    private async Task<bool> DeleteCommentAsync(long commentId, CancellationToken cancellationToken)
    {
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        try
        {
            var locked = await LockObjectRowAsync(commentId, cancellationToken);
            if (locked?.otype != GraphObjectType.Comment)
            {
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
                return false;
            }

            var mediaIds = await GetContainedMediaIdsAsync(commentId, cancellationToken);
            var tombstone = await MutateCommentObjectAsync(
                commentId,
                data =>
                {
                    if (!GraphJson.IsCommentDeleted(data))
                    {
                        GraphJson.ApplyCommentTombstone(data, GraphJson.UtcNowString());
                    }
                    return true;
                },
                cancellationToken);
            if (tombstone is null)
            {
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
                return false;
            }

            await _associationService.DeleteAllAssociationAsync(
                commentId,
                GraphAssociationType.LikedBy,
                cancellationToken);
            await _associationService.DeleteAllAssociationAsync(
                commentId,
                GraphAssociationType.Mentioned,
                cancellationToken);
            foreach (var mediaId in mediaIds)
            {
                await _associationService.DeleteOneAssociationAsync(
                    commentId,
                    GraphAssociationType.Contained,
                    mediaId,
                    cancellationToken);
            }
            await DeleteOrphanMediaAsync(mediaIds, cancellationToken);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            return true;
        }
        catch
        {
            await RollbackAndInvalidateAsync(transaction, commentId);
            throw;
        }
    }

    public async Task<ContentResult?> GetContentAsync(long contentId, CancellationToken cancellationToken = default)
    {
        var item = await _objectService.RetrieveObjectAsync(contentId, cancellationToken);
        if (item is null)
        {
            return null;
        }

        var authorId = await GetAuthorIdAsync(contentId, cancellationToken);
        var media = await GetMediaAsync(contentId, cancellationToken);
        return await BuildContentResultAsync(item, authorId, media, cancellationToken);
    }

    public Task<bool> IsAuthorAsync(long userId, long contentId, CancellationToken cancellationToken = default)
    {
        return _associationService.HasAssociationAsync(
            userId,
            GraphAssociationType.Authored,
            contentId,
            cancellationToken);
    }

    public async Task<bool> CanDeleteContentAsync(
        long userId,
        long contentId,
        CancellationToken cancellationToken = default)
    {
        if (await IsAuthorAsync(userId, contentId, cancellationToken))
        {
            return true;
        }

        var content = await _objectService.RetrieveObjectAsync(contentId, cancellationToken);
        if (content?.otype != GraphObjectType.GroupPost)
        {
            return false;
        }

        var groupId = await GetPublishedGroupIdAsync(contentId, cancellationToken);
        return groupId > 0 && await _associationService.HasAssociationAsync(
            userId,
            GraphAssociationType.Admin,
            groupId,
            cancellationToken);
    }

    public async Task<IHomePostResult?> GetPostDetailAsync(
        long viewerId,
        long postId,
        CancellationToken cancellationToken = default)
    {
        var detail = (await GetPostDetailsAsync(viewerId, new[] { postId }, cancellationToken)).FirstOrDefault();
        if (detail is not null)
        {
            return detail;
        }

        // Notifications for a like or a mention on a comment carry the comment's id, so opening
        // one used to land on an "unavailable content" screen. Resolving the comment to the post
        // that contains it makes the deep link work; visibility is still decided entirely by the
        // normal post-detail path below.
        var rootPostId = await ResolveRootPostIdAsync(postId, cancellationToken);
        return rootPostId <= 0 || rootPostId == postId
            ? null
            : (await GetPostDetailsAsync(viewerId, new[] { rootPostId }, cancellationToken)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<IHomePostResult>> GetPostDetailsAsync(
        long viewerId,
        IReadOnlyList<long> postIds,
        CancellationToken cancellationToken = default) =>
        await GetPostDetailsCoreAsync(viewerId, postIds, null, cancellationToken);

    public async Task<IReadOnlyList<IHomePostResult>> GetGroupPostDetailsAsync(
        long viewerId,
        long groupId,
        IReadOnlyList<long> postIds,
        CancellationToken cancellationToken = default)
    {
        if (groupId <= 0)
        {
            return Array.Empty<IHomePostResult>();
        }

        return await GetPostDetailsCoreAsync(viewerId, postIds, groupId, cancellationToken);
    }

    private async Task<IReadOnlyList<IHomePostResult>> GetPostDetailsCoreAsync(
        long viewerId,
        IReadOnlyList<long> postIds,
        long? groupContextId,
        CancellationToken cancellationToken)
    {
        if (postIds.Count > MaxPostDetailIds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(postIds),
                $"At most {MaxPostDetailIds} post IDs can be requested.");
        }

        var orderedPostIds = postIds
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (orderedPostIds.Length == 0)
        {
            return Array.Empty<IHomePostResult>();
        }

        var posts = await _dbContext.ObjectsTb
            .AsNoTracking()
            .Where(item => orderedPostIds.Contains(item.id) &&
                (item.otype == GraphObjectType.FeedPost ||
                 item.otype == GraphObjectType.GroupPost ||
                 item.otype == GraphObjectType.Reel))
            .ToDictionaryAsync(item => item.id, cancellationToken);
        if (posts.Count == 0)
        {
            return Array.Empty<IHomePostResult>();
        }

        var loadedPostIds = posts.Keys.ToArray();
        var postLinks = await _dbContext.AssociationsTb
            .AsNoTracking()
            .Where(item => loadedPostIds.Contains(item.id1) &&
                (item.atype == GraphAssociationType.AuthoredBy ||
                 item.atype == GraphAssociationType.Contained ||
                 item.atype == GraphAssociationType.PublishedIn ||
                 item.atype == GraphAssociationType.Share ||
                 item.atype == GraphAssociationType.Mentioned ||
                 item.atype == GraphAssociationType.Tagged))
            .ToListAsync(cancellationToken);
        var authorByPost = postLinks
            .Where(item => item.atype == GraphAssociationType.AuthoredBy)
            .GroupBy(item => item.id1)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.time).First().id2);
        var groupByPost = postLinks
            .Where(item => item.atype == GraphAssociationType.PublishedIn)
            .GroupBy(item => item.id1)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.time).First().id2);
        var sourceByPost = postLinks
            .Where(item => item.atype == GraphAssociationType.Share)
            .GroupBy(item => item.id1)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.time).First().id2);
        var sourceIds = sourceByPost.Values.Distinct().ToArray();
        var sourceLinks = sourceIds.Length == 0
            ? new List<Associations>()
            : await _dbContext.AssociationsTb
                .AsNoTracking()
                .Where(item => sourceIds.Contains(item.id1) &&
                    (item.atype == GraphAssociationType.AuthoredBy ||
                     item.atype == GraphAssociationType.Contained ||
                     item.atype == GraphAssociationType.Mentioned ||
                     item.atype == GraphAssociationType.PublishedIn))
                .ToListAsync(cancellationToken);
        var sourceAuthorBySource = sourceLinks
            .Where(item => item.atype == GraphAssociationType.AuthoredBy)
            .GroupBy(item => item.id1)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.time).First().id2);
        var sourceGroupBySource = sourceLinks
            .Where(item => item.atype == GraphAssociationType.PublishedIn)
            .GroupBy(item => item.id1)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.time).First().id2);
        var postMentionTokenIds = posts.Values
            .SelectMany(post => MentionTokenCodec.ExtractUserIds(
                GraphJson.String(GraphJson.ParseObject(post.data), "content")))
            .Distinct();
        var relatedIds = authorByPost.Values
            .Concat(groupByPost.Values)
            .Concat(sourceIds)
            .Concat(sourceAuthorBySource.Values)
            .Concat(sourceGroupBySource.Values)
            .Concat(postLinks
                .Where(item => item.atype == GraphAssociationType.Contained)
                .Select(item => item.id2))
            .Concat(sourceLinks
                .Where(item => item.atype == GraphAssociationType.Contained)
                .Select(item => item.id2))
            .Concat(postLinks
                .Where(item => item.atype == GraphAssociationType.Mentioned)
                .Select(item => item.id2))
            .Concat(postLinks
                .Where(item => item.atype == GraphAssociationType.Tagged)
                .Select(item => item.id2))
            .Concat(sourceLinks
                .Where(item => item.atype == GraphAssociationType.Mentioned)
                .Select(item => item.id2))
            .Concat(postMentionTokenIds)
            .Distinct()
            .ToArray();
        var relatedObjects = relatedIds.Length == 0
            ? new Dictionary<long, Objects>()
            : await _dbContext.ObjectsTb
                .AsNoTracking()
                .Where(item => relatedIds.Contains(item.id))
                .ToDictionaryAsync(item => item.id, cancellationToken);

        var referencedUserIds = postLinks
            .Where(item => item.atype is GraphAssociationType.Mentioned or GraphAssociationType.Tagged)
            .Select(item => item.id2)
            .Concat(sourceLinks
                .Where(item => item.atype == GraphAssociationType.Mentioned)
                .Select(item => item.id2))
            .Concat(postMentionTokenIds)
            .Distinct();
        var relationTargetIds = authorByPost.Values
            .Concat(groupByPost.Values)
            .Concat(sourceAuthorBySource.Values)
            .Concat(sourceGroupBySource.Values)
            .Concat(referencedUserIds)
            .Distinct()
            .ToArray();
        var viewerLinks = relationTargetIds.Length == 0
            ? new List<Associations>()
            : await _dbContext.AssociationsTb
                .AsNoTracking()
                .Where(item => item.id1 == viewerId &&
                    relationTargetIds.Contains(item.id2) &&
                    (item.atype == GraphAssociationType.Friend ||
                     item.atype == GraphAssociationType.Followed ||
                     item.atype == GraphAssociationType.Blocked ||
                     item.atype == GraphAssociationType.BlockedBy ||
                     item.atype == GraphAssociationType.Member ||
                     item.atype == GraphAssociationType.Admin ||
                     item.atype == GraphAssociationType.GroupJoinRequest))
                .ToListAsync(cancellationToken);
        var friends = RelationTargets(viewerLinks, GraphAssociationType.Friend);
        var followed = RelationTargets(viewerLinks, GraphAssociationType.Followed);
        var blocked = RelationTargets(viewerLinks, GraphAssociationType.Blocked, GraphAssociationType.BlockedBy);
        var participatingGroups = RelationTargets(viewerLinks, GraphAssociationType.Member, GraphAssociationType.Admin);
        var pendingGroups = RelationTargets(viewerLinks, GraphAssociationType.GroupJoinRequest);
        var sharedGroupIds = sourceIds
            .Where(id => relatedObjects.TryGetValue(id, out var source) && source.otype == GraphObjectType.Group)
            .Concat(sourceGroupBySource.Values)
            .Distinct()
            .ToArray();
        var sharedGroupMemberCounts = sharedGroupIds.Length == 0
            ? new Dictionary<long, long>()
            : (await _dbContext.AssociationsTb
                .AsNoTracking()
                .Where(item => sharedGroupIds.Contains(item.id2) &&
                    (item.atype == GraphAssociationType.Member || item.atype == GraphAssociationType.Admin))
                .Select(item => new { item.id1, item.id2 })
                .Distinct()
                .ToListAsync(cancellationToken))
                .GroupBy(item => item.id2)
                .ToDictionary(group => group.Key, group => (long)group.Select(item => item.id1).Distinct().Count());

        SharedPostGroupResult? BuildSharedGroup(long sharedGroupId)
        {
            if (!relatedObjects.TryGetValue(sharedGroupId, out var sharedGroup) ||
                sharedGroup.otype != GraphObjectType.Group)
            {
                return null;
            }

            var sharedGroupData = GraphJson.ParseObject(sharedGroup.data);
            return new SharedPostGroupResult(
                sharedGroupId,
                GraphJson.String(sharedGroupData, "name"),
                GraphJson.String(sharedGroupData, "avatar"),
                GraphJson.String(sharedGroupData, "background"),
                GraphJson.Int(sharedGroupData, "privacy"),
                sharedGroupMemberCounts.GetValueOrDefault(sharedGroupId),
                participatingGroups.Contains(sharedGroupId),
                pendingGroups.Contains(sharedGroupId));
        }

        var results = new List<IHomePostResult>(orderedPostIds.Length);

        foreach (var postId in orderedPostIds)
        {
            if (!posts.TryGetValue(postId, out var post) ||
                !authorByPost.TryGetValue(postId, out var authorId) ||
                !relatedObjects.TryGetValue(authorId, out var author) ||
                author.otype != GraphObjectType.User)
            {
                continue;
            }

            var isExactGroupContext = groupContextId is > 0 &&
                post.otype == GraphObjectType.GroupPost &&
                groupByPost.GetValueOrDefault(postId) == groupContextId.Value;
            if ((groupContextId is not null && !isExactGroupContext) ||
                (viewerId != authorId && blocked.Contains(authorId) && !isExactGroupContext))
            {
                continue;
            }

            Objects? group = null;
            var groupId = 0L;
            var postData = GraphJson.ParseObject(post.data);
            var privacy = GraphJson.Int(postData, "privacy");
            if (post.otype == GraphObjectType.GroupPost)
            {
                if (!groupByPost.TryGetValue(postId, out groupId) ||
                    !relatedObjects.TryGetValue(groupId, out group) ||
                    group.otype != GraphObjectType.Group)
                {
                    continue;
                }

                privacy = GraphJson.Int(GraphJson.ParseObject(group.data), "privacy");
            }

            var canView = post.otype is GraphObjectType.FeedPost or GraphObjectType.Reel
                ? CanViewFeedLikeContent(viewerId, authorId, privacy, friends, followed, blocked)
                : privacy == 0 || participatingGroups.Contains(groupId);
            if (!canView)
            {
                continue;
            }

            var authorData = GraphJson.ParseObject(author.data);
            var postAuthor = new PostAuthorResult(
                author.id,
                GraphJson.String(authorData, "name"),
                GraphJson.String(authorData, "avatar"),
                IsVerifyActive(authorData),
                viewerId != authorId &&
                GraphJson.Int(authorData, "privacy") == 1 &&
                !friends.Contains(authorId) &&
                !followed.Contains(authorId));
            var media = postLinks
                .Where(item => item.id1 == postId && item.atype == GraphAssociationType.Contained)
                .OrderByDescending(item => item.time)
                .Select(item => relatedObjects.TryGetValue(item.id2, out var mediaObject)
                    ? BuildMediaResult(mediaObject)
                    : null)
                .OfType<MediaResult>()
                .ToArray();
            var content = GraphJson.String(postData, "content");
            var create = GraphJson.String(postData, "create");
            var mentions = BuildMentionUsers(content, relatedObjects, blocked, viewerId);
            SharedPostSourceResult? sharedSource = null;
            if (sourceByPost.TryGetValue(post.id, out var sourceId))
            {
                if (!relatedObjects.TryGetValue(sourceId, out var source) ||
                    source.otype is not (GraphObjectType.Group or GraphObjectType.FeedPost or GraphObjectType.GroupPost or GraphObjectType.Reel))
                {
                    sharedSource = new SharedPostSourceResult(
                        sourceId,
                        false,
                        null,
                        null,
                        null,
                        Array.Empty<MediaResult>(),
                        Array.Empty<MentionUserResult>());
                }
                else if (source.otype == GraphObjectType.Group)
                {
                    var sharedGroup = BuildSharedGroup(sourceId);
                    sharedSource = new SharedPostSourceResult(
                        sourceId,
                        sharedGroup is not null,
                        source.otype,
                        null,
                        null,
                        Array.Empty<MediaResult>(),
                        Array.Empty<MentionUserResult>(),
                        sharedGroup?.Privacy,
                        GraphJson.String(GraphJson.ParseObject(source.data), "create"),
                        sharedGroup);
                }
                else
                {
                    var sourceData = GraphJson.ParseObject(source.data);
                    var sourceAuthorId = sourceAuthorBySource.GetValueOrDefault(sourceId);
                    relatedObjects.TryGetValue(sourceAuthorId, out var sourceAuthor);
                    var hasSourceAuthor = sourceAuthorId > 0 &&
                        sourceAuthor is not null &&
                        sourceAuthor.otype == GraphObjectType.User;
                    var sourcePrivacy = GraphJson.Int(sourceData, "privacy");
                    SharedPostGroupResult? sharedGroup = null;
                    var requiresGroupMembership = false;
                    bool sourceAvailable;

                    if (source.otype == GraphObjectType.GroupPost)
                    {
                        var sourceGroupId = sourceGroupBySource.GetValueOrDefault(sourceId);
                        sharedGroup = BuildSharedGroup(sourceGroupId);
                        sourcePrivacy = sharedGroup?.Privacy ?? 1;
                        sourceAvailable = hasSourceAuthor &&
                            sharedGroup is not null &&
                            !blocked.Contains(sourceAuthorId) &&
                            (sourcePrivacy == 0 || sharedGroup.ViewerIsMember);
                        requiresGroupMembership = hasSourceAuthor &&
                            sharedGroup is not null &&
                            !blocked.Contains(sourceAuthorId) &&
                            sourcePrivacy != 0 &&
                            !sharedGroup.ViewerIsMember;
                    }
                    else
                    {
                        sourceAvailable = hasSourceAuthor &&
                            CanViewFeedLikeContent(
                                viewerId,
                                sourceAuthorId,
                                sourcePrivacy,
                                friends,
                                followed,
                                blocked);
                    }

                    if (!sourceAvailable)
                    {
                        sharedSource = new SharedPostSourceResult(
                            sourceId,
                            false,
                            source.otype,
                            null,
                            null,
                            Array.Empty<MediaResult>(),
                            Array.Empty<MentionUserResult>(),
                            sourcePrivacy,
                            null,
                            requiresGroupMembership ? sharedGroup : null,
                            requiresGroupMembership);
                    }
                    else
                    {
                        var sourceAuthorData = GraphJson.ParseObject(sourceAuthor!.data);
                        var sourceMedia = sourceLinks
                            .Where(item => item.id1 == sourceId && item.atype == GraphAssociationType.Contained)
                            .OrderByDescending(item => item.time)
                            .Select(item => relatedObjects.TryGetValue(item.id2, out var mediaObject)
                                ? BuildMediaResult(mediaObject)
                                : null)
                            .OfType<MediaResult>()
                            .ToArray();
                        var sourceContent = GraphJson.String(sourceData, "content");
                        sharedSource = new SharedPostSourceResult(
                            sourceId,
                            true,
                            source.otype,
                            sourceContent,
                            new UserSummaryResult(
                                sourceAuthorId,
                                GraphJson.String(sourceAuthorData, "name"),
                                GraphJson.String(sourceAuthorData, "avatar"),
                                IsVerifyActive(sourceAuthorData)),
                            sourceMedia,
                            BuildMentionUsers(sourceContent, relatedObjects, blocked, viewerId),
                            sourcePrivacy,
                            GraphJson.String(sourceData, "create"),
                            sharedGroup,
                            AspectRatio: source.otype == GraphObjectType.Reel
                                ? GraphJson.NullableDouble(sourceData, "aspectRatio")
                                : null,
                            FocalPointX: source.otype == GraphObjectType.Reel
                                ? GraphJson.NullableDouble(sourceData, "focalPointX")
                                : null,
                            FocalPointY: source.otype == GraphObjectType.Reel
                                ? GraphJson.NullableDouble(sourceData, "focalPointY")
                                : null);
                    }
                }
            }

            if (post.otype == GraphObjectType.GroupPost && group is not null)
            {
                var groupData = GraphJson.ParseObject(group.data);
                var groupTaggedUsers = BuildTaggedUsers(postId, postLinks, relatedObjects, blocked, viewerId);
                results.Add(new GroupPostDetailResult(
                    post.id,
                    post.otype,
                    content,
                    privacy,
                    create,
                    postAuthor,
                    new PostGroupResult(
                        group.id,
                        GraphJson.String(groupData, "name"),
                        GraphJson.String(groupData, "avatar"),
                        !participatingGroups.Contains(group.id),
                        pendingGroups.Contains(group.id)),
                    media,
                    mentions,
                    groupTaggedUsers,
                    sharedSource));
                continue;
            }

            if (post.otype == GraphObjectType.Reel)
            {
                results.Add(new ReelDetailResult(
                    post.id,
                    post.otype,
                    content,
                    privacy,
                    create,
                    GraphJson.NullableDouble(postData, "aspectRatio"),
                    GraphJson.NullableDouble(postData, "focalPointX"),
                    GraphJson.NullableDouble(postData, "focalPointY"),
                    postAuthor,
                    media,
                    mentions));
                continue;
            }

            var taggedUsers = BuildTaggedUsers(postId, postLinks, relatedObjects, blocked, viewerId);
            results.Add(new FeedPostDetailResult(
                post.id,
                post.otype,
                content,
                privacy,
                create,
                postAuthor,
                media,
                sharedSource,
                mentions,
                taggedUsers));
        }

        return results;
    }

    public async Task<ContentResult> CreateCommentAsync(CreateCommentInput input, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input.Content) && input.Media is null)
        {
            throw new ArgumentException("A comment must contain text or one image.", nameof(input));
        }

        if (input.Media is not null && (input.Media.Type != 0 || string.IsNullOrWhiteSpace(input.Media.Url)))
        {
            throw new ArgumentException("Comment media must be one image with a valid URL.", nameof(input));
        }

        await EnsureReferencesAllowedAsync(input.AuthorId, MentionUserIds(input.Content), cancellationToken);
        var rootContentId = await ResolveRootPostIdAsync(input.TargetId, cancellationToken);
        if (rootContentId <= 0)
        {
            throw new InvalidOperationException("The comment target is unavailable.");
        }
        var rootPreflight = await _objectService.RetrieveObjectAsync(rootContentId, cancellationToken);
        if (rootPreflight?.otype is not (GraphObjectType.FeedPost or GraphObjectType.GroupPost or GraphObjectType.Reel))
        {
            throw new InvalidOperationException("The comment target is unavailable.");
        }
        var targetGroupId = rootPreflight.otype == GraphObjectType.GroupPost
            ? await GetPublishedGroupIdAsync(rootContentId, cancellationToken)
            : 0;
        if (rootPreflight.otype == GraphObjectType.GroupPost && targetGroupId <= 0)
        {
            throw new InvalidOperationException("The comment target group is unavailable.");
        }

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        SocialGraphObjectResult? comment = null;
        IReadOnlyList<MediaResult> media = Array.Empty<MediaResult>();
        MediaReservation? mediaReservation = null;
        var committed = false;
        var commitAttempted = false;
        try
        {
            await LockAuthorForContentCreationAsync(input.AuthorId, cancellationToken);
            if (targetGroupId > 0)
            {
                await AcquireGroupLifecycleLockAsync(targetGroupId, cancellationToken);
                var lockedGroup = await LockObjectRowAsync(targetGroupId, cancellationToken);
                if (lockedGroup?.otype != GraphObjectType.Group)
                {
                    throw new InvalidOperationException("The comment target group is unavailable.");
                }
            }
            var lockedRoot = await LockObjectRowAsync(rootContentId, cancellationToken, rootPreflight);
            if (lockedRoot?.otype is not (GraphObjectType.FeedPost or GraphObjectType.GroupPost or GraphObjectType.Reel))
            {
                throw new InvalidOperationException("The comment target is unavailable.");
            }
            if (input.TargetId != rootContentId)
            {
                var lockedTarget = await LockObjectRowAsync(input.TargetId, cancellationToken);
                if (lockedTarget?.otype != GraphObjectType.Comment ||
                    GraphJson.IsCommentDeleted(GraphJson.ParseObject(lockedTarget.data)))
                {
                    throw new InvalidOperationException("The replied-to comment is unavailable.");
                }
            }
            comment = await _objectService.AddObjectAsync(GraphObjectType.Comment, GraphJson.ContentJson(input.Content), cancellationToken);
            media = await AttachSingleMediaAsync(comment.id, input.Media, cancellationToken);
            var mentionedUserIds = MentionUserIds(input.Content);
            var mutations = new List<AssociationMutation>
            {
                new(input.AuthorId, GraphAssociationType.Authored, comment.id, true),
                new(input.TargetId, GraphAssociationType.HaveComment, comment.id, true)
            };
            mutations.AddRange(mentionedUserIds.Select(userId =>
                new AssociationMutation(comment.id, GraphAssociationType.Mentioned, userId, true)));
            await _associationService.ApplyMutationsAsync(
                mutations,
                cancellationToken);

            mediaReservation = await ReserveAndQueueMediaAsync(
                MediaLifecycleReferences.ForMedia(media),
                input.AuthorId,
                cancellationToken);
            if (transaction is not null)
            {
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken);
            }
            committed = true;
        }
        catch
        {
            if (!commitAttempted && transaction is not null)
            {
                // The comment was written through Redis before the commit, so the rollback has
                // to drop it from the cache as well or reads would keep returning it.
                await RollbackAndInvalidateAsync(transaction, comment?.id);
            }
            else if (!commitAttempted && comment is not null)
            {
                await _associationService.DeleteObjectAssociationsAsync(comment.id, CancellationToken.None);
                await _objectService.DeleteObjectAsync(comment.id, CancellationToken.None);
                await DeleteOrphanMediaAsync(media.Select(item => item.Id).ToArray(), CancellationToken.None, comment.id);
            }

            if (!committed && !commitAttempted)
            {
                await CancelReservationBestEffortAsync(mediaReservation);
            }

            throw;
        }

        var persistedComment = comment!;
        var mentionedUserIdsForNotifications = MentionUserIds(input.Content);
        var targetAuthorId = await GetAuthorIdAsync(input.TargetId, cancellationToken);
        if (targetAuthorId > 0 && targetAuthorId != input.AuthorId)
        {
            await _externalServiceClient.NotifyAsync(input.AuthorId, targetAuthorId, ExternalNotificationAction.Comment, input.TargetId, null, cancellationToken);
        }

        foreach (var mentionedUserId in mentionedUserIdsForNotifications.Where(userId => userId != input.AuthorId && userId != targetAuthorId))
        {
            await _externalServiceClient.NotifyAsync(
                input.AuthorId,
                mentionedUserId,
                ExternalNotificationAction.Mention,
                persistedComment.id,
                null,
                cancellationToken);
        }

        await QueueRecommendationInteractionIfContentAsync(
            input.AuthorId,
            input.TargetId,
            RecommendationInteractionAction.Comment,
            cancellationToken);

        return await BuildContentResultAsync(persistedComment, input.AuthorId, media, cancellationToken);
    }

    public async Task<NormalStoryResult> CreateNormalStoryAsync(
        CreateNormalStoryInput input,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        SocialGraphObjectResult? story = null;
        MediaReservation? mediaReservation = null;
        var committed = false;
        var commitAttempted = false;
        try
        {
            await LockAuthorForContentCreationAsync(input.AuthorId, cancellationToken);
            story = await _objectService.AddObjectAsync(GraphObjectType.Story, GraphJson.StoryJson(input.Content), cancellationToken);
            var media = await AttachSingleMediaAsync(story.id, input.Media, cancellationToken);
            await _associationService.AddAssociationAsync(input.AuthorId, GraphAssociationType.Authored, story.id, cancellationToken);
            mediaReservation = await ReserveAndQueueMediaAsync(
                MediaLifecycleReferences.ForMedia(media),
                input.AuthorId,
                cancellationToken);
            if (transaction is not null)
            {
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken);
            }
            committed = true;

            var data = GraphJson.ParseObject(story.data);
            return new NormalStoryResult(
                story.id,
                GraphJson.String(data, "content"),
                GraphJson.String(data, "create"),
                media);
        }
        catch
        {
            if (!committed && !commitAttempted)
            {
                await RollbackAndInvalidateAsync(transaction, story?.id);
                await CancelReservationBestEffortAsync(mediaReservation);
            }
            throw;
        }
    }

    public async Task<IHomeStoryResult> CreateShareStoryAsync(
        CreateShareStoryInput input,
        CancellationToken cancellationToken = default)
    {
        var sharedSourceId = await ResolveCanonicalShareSourceIdAsync(input.SharedSourceId, cancellationToken);
        var sharedSource = await RequireStoryShareSourceAsync(input.AuthorId, sharedSourceId, cancellationToken);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        SocialGraphObjectResult? story = null;
        var commitAttempted = false;
        try
        {
            await LockAuthorForContentCreationAsync(input.AuthorId, cancellationToken);
            story = await _objectService.AddObjectAsync(
                GraphObjectType.Story,
                GraphJson.StoryJson(input.Content),
                cancellationToken);
            await _associationService.AddAssociationAsync(
                input.AuthorId,
                GraphAssociationType.Authored,
                story.id,
                cancellationToken);
            await _associationService.AddAssociationAsync(
                story.id,
                GraphAssociationType.Share,
                sharedSourceId,
                cancellationToken);
            await _externalServiceClient.RecordRecommendationInteractionAsync(
                input.AuthorId,
                sharedSourceId,
                RecommendationInteractionAction.Share,
                cancellationToken);

            var sourceAuthorId = sharedSource switch
            {
                FeedPostSharedSourceResult feedPost => feedPost.Author?.Id ?? 0,
                ReelSharedSourceResult reel => reel.Author?.Id ?? 0,
                _ => 0
            };
            if (sourceAuthorId > 0 && sourceAuthorId != input.AuthorId)
            {
                await _externalServiceClient.NotifyAsync(
                    input.AuthorId,
                    sourceAuthorId,
                    ExternalNotificationAction.Share,
                    sharedSourceId,
                    new { shareId = story.id, shareType = "story" },
                    cancellationToken);
            }

            if (transaction is not null)
            {
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken);
            }

            return BuildShareStoryResult(story, sharedSource);
        }
        catch
        {
            if (!commitAttempted)
            {
                await RollbackAndInvalidateAsync(transaction, story?.id);
            }
            throw;
        }
    }

    public async Task<DeleteStoryPayload> DeleteStoryAsync(
        DeleteStoryInput input,
        CancellationToken cancellationToken = default)
    {
        var story = await _objectService.RetrieveObjectAsync(input.StoryId, cancellationToken);
        if (story is null)
        {
            return new DeleteStoryPayload(false, "Story not found.");
        }

        if (story.otype != GraphObjectType.Story)
        {
            return new DeleteStoryPayload(false, "Object is not a story.");
        }

        var authorId = await GetAuthorIdAsync(input.StoryId, cancellationToken);
        if (authorId != input.AuthorId)
        {
            return new DeleteStoryPayload(false, "Only the story author can delete this story.");
        }

        var deleted = await DeleteStoryWithTemporaryMediaAsync(input.StoryId, cancellationToken);
        return deleted
            ? new DeleteStoryPayload(true, "Story deleted.")
            : new DeleteStoryPayload(false, "Story delete failed.");
    }

    public async Task<HomeStoryPageResult> GetHomeStoriesAsync(
        long userId,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, 50);
        var visibleAuthorIds = await GetVisibleStoryAuthorIdsAsync(userId, cancellationToken);
        if (visibleAuthorIds.Count == 0)
        {
            return new HomeStoryPageResult(Array.Empty<HomeStoryBucketResult>(), null, false);
        }

        var now = DateTimeOffset.UtcNow;
        var buckets = await GetActiveStoryBucketCandidatesAsync(
            visibleAuthorIds,
            now,
            cancellationToken);

        if (TryDecodeStoryCursor(cursor, out var decodedCursor))
        {
            buckets = buckets
                .Where(item => item.LatestCreate < decodedCursor.LatestCreate ||
                    item.LatestCreate == decodedCursor.LatestCreate && item.AuthorId < decodedCursor.AuthorId)
                .ToArray();
        }

        var pageCandidates = buckets.Take(take + 1).ToArray();
        var selectedCandidates = pageCandidates.Take(take).ToArray();
        var selectedAuthorIds = selectedCandidates.Select(item => item.AuthorId).ToHashSet();
        var activeStories = await GetActiveStoriesAsync(selectedAuthorIds, now, cancellationToken);
        var storyItems = await BuildHomeStoryItemsAsync(userId, activeStories, cancellationToken);
        var authorSummaries = await GetUserSummariesAsync(selectedAuthorIds, cancellationToken);
        var storiesByAuthor = activeStories
            .GroupBy(item => item.AuthorId)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.CreatedAt).ToArray());
        var visibleStoryIds = activeStories
            .Where(story => storyItems.ContainsKey(story.Story.id))
            .Select(story => story.Story.id)
            .Distinct()
            .ToArray();
        var watchedStoryIds = visibleStoryIds.Length == 0
            ? new HashSet<long>()
            : (await _dbContext.AssociationsTb
                .AsNoTracking()
                .Where(item => item.id1 == userId &&
                               item.atype == GraphAssociationType.Watched &&
                               visibleStoryIds.Contains(item.id2))
                .Select(item => item.id2)
                .Distinct()
                .ToListAsync(cancellationToken))
                .ToHashSet();
        var resultItems = new List<HomeStoryBucketResult>(selectedCandidates.Length);

        foreach (var candidate in selectedCandidates)
        {
            if (!authorSummaries.TryGetValue(candidate.AuthorId, out var author) ||
                !storiesByAuthor.TryGetValue(candidate.AuthorId, out var authorStories))
            {
                continue;
            }

            var visibleStories = authorStories
                .Where(story => storyItems.ContainsKey(story.Story.id))
                .ToArray();
            if (visibleStories.Length == 0)
            {
                continue;
            }

            var unseenCount = visibleStories.Count(story => !watchedStoryIds.Contains(story.Story.id));
            resultItems.Add(new HomeStoryBucketResult(
                author,
                visibleStories[^1].CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                unseenCount > 0,
                unseenCount,
                visibleStories.Select(story => storyItems[story.Story.id]).ToArray()));
        }

        var endCursor = selectedCandidates.Length == 0
            ? null
            : EncodeStoryCursor(selectedCandidates[^1].LatestCreate, selectedCandidates[^1].AuthorId);

        return new HomeStoryPageResult(resultItems, endCursor, pageCandidates.Length > take);
    }

    public async Task<HomeStoryBucketResult?> GetMyStoriesAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        var authorSummaries = await GetUserSummariesAsync(new HashSet<long> { userId }, cancellationToken);
        if (!authorSummaries.TryGetValue(userId, out var author))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var activeStories = await GetActiveStoriesAsync(new HashSet<long> { userId }, now, cancellationToken);
        if (activeStories.Count == 0)
        {
            return null;
        }

        var storyItems = await BuildHomeStoryItemsAsync(userId, activeStories, cancellationToken);
        var orderedStories = activeStories
            .Where(story => storyItems.ContainsKey(story.Story.id))
            .OrderBy(story => story.CreatedAt)
            .ToArray();
        if (orderedStories.Length == 0)
        {
            return null;
        }

        return new HomeStoryBucketResult(
            author,
            orderedStories[^1].CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            false,
            0,
            orderedStories.Select(story => storyItems[story.Story.id]).ToArray());
    }

    public async Task<int> CleanupExpiredStoriesAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, 500);
        var candidates = await _dbContext.ObjectsTb
            .AsNoTracking()
            .Where(item => item.otype == GraphObjectType.Story)
            .OrderBy(item => item.id)
            .Take(take)
            .Select(item => new { item.id, item.data })
            .ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var deleted = 0;

        foreach (var candidate in candidates)
        {
            var data = GraphJson.ParseObject(candidate.data);
            if (TryGetDateTimeOffset(data, "expire", out var expiresAt) && expiresAt > now)
            {
                // Story IDs are allocation ordered, not expiry ordered. An active low-ID
                // story must not prevent an expired higher-ID story (and its media) from
                // being reclaimed in this sweep.
                continue;
            }

            if (await DeleteStoryWithTemporaryMediaAsync(candidate.id, cancellationToken))
            {
                deleted++;
            }
        }

        return deleted;
    }

    public async Task<ContentResult> CreateReelAsync(CreateReelInput input, CancellationToken cancellationToken = default)
    {
        if (input.Privacy is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Reel privacy must be between 0 and 3.");
        }

        if (input.AspectRatio is { } aspectRatio &&
            (!double.IsFinite(aspectRatio) ||
             aspectRatio < MinReelAspectRatio - ReelPresentationEpsilon ||
             aspectRatio > MaxReelAspectRatio + ReelPresentationEpsilon))
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                $"Reel aspect ratio must be between {MinReelAspectRatio} and {MaxReelAspectRatio}.");
        }

        ValidateReelFocalPoint(input.FocalPointX, nameof(input.FocalPointX));
        ValidateReelFocalPoint(input.FocalPointY, nameof(input.FocalPointY));

        await EnsureReferencesAllowedAsync(input.AuthorId, MentionUserIds(input.Content), cancellationToken);
        double? normalizedAspectRatio = input.AspectRatio is { } value
            ? Math.Clamp(Math.Round(value, 6), MinReelAspectRatio, MaxReelAspectRatio)
            : null;
        var hasFocalPoint = input.FocalPointX.HasValue || input.FocalPointY.HasValue;
        double? normalizedFocalPointX = hasFocalPoint
            ? Math.Round(input.FocalPointX ?? 0.5d, 6)
            : null;
        double? normalizedFocalPointY = hasFocalPoint
            ? Math.Round(input.FocalPointY ?? 0.5d, 6)
            : null;
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        SocialGraphObjectResult? reel = null;
        MediaReservation? mediaReservation = null;
        var committed = false;
        var commitAttempted = false;
        try
        {
            await LockAuthorForContentCreationAsync(input.AuthorId, cancellationToken);
            reel = await _objectService.AddObjectAsync(
                GraphObjectType.Reel,
                GraphJson.ReelJson(
                    input.Content,
                    input.Privacy,
                    normalizedAspectRatio,
                    normalizedFocalPointX,
                    normalizedFocalPointY),
                cancellationToken);
            var media = await AttachSingleMediaAsync(reel.id, input.Media, cancellationToken);
            await _associationService.AddAssociationAsync(input.AuthorId, GraphAssociationType.Authored, reel.id, cancellationToken);
            foreach (var userId in MentionUserIds(input.Content))
            {
                if (!await AddUserReferenceAsync(
                        reel.id,
                        userId,
                        input.AuthorId,
                        GraphObjectType.Reel,
                        0,
                        GraphAssociationType.Mentioned,
                        ExternalNotificationAction.Mention,
                        cancellationToken))
                {
                    throw new InvalidOperationException("Unable to mention the selected account.");
                }
            }
            mediaReservation = await ReserveAndQueueMediaAsync(
                MediaLifecycleReferences.ForMedia(media),
                input.AuthorId,
                cancellationToken);
            await _externalServiceClient.CreateSearchIndexAsync(reel.id, "reel", input.Content, cancellationToken);
            await _externalServiceClient.CreatePostEmbeddingAsync(reel.id, input.Content, media.Select(item => item.Url).ToArray(), cancellationToken);
            if (transaction is not null)
            {
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken);
            }
            committed = true;
            return await BuildContentResultAsync(reel, input.AuthorId, media, cancellationToken);
        }
        catch
        {
            if (!committed && !commitAttempted)
            {
                await RollbackAndInvalidateAsync(transaction, reel?.id);
                await CancelReservationBestEffortAsync(mediaReservation);
            }
            throw;
        }
    }

    public async Task<long> ResolveCanonicalShareSourceIdAsync(
        long sourceId,
        CancellationToken cancellationToken = default)
    {
        var current = sourceId;
        var visited = new HashSet<long>();
        const int maxDepth = 32;

        for (var depth = 0; depth < maxDepth; depth++)
        {
            if (!visited.Add(current))
            {
                throw new InvalidOperationException("A cycle was detected in the share-source chain.");
            }

            var objectType = await _dbContext.ObjectsTb
                .AsNoTracking()
                .Where(item => item.id == current)
                .Select(item => (short?)item.otype)
                .FirstOrDefaultAsync(cancellationToken);
            if (objectType is not (GraphObjectType.FeedPost or GraphObjectType.GroupPost))
            {
                return current;
            }

            var next = await _dbContext.AssociationsTb
                .AsNoTracking()
                .Where(item => item.id1 == current && item.atype == GraphAssociationType.Share)
                .OrderByDescending(item => item.time)
                .Select(item => (long?)item.id2)
                .FirstOrDefaultAsync(cancellationToken);
            if (next is null or <= 0)
            {
                return current;
            }

            current = next.Value;
        }

        throw new InvalidOperationException($"The share-source chain exceeds the supported depth of {maxDepth}.");
    }

    public async Task<ContentResult> SharePostAsync(SharePostInput input, CancellationToken cancellationToken = default)
    {
        var sourceId = await ResolveCanonicalShareSourceIdAsync(input.SourceId, cancellationToken);
        var destinationGroupId = input.DestinationGroupId is > 0 ? input.DestinationGroupId.Value : 0;
        if (destinationGroupId == 0 && input.Privacy is (< 0 or > 3))
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Feed privacy must be between 0 and 3.");
        }

        var mentionedUserIds = MentionUserIds(input.Content);
        await EnsureReferencesAllowedAsync(input.AuthorId, mentionedUserIds, cancellationToken);
        if (destinationGroupId > 0)
        {
            await EnsureGroupReferencesAllowedAsync(input.AuthorId, destinationGroupId, mentionedUserIds, cancellationToken);
        }

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        SocialGraphObjectResult? wrapper = null;
        ContentResult post;
        try
        {
            await LockAuthorForContentCreationAsync(input.AuthorId, cancellationToken);
            if (destinationGroupId > 0)
            {
                await AcquireGroupLifecycleLockAsync(destinationGroupId, cancellationToken);
            }
            if (destinationGroupId > 0 &&
                !await LockCurrentGroupParticipationAsync(input.AuthorId, destinationGroupId, cancellationToken))
            {
                throw new InvalidOperationException("Only current group members and administrators can publish group posts.");
            }

            var wrapperType = destinationGroupId > 0 ? GraphObjectType.GroupPost : GraphObjectType.FeedPost;
            var wrapperData = destinationGroupId > 0
                ? GraphJson.GroupPostJson(input.Content)
                : GraphJson.PostJson(input.Content, input.Privacy);
            wrapper = await _objectService.AddObjectAsync(wrapperType, wrapperData, cancellationToken);
            if (!await _associationService.AddAssociationAsync(input.AuthorId, GraphAssociationType.Authored, wrapper.id, cancellationToken))
            {
                throw new InvalidOperationException("Unable to attach the share author.");
            }
            if (destinationGroupId > 0)
            {
                if (!await _associationService.AddAssociationAsync(destinationGroupId, GraphAssociationType.Published, wrapper.id, cancellationToken))
                {
                    throw new InvalidOperationException("Unable to publish the share in the selected group.");
                }
            }

            if (!await _associationService.AddAssociationAsync(wrapper.id, GraphAssociationType.Share, sourceId, cancellationToken))
            {
                throw new InvalidOperationException("Unable to attach the canonical share source.");
            }
            foreach (var userId in mentionedUserIds)
            {
                if (!await AddUserReferenceAsync(
                        wrapper.id,
                        userId,
                        input.AuthorId,
                        wrapperType,
                        destinationGroupId,
                        GraphAssociationType.Mentioned,
                        ExternalNotificationAction.Mention,
                        cancellationToken,
                        groupReferenceAlreadyValidated: destinationGroupId > 0))
                {
                    throw new InvalidOperationException("Unable to mention the selected account.");
                }
            }

            await _externalServiceClient.CreateSearchIndexAsync(
                wrapper.id,
                destinationGroupId > 0 ? "groupPost" : "feedPost",
                input.Content,
                cancellationToken);
            await _externalServiceClient.CreatePostEmbeddingAsync(
                wrapper.id,
                input.Content,
                Array.Empty<string>(),
                cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            post = await BuildContentResultAsync(wrapper, input.AuthorId, Array.Empty<MediaResult>(), cancellationToken);
        }
        catch
        {
            await RollbackAndInvalidateAsync(transaction, wrapper?.id);
            throw;
        }

        await QueueRecommendationInteractionIfContentAsync(
            input.AuthorId,
            sourceId,
            RecommendationInteractionAction.Share,
            cancellationToken);
        var sourceAuthorId = await GetAuthorIdAsync(sourceId, cancellationToken);
        if (sourceAuthorId > 0 && sourceAuthorId != input.AuthorId)
        {
            await _externalServiceClient.NotifyAsync(
                input.AuthorId,
                sourceAuthorId,
                ExternalNotificationAction.Share,
                sourceId,
                new { shareId = post.Id, shareType = destinationGroupId > 0 ? "groupPost" : "feedPost" },
                cancellationToken);
        }

        return post;
    }

    public async Task<bool> LikeAsync(long userId, long targetId, CancellationToken cancellationToken = default)
    {
        var result = await _associationService.AddAssociationAsync(userId, GraphAssociationType.Liked, targetId, cancellationToken);
        if (result)
        {
            await QueueRecommendationInteractionIfContentAsync(
                userId,
                targetId,
                RecommendationInteractionAction.Like,
                cancellationToken);
            var targetAuthorId = await GetAuthorIdAsync(targetId, cancellationToken);
            if (targetAuthorId > 0 && targetAuthorId != userId)
            {
                await _externalServiceClient.NotifyAsync(userId, targetAuthorId, ExternalNotificationAction.Like, targetId, null, cancellationToken);
            }
        }

        return result;
    }

    public async Task<bool> UnlikeAsync(long userId, long targetId, CancellationToken cancellationToken = default)
    {
        var result = await _associationService.DeleteOneAssociationAsync(userId, GraphAssociationType.Liked, targetId, cancellationToken);
        if (result)
        {
            await QueueRecommendationInteractionIfContentAsync(
                userId,
                targetId,
                RecommendationInteractionAction.Unlike,
                cancellationToken);
        }

        return result;
    }

    public async Task<bool> SaveAsync(long userId, long targetId, CancellationToken cancellationToken = default)
    {
        var result = await _associationService.AddAssociationAsync(userId, GraphAssociationType.Saved, targetId, cancellationToken);
        if (result)
        {
            await QueueRecommendationInteractionIfContentAsync(
                userId,
                targetId,
                RecommendationInteractionAction.Save,
                cancellationToken);
        }

        return result;
    }

    public async Task<bool> UnsaveAsync(long userId, long targetId, CancellationToken cancellationToken = default)
    {
        var result = await _associationService.DeleteOneAssociationAsync(userId, GraphAssociationType.Saved, targetId, cancellationToken);
        if (result)
        {
            await QueueRecommendationInteractionIfContentAsync(
                userId,
                targetId,
                RecommendationInteractionAction.Unsave,
                cancellationToken);
        }

        return result;
    }

    public async Task<bool> WatchAsync(long userId, long targetId, CancellationToken cancellationToken = default)
    {
        var result = await _associationService.AddAssociationAsync(userId, GraphAssociationType.Watched, targetId, cancellationToken);
        if (result)
        {
            await QueueRecommendationInteractionIfContentAsync(
                userId,
                targetId,
                RecommendationInteractionAction.Watch,
                cancellationToken);
        }

        return result;
    }

    public async Task<bool> TagAsync(long postId, long userId, CancellationToken cancellationToken = default)
    {
        var source = await _objectService.RetrieveObjectAsync(postId, cancellationToken);
        if (source is null)
        {
            return false;
        }

        var authorId = await GetAuthorIdAsync(postId, cancellationToken);
        var groupId = source.otype == GraphObjectType.GroupPost
            ? await GetPublishedGroupIdAsync(postId, cancellationToken)
            : 0;
        return await AddUserReferenceAsync(
            postId,
            userId,
            authorId,
            source.otype,
            groupId,
            GraphAssociationType.Tagged,
            ExternalNotificationAction.Tag,
            cancellationToken);
    }

    public async Task<bool> MentionAsync(long sourceId, long userId, CancellationToken cancellationToken = default)
    {
        var source = await _objectService.RetrieveObjectAsync(sourceId, cancellationToken);
        if (source is null)
        {
            return false;
        }
        if (source.otype == GraphObjectType.Comment &&
            GraphJson.IsCommentDeleted(GraphJson.ParseObject(source.data)))
        {
            return false;
        }

        var authorId = await GetAuthorIdAsync(sourceId, cancellationToken);
        var groupId = source.otype == GraphObjectType.GroupPost
            ? await GetPublishedGroupIdAsync(sourceId, cancellationToken)
            : 0;
        return await AddUserReferenceAsync(
            sourceId,
            userId,
            authorId,
            source.otype,
            groupId,
            GraphAssociationType.Mentioned,
            ExternalNotificationAction.Mention,
            cancellationToken);
    }

    private async Task<bool> AddUserReferenceAsync(
        long sourceId,
        long userId,
        long authorId,
        short sourceType,
        long groupId,
        short associationType,
        short notificationAction,
        CancellationToken cancellationToken,
        bool groupReferenceAlreadyValidated = false)
    {
        if (authorId <= 0 || await _blockVisibility.IsBlockedEitherDirectionAsync(authorId, userId, cancellationToken))
        {
            return false;
        }

        if (sourceType == GraphObjectType.GroupPost && !groupReferenceAlreadyValidated)
        {
            if (groupId <= 0)
            {
                return false;
            }

            await EnsureGroupReferencesAllowedAsync(authorId, groupId, new[] { userId }, cancellationToken);
        }

        var result = await _associationService.AddAssociationAsync(sourceId, associationType, userId, cancellationToken);
        if (result && authorId > 0 && authorId != userId)
        {
            await _externalServiceClient.NotifyAsync(authorId, userId, notificationAction, sourceId, null, cancellationToken);
        }

        return result;
    }

    private async Task QueueRecommendationInteractionIfContentAsync(
        long userId,
        long targetId,
        string action,
        CancellationToken cancellationToken)
    {
        var target = await _objectService.RetrieveObjectAsync(targetId, cancellationToken);
        if (target?.otype is not (GraphObjectType.FeedPost or GraphObjectType.GroupPost or GraphObjectType.Reel))
        {
            return;
        }

        await _externalServiceClient.RecordRecommendationInteractionAsync(
            userId,
            targetId,
            action,
            cancellationToken);
    }

    private Task<IReadOnlyList<MediaResult>> AttachSingleMediaAsync(
        long contentId,
        MediaInput? media,
        CancellationToken cancellationToken)
    {
        return AttachMediaAsync(contentId, media is null ? null : new[] { media }, cancellationToken);
    }

    private async Task<MediaReservation?> ReserveAndQueueMediaAsync(
        IReadOnlyList<MediaLifecycleReference> references,
        long ownerUserId,
        CancellationToken cancellationToken)
    {
        if (references.Count == 0)
        {
            return null;
        }

        var operationAt = await _externalServiceClient.GetMediaOperationTimeAsync(cancellationToken);
        if (_mediaOwnershipGuard is not null)
        {
            await _mediaOwnershipGuard.EnsureReferencesOwnedAsync(
                ownerUserId,
                references,
                operationAt,
                cancellationToken);
        }

        var reservation = new MediaReservation(ownerUserId, references, operationAt);
        try
        {
            await _externalServiceClient.FinalizeMediaAsync(
                references,
                ownerUserId,
                operationAt,
                cancellationToken);
            return reservation;
        }
        catch
        {
            await CancelReservationBestEffortAsync(reservation);
            throw;
        }
    }

    private Task CancelReservationBestEffortAsync(MediaReservation? reservation)
    {
        return reservation is null || _mediaOwnershipGuard is null
            ? Task.CompletedTask
            : _mediaOwnershipGuard.CancelReferenceReservationBestEffortAsync(
                reservation.OwnerUserId,
                reservation.References,
                reservation.OperationAt,
                CancellationToken.None);
    }

    private async Task<IReadOnlyList<MediaResult>> AttachMediaAsync(
        long contentId,
        IReadOnlyList<MediaInput>? media,
        CancellationToken cancellationToken)
    {
        if (media is null || media.Count == 0)
        {
            return Array.Empty<MediaResult>();
        }

        var results = new List<MediaResult>(media.Count);
        foreach (var input in media)
        {
            var mediaObject = await _objectService.AddObjectAsync(GraphObjectType.Media, GraphJson.MediaJson(input.Type, input.Url), cancellationToken);
            await _associationService.AddAssociationAsync(contentId, GraphAssociationType.Contained, mediaObject.id, cancellationToken);
            results.Add(new MediaResult(mediaObject.id, input.Type, input.Url));
        }

        return results;
    }

    private sealed record MediaReservation(
        long OwnerUserId,
        IReadOnlyList<MediaLifecycleReference> References,
        DateTimeOffset OperationAt);

    private async Task<IReadOnlyList<MediaResult>> GetMediaAsync(long contentId, CancellationToken cancellationToken)
    {
        var mediaIds = await _associationService.RetrieveAssociationAsync(contentId, GraphAssociationType.Contained, null, 100, cancellationToken);
        var results = new List<MediaResult>(mediaIds.items.Count);
        foreach (var edge in mediaIds.items)
        {
            var item = await _objectService.RetrieveObjectAsync(edge.id2, cancellationToken);
            if (item is null || item.otype != GraphObjectType.Media)
            {
                continue;
            }

            var data = GraphJson.ParseObject(item.data);
            results.Add(new MediaResult(item.id, GraphJson.Int(data, "type"), GraphJson.String(data, "url")));
        }

        return results;
    }

    private async Task<long> GetAuthorIdAsync(long contentId, CancellationToken cancellationToken)
    {
        var author = await _associationService.RetrieveAssociationAsync(contentId, GraphAssociationType.AuthoredBy, null, 1, cancellationToken);
        return author.items.FirstOrDefault()?.id2 ?? 0;
    }

    private async Task<ContentResult> BuildContentResultAsync(
        SocialGraphObjectResult item,
        long authorId,
        IReadOnlyList<MediaResult> media,
        CancellationToken cancellationToken)
    {
        var data = GraphJson.ParseObject(item.data);
        var deletedComment = item.otype == GraphObjectType.Comment && GraphJson.IsCommentDeleted(data);
        var privacy = await GetContentPrivacyAsync(item, data, cancellationToken);
        return new ContentResult(
            item.id,
            item.otype,
            deletedComment ? string.Empty : GraphJson.String(data, "content"),
            privacy,
            GraphJson.String(data, "create"),
            authorId,
            deletedComment ? Array.Empty<MediaResult>() : media,
            item.otype == GraphObjectType.Reel ? GraphJson.NullableDouble(data, "aspectRatio") : null,
            item.otype == GraphObjectType.Reel ? GraphJson.NullableDouble(data, "focalPointX") : null,
            item.otype == GraphObjectType.Reel ? GraphJson.NullableDouble(data, "focalPointY") : null);
    }

    private static void ValidateReelFocalPoint(double? focalPoint, string parameterName)
    {
        if (focalPoint is { } value && (!double.IsFinite(value) || value is < 0d or > 1d))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Reel focal points must be between 0 and 1.");
        }
    }

    private async Task<int> GetContentPrivacyAsync(
        SocialGraphObjectResult item,
        System.Text.Json.Nodes.JsonObject data,
        CancellationToken cancellationToken)
    {
        if (item.otype is GraphObjectType.FeedPost or GraphObjectType.Reel)
        {
            return GraphJson.Int(data, "privacy");
        }

        if (item.otype != GraphObjectType.GroupPost)
        {
            return 0;
        }

        var groupId = await GetPublishedGroupIdAsync(item.id, cancellationToken);
        var group = await _objectService.RetrieveObjectAsync(groupId, cancellationToken);
        if (group is null || group.otype != GraphObjectType.Group)
        {
            return 0;
        }

        return GraphJson.Int(GraphJson.ParseObject(group.data), "privacy");
    }

    private async Task<long> GetPublishedGroupIdAsync(long postId, CancellationToken cancellationToken)
    {
        var group = await _associationService.RetrieveAssociationAsync(postId, GraphAssociationType.PublishedIn, null, 1, cancellationToken);
        return group.items.FirstOrDefault()?.id2 ?? 0;
    }

    private static bool CanViewFeedLikeContent(
        long viewerId,
        long authorId,
        int privacy,
        IReadOnlySet<long> friends,
        IReadOnlySet<long> followed,
        IReadOnlySet<long> blocked)
    {
        if (authorId <= 0 || viewerId != authorId && blocked.Contains(authorId))
        {
            return false;
        }

        return viewerId == authorId || privacy switch
        {
            0 => true,
            1 => friends.Contains(authorId) || followed.Contains(authorId),
            2 => friends.Contains(authorId),
            3 => false,
            _ => false
        };
    }

    private static HashSet<long> RelationTargets(
        IReadOnlyList<Associations> associations,
        params short[] associationTypes)
    {
        return associations
            .Where(item => associationTypes.Contains(item.atype))
            .Select(item => item.id2)
            .ToHashSet();
    }

    /// <summary>
    /// Authors whose stories the viewer may see: friends and followed accounts, minus blocks.
    /// Following is deliberately sufficient on its own — the author's profile privacy does not
    /// further restrict stories. Blocks still override every relationship, in both directions.
    /// </summary>
    private async Task<IReadOnlySet<long>> GetVisibleStoryAuthorIdsAsync(long userId, CancellationToken cancellationToken)
    {
        var friends = await GetAssociationIdsAsync(userId, GraphAssociationType.Friend, 500, cancellationToken);
        var followed = await GetAssociationIdsAsync(userId, GraphAssociationType.Followed, 500, cancellationToken);

        var candidates = friends
            .Concat(followed)
            .Where(id => id != userId)
            .ToHashSet();
        if (candidates.Count == 0)
        {
            return candidates;
        }

        // Block wins in both directions.
        var candidateIds = candidates.ToArray();
        var blocked = await _dbContext.AssociationsTb
            .AsNoTracking()
            .Where(item => item.id1 == userId &&
                candidateIds.Contains(item.id2) &&
                (item.atype == GraphAssociationType.Blocked ||
                 item.atype == GraphAssociationType.BlockedBy))
            .Select(item => item.id2)
            .ToListAsync(cancellationToken);
        candidates.ExceptWith(blocked);

        return candidates;
    }

    private async Task<IReadOnlyList<long>> GetAssociationIdsAsync(long id1, short atype, int limit, CancellationToken cancellationToken)
    {
        var remaining = Math.Clamp(limit, 1, 1000);
        var results = new List<long>(remaining);
        string? cursor = null;

        do
        {
            var page = await _associationService.RetrieveAssociationAsync(id1, atype, cursor, Math.Min(remaining, 100), cancellationToken);
            results.AddRange(page.items.Select(item => item.id2));
            remaining -= page.items.Count;
            cursor = page.nextCursor;
        }
        while (cursor is not null && remaining > 0);

        return results;
    }

    private async Task<bool> DeleteStoryWithTemporaryMediaAsync(long storyId, CancellationToken cancellationToken)
    {
        var mediaIds = await GetContainedMediaIdsAsync(storyId, cancellationToken);

        IDbContextTransaction? transaction = null;
        if (_dbContext.Database.IsRelational())
        {
            transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        }

        try
        {
            var locked = await LockObjectRowAsync(storyId, cancellationToken);
            if (locked?.otype != GraphObjectType.Story)
            {
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
                return false;
            }

            mediaIds = await GetContainedMediaIdsAsync(storyId, cancellationToken);
            await _associationService.DeleteObjectAssociationsAsync(storyId, cancellationToken);
            var deleted = await _objectService.DeleteObjectAsync(storyId, cancellationToken);
            if (deleted)
            {
                await DeleteOrphanMediaAsync(mediaIds, cancellationToken, storyId);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return deleted;
        }
        catch
        {
            // Objects and edges are written through Redis before the transaction commits, so
            // rolling back without invalidating left content that does not exist in the cache,
            // and the read paths prefer the cache over PostgreSQL.
            await RollbackAndInvalidateAsync(transaction, storyId);
            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Rolls the transaction back and drops anything the failed attempt left in the cache.
    /// </summary>
    private async Task RollbackAndInvalidateAsync(IDbContextTransaction? transaction, long? objectId)
    {
        if (transaction is null)
        {
            return;
        }

        await transaction.RollbackAsync(CancellationToken.None);
        if (objectId is { } id)
        {
            await _objectService.InvalidateObjectCacheAsync(id);
        }
    }

    private async Task DeleteOrphanMediaAsync(
        IEnumerable<long> mediaIds,
        CancellationToken cancellationToken,
        long? removedContainerId = null)
    {
        foreach (var mediaId in mediaIds.Distinct())
        {
            var stillContained = await _dbContext.AssociationsTb
                .AsNoTracking()
                .AnyAsync(
                    item => item.atype == GraphAssociationType.Contained &&
                            item.id2 == mediaId &&
                            (!removedContainerId.HasValue || item.id1 != removedContainerId.Value),
                    cancellationToken);
            if (stillContained)
            {
                continue;
            }

            var media = await _dbContext.ObjectsTb
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.id == mediaId, cancellationToken);
            if (media?.otype != GraphObjectType.Media)
            {
                continue;
            }

            var mediaUrl = GraphJson.String(GraphJson.ParseObject(media.data), "url");
            await _associationService.DeleteObjectAssociationsAsync(mediaId, cancellationToken);
            if (await _objectService.DeleteObjectAsync(mediaId, cancellationToken) &&
                !string.IsNullOrWhiteSpace(mediaUrl))
            {
                // Detach this exact Media object. Upload Server owns the physical reference count,
                // so another Media object or profile slot may safely keep the same URL alive.
                await _externalServiceClient.DeleteMediaAsync(
                    new[] { MediaLifecycleReferences.ForMedia(mediaId, mediaUrl) },
                    null,
                    cancellationToken);
            }
        }
    }

    /// <summary>
    /// Serializes edits and tombstones against the comment row. This prevents concurrent edits
    /// from dropping a revision and prevents an edit racing with delete from reviving content.
    /// </summary>
    private async Task<SocialGraphObjectResult?> MutateCommentObjectAsync(
        long commentId,
        Func<JsonObject, bool> mutate,
        CancellationToken cancellationToken,
        Func<Task>? mutateAssociations = null)
    {
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        try
        {
            Objects? row;
            if (_dbContext.Database.IsRelational())
            {
                row = await _dbContext.ObjectsTb
                    .FromSqlInterpolated($"SELECT * FROM social_graph.objects WHERE id = {commentId} AND otype = {GraphObjectType.Comment} FOR UPDATE")
                    .SingleOrDefaultAsync(cancellationToken);
            }
            else
            {
                row = await _dbContext.ObjectsTb
                    .SingleOrDefaultAsync(
                        item => item.id == commentId && item.otype == GraphObjectType.Comment,
                        cancellationToken);
            }

            if (row is null)
            {
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
                return null;
            }

            var data = GraphJson.ParseObject(row.data);
            if (!mutate(data))
            {
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
                return null;
            }

            var nextData = data.ToJsonString();
            var changed = !string.Equals(row.data, nextData, StringComparison.Ordinal);
            if (changed)
            {
                row.data = nextData;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            if (mutateAssociations is not null)
            {
                // Mention edges and their outbox notifications commit under the same row lock as
                // the text revision, so delete cannot interleave and recreate references on a
                // tombstone after its cleanup has completed.
                await mutateAssociations();
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            if (changed)
            {
                await _objectService.InvalidateObjectCacheAsync(commentId);
            }

            return new SocialGraphObjectResult(row.id, row.otype, row.data);
        }
        catch
        {
            await RollbackAndInvalidateAsync(transaction, commentId);
            throw;
        }
    }

    private async Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (!_dbContext.Database.IsRelational() || _dbContext.Database.CurrentTransaction is not null)
        {
            return null;
        }

        return await _dbContext.Database.BeginTransactionAsync(cancellationToken);
    }

    private async Task<Objects?> LockObjectRowAsync(
        long objectId,
        CancellationToken cancellationToken,
        SocialGraphObjectResult? nonRelationalFallback = null)
    {
        if (_dbContext.Database.IsRelational())
        {
            return await _dbContext.ObjectsTb
                .FromSqlInterpolated($"SELECT * FROM social_graph.objects WHERE id = {objectId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
        }

        var entity = await _dbContext.ObjectsTb.SingleOrDefaultAsync(
            item => item.id == objectId,
            cancellationToken);
        if (entity is not null)
        {
            return entity;
        }

        var current = nonRelationalFallback ??
            await _objectService.RetrieveObjectAsync(objectId, cancellationToken);
        return current is null
            ? null
            : new Objects { id = current.id, otype = current.otype, data = current.data };
    }

    private async Task LockAuthorForContentCreationAsync(
        long authorId,
        CancellationToken cancellationToken)
    {
        if (!_dbContext.Database.IsRelational())
        {
            return;
        }

        var author = await _dbContext.ObjectsTb
            .FromSqlInterpolated($"SELECT * FROM social_graph.objects WHERE id = {authorId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (author?.otype != GraphObjectType.User)
        {
            throw new InvalidOperationException("The content author is no longer active.");
        }
    }

    private Task AcquireGroupLifecycleLockAsync(
        long groupId,
        CancellationToken cancellationToken)
    {
        if (!_dbContext.Database.IsRelational() ||
            _dbContext.Database.CurrentTransaction is null ||
            !string.Equals(
                _dbContext.Database.ProviderName,
                "Npgsql.EntityFrameworkCore.PostgreSQL",
                StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        return _dbContext.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(@groupId)",
            new object[] { new NpgsqlParameter("groupId", groupId) },
            cancellationToken);
    }

    private async Task<bool> LockCurrentGroupParticipationAsync(
        long userId,
        long groupId,
        CancellationToken cancellationToken)
    {
        if (userId <= 0 || groupId <= 0)
        {
            return false;
        }

        if (!_dbContext.Database.IsRelational())
        {
            return await _associationService.HasAssociationAsync(
                    userId,
                    GraphAssociationType.Member,
                    groupId,
                    cancellationToken) ||
                await _associationService.HasAssociationAsync(
                    userId,
                    GraphAssociationType.Admin,
                    groupId,
                    cancellationToken);
        }

        // Hold a row lock until the surrounding content transaction commits. A concurrent
        // leave/remove operation must therefore finish before this check or wait until the
        // GroupPost is committed; it cannot revoke membership in the gap between policy
        // validation and the Published edge write.
        var participants = await _dbContext.Database.SqlQueryRaw<long>(
                """
                SELECT id1 AS "Value"
                FROM social_graph.associations
                WHERE id1 = @user_id
                  AND id2 = @group_id
                  AND atype IN (@member_type, @admin_type)
                LIMIT 1
                FOR KEY SHARE
                """,
                new NpgsqlParameter("user_id", userId),
                new NpgsqlParameter("group_id", groupId),
                new NpgsqlParameter("member_type", GraphAssociationType.Member),
                new NpgsqlParameter("admin_type", GraphAssociationType.Admin))
            .ToListAsync(cancellationToken);
        return participants.Count > 0;
    }

    private static IReadOnlyList<long> NormalizeUserIds(IReadOnlyList<long>? userIds)
    {
        if (userIds is null || userIds.Count == 0)
        {
            return Array.Empty<long>();
        }

        if (userIds.Any(id => id <= 0))
        {
            throw new ArgumentException("User IDs must be positive.", nameof(userIds));
        }

        return userIds.Distinct().Take(100).ToArray();
    }

    private static IReadOnlyList<long> MentionUserIds(string content) =>
        MentionTokenCodec.ExtractUserIds(content)
            .Take(100)
            .ToArray();

    private static IReadOnlyList<MentionUserResult> BuildMentionUsers(
        string content,
        IReadOnlyDictionary<long, Objects> objects,
        IReadOnlySet<long> blockedUserIds,
        long viewerId) =>
        MentionTokenCodec.ExtractUserIds(content)
            .Select(userId =>
            {
                if (userId != viewerId && blockedUserIds.Contains(userId))
                {
                    return new MentionUserResult(userId, string.Empty, false);
                }

                if (!objects.TryGetValue(userId, out var user) || user.otype != GraphObjectType.User)
                {
                    return new MentionUserResult(userId, string.Empty, false);
                }

                var data = GraphJson.ParseObject(user.data);
                return new MentionUserResult(userId, GraphJson.String(data, "name"), true);
            })
            .ToArray();

    private static IReadOnlyList<UserSummaryResult> BuildTaggedUsers(
        long postId,
        IReadOnlyList<Associations> postLinks,
        IReadOnlyDictionary<long, Objects> objects,
        IReadOnlySet<long> blockedUserIds,
        long viewerId) =>
        postLinks
            .Where(item => item.id1 == postId && item.atype == GraphAssociationType.Tagged)
            .Where(item => item.id2 == viewerId || !blockedUserIds.Contains(item.id2))
            .OrderBy(item => item.time)
            .ThenBy(item => item.id2)
            .Select(item => objects.TryGetValue(item.id2, out var taggedUser) && taggedUser.otype == GraphObjectType.User
                ? BuildUserSummary(taggedUser)
                : null)
            .OfType<UserSummaryResult>()
            .ToArray();

    private async Task SyncMentionAssociationsAsync(
        long sourceId,
        long authorId,
        string content,
        CancellationToken cancellationToken)
    {
        var desired = MentionUserIds(content).ToHashSet();
        await EnsureReferencesAllowedAsync(authorId, desired, cancellationToken);
        var existing = (await _dbContext.AssociationsTb
                .AsNoTracking()
                .Where(item => item.id1 == sourceId && item.atype == GraphAssociationType.Mentioned)
                .Select(item => item.id2)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        var added = desired.Except(existing).ToArray();
        var removed = existing.Except(desired).ToArray();
        var mutations = added
            .Select(userId => new AssociationMutation(sourceId, GraphAssociationType.Mentioned, userId, true))
            .Concat(removed.Select(userId => new AssociationMutation(sourceId, GraphAssociationType.Mentioned, userId, false)))
            .ToArray();
        if (mutations.Length > 0)
        {
            await _associationService.ApplyMutationsAsync(mutations, cancellationToken);
        }

        foreach (var userId in added.Where(userId => userId != authorId))
        {
            await _externalServiceClient.NotifyAsync(
                authorId,
                userId,
                ExternalNotificationAction.Mention,
                sourceId,
                null,
                cancellationToken);
        }
    }

    private async Task EnsureReferencesAllowedAsync(
        long authorId,
        IEnumerable<long> referencedUserIds,
        CancellationToken cancellationToken)
    {
        var referenced = referencedUserIds
            .Where(id => id > 0 && id != authorId)
            .Distinct()
            .ToArray();
        if (referenced.Length == 0)
        {
            return;
        }

        var blocked = await _blockVisibility.GetBlockedUserIdsAsync(authorId, referenced, cancellationToken);
        if (blocked.Count > 0)
        {
            // Deliberately omit the affected id: the mutation must not become an account
            // enumeration oracle for users that are hidden by a block.
            throw new InvalidOperationException("A blocked account cannot be tagged or mentioned.");
        }
    }

    private async Task EnsureGroupReferencesAllowedAsync(
        long authorId,
        long groupId,
        IEnumerable<long> referencedUserIds,
        CancellationToken cancellationToken)
    {
        var referenced = referencedUserIds
            .Where(id => id > 0 && id != authorId)
            .Distinct()
            .Take(100)
            .ToArray();
        foreach (var userId in referenced)
        {
            var isFriend = await _associationService.HasAssociationAsync(
                authorId,
                GraphAssociationType.Friend,
                userId,
                cancellationToken);
            var participates = await _associationService.HasAssociationAsync(
                    userId,
                    GraphAssociationType.Member,
                    groupId,
                    cancellationToken) ||
                await _associationService.HasAssociationAsync(
                    userId,
                    GraphAssociationType.Admin,
                    groupId,
                    cancellationToken);
            if (!isFriend || !participates)
            {
                // Keep this deliberately generic so the mutation cannot reveal whether a
                // hidden account is a friend, a group participant, or blocked.
                throw new InvalidOperationException("A selected account cannot be tagged or mentioned in this group.");
            }
        }
    }

    private async Task<IReadOnlyList<long>> GetContainedMediaIdsAsync(long contentId, CancellationToken cancellationToken)
    {
        // Destructive paths must enumerate PostgreSQL's authoritative edge set directly.
        // The association read model may be stale and its page API is capped, either of
        // which could leave exact Upload references permanently attached after deletion.
        return await _dbContext.AssociationsTb
            .AsNoTracking()
            .Where(edge => edge.id1 == contentId && edge.atype == GraphAssociationType.Contained)
            .Select(edge => edge.id2)
            .Distinct()
            .ToArrayAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<long>> GetDescendantCommentIdsAsync(
        long contentId,
        CancellationToken cancellationToken)
    {
        var descendants = new List<long>();
        var seen = new HashSet<long>();
        var frontier = new HashSet<long> { contentId };
        for (var depth = 0; depth < MaxCommentChainDepth && frontier.Count > 0; depth++)
        {
            var next = await _dbContext.AssociationsTb
                .AsNoTracking()
                .Where(edge => frontier.Contains(edge.id1) &&
                              edge.atype == GraphAssociationType.HaveComment)
                .Join(
                    _dbContext.ObjectsTb.AsNoTracking(),
                    edge => edge.id2,
                    obj => obj.id,
                    (edge, obj) => new { edge.id2, obj.otype })
                .Where(item => item.otype == GraphObjectType.Comment)
                .Select(item => item.id2)
                .Distinct()
                .ToListAsync(cancellationToken);
            next = next.Where(seen.Add).ToList();
            if (next.Count == 0)
            {
                return descendants;
            }

            descendants.AddRange(next);
            frontier = next.ToHashSet();
        }

        if (frontier.Count > 0 && await (
                from edge in _dbContext.AssociationsTb.AsNoTracking()
                join obj in _dbContext.ObjectsTb.AsNoTracking() on edge.id2 equals obj.id
                where frontier.Contains(edge.id1) &&
                      edge.atype == GraphAssociationType.HaveComment &&
                      obj.otype == GraphObjectType.Comment
                select edge.id2)
            .AnyAsync(cancellationToken))
        {
            // A malformed/cyclic chain must fail closed rather than silently leaving
            // descendant media permanently attached when the parent is deleted.
            throw new InvalidOperationException(
                $"Comment descendant depth exceeds the supported bound of {MaxCommentChainDepth}.");
        }

        return descendants;
    }

    private async Task<IReadOnlyList<StoryBucketCandidate>> GetActiveStoryBucketCandidatesAsync(
        IReadOnlySet<long> authorIds,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (authorIds.Count == 0)
        {
            return Array.Empty<StoryBucketCandidate>();
        }

        var latestStoryIds = await (
            from association in _dbContext.AssociationsTb.AsNoTracking()
            join obj in _dbContext.ObjectsTb.AsNoTracking() on association.id2 equals obj.id
            where authorIds.Contains(association.id1) &&
                association.atype == GraphAssociationType.Authored &&
                obj.otype == GraphObjectType.Story
            group obj by association.id1 into stories
            select new
            {
                AuthorId = stories.Key,
                StoryId = stories.Max(item => item.id)
            })
            .ToListAsync(cancellationToken);
        if (latestStoryIds.Count == 0)
        {
            return Array.Empty<StoryBucketCandidate>();
        }

        var latestIds = latestStoryIds.Select(item => item.StoryId).ToArray();
        var latestData = await _dbContext.ObjectsTb
            .AsNoTracking()
            .Where(item => latestIds.Contains(item.id))
            .ToDictionaryAsync(item => item.id, item => item.data, cancellationToken);
        var candidates = new List<StoryBucketCandidate>(latestStoryIds.Count);

        foreach (var latest in latestStoryIds)
        {
            if (!latestData.TryGetValue(latest.StoryId, out var rawData))
            {
                continue;
            }

            var data = GraphJson.ParseObject(rawData);
            if (!TryGetDateTimeOffset(data, "expire", out var expiresAt) || expiresAt <= now)
            {
                continue;
            }

            var createdAt = TryGetDateTimeOffset(data, "create", out var parsedCreatedAt)
                ? parsedCreatedAt
                : DateTimeOffset.UnixEpoch;
            candidates.Add(new StoryBucketCandidate(latest.AuthorId, createdAt));
        }

        return candidates
            .OrderByDescending(item => item.LatestCreate)
            .ThenByDescending(item => item.AuthorId)
            .ToArray();
    }

    private async Task<IReadOnlyList<ActiveStoryRow>> GetActiveStoriesAsync(
        IReadOnlySet<long> authorIds,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (authorIds.Count == 0)
        {
            return Array.Empty<ActiveStoryRow>();
        }

        var storyRows = await (
            from association in _dbContext.AssociationsTb.AsNoTracking()
            join obj in _dbContext.ObjectsTb.AsNoTracking() on association.id2 equals obj.id
            where authorIds.Contains(association.id1) &&
                association.atype == GraphAssociationType.Authored &&
                obj.otype == GraphObjectType.Story
            select new StoryRow(association.id1, obj.id, obj.data))
            .ToListAsync(cancellationToken);
        var results = new List<ActiveStoryRow>(storyRows.Count);

        foreach (var row in storyRows)
        {
            var data = GraphJson.ParseObject(row.Data);
            if (!TryGetDateTimeOffset(data, "expire", out var expiresAt) || expiresAt <= now)
            {
                continue;
            }

            var createdAt = TryGetDateTimeOffset(data, "create", out var parsedCreatedAt)
                ? parsedCreatedAt
                : DateTimeOffset.UnixEpoch;
            results.Add(new ActiveStoryRow(
                row.AuthorId,
                new SocialGraphObjectResult(row.StoryId, GraphObjectType.Story, row.Data),
                createdAt));
        }

        return results;
    }

    private async Task<IReadOnlyDictionary<long, IHomeStoryResult>> BuildHomeStoryItemsAsync(
        long viewerId,
        IReadOnlyList<ActiveStoryRow> stories,
        CancellationToken cancellationToken)
    {
        if (stories.Count == 0)
        {
            return new Dictionary<long, IHomeStoryResult>();
        }

        var storyIds = stories.Select(item => item.Story.id).ToArray();
        var storyLinks = await _dbContext.AssociationsTb
            .AsNoTracking()
            .Where(item => storyIds.Contains(item.id1) &&
                (item.atype == GraphAssociationType.Share || item.atype == GraphAssociationType.Contained))
            .ToListAsync(cancellationToken);
        var shareByStory = storyLinks
            .Where(item => item.atype == GraphAssociationType.Share)
            .GroupBy(item => item.id1)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.time).First().id2);
        var sourceIds = shareByStory.Values.Distinct().ToArray();
        var visibleSharedSources = sourceIds.Length == 0
            ? new Dictionary<long, IStorySharedSourceResult>()
            : (await GetPostDetailsAsync(viewerId, sourceIds, cancellationToken))
                .Where(item => item is FeedPostDetailResult or ReelDetailResult)
                .ToDictionary(GetHomePostId, BuildStorySharedSourceResult);

        var relatedIds = storyLinks
            .Where(item => item.atype == GraphAssociationType.Contained)
            .Select(item => item.id2)
            .Distinct()
            .ToArray();
        var relatedObjects = relatedIds.Length == 0
            ? new Dictionary<long, Objects>()
            : await _dbContext.ObjectsTb
                .AsNoTracking()
                .Where(item => relatedIds.Contains(item.id))
                .ToDictionaryAsync(item => item.id, cancellationToken);
        var results = new Dictionary<long, IHomeStoryResult>(stories.Count);

        foreach (var story in stories)
        {
            if (shareByStory.TryGetValue(story.Story.id, out var sourceId))
            {
                if (!visibleSharedSources.TryGetValue(sourceId, out var sharedSource))
                {
                    continue;
                }

                results[story.Story.id] = BuildShareStoryResult(story.Story, sharedSource);
                continue;
            }

            var media = storyLinks
                .Where(item => item.id1 == story.Story.id && item.atype == GraphAssociationType.Contained)
                .OrderByDescending(item => item.time)
                .Select(item => relatedObjects.TryGetValue(item.id2, out var mediaObject)
                    ? BuildMediaResult(mediaObject)
                    : null)
                .OfType<MediaResult>()
                .ToArray();
            var data = GraphJson.ParseObject(story.Story.data);
            results[story.Story.id] = new NormalStoryResult(
                story.Story.id,
                GraphJson.String(data, "content"),
                GraphJson.String(data, "create"),
                media);
        }

        return results;
    }

    private async Task<IReadOnlyDictionary<long, UserSummaryResult>> GetUserSummariesAsync(
        IReadOnlySet<long> userIds,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<long, UserSummaryResult>();
        }

        var users = await _dbContext.ObjectsTb
            .AsNoTracking()
            .Where(item => userIds.Contains(item.id) && item.otype == GraphObjectType.User)
            .ToListAsync(cancellationToken);
        return users.ToDictionary(item => item.id, BuildUserSummary);
    }

    private async Task<IStorySharedSourceResult> RequireStoryShareSourceAsync(
        long viewerId,
        long sourceId,
        CancellationToken cancellationToken)
    {
        var source = (await GetPostDetailsAsync(viewerId, new[] { sourceId }, cancellationToken))
            .FirstOrDefault();
        return source is FeedPostDetailResult or ReelDetailResult
            ? BuildStorySharedSourceResult(source)
            : throw new ArgumentException(
                "The shared source is unavailable or not visible to the current user.",
                nameof(sourceId));
    }

    private static IStorySharedSourceResult BuildStorySharedSourceResult(IHomePostResult source)
    {
        return source switch
        {
            FeedPostDetailResult feedPost => new FeedPostSharedSourceResult(
                feedPost.Id,
                feedPost.Content,
                feedPost.Media.FirstOrDefault(),
                ToUserSummary(feedPost.Author)),
            ReelDetailResult reel => new ReelSharedSourceResult(
                reel.Id,
                reel.Content,
                reel.Media.FirstOrDefault(),
                ToUserSummary(reel.Author)),
            _ => throw new InvalidOperationException("Unsupported story shared source type.")
        };
    }

    private static long GetHomePostId(IHomePostResult post) => post switch
    {
        FeedPostDetailResult feedPost => feedPost.Id,
        ReelDetailResult reel => reel.Id,
        GroupPostDetailResult groupPost => groupPost.Id,
        _ => 0
    };

    private static UserSummaryResult ToUserSummary(PostAuthorResult author) =>
        new(author.Id, author.Name, author.Avatar, author.IsVerified);

    private static IHomeStoryResult BuildShareStoryResult(
        SocialGraphObjectResult story,
        IStorySharedSourceResult sharedSource)
    {
        var data = GraphJson.ParseObject(story.data);
        return sharedSource switch
        {
            FeedPostSharedSourceResult feedPost => new FeedPostShareStoryResult(
                story.id,
                GraphJson.String(data, "content"),
                GraphJson.String(data, "create"),
                feedPost),
            ReelSharedSourceResult reel => new ReelShareStoryResult(
                story.id,
                GraphJson.String(data, "content"),
                GraphJson.String(data, "create"),
                reel),
            _ => throw new InvalidOperationException("Unsupported story shared source result.")
        };
    }

    private static UserSummaryResult BuildUserSummary(Objects user)
    {
        var data = GraphJson.ParseObject(user.data);
        return new UserSummaryResult(
            user.id,
            GraphJson.String(data, "name"),
            GraphJson.String(data, "avatar"),
            IsVerifyActive(data));
    }

    private static MediaResult? BuildMediaResult(Objects media)
    {
        if (media.otype != GraphObjectType.Media)
        {
            return null;
        }

        var data = GraphJson.ParseObject(media.data);
        return new MediaResult(media.id, GraphJson.Int(data, "type"), GraphJson.String(data, "url"));
    }

    private async Task<UserSummaryResult?> BuildUserSummaryAsync(long userId, CancellationToken cancellationToken)
    {
        var user = await _objectService.RetrieveObjectAsync(userId, cancellationToken);
        if (user is null || user.otype != GraphObjectType.User)
        {
            return null;
        }

        var data = GraphJson.ParseObject(user.data);
        return new UserSummaryResult(
            user.id,
            GraphJson.String(data, "name"),
            GraphJson.String(data, "avatar"),
            IsVerifyActive(data));
    }

    private static bool IsVerifyActive(System.Text.Json.Nodes.JsonObject data)
    {
        var verify = GraphJson.String(data, "verify");
        return DateTimeOffset.TryParse(verify, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiresAt) &&
            expiresAt > DateTimeOffset.UtcNow;
    }

    private async Task<MediaResult?> GetFirstMediaAsync(long contentId, CancellationToken cancellationToken)
    {
        var mediaIds = await _associationService.RetrieveAssociationAsync(contentId, GraphAssociationType.Contained, null, 1, cancellationToken);
        var mediaId = mediaIds.items.FirstOrDefault()?.id2 ?? 0;
        if (mediaId <= 0)
        {
            return null;
        }

        var item = await _objectService.RetrieveObjectAsync(mediaId, cancellationToken);
        if (item is null || item.otype != GraphObjectType.Media)
        {
            return null;
        }

        var data = GraphJson.ParseObject(item.data);
        return new MediaResult(item.id, GraphJson.Int(data, "type"), GraphJson.String(data, "url"));
    }

    private static bool TryGetDateTimeOffset(
        System.Text.Json.Nodes.JsonObject data,
        string field,
        out DateTimeOffset value)
    {
        return DateTimeOffset.TryParse(
            GraphJson.String(data, field),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out value);
    }

    private static string EncodeStoryCursor(DateTimeOffset latestCreate, long authorId)
    {
        var payload = JsonSerializer.Serialize(new StoryCursor(latestCreate, authorId));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
    }

    private static bool TryDecodeStoryCursor(string? cursor, out StoryCursor storyCursor)
    {
        storyCursor = default;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return JsonSerializer.Deserialize<StoryCursor>(json) is { } decoded && SetCursor(decoded, out storyCursor);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool SetCursor(StoryCursor source, out StoryCursor target)
    {
        target = source;
        return source.AuthorId > 0;
    }

    private sealed record StoryRow(long AuthorId, long StoryId, string Data);

    private sealed record ActiveStoryRow(long AuthorId, SocialGraphObjectResult Story, DateTimeOffset CreatedAt);

    private sealed record StoryBucketCandidate(long AuthorId, DateTimeOffset LatestCreate);

    private readonly record struct StoryCursor(DateTimeOffset LatestCreate, long AuthorId);
}
