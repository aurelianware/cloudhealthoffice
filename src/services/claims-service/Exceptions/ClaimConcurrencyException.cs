using CloudHealthOffice.Infrastructure.Data;

namespace ClaimsService.Exceptions;

/// <summary>
/// A claim write based on a stale read: another write changed the claim (its
/// <see cref="Models.Claim.Revision"/>) since it was read. Nothing was
/// written; 409 Conflict at the API boundary. The caller re-reads and retries.
/// </summary>
public sealed class ClaimConcurrencyException : ConcurrencyConflictException
{
    public ClaimConcurrencyException(string claimId, long expectedRevision)
        : base($"Claim {claimId} was changed by another write (expected revision {expectedRevision}); re-read it and retry.")
    {
        ClaimId = claimId;
        ExpectedRevision = expectedRevision;
    }

    public string ClaimId { get; }
    public long ExpectedRevision { get; }
}
