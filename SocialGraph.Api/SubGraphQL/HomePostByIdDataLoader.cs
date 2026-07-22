namespace SocialGraph.Api.SubGraphQL;

using GreenDonut;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Infrastructure;
using SocialGraph.Api.Service;

public sealed class HomePostByIdDataLoader : BatchDataLoader<long, IHomePostResult>
{
    private readonly IContentGraphService _contentGraphService;
    private readonly ITrustedCallerAccessor _trustedCaller;

    public HomePostByIdDataLoader(
        IContentGraphService contentGraphService,
        ITrustedCallerAccessor trustedCaller,
        IBatchScheduler batchScheduler,
        DataLoaderOptions options)
        : base(batchScheduler, options)
    {
        _contentGraphService = contentGraphService;
        _trustedCaller = trustedCaller;
    }

    protected override async Task<IReadOnlyDictionary<long, IHomePostResult>> LoadBatchAsync(
        IReadOnlyList<long> keys,
        CancellationToken cancellationToken)
    {
        var viewerId = _trustedCaller.RequireUserId();
        var details = await _contentGraphService.GetPostDetailsAsync(
            viewerId,
            keys,
            cancellationToken);

        return details.ToDictionary(GetPostId);
    }

    private static long GetPostId(IHomePostResult post) => post switch
    {
        FeedPostDetailResult feedPost => feedPost.Id,
        ReelDetailResult reel => reel.Id,
        GroupPostDetailResult groupPost => groupPost.Id,
        _ => throw new InvalidOperationException("Unsupported home post result type.")
    };
}
