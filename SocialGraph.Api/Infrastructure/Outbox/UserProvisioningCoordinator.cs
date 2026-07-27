namespace SocialGraph.Api.Infrastructure.Outbox;

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SocialGraph.Api.Database;
using SocialGraph.Api.Service;

/// <summary>
/// Completes or compensates the registration saga atomically with the outbox state.
/// Auth is provisioned first. Only after it acknowledges the idempotent create do the
/// Search/Recommendation/Messenger projections become dispatchable. A terminal Auth failure
/// removes the SocialGraph profile and queues idempotent deletes for every possible partial
/// downstream write, eliminating permanently half-created accounts.
/// </summary>
public sealed class UserProvisioningCoordinator(
    MyDbContext dbContext,
    IExternalServiceClient externalServices,
    IUserGraphService users,
    IOutboxPayloadProtector payloadProtector) : IUserProvisioningCoordinator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task CompleteAsync(
        IIntegrationOutboxStore store,
        IntegrationOutboxMessage message,
        CancellationToken cancellationToken = default)
    {
        var payload = ReadPayload(message);
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        try
        {
            await externalServices.CreateSearchIndexAsync(
                payload.UserId,
                "user",
                payload.Name,
                cancellationToken);
            await externalServices.CreateUserEmbeddingAsync(payload.UserId, cancellationToken);
            await externalServices.CreateMessengerUserAsync(payload.UserId, cancellationToken);
            await store.MarkCompletedAsync(message.id, cancellationToken);
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

            throw;
        }
    }

    public async Task CompensateAsync(
        IIntegrationOutboxStore store,
        IntegrationOutboxMessage message,
        string error,
        CancellationToken cancellationToken = default)
    {
        var userId = message.aggregate_id.GetValueOrDefault();
        if (userId <= 0)
        {
            await store.MarkFailedAsync(
                message.id,
                error,
                TimeSpan.Zero,
                deadLetter: true,
                cancellationToken);
            return;
        }

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        try
        {
            await store.MarkFailedAsync(
                message.id,
                error,
                TimeSpan.Zero,
                deadLetter: true,
                cancellationToken);

            // DeleteUserAsync queues Auth/Search/Recommendation/Messenger deletion in the same
            // EF transaction. If the profile was already absent, still queue those idempotent
            // deletes because Auth may have accepted the request before its response was lost.
            if (!await users.DeleteUserAsync(userId, cancellationToken))
            {
                await externalServices.DeleteUserAsync(userId, cancellationToken);
            }

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

            throw;
        }
    }

    private UserCreateEvent ReadPayload(IntegrationOutboxMessage message)
    {
        try
        {
            return JsonSerializer.Deserialize<UserCreateEvent>(
                       payloadProtector.Unprotect(message.payload),
                       JsonOptions)
                   ?? throw new PermanentOutboxException("User provisioning payload is empty.");
        }
        catch (PermanentOutboxException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or CryptographicException or FormatException)
        {
            throw new PermanentOutboxException("User provisioning payload is invalid.", exception);
        }
    }

    private async Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsRelational() || dbContext.Database.CurrentTransaction is not null)
        {
            return null;
        }

        return await dbContext.Database.BeginTransactionAsync(cancellationToken);
    }
}
