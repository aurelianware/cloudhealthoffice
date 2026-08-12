using PremiumBillingService.Models;

namespace PremiumBillingService.Repositories;

public interface IPremiumInvoiceRepository
{
    Task<PremiumInvoice?> GetByIdAsync(string id);
    Task<IEnumerable<PremiumInvoice>> GetByGroupNumberAsync(string groupNumber);
    Task<IEnumerable<PremiumInvoice>> GetByBillingPeriodAsync(DateTime billingPeriodStart);
    Task<IEnumerable<PremiumInvoice>> GetByStatusAsync(InvoiceStatus status);
    Task<IEnumerable<PremiumInvoice>> SearchAsync(
        string? groupNumber = null,
        DateTime? periodFrom = null,
        DateTime? periodTo = null,
        InvoiceStatus? status = null,
        int page = 1,
        int pageSize = 50);
    Task<IEnumerable<PremiumInvoice>> GetOverdueAsync();

    /// <summary>
    /// Return invoices whose <c>LineItems</c> include at least one line for
    /// the given <paramref name="memberId"/>, newest first. Limited to
    /// <paramref name="take"/> invoices — the portal Member Details Premium
    /// tab shows the last 12 billing periods.
    /// </summary>
    Task<IEnumerable<PremiumInvoice>> ListByMemberAsync(string memberId, int take = 12);

    Task<PremiumInvoice> CreateAsync(PremiumInvoice invoice);
    Task<PremiumInvoice> UpdateAsync(PremiumInvoice invoice);
    Task DeleteAsync(string id);
}
