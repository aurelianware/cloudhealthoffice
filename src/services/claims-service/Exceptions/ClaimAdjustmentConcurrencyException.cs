using CloudHealthOffice.Infrastructure.Data;

namespace ClaimsService.Exceptions;

/// <summary>
/// A claim adjustment write based on a stale read: another write changed the
/// adjustment (its <see cref="Models.ClaimAdjustment.Revision"/>) since it was
/// read. Nothing was written; 409 Conflict at the API boundary. The caller
/// re-reads and retries.
/// </summary>
public sealed class ClaimAdjustmentConcurrencyException : ConcurrencyConflictException
{
    public ClaimAdjustmentConcurrencyException(string adjustmentId, long expectedRevision)
        : base($"Claim adjustment {adjustmentId} was changed by another write (expected revision {expectedRevision}); re-read it and retry.")
    {
        AdjustmentId = adjustmentId;
        ExpectedRevision = expectedRevision;
    }

    public string AdjustmentId { get; }
    public long ExpectedRevision { get; }
}
