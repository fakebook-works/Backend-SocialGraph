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
        var post = await _objectService.AddObjectAsync(GraphObjectType.FeedPost, GraphJson.PostJson(input.Content, input.Privacy), cancellationToken);
        var media = await AttachMediaAsync(input.AuthorId, post.id, input.Media, cancellationToken);
        await _associationService.AddAssociationAsync(input.AuthorId, GraphAssociationType.Authored, post.id, cancellationToken);
        await _externalServiceClient.CreateSearchIndexAsync(post.id, "post", input.Content, cancellationToken);
        await _externalServiceClient.CreatePostEmbeddingAsync(post.id, input.Content, media.Select(item => item.Url).ToArray(), cancellationToken);
        return await BuildContentResultAsync(post, input.AuthorId, media, cancellationToken);
    }

    public async Task<ContentResult> CreateGroupPostAsync(CreateGroupPostInput input, CancellationToken cancellationToken = default)
    {
        var post = await _objectService.AddObjectAsync(GraphObjectType.GroupPost, GraphJson.GroupPostJson(input.Content), cancellationToken);
        var media = await AttachMediaAsync(input.AuthorId, post.id, input.Media, cancellationToken);
        await _associationService.AddAssociationAsync(input.AuthorId, GraphAssociationType.Authored, post.id, cancellationToken);
        await _associationService.AddAssociationAsync(input.GroupId, GraphAssociationType.Published, post.id, cancellationToken);
        await _externalServiceClient.CreateSearchIndexAsync(post.id, "post", input.Content, cancellationToken);
        await _externalServiceClient.CreatePostEmbeddingAsync(post.id, input.Content, media.Select(item => item.Url).ToArray(), cancellationToken);
        return await BuildContentResultAsync(post, input.AuthorId, media, cancellationToken);
    }

    public async Task<ContentResult?> UpdatePostAsync(UpdatePostInput input, CancellationToken cancellationToken = default)
    {
        var post = await _objectService.UpdateObjectAsync(
            input.Id,
            GraphObjectType.FeedPost,
            GraphJson.PatchJson(("privacy", input.Privacy)),
            cancellationToken);

        return post is null ? null : await GetContentAsync(input.Id, cancellationToken);
    }

    public async Task<bool> DeleteContentAsync(long contentId, CancellationToken cancellationToken = default)
    {
        var item = await _objectService.RetrieveObjectAsync(contentId, cancellationToken);
        if (item is null)
        {
            return false;
        }

        await _associationService.DeleteObjectAssociationsAsync(contentId, cancellationToken);
        var deleted = await _objectService.DeleteObjectAsync(contentId, cancellationToken);
        if (deleted && (item.otype == GraphObjectType.FeedPost || item.otype == GraphObjectType.GroupPost))
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

    public async Task<PostDetailResult?> GetPostDetailAsync(
        long userId,
        long postId,
        CancellationToken cancellationToken = default)
    {
        var item = await _objectService.RetrieveObjectAsync(postId, cancellationToken);
        if (item is null || item.otype is not (GraphObjectType.FeedPost or GraphObjectType.GroupPost))
        {
            return null;
        }

        var data = GraphJson.ParseObject(item.data);
        var authorId = await GetAuthorIdAsync(postId, cancellationToken);
        var authorObject = await _dbContext.ObjectsTb
            .AsNoTracking()
            .FirstOrDefaultAsync(obj => obj.id == authorId && obj.otype == GraphObjectType.User, cancellationToken);
        if (authorObject is null)
        {
            return null;
        }

        GroupSummaryResult? group = null;
        long groupId = 0;
        int privacy;
        if (item.otype == GraphObjectType.GroupPost)
        {
            var groupObject = await GetPublishedGroupObjectAsync(postId, cancellationToken);
            if (groupObject is null)
            {
                return null;
            }

            groupId = groupObject.id;
            var groupData = GraphJson.ParseObject(groupObject.data);
            privacy = GraphJson.Int(groupData, "privacy");
            group = new GroupSummaryResult(
                groupObject.id,
                GraphJson.String(groupData, "name"),
                GraphJson.String(groupData, "avatar"));
        }
        else
        {
            privacy = GraphJson.Int(data, "privacy");
        }

        if (!await CanViewPostAsync(userId, authorId, item.otype, privacy, groupId, cancellationToken))
        {
            return null;
        }

        return new PostDetailResult(
            item.id,
            item.otype,
            GraphJson.String(data, "content"),
            privacy,
            GraphJson.String(data, "create"),
            BuildUserSummary(authorObject),
            group,
            await BuildPostViewerRelationAsync(userId, authorObject, groupId, cancellationToken),
            await GetMediaAsync(postId, cancellationToken));
    }

    public async Task<IReadOnlyList<PostDetailResult>> GetPostDetailsAsync(
        long userId,
        IReadOnlyList<long> postIds,
        CancellationToken cancellationToken = default)
    {
        var results = new List<PostDetailResult>(postIds.Count);
        var seen = new HashSet<long>();
        foreach (var postId in postIds)
        {
            if (!seen.Add(postId))
            {
                continue;
            }

            var detail = await GetPostDetailAsync(userId, postId, cancellationToken);
            if (detail is not null)
            {
                results.Add(detail);
            }
        }

        return results;
    }

    public async Task<ContentResult> CreateCommentAsync(CreateCommentInput input, CancellationToken cancellationToken = default)
    {
        var comment = await _objectService.AddObjectAsync(GraphObjectType.Comment, GraphJson.ContentJson(input.Content), cancellationToken);
        await _associationService.AddAssociationAsync(input.AuthorId, GraphAssociationType.Authored, comment.id, cancellationToken);
        await _associationService.AddAssociationAsync(input.TargetId, GraphAssociationType.Comment, comment.id, cancellationToken);

        var targetAuthorId = await GetAuthorIdAsync(input.TargetId, cancellationToken);
        if (targetAuthorId > 0 && targetAuthorId != input.AuthorId)
        {
            await _externalServiceClient.NotifyAsync(input.AuthorId, targetAuthorId, ExternalNotificationAction.Comment, input.TargetId, null, cancellationToken);
        }

        return await BuildContentResultAsync(comment, input.AuthorId, Array.Empty<MediaResult>(), cancellationToken);
    }

    public async Task<NormalStoryResult> CreateNormalStoryAsync(
        CreateNormalStoryInput input,
        CancellationToken cancellationToken = default)
    {
        var story = await _objectService.AddObjectAsync(GraphObjectType.Story, GraphJson.StoryJson(input.Content), cancellationToken);
        var media = await AttachTemporarySingleMediaAsync(story.id, input.Media, cancellationToken);
        await _associationService.AddAssociationAsync(input.AuthorId, GraphAssociationType.Authored, story.id, cancellationToken);

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
        var sharedSource = await RequireStoryShareSourceAsync(input.SharedSourceId, cancellationToken);

        var story = await _objectService.AddObjectAsync(GraphObjectType.Story, GraphJson.StoryJson(input.Content), cancellationToken);
        await _associationService.AddAssociationAsync(input.AuthorId, GraphAssociationType.Authored, story.id, cancellationToken);
        await _associationService.AddAssociationAsync(story.id, GraphAssociationType.Share, input.SharedSourceId, cancellationToken);

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

            resultItems.Add(new HomeStoryBucketResult(
                author,
                visibleStories[^1].CreatedAt.ToString("O", CultureInfo.InvariantCulture),
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
        var reel = await _objectService.AddObjectAsync(GraphObjectType.Reel, GraphJson.ContentJson(input.Content), cancellationToken);
        var media = await AttachSingleMediaAsync(input.AuthorId, reel.id, input.Media, cancellationToken);
        await _associationService.AddAssociationAsync(input.AuthorId, GraphAssociationType.Authored, reel.id, cancellationToken);
        return await BuildContentResultAsync(reel, input.AuthorId, media, cancellationToken);
    }

    public async Task<ContentResult> SharePostAsync(SharePostInput input, CancellationToken cancellationToken = default)
    {
        var post = await CreateFeedPostAsync(new CreateFeedPostInput(input.AuthorId, input.Content, input.Privacy, Array.Empty<MediaInput>()), cancellationToken);
        await _associationService.AddAssociationAsync(post.Id, GraphAssociationType.Share, input.SourceId, cancellationToken);
        return post;
    }

    public async Task<bool> LikeAsync(long userId, long targetId, CancellationToken cancellationToken = default)
    {
        var result = await _associationService.AddAssociationAsync(userId, GraphAssociationType.Liked, targetId, cancellationToken);
        var targetAuthorId = await GetAuthorIdAsync(targetId, cancellationToken);
        if (targetAuthorId > 0 && targetAuthorId != userId)
        {
            await _externalServiceClient.NotifyAsync(userId, targetAuthorId, ExternalNotificationAction.Like, targetId, null, cancellationToken);
        }

        return result;
    }

    public Task<bool> UnlikeAsync(long userId, long targetId, CancellationToken cancellationToken = default)
    {
        return _associationService.DeleteOneAssociationAsync(userId, GraphAssociationType.Liked, targetId, cancellationToken);
    }

    public Task<bool> SaveAsync(long userId, long targetId, CancellationToken cancellationToken = default)
    {
        return _associationService.AddAssociationAsync(userId, GraphAssociationType.Saved, targetId, cancellationToken);
    }

    public Task<bool> UnsaveAsync(long userId, long targetId, CancellationToken cancellationToken = default)
    {
        return _associationService.DeleteOneAssociationAsync(userId, GraphAssociationType.Saved, targetId, cancellationToken);
    }

    public Task<bool> WatchAsync(long userId, long targetId, CancellationToken cancellationToken = default)
    {
        return _associationService.AddAssociationAsync(userId, GraphAssociationType.Watched, targetId, cancellationToken);
    }

    public async Task<bool> TagAsync(long postId, long userId, CancellationToken cancellationToken = default)
    {
        var result = await _associationService.AddAssociationAsync(postId, GraphAssociationType.Tagged, userId, cancellationToken);
        var authorId = await GetAuthorIdAsync(postId, cancellationToken);
        await _externalServiceClient.NotifyAsync(authorId, userId, ExternalNotificationAction.Tag, postId, null, cancellationToken);
        return result;
    }

    public async Task<bool> MentionAsync(long sourceId, long userId, CancellationToken cancellationToken = default)
    {
        var result = await _associationService.AddAssociationAsync(sourceId, GraphAssociationType.Mentioned, userId, cancellationToken);
        var authorId = await GetAuthorIdAsync(sourceId, cancellationToken);
        await _externalServiceClient.NotifyAsync(authorId, userId, ExternalNotificationAction.Mention, sourceId, null, cancellationToken);
        return result;
    }

    private Task<IReadOnlyList<MediaResult>> AttachSingleMediaAsync(
        long ownerId,
        long contentId,
        MediaInput? media,
        CancellationToken cancellationToken)
    {
        return AttachMediaAsync(ownerId, contentId, media is null ? null : new[] { media }, cancellationToken);
    }

    private Task<IReadOnlyList<MediaResult>> AttachTemporarySingleMediaAsync(
        long contentId,
        MediaInput? media,
        CancellationToken cancellationToken)
    {
        return AttachMediaAsync(0, contentId, media is null ? null : new[] { media }, cancellationToken, createOwned: false);
    }

    private async Task<IReadOnlyList<MediaResult>> AttachMediaAsync(
        long ownerId,
        long contentId,
        IReadOnlyList<MediaInput>? media,
        CancellationToken cancellationToken,
        bool createOwned = true)
    {
        if (media is null || media.Count == 0)
        {
            return Array.Empty<MediaResult>();
        }

        var results = new List<MediaResult>(media.Count);
        foreach (var input in media)
        {
            var mediaObject = await _objectService.AddObjectAsync(GraphObjectType.Media, GraphJson.MediaJson(input.Type, input.Url), cancellationToken);
            if (createOwned)
            {
                await _associationService.AddAssociationAsync(ownerId, GraphAssociationType.Owned, mediaObject.id, cancellationToken);
            }

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
        if (item.otype == GraphObjectType.FeedPost)
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

    private async Task<Objects?> GetPublishedGroupObjectAsync(long postId, CancellationToken cancellationToken)
    {
        var groupId = await GetPublishedGroupIdAsync(postId, cancellationToken);
        if (groupId <= 0)
        {
            return null;
        }

        return await _dbContext.ObjectsTb
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.id == groupId && item.otype == GraphObjectType.Group, cancellationToken);
    }

    private async Task<bool> CanViewPostAsync(
        long userId,
        long authorId,
        short postType,
        int privacy,
        long groupId,
        CancellationToken cancellationToken)
    {
        if (userId == authorId || privacy == 0)
        {
            return true;
        }

        if (postType == GraphObjectType.FeedPost)
        {
            return await AssociationExistsAsync(userId, GraphAssociationType.Friend, authorId, cancellationToken);
        }

        return postType == GraphObjectType.GroupPost &&
            groupId > 0 &&
            await IsGroupParticipantAsync(userId, groupId, cancellationToken);
    }

    private async Task<PostViewerRelationResult> BuildPostViewerRelationAsync(
        long userId,
        Objects author,
        long groupId,
        CancellationToken cancellationToken)
    {
        var canFollow = false;
        if (userId != author.id)
        {
            var authorData = GraphJson.ParseObject(author.data);
            if (GraphJson.Int(authorData, "privacy") == 1)
            {
                var alreadyFriend = await AssociationExistsAsync(userId, GraphAssociationType.Friend, author.id, cancellationToken);
                var alreadyFollowed = await AssociationExistsAsync(userId, GraphAssociationType.Followed, author.id, cancellationToken);
                canFollow = !alreadyFriend && !alreadyFollowed;
            }
        }

        bool? canJoin = null;
        if (groupId > 0)
        {
            canJoin = !await IsGroupParticipantAsync(userId, groupId, cancellationToken);
        }

        return new PostViewerRelationResult(
            canFollow,
            canJoin);
    }

    private Task<bool> IsGroupParticipantAsync(long userId, long groupId, CancellationToken cancellationToken)
    {
        return _dbContext.AssociationsTb
            .AsNoTracking()
            .AnyAsync(
                item => item.id1 == userId &&
                    item.id2 == groupId &&
                    (item.atype == GraphAssociationType.Member || item.atype == GraphAssociationType.Admin),
                cancellationToken);
    }

    private Task<bool> AssociationExistsAsync(
        long id1,
        short atype,
        long id2,
        CancellationToken cancellationToken)
    {
        return _dbContext.AssociationsTb
            .AsNoTracking()
            .AnyAsync(item => item.id1 == id1 && item.atype == atype && item.id2 == id2, cancellationToken);
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
        var protectedMediaIds = mediaIds.Count == 0
            ? new HashSet<long>()
            : (await _dbContext.AssociationsTb
                .AsNoTracking()
                .Where(item => mediaIds.Contains(item.id2) &&
                    (item.atype == GraphAssociationType.Owned ||
                     item.atype == GraphAssociationType.Contained && item.id1 != storyId))
                .Select(item => item.id2)
                .Distinct()
                .ToListAsync(cancellationToken))
                .ToHashSet();
        var temporaryMediaIds = mediaIds.Where(id => !protectedMediaIds.Contains(id)).ToArray();

        IDbContextTransaction? transaction = null;
        if (_dbContext.Database.IsRelational())
        {
            transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        }

        try
        {
            await _associationService.DeleteObjectAssociationsAsync(storyId, cancellationToken);
            var deleted = await _objectService.DeleteObjectAsync(storyId, cancellationToken);

            foreach (var mediaId in temporaryMediaIds)
            {
                await _associationService.DeleteObjectAssociationsAsync(mediaId, cancellationToken);
                await _objectService.DeleteObjectAsync(mediaId, cancellationToken);
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

    private async Task<IReadOnlyList<long>> GetContainedMediaIdsAsync(long contentId, CancellationToken cancellationToken)
    {
        var media = await _associationService.RetrieveAssociationAsync(contentId, GraphAssociationType.Contained, null, 100, cancellationToken);
        return media.items.Select(item => item.id2).ToArray();
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
        var verify = GraphJson.String(data, "verify");
        return new UserSummaryResult(
            user.id,
            GraphJson.String(data, "name"),
            GraphJson.String(data, "avatar"),
            DateTimeOffset.TryParse(verify, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiresAt) &&
                expiresAt > DateTimeOffset.UtcNow);
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
        var verify = GraphJson.String(data, "verify");
        return new UserSummaryResult(
            user.id,
            GraphJson.String(data, "name"),
            GraphJson.String(data, "avatar"),
            DateTimeOffset.TryParse(verify, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiresAt) &&
                expiresAt > DateTimeOffset.UtcNow);
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
