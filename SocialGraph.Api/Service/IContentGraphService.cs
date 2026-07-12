namespace SocialGraph.Api.Service;

using SocialGraph.Api.Contracts;

public interface IContentGraphService
{
    Task<ContentResult> CreateFeedPostAsync(CreateFeedPostInput input, CancellationToken cancellationToken = default);
    Task<ContentResult> CreateGroupPostAsync(CreateGroupPostInput input, CancellationToken cancellationToken = default);
    Task<ContentResult?> UpdatePostAsync(UpdatePostInput input, CancellationToken cancellationToken = default);
    Task<bool> DeleteContentAsync(long contentId, CancellationToken cancellationToken = default);
    Task<ContentResult?> GetContentAsync(long contentId, CancellationToken cancellationToken = default);
    Task<PostDetailResult?> GetPostDetailAsync(long userId, long postId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PostDetailResult>> GetPostDetailsAsync(long userId, IReadOnlyList<long> postIds, CancellationToken cancellationToken = default);
    Task<ContentResult> CreateCommentAsync(CreateCommentInput input, CancellationToken cancellationToken = default);
    Task<NormalStoryResult> CreateNormalStoryAsync(CreateNormalStoryInput input, CancellationToken cancellationToken = default);
    Task<IHomeStoryResult> CreateShareStoryAsync(CreateShareStoryInput input, CancellationToken cancellationToken = default);
    Task<DeleteStoryPayload> DeleteStoryAsync(DeleteStoryInput input, CancellationToken cancellationToken = default);
    Task<HomeStoryPageResult> GetHomeStoriesAsync(long userId, int limit, string? cursor, CancellationToken cancellationToken = default);
    Task<HomeStoryBucketResult?> GetMyStoriesAsync(long userId, CancellationToken cancellationToken = default);
    Task<int> CleanupExpiredStoriesAsync(int limit, CancellationToken cancellationToken = default);
    Task<ContentResult> CreateReelAsync(CreateReelInput input, CancellationToken cancellationToken = default);
    Task<ContentResult> SharePostAsync(SharePostInput input, CancellationToken cancellationToken = default);
    Task<bool> LikeAsync(long userId, long targetId, CancellationToken cancellationToken = default);
    Task<bool> UnlikeAsync(long userId, long targetId, CancellationToken cancellationToken = default);
    Task<bool> SaveAsync(long userId, long targetId, CancellationToken cancellationToken = default);
    Task<bool> UnsaveAsync(long userId, long targetId, CancellationToken cancellationToken = default);
    Task<bool> WatchAsync(long userId, long targetId, CancellationToken cancellationToken = default);
    Task<bool> TagAsync(long postId, long userId, CancellationToken cancellationToken = default);
    Task<bool> MentionAsync(long sourceId, long userId, CancellationToken cancellationToken = default);
}
