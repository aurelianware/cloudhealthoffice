using CapitationService.Models;

namespace CapitationService.Repositories;

public interface ICapitationDisbursementRepository
{
    Task<CapitationDisbursement?> GetByIdAsync(string id);
    Task<IEnumerable<CapitationDisbursement>> GetByStatementIdAsync(string statementId);
    Task<IEnumerable<CapitationDisbursement>> GetByStatusAsync(DisbursementStatus status);
    Task<IEnumerable<CapitationDisbursement>> GetByStripeTransferIdAsync(string transferId);
    Task<CapitationDisbursement> CreateAsync(CapitationDisbursement disbursement);
    Task<CapitationDisbursement> UpdateAsync(CapitationDisbursement disbursement);

    /// <summary>
    /// Pending to <see cref="DisbursementStatus.Releasing"/> under <paramref name="claimId"/>,
    /// as one conditional write: of two releases racing for the same disbursement
    /// exactly one gets it. False when it is no longer Pending.
    /// </summary>
    Task<bool> TryClaimForReleaseAsync(string id, string claimId, DateTime claimedAt, string releasedBy);

    /// <summary>
    /// Releasing under <paramref name="claimId"/> back to Pending (nothing was sent),
    /// recording why. Does nothing when it is not held by that claim.
    /// </summary>
    Task ReleaseClaimAsync(string id, string claimId, string? reason);
}
