namespace SocialGraph.Api.Service;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;

public sealed class ContentGraphService : IContentGraphService
{
    public const int MaxPostDetailIds = 100;

    private readonly MyDbContext _dbContext;
    private readonly IObjectService _objectService;
    private readonly IAssociationService _associationService;
    private readonly IExternalServiceClient _externalServiceClient;

    public ContentGraphService(
        MyDbContext dbContext,
        IObjectService objectService,
        IAssociationService associationService,
        IExternalServiceClient externalServiceClient)
    {
        _dbContext = dbContext;
        _objectService = objectService;
        _associationService = associationService;
        _externalServiceClient = externalServiceClient;
    }

    public async Task<ContentResult> CreateFeedPostAsync(CreateFeedPostInput input, CancellationToken cancellationToken = default)
    {
        if (input.Privacy is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Feed privacy must be between 0 and 3.");
        }

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        try
        {
            var post = await _objectService.AddObjectAsync(GraphObjectType.FeedPost, GraphJson.PostJson(input.Content, input.Privacy), cancellationToken);
            var media = await AttachMediaAsync(post.id, input.Media, cancellationToken);
            await _associationService.AddAssociationAsync(input.AuthorId, GraphAssociationType.Authored, post.id, cancellationToken);
            foreach (var userId in NormalizeUserIds(input.TaggedUserIds))
            {
                if (!await TagAsync(post.id, userId, cancellationToken))
                {
                    throw new InvalidOperationException($"Unable to tag user {userId}.");
                }
            }

            foreach (var userId in MentionUserIds(input.Content))
            {
                if (!await MentionAsync(post.id, userId, cancellationToken))
                {
                    throw new InvalidOperationException($"Unable to mention user {userId}.");
                }
            }

            await _externalServiceClient.CreateSearchIndexAsync(post.id, "feedPost", input.Content, cancellationToken);
            await _externalServiceClient.CreatePostEmbeddingAsync(post.id, input.Content, media.Select(item => item.Url).ToArray(), cancellationToken);
            await _externalServiceClient.FinalizeMediaAsync(media.Select(item => item.Url).ToArray(), cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return await BuildContentResultAsync(post, input.AuthorId, media, cancellationToken);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            throw;
        }
    }

    public async Task<ContentResult> CreateGroupPostAsync(CreateGroupPostInput input, CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        try
        {
            var post = await _objectService.AddObjectAsync(GraphObjectType.GroupPost, GraphJson.GroupPostJson(input.Content), cancellationToken);
            var media = await AttachMediaAsync(post.id, input.Media, cancellationToken);
            await _associationService.AddAssociationAsync(input.AuthorId, GraphAssociationType.Authored, post.id, cancellationToken);
            await _associationService.AddAssociationAsync(input.GroupId, GraphAssociationType.Published, post.id, cancellationToken);
            foreach (var userId in MentionUserIds(input.Content))
            {
                if (!await MentionAsync(post.id, userId, cancellationToken))
                {
                    throw new InvalidOperationException($"Unable to mention user {userId}.");
                }
            }

            await _externalServiceClient.CreateSearchIndexAsync(post.id, "groupPost", input.Content, cancellationToken);
            await _externalServiceClient.CreatePostEmbeddingAsync(post.id, input.Content, media.Select(item => item.Url).ToArray(), cancellationToken);
            await _externalServiceClient.FinalizeMediaAsync(media.Select(item => item.Url).ToArray(), cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return await BuildContentResultAsync(post, input.AuthorId, media, cancellationToken);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            throw;
        }
    }

    public async Task<ContentResult?> UpdatePostAsync(UpdatePostInput input, CancellationToken cancellationToken = default)
    {
        var current = await _objectService.RetrieveObjectAsync(input.Id, cancellationToken);
        if (current?.otype is not (GraphObjectType.FeedPost or GraphObjectType.GroupPost or GraphObjectType.Reel))
        {
            return null;
        }

        if (current.otype is GraphObjectType.FeedPost or GraphObjectType.Reel && input.Privacy is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Feed post and reel privacy must be between 0 and 3.");
        }

        var currentData = GraphJson.ParseObject(current.data);
        var authorId = await GetAuthorIdAsync(input.Id, cancellationToken);
        var post = await _objectService.UpdateObjectAsync(
            input.Id,
            current.otype,
            GraphJson.PatchJson(
                ("content", input.Content),
                ("privacy", current.otype is GraphObjectType.FeedPost or GraphObjectType.Reel ? input.Privacy : null)),
            cancellationToken);
        if (post is null)
        {
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
            await _externalServiceClient.FinalizeMediaAsync(media.Select(item => item.Url).ToArray(), cancellationToken);
            await DeleteOrphanMediaAsync(existingMediaIds, cancellationToken);
        }

        var content = input.Content ?? GraphJson.String(currentData, "content");
        if (input.Content is not null)
        {
            await SyncMentionAssociationsAsync(input.Id, authorId, content, cancellationToken);
            await _externalServiceClient.UpdateSearchIndexAsync(
                input.Id,
                current.otype switch
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

        return await BuildContentResultAsync(post, authorId, media, cancellationToken);
    }

    public async Task<bool> DeleteContentAsync(long contentId, CancellationToken cancellationToken = default)
    {
        var item = await _objectService.RetrieveObjectAsync(contentId, cancellationToken);
        if (item is null)
        {
            return false;
        }

        var mediaIds = await GetContainedMediaIdsAsync(contentId, cancellationToken);
        await _associationService.DeleteObjectAssociationsAsync(contentId, cancellationToken);
        var deleted = await _objectService.DeleteObjectAsync(contentId, cancellationToken);
        if (deleted)
        {
            await DeleteOrphanMediaAsync(mediaIds, cancellationToken, contentId);
        }
        if (deleted && (item.otype == GraphObjectType.FeedPost ||
                        item.otype == GraphObjectType.GroupPost ||
                        item.otype == GraphObjectType.Reel))
        {
            await _externalServiceClient.DeletePostEmbeddingAsync(contentId, cancellationToken);
            await _externalServiceClient.DeleteSearchIndexAsync(contentId, cancellationToken);
        }

        return deleted;
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

    public async Task<IHomePostResult?> GetPostDetailAsync(
        long viewerId,
        long postId,
        CancellationToken cancellationToken = default)
    {
        return (await GetPostDetailsAsync(viewerId, new[] { postId }, cancellationToken)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<IHomePostResult>> GetPostDetailsAsync(
        long viewerId,
        IReadOnlyList<long> postIds,
        CancellationToken cancellationToken = default)
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
                     item.atype == GraphAssociationType.Mentioned))
                .ToListAsync(cancellationToken);
        var sourceAuthorBySource = sourceLinks
            .Where(item => item.atype == GraphAssociationType.AuthoredBy)
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

        var relationTargetIds = authorByPost.Values
            .Concat(groupByPost.Values)
            .Concat(sourceAuthorBySource.Values)
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
                     item.atype == GraphAssociationType.Admin))
                .ToListAsync(cancellationToken);
        var friends = RelationTargets(viewerLinks, GraphAssociationType.Friend);
        var followed = RelationTargets(viewerLinks, GraphAssociationType.Followed);
        var blocked = RelationTargets(viewerLinks, GraphAssociationType.Blocked, GraphAssociationType.BlockedBy);
        var participatingGroups = RelationTargets(viewerLinks, GraphAssociationType.Member, GraphAssociationType.Admin);
        var results = new List<IHomePostResult>(orderedPostIds.Length);

        foreach (var postId in orderedPostIds)
        {
            if (!posts.TryGetValue(postId, out var post) ||
                !authorByPost.TryGetValue(postId, out var authorId) ||
                !relatedObjects.TryGetValue(authorId, out var author) ||
                author.otype != GraphObjectType.User ||
                viewerId != authorId && blocked.Contains(authorId))
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
                ? viewerId == authorId || privacy switch
                {
                    0 => true,
                    1 => friends.Contains(authorId) || followed.Contains(authorId),
                    2 => friends.Contains(authorId),
                    3 => false,
                    _ => false
                }
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
            var mentions = BuildMentionUsers(content, relatedObjects);

            if (post.otype == GraphObjectType.GroupPost && group is not null)
            {
                var groupData = GraphJson.ParseObject(group.data);
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
                        !participatingGroups.Contains(group.id)),
                    media,
                    mentions));
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
                    postAuthor,
                    media,
                    mentions));
                continue;
            }

            SharedPostSourceResult? sharedSource = null;
            if (sourceByPost.TryGetValue(post.id, out var sourceId))
            {
                if (!relatedObjects.TryGetValue(sourceId, out var source) ||
                    source.otype is not (GraphObjectType.FeedPost or GraphObjectType.Reel))
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
                else
                {
                    var sourceData = GraphJson.ParseObject(source.data);
                    var sourceIsPublic = source.otype == GraphObjectType.Reel ||
                        GraphJson.Int(sourceData, "privacy") == 0;
                    var sourceAuthorId = sourceAuthorBySource.GetValueOrDefault(sourceId);
                    relatedObjects.TryGetValue(sourceAuthorId, out var sourceAuthor);
                    var hasSourceAuthor = sourceAuthorId > 0 &&
                        sourceAuthor is not null &&
                        sourceAuthor.otype == GraphObjectType.User;
                    var sourceAvailable = sourceIsPublic &&
                        hasSourceAuthor &&
                        (viewerId == sourceAuthorId || !blocked.Contains(sourceAuthorId));

                    if (!sourceAvailable)
                    {
                        sharedSource = new SharedPostSourceResult(
                            sourceId,
                            false,
                            source.otype,
                            null,
                            null,
                            Array.Empty<MediaResult>(),
                            Array.Empty<MentionUserResult>());
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
                            BuildMentionUsers(sourceContent, relatedObjects),
                            GraphJson.Int(sourceData, "privacy"),
                            GraphJson.String(sourceData, "create"));
                    }
                }
            }

            var taggedUsers = postLinks
                .Where(item => item.id1 == postId && item.atype == GraphAssociationType.Tagged)
                .OrderBy(item => item.time)
                .ThenBy(item => item.id2)
                .Select(item => relatedObjects.TryGetValue(item.id2, out var taggedUser) && taggedUser.otype == GraphObjectType.User
                    ? BuildUserSummary(taggedUser)
                    : null)
                .OfType<UserSummaryResult>()
                .ToArray();
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

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        SocialGraphObjectResult? comment = null;
        IReadOnlyList<MediaResult> media = Array.Empty<MediaResult>();
        try
        {
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

            await _externalServiceClient.FinalizeMediaAsync(media.Select(item => item.Url).ToArray(), cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            else if (comment is not null)
            {
                await _associationService.DeleteObjectAssociationsAsync(comment.id, CancellationToken.None);
                await _objectService.DeleteObjectAsync(comment.id, CancellationToken.None);
                await DeleteOrphanMediaAsync(media.Select(item => item.Id).ToArray(), CancellationToken.None, comment.id);
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
        var story = await _objectService.AddObjectAsync(GraphObjectType.Story, GraphJson.StoryJson(input.Content), cancellationToken);
        var media = await AttachSingleMediaAsync(story.id, input.Media, cancellationToken);
        await _associationService.AddAssociationAsync(input.AuthorId, GraphAssociationType.Authored, story.id, cancellationToken);
        await _externalServiceClient.FinalizeMediaAsync(media.Select(item => item.Url).ToArray(), cancellationToken);

        var data = GraphJson.ParseObject(story.data);
        return new NormalStoryResult(
            story.id,
            GraphJson.String(data, "content"),
            GraphJson.String(data, "create"),
            media);
    }

    public async Task<IHomeStoryResult> CreateShareStoryAsync(
        CreateShareStoryInput input,
        CancellationToken cancellationToken = default)
    {
        var sharedSourceId = await ResolveCanonicalShareSourceIdAsync(input.SharedSourceId, cancellationToken);
        var sharedSource = await RequireStoryShareSourceAsync(sharedSourceId, cancellationToken);

        var story = await _objectService.AddObjectAsync(GraphObjectType.Story, GraphJson.StoryJson(input.Content), cancellationToken);
        await _associationService.AddAssociationAsync(input.AuthorId, GraphAssociationType.Authored, story.id, cancellationToken);
        await _associationService.AddAssociationAsync(story.id, GraphAssociationType.Share, sharedSourceId, cancellationToken);
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

        return BuildShareStoryResult(story, sharedSource);
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
        var storyItems = await BuildHomeStoryItemsAsync(activeStories, cancellationToken);
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

        var storyItems = await BuildHomeStoryItemsAsync(activeStories, cancellationToken);
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
                break;
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

        var reel = await _objectService.AddObjectAsync(GraphObjectType.Reel, GraphJson.PostJson(input.Content, input.Privacy), cancellationToken);
        var media = await AttachSingleMediaAsync(reel.id, input.Media, cancellationToken);
        await _associationService.AddAssociationAsync(input.AuthorId, GraphAssociationType.Authored, reel.id, cancellationToken);
        await _externalServiceClient.FinalizeMediaAsync(media.Select(item => item.Url).ToArray(), cancellationToken);
        await _externalServiceClient.CreateSearchIndexAsync(reel.id, "reel", input.Content, cancellationToken);
        await _externalServiceClient.CreatePostEmbeddingAsync(reel.id, input.Content, media.Select(item => item.Url).ToArray(), cancellationToken);
        return await BuildContentResultAsync(reel, input.AuthorId, media, cancellationToken);
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
            if (objectType != GraphObjectType.FeedPost)
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
        var post = await CreateFeedPostAsync(new CreateFeedPostInput(input.AuthorId, input.Content, input.Privacy, Array.Empty<MediaInput>()), cancellationToken);
        await _associationService.AddAssociationAsync(post.Id, GraphAssociationType.Share, sourceId, cancellationToken);
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
                new { shareId = post.Id, shareType = "feedPost" },
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
        var result = await _associationService.AddAssociationAsync(postId, GraphAssociationType.Tagged, userId, cancellationToken);
        var authorId = await GetAuthorIdAsync(postId, cancellationToken);
        if (result && authorId > 0 && authorId != userId)
        {
            await _externalServiceClient.NotifyAsync(authorId, userId, ExternalNotificationAction.Tag, postId, null, cancellationToken);
        }

        return result;
    }

    public async Task<bool> MentionAsync(long sourceId, long userId, CancellationToken cancellationToken = default)
    {
        var result = await _associationService.AddAssociationAsync(sourceId, GraphAssociationType.Mentioned, userId, cancellationToken);
        var authorId = await GetAuthorIdAsync(sourceId, cancellationToken);
        if (result && authorId > 0 && authorId != userId)
        {
            await _externalServiceClient.NotifyAsync(authorId, userId, ExternalNotificationAction.Mention, sourceId, null, cancellationToken);
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
        var privacy = await GetContentPrivacyAsync(item, data, cancellationToken);
        return new ContentResult(
            item.id,
            item.otype,
            GraphJson.String(data, "content"),
            privacy,
            GraphJson.String(data, "create"),
            authorId,
            media);
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

    private static HashSet<long> RelationTargets(
        IReadOnlyList<Associations> associations,
        params short[] associationTypes)
    {
        return associations
            .Where(item => associationTypes.Contains(item.atype))
            .Select(item => item.id2)
            .ToHashSet();
    }

    private async Task<IReadOnlySet<long>> GetVisibleStoryAuthorIdsAsync(long userId, CancellationToken cancellationToken)
    {
        var friends = await GetAssociationIdsAsync(userId, GraphAssociationType.Friend, 500, cancellationToken);
        var followed = await GetAssociationIdsAsync(userId, GraphAssociationType.Followed, 500, cancellationToken);

        return friends
            .Concat(followed)
            .Where(id => id != userId)
            .ToHashSet();
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
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

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
            var otherContainedMediaIds = await _dbContext.AssociationsTb
                .AsNoTracking()
                .Where(item => item.atype == GraphAssociationType.Contained && item.id2 != mediaId)
                .Select(item => item.id2)
                .Distinct()
                .ToArrayAsync(cancellationToken);
            var sameAssetStillContained = !string.IsNullOrWhiteSpace(mediaUrl) &&
                otherContainedMediaIds.Length > 0 &&
                (await _dbContext.ObjectsTb
                    .AsNoTracking()
                    .Where(item => otherContainedMediaIds.Contains(item.id) && item.otype == GraphObjectType.Media)
                    .Select(item => item.data)
                    .ToListAsync(cancellationToken))
                .Any(data => string.Equals(
                    GraphJson.String(GraphJson.ParseObject(data), "url"),
                    mediaUrl,
                    StringComparison.OrdinalIgnoreCase));
            await _associationService.DeleteObjectAssociationsAsync(mediaId, cancellationToken);
            if (await _objectService.DeleteObjectAsync(mediaId, cancellationToken) &&
                !string.IsNullOrWhiteSpace(mediaUrl) &&
                !sameAssetStillContained)
            {
                await _externalServiceClient.DeleteMediaAsync(new[] { mediaUrl }, cancellationToken);
            }
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
        IReadOnlyDictionary<long, Objects> objects) =>
        MentionTokenCodec.ExtractUserIds(content)
            .Select(userId =>
            {
                if (!objects.TryGetValue(userId, out var user) || user.otype != GraphObjectType.User)
                {
                    return new MentionUserResult(userId, string.Empty, false);
                }

                var data = GraphJson.ParseObject(user.data);
                return new MentionUserResult(userId, GraphJson.String(data, "name"), true);
            })
            .ToArray();

    private async Task SyncMentionAssociationsAsync(
        long sourceId,
        long authorId,
        string content,
        CancellationToken cancellationToken)
    {
        var desired = MentionUserIds(content).ToHashSet();
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

    private async Task<IReadOnlyList<long>> GetContainedMediaIdsAsync(long contentId, CancellationToken cancellationToken)
    {
        var media = await _associationService.RetrieveAssociationAsync(contentId, GraphAssociationType.Contained, null, 100, cancellationToken);
        return media?.items.Select(item => item.id2).ToArray() ?? Array.Empty<long>();
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
        var sourceObjects = sourceIds.Length == 0
            ? new Dictionary<long, Objects>()
            : await _dbContext.ObjectsTb
                .AsNoTracking()
                .Where(item => sourceIds.Contains(item.id))
                .ToDictionaryAsync(item => item.id, cancellationToken);
        var visibleSourceIds = sourceObjects.Values
            .Where(IsStoryShareSourceVisible)
            .Select(item => item.id)
            .ToArray();
        var sourceLinks = visibleSourceIds.Length == 0
            ? new List<Associations>()
            : await _dbContext.AssociationsTb
                .AsNoTracking()
                .Where(item => visibleSourceIds.Contains(item.id1) &&
                    (item.atype == GraphAssociationType.AuthoredBy || item.atype == GraphAssociationType.Contained))
                .ToListAsync(cancellationToken);

        var relatedIds = storyLinks
            .Where(item => item.atype == GraphAssociationType.Contained)
            .Select(item => item.id2)
            .Concat(sourceLinks.Select(item => item.id2))
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
                if (!sourceObjects.TryGetValue(sourceId, out var source) || !IsStoryShareSourceVisible(source))
                {
                    continue;
                }

                var sharedSource = BuildSharedSourceResult(source, sourceLinks, relatedObjects);
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
        long sourceId,
        CancellationToken cancellationToken)
    {
        var source = await _objectService.RetrieveObjectAsync(sourceId, cancellationToken)
            ?? throw new ArgumentException("Shared source does not exist.", nameof(sourceId));
        if (!IsStoryShareSourceVisible(source.otype, source.data))
        {
            throw source.otype == GraphObjectType.FeedPost
                ? new ArgumentException("Only public feed posts can be shared to story.", nameof(sourceId))
                : new ArgumentException("Stories can only share public feed posts or reels.", nameof(sourceId));
        }

        return await BuildSharedSourceResultAsync(source, cancellationToken);
    }

    private async Task<IStorySharedSourceResult> BuildSharedSourceResultAsync(
        SocialGraphObjectResult source,
        CancellationToken cancellationToken)
    {
        var data = GraphJson.ParseObject(source.data);
        var content = GraphJson.String(data, "content");
        var authorId = await GetAuthorIdAsync(source.id, cancellationToken);
        var author = authorId > 0 ? await BuildUserSummaryAsync(authorId, cancellationToken) : null;
        var media = await GetFirstMediaAsync(source.id, cancellationToken);

        return source.otype switch
        {
            GraphObjectType.FeedPost => new FeedPostSharedSourceResult(source.id, content, media, author),
            GraphObjectType.Reel => new ReelSharedSourceResult(source.id, content, media, author),
            _ => throw new InvalidOperationException("Unsupported story shared source type.")
        };
    }

    private static IStorySharedSourceResult BuildSharedSourceResult(
        Objects source,
        IReadOnlyList<Associations> sourceLinks,
        IReadOnlyDictionary<long, Objects> relatedObjects)
    {
        var data = GraphJson.ParseObject(source.data);
        var authorId = sourceLinks
            .Where(item => item.id1 == source.id && item.atype == GraphAssociationType.AuthoredBy)
            .OrderByDescending(item => item.time)
            .Select(item => item.id2)
            .FirstOrDefault();
        var author = authorId > 0 && relatedObjects.TryGetValue(authorId, out var authorObject) &&
                     authorObject.otype == GraphObjectType.User
            ? BuildUserSummary(authorObject)
            : null;
        var mediaId = sourceLinks
            .Where(item => item.id1 == source.id && item.atype == GraphAssociationType.Contained)
            .OrderByDescending(item => item.time)
            .Select(item => item.id2)
            .FirstOrDefault();
        var media = mediaId > 0 && relatedObjects.TryGetValue(mediaId, out var mediaObject)
            ? BuildMediaResult(mediaObject)
            : null;
        var content = GraphJson.String(data, "content");

        return source.otype switch
        {
            GraphObjectType.FeedPost => new FeedPostSharedSourceResult(source.id, content, media, author),
            GraphObjectType.Reel => new ReelSharedSourceResult(source.id, content, media, author),
            _ => throw new InvalidOperationException("Unsupported story shared source type.")
        };
    }

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

    private static bool IsStoryShareSourceVisible(Objects source) =>
        IsStoryShareSourceVisible(source.otype, source.data);

    private static bool IsStoryShareSourceVisible(short objectType, string rawData)
    {
        if (objectType == GraphObjectType.Reel)
        {
            return true;
        }

        return objectType == GraphObjectType.FeedPost &&
               GraphJson.Int(GraphJson.ParseObject(rawData), "privacy") == 0;
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
