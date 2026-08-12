using PremiumBillingService.Models;

namespace PremiumBillingService.Repositories;

public interface IEftDraftRepository
{
    Task<EftDraft> CreateAsync(EftDraft draft);
    Task<EftDraft?> GetByIdAsync(string id);
    Task<EftDraft> UpdateAsync(EftDraft draft);
    Task<IEnumerable<EftDraft>> GetByInvoiceIdAsync(string invoiceId);
    Task<IEnumerable<EftDraft>> GetByStatusAsync(EftDraftStatus status);
    Task<IEnumerable<EftDraft>> GetByStripePaymentIntentIdAsync(string paymentIntentId);
    Task<IEnumerable<EftDraft>> GetPendingDraftsAsync();
}
