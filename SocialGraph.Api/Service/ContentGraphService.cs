namespace SocialGraph.Api.Service;

using SocialGraph.Api.Contracts;

public sealed class ContentGraphService : IContentGraphService
{
    private readonly IObjectService _objectService;
    private readonly IAssociationService _associationService;
    private readonly IExternalServiceClient _externalServiceClient;

    public ContentGraphService(
        IObjectService objectService,
        IAssociationService associationService,
        IExternalServiceClient externalServiceClient)
    {
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
        var story = await _objectService.AddObjectAsync(GraphObjectType.Story, GraphJson.StoryJson(input.Content), cancellationToken);
        var media = await AttachSingleMediaAsync(input.AuthorId, story.id, input.Media, cancellationToken);
        await _associationService.AddAssociationAsync(input.AuthorId, GraphAssociationType.Authored, story.id, cancellationToken);
        return await BuildContentResultAsync(story, input.AuthorId, media, cancellationToken);
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

    private async Task<IReadOnlyList<MediaResult>> AttachMediaAsync(
        long ownerId,
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
            await _associationService.AddAssociationAsync(ownerId, GraphAssociationType.Owned, mediaObject.id, cancellationToken);
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
}
