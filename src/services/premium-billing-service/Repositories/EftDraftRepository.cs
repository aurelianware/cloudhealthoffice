using PremiumBillingService.Models;

namespace PremiumBillingService.Repositories;

public interface IEftDraftRepository
{
    /// <summary>
    /// Creates the draft. An active draft (<see cref="EftDraft.IsActive"/>) takes
    /// its invoice: <see cref="InvoiceDraftConflictException"/> when the invoice
    /// already has an active draft, and nothing is written.
    /// </summary>
    Task<EftDraft> CreateAsync(EftDraft draft);

    Task<EftDraft?> GetByIdAsync(string id);

    /// <summary>Replaces the draft; one that is no longer active frees its invoice.</summary>
    Task<EftDraft> UpdateAsync(EftDraft draft);
    Task<IEnumerable<EftDraft>> GetByInvoiceIdAsync(string invoiceId);
    Task<IEnumerable<EftDraft>> GetByStatusAsync(EftDraftStatus status);
    Task<IEnumerable<EftDraft>> GetByStripePaymentIntentIdAsync(string paymentIntentId);
    Task<IEnumerable<EftDraft>> GetPendingDraftsAsync();

    /// <summary>
    /// Pending to <see cref="EftDraftStatus.Releasing"/> under <paramref name="claimId"/>,
    /// as one conditional write: of two releases racing for the same draft exactly
    /// one gets it. False when the draft is no longer Pending.
    /// </summary>
    Task<bool> TryClaimForReleaseAsync(string id, string claimId, DateTime claimedAt, string releasedBy);

    /// <summary>
    /// Releasing under <paramref name="claimId"/> back to Pending (nothing was sent),
    /// recording why. Does nothing when the draft is not held by that claim.
    /// </summary>
    Task ReleaseClaimAsync(string id, string claimId, string? reason);

    /// <summary>
    /// Pending to Cancelled as one conditional write, freeing the invoice for a
    /// new draft. False when the draft is no longer Pending.
    /// </summary>
    Task<bool> TryCancelPendingAsync(string id, string cancelledBy);
}
