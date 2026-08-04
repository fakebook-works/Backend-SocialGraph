namespace SocialGraph.Api.SubGraphQL;

using GreenDonut;
using SocialGraph.Api.Infrastructure;
using SocialGraph.Api.Service;

/// <summary>
/// Batches the user references Fusion asks this subgraph to resolve.
/// </summary>
/// <remarks>
/// Fusion calls the reference resolver once per representation in _entities, and the
/// resolver ran two block checks and a profile read for each one — roughly four queries
/// per user. Rendering a Messenger inbox with thirty participants therefore issued around
/// a hundred and twenty queries against a database that sits on the other side of a
/// tailnet, where every round trip is real latency.
///
/// GetProfilesForViewerAsync already answers the whole set in one query and applies the
/// same block filtering in both directions, so a blocked or missing user is simply absent
/// from the result — which is exactly the null the resolver returned before.
/// </remarks>
public sealed class FederatedUserByIdDataLoader : BatchDataLoader<long, FederatedUser?>
{
    private readonly IUserGraphService _userGraphService;
    private readonly ITrustedCallerAccessor _trustedCaller;

    public FederatedUserByIdDataLoader(
        IUserGraphService userGraphService,
        ITrustedCallerAccessor trustedCaller,
        IBatchScheduler batchScheduler,
        DataLoaderOptions options)
        : base(batchScheduler, options)
    {
        _userGraphService = userGraphService;
        _trustedCaller = trustedCaller;
    }

    protected override async Task<IReadOnlyDictionary<long, FederatedUser?>> LoadBatchAsync(
        IReadOnlyList<long> keys,
        CancellationToken cancellationToken)
    {
        // Every key in a batch belongs to the same request, so one viewer governs them all.
        var viewerId = _trustedCaller.RequireUserId();
        var profiles = await _userGraphService.GetProfilesForViewerAsync(viewerId, keys, cancellationToken);

        var profilesById = profiles.ToDictionary(profile => profile.Id);

        // DataLoader must return a value for every requested key. A blocked user is
        // intentionally absent from GetProfilesForViewerAsync; mapping that key to
        // null preserves the nullable User field in the federated schema instead of
        // turning one hidden participant into an error for the whole inbox query.
        return keys.ToDictionary(
            id => id,
            id => profilesById.TryGetValue(id, out var profile)
                ? new FederatedUser(
                    profile.Id,
                    profile.Name,
                    profile.Avatar,
                    profile.Bio,
                    profile.IsVerified,
                    profile.FriendCount,
                    profile.FollowerCount,
                    profile.FollowingCount,
                    profile.Privacy)
                : null);
    }
}
