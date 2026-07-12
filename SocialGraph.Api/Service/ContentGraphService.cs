namespace SocialGraph.Api.Service;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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

    public async Task<ContentResult> CreateStoryAsync(CreateStoryInput input, CancellationToken cancellationToken = default)
    {
        if (input.Media is not null && input.SharedSourceId is not null)
        {
            throw new ArgumentException("Story can attach media or share a source, not both.", nameof(input));
        }

        if (input.SharedSourceId is long sharedSourceId)
        {
            await ValidateStoryShareSourceAsync(sharedSourceId, cancellationToken);
        }

        var story = await _objectService.AddObjectAsync(GraphObjectType.Story, GraphJson.StoryJson(input.Content), cancellationToken);
        var media = await AttachTemporarySingleMediaAsync(story.id, input.Media, cancellationToken);
        await _associationService.AddAssociationAsync(input.AuthorId, GraphAssociationType.Authored, story.id, cancellationToken);

        if (input.SharedSourceId is long sourceId)
        {
            await _associationService.AddAssociationAsync(story.id, GraphAssociationType.Share, sourceId, cancellationToken);
        }

        return await BuildContentResultAsync(story, input.AuthorId, media, cancellationToken);
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

        var storyRows = await (
            from association in _dbContext.AssociationsTb.AsNoTracking()
            join obj in _dbContext.ObjectsTb.AsNoTracking() on association.id2 equals obj.id
            where visibleAuthorIds.Contains(association.id1) &&
                association.atype == GraphAssociationType.Authored &&
                obj.otype == GraphObjectType.Story
            select new StoryRow(association.id1, obj.id, obj.data))
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var activeStoriesByAuthor = new Dictionary<long, List<ActiveStoryRow>>();
        var expiredStoryIds = new List<long>();

        foreach (var row in storyRows)
        {
            var data = GraphJson.ParseObject(row.Data);
            if (!TryGetDateTimeOffset(data, "expire", out var expiresAt) || expiresAt <= now)
            {
                expiredStoryIds.Add(row.StoryId);
                continue;
            }

            var createdAt = TryGetDateTimeOffset(data, "create", out var parsedCreatedAt)
                ? parsedCreatedAt
                : DateTimeOffset.UnixEpoch;

            if (!activeStoriesByAuthor.TryGetValue(row.AuthorId, out var authorStories))
            {
                authorStories = [];
                activeStoriesByAuthor[row.AuthorId] = authorStories;
            }

            authorStories.Add(new ActiveStoryRow(
                row.AuthorId,
                new SocialGraphObjectResult(row.StoryId, GraphObjectType.Story, row.Data),
                createdAt));
        }

        foreach (var expiredStoryId in expiredStoryIds)
        {
            await DeleteExpiredStoryWithMediaAsync(expiredStoryId, cancellationToken);
        }

        var buckets = activeStoriesByAuthor
            .Select(item => new StoryBucketCandidate(
                item.Key,
                item.Value.Max(story => story.CreatedAt),
                item.Value.OrderBy(story => story.CreatedAt).ToArray()))
            .OrderByDescending(item => item.LatestCreate)
            .ThenByDescending(item => item.AuthorId)
            .ToArray();

        if (TryDecodeStoryCursor(cursor, out var decodedCursor))
        {
            buckets = buckets
                .Where(item => item.LatestCreate < decodedCursor.LatestCreate ||
                    item.LatestCreate == decodedCursor.LatestCreate && item.AuthorId < decodedCursor.AuthorId)
                .ToArray();
        }

        var pageCandidates = buckets.Take(take + 1).ToArray();
        var selectedCandidates = pageCandidates.Take(take).ToArray();
        var resultItems = new List<HomeStoryBucketResult>(selectedCandidates.Length);

        foreach (var candidate in selectedCandidates)
        {
            var author = await BuildUserSummaryAsync(candidate.AuthorId, cancellationToken);
            if (author is null)
            {
                continue;
            }

            var stories = new List<IHomeStoryResult>(candidate.Stories.Count);
            foreach (var story in candidate.Stories)
            {
                stories.Add(await BuildHomeStoryItemAsync(story.Story, cancellationToken));
            }

            resultItems.Add(new HomeStoryBucketResult(
                author,
                candidate.LatestCreate.ToString("O", CultureInfo.InvariantCulture),
                stories));
        }

        var endCursor = selectedCandidates.Length == 0
            ? null
            : EncodeStoryCursor(selectedCandidates[^1].LatestCreate, selectedCandidates[^1].AuthorId);

        return new HomeStoryPageResult(resultItems, endCursor, pageCandidates.Length > take);
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

    private async Task DeleteExpiredStoryWithMediaAsync(long storyId, CancellationToken cancellationToken)
    {
        var mediaIds = await GetContainedMediaIdsAsync(storyId, cancellationToken);

        await _associationService.DeleteObjectAssociationsAsync(storyId, cancellationToken);
        await _objectService.DeleteObjectAsync(storyId, cancellationToken);

        foreach (var mediaId in mediaIds)
        {
            await _associationService.DeleteObjectAssociationsAsync(mediaId, cancellationToken);
            await _objectService.DeleteObjectAsync(mediaId, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<long>> GetContainedMediaIdsAsync(long contentId, CancellationToken cancellationToken)
    {
        var media = await _associationService.RetrieveAssociationAsync(contentId, GraphAssociationType.Contained, null, 100, cancellationToken);
        return media.items.Select(item => item.id2).ToArray();
    }

    private async Task<IHomeStoryResult> BuildHomeStoryItemAsync(
        SocialGraphObjectResult story,
        CancellationToken cancellationToken)
    {
        var data = GraphJson.ParseObject(story.data);
        var sharedSource = await GetSharedSourceAsync(story.id, cancellationToken);
        if (sharedSource is not null)
        {
            return new ShareStoryResult(
                story.id,
                GraphJson.String(data, "content"),
                GraphJson.String(data, "create"),
                GraphJson.String(data, "expire"),
                sharedSource);
        }

        return new NormalStoryResult(
            story.id,
            GraphJson.String(data, "content"),
            GraphJson.String(data, "create"),
            GraphJson.String(data, "expire"),
            await GetMediaAsync(story.id, cancellationToken));
    }

    private async Task<IStorySharedSourceResult?> GetSharedSourceAsync(long storyId, CancellationToken cancellationToken)
    {
        var share = await _associationService.RetrieveAssociationAsync(storyId, GraphAssociationType.Share, null, 1, cancellationToken);
        var sourceId = share.items.FirstOrDefault()?.id2 ?? 0;
        if (sourceId <= 0)
        {
            return null;
        }

        var source = await _objectService.RetrieveObjectAsync(sourceId, cancellationToken);
        if (source is null)
        {
            return null;
        }

        return await BuildSharedSourceResultAsync(source, cancellationToken);
    }

    private async Task<IStorySharedSourceResult> BuildSharedSourceResultAsync(
        SocialGraphObjectResult source,
        CancellationToken cancellationToken)
    {
        var data = GraphJson.ParseObject(source.data);
        var authorId = await GetAuthorIdAsync(source.id, cancellationToken);
        var author = authorId > 0 ? await BuildUserSummaryAsync(authorId, cancellationToken) : null;
        var media = await GetMediaAsync(source.id, cancellationToken);
        var content = GraphJson.String(data, "content");
        var createdAt = GraphJson.String(data, "create");

        return source.otype switch
        {
            GraphObjectType.FeedPost => new FeedPostSharedSourceResult(
                source.id,
                content,
                await GetContentPrivacyAsync(source, data, cancellationToken),
                createdAt,
                author,
                media),

            GraphObjectType.GroupPost => new GroupPostSharedSourceResult(
                source.id,
                content,
                await GetContentPrivacyAsync(source, data, cancellationToken),
                createdAt,
                author,
                await BuildPublishedGroupSummaryAsync(source.id, cancellationToken),
                media),

            GraphObjectType.Reel => new ReelSharedSourceResult(
                source.id,
                content,
                createdAt,
                author,
                media),

            _ => throw new InvalidOperationException("Unsupported story shared source type.")
        };
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
            verify,
            DateTimeOffset.TryParse(verify, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiresAt) &&
                expiresAt > DateTimeOffset.UtcNow);
    }

    private async Task<GroupSummaryResult?> BuildPublishedGroupSummaryAsync(long postId, CancellationToken cancellationToken)
    {
        var groupId = await GetPublishedGroupIdAsync(postId, cancellationToken);
        var group = await _objectService.RetrieveObjectAsync(groupId, cancellationToken);
        if (group is null || group.otype != GraphObjectType.Group)
        {
            return null;
        }

        var data = GraphJson.ParseObject(group.data);
        return new GroupSummaryResult(
            group.id,
            GraphJson.String(data, "name"),
            GraphJson.String(data, "avatar"),
            GraphJson.String(data, "background"),
            GraphJson.Int(data, "privacy"));
    }

    private async Task ValidateStoryShareSourceAsync(long sourceId, CancellationToken cancellationToken)
    {
        var source = await _objectService.RetrieveObjectAsync(sourceId, cancellationToken)
            ?? throw new ArgumentException("Shared source does not exist.", nameof(sourceId));

        var data = GraphJson.ParseObject(source.data);
        switch (source.otype)
        {
            case GraphObjectType.FeedPost:
                if (GraphJson.Int(data, "privacy") != 0)
                {
                    throw new ArgumentException("Only public feed posts can be shared to story.", nameof(sourceId));
                }

                return;

            case GraphObjectType.GroupPost:
                var groupId = await GetPublishedGroupIdAsync(source.id, cancellationToken);
                var group = await _objectService.RetrieveObjectAsync(groupId, cancellationToken);
                if (group is null || group.otype != GraphObjectType.Group)
                {
                    throw new ArgumentException("Group post source is missing its group.", nameof(sourceId));
                }

                if (GraphJson.Int(GraphJson.ParseObject(group.data), "privacy") != 0)
                {
                    throw new ArgumentException("Only posts in public groups can be shared to story.", nameof(sourceId));
                }

                return;

            case GraphObjectType.Reel:
                return;

            default:
                throw new ArgumentException("Stories can only share public feed posts, public group posts, or reels.", nameof(sourceId));
        }
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

    private sealed record StoryBucketCandidate(long AuthorId, DateTimeOffset LatestCreate, IReadOnlyList<ActiveStoryRow> Stories);

    private readonly record struct StoryCursor(DateTimeOffset LatestCreate, long AuthorId);
}
