namespace SocialGraph.Api.Service;

using SocialGraph.Api.Contracts;

public interface IMessagingPermissionService
{
    Task<MessagingPermissionCheckResponse> CheckAsync(
        MessagingPermissionCheckRequest request,
        CancellationToken cancellationToken = default);
}

