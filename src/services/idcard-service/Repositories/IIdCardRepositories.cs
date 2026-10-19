using CloudHealthOffice.Infrastructure.Data;
using IdCardService.Models;

namespace IdCardService.Repositories;

/// <summary>
/// An ID card save based on a stale read: another write changed the card (its
/// <see cref="IdCardRecord.Revision"/>) since it was read. Nothing was written;
/// 409 Conflict at the API boundary. The caller re-reads and retries.
/// </summary>
public sealed class IdCardConcurrencyException : ConcurrencyConflictException
{
    public IdCardConcurrencyException(string cardId, long expectedRevision, Exception? inner = null)
        : base($"ID card {cardId} was changed by another write (expected revision {expectedRevision}); re-read it and retry.", inner)
    {
        CardId = cardId;
        ExpectedRevision = expectedRevision;
    }

    public string CardId { get; }
    public long ExpectedRevision { get; }
}

public interface IIdCardOrderRepository
{
    Task UpsertAsync(IdCardOrder order, CancellationToken ct = default);
    Task<IdCardOrder?> GetAsync(string tenantId, string orderId, CancellationToken ct = default);
}

public interface IIdCardRecordRepository
{
    /// <summary>
    /// Inserts a new card, or saves a card read earlier. The Mongo store saves
    /// only if the card's <see cref="IdCardRecord.Revision"/> is still the one
    /// read and throws <see cref="IdCardConcurrencyException"/> otherwise.
    /// </summary>
    Task UpsertAsync(IdCardRecord record, CancellationToken ct = default);
    Task<IdCardRecord?> FindByCardIdAsync(string tenantId, string cardId, CancellationToken ct = default);
    Task<List<IdCardRecord>> ListForMemberAsync(string tenantId, string memberId, CancellationToken ct = default);
    Task<List<IdCardRecord>> ListIssuedSinceAsync(DateTime since, CancellationToken ct = default);
}

public interface IIdCardTemplateRepository
{
    Task UpsertAsync(IdCardTemplate template, CancellationToken ct = default);
    Task<IdCardTemplate?> FindBySponsorAndPlanAsync(string tenantId, string sponsorId, string planId, CancellationToken ct = default);
    Task<IdCardTemplate?> FindSponsorDefaultAsync(string tenantId, string sponsorId, CancellationToken ct = default);
    Task<IdCardTemplate?> FindGlobalDefaultAsync(string tenantId, CancellationToken ct = default);
}
