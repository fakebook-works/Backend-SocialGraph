namespace SocialGraph.Api.Service;

using SocialGraph.Api.Contracts;

public interface IBillingClient
{
    Task<IReadOnlyList<EntitlementResult>> GetActiveEntitlementsAsync(long userId, CancellationToken cancellationToken = default);
    Task<bool> IsVerifiedAsync(long userId, CancellationToken cancellationToken = default);
    Task<double> GetFeedBoostMultiplierAsync(long userId, CancellationToken cancellationToken = default);
}
