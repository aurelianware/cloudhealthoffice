using ProviderService.Models;

namespace ProviderService.Repositories;

/// <summary>
/// Read-side seam for the append-only
/// <see cref="CredentialingEvent"/> stream (capability 5.6).
/// Append happens exclusively through
/// <see cref="Services.ICredentialingEventPublisher"/>; this interface
/// exposes no Update/Delete operations to enforce the append-only
/// invariant at the type level.
/// </summary>
public interface ICredentialingEventRepository
{
    /// <summary>
    /// Full chain for <paramref name="providerId"/> in ascending
    /// <see cref="CredentialingEvent.Version"/> order. Used by the
    /// service layer to feed <see cref="Services.CredentialingProjector"/>.
    /// </summary>
    Task<IReadOnlyList<CredentialingEvent>> ListAscendingAsync(
        string tenantId, string providerId, CancellationToken ct = default);

    /// <summary>
    /// Single event by client-supplied
    /// <see cref="CredentialingEvent.EventId"/>. Used by the publisher
    /// for idempotency probing and by tests for direct chain inspection.
    /// </summary>
    Task<CredentialingEvent?> GetByEventIdAsync(
        string tenantId, string providerId, string eventId, CancellationToken ct = default);

    /// <summary>
    /// Newest-first page of the chain for the
    /// <c>GET /credentialing/history</c> endpoint. Cursor format is
    /// opaque base64 of <c>{lastVersion}</c>; a missing or invalid cursor
    /// starts from the head. The continuation token is null when the
    /// returned page is the last one.
    /// </summary>
    Task<CredentialingHistoryPage> ListHistoryDescendingAsync(
        string tenantId,
        string providerId,
        string? continuationToken,
        int limit,
        CancellationToken ct = default);
}

/// <summary>Page envelope returned by <see cref="ICredentialingEventRepository.ListHistoryDescendingAsync"/>.</summary>
public sealed record CredentialingHistoryPage(
    IReadOnlyList<CredentialingEvent> Items,
    string? ContinuationToken);
