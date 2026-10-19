using CloudHealthOffice.Infrastructure.Data;

namespace PaymentService.Repositories;

/// <summary>
/// A payment, payment run or reversal run save based on a stale read: another
/// write changed the document (its <c>Revision</c>) since it was read. Nothing
/// was written; 409 Conflict at the API boundary. The caller re-reads and retries.
/// </summary>
public sealed class PaymentConcurrencyException : ConcurrencyConflictException
{
    public PaymentConcurrencyException(string entity, string id, long expectedRevision)
        : base($"{entity} {id} was changed by another write (expected revision {expectedRevision}); re-read it and retry.")
    {
        Entity = entity;
        Id = id;
        ExpectedRevision = expectedRevision;
    }

    /// <summary>"Payment", "Payment run" or "Reversal run".</summary>
    public string Entity { get; }
    public string Id { get; }
    public long ExpectedRevision { get; }
}
