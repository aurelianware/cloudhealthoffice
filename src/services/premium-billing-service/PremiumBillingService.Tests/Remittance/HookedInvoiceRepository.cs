using PremiumBillingService.Models;
using PremiumBillingService.Repositories;

namespace PremiumBillingService.Tests.Remittance;

/// <summary>
/// The real invoice repository with a hook before each save, so a test can
/// fail a save or change the invoice underneath (another writer) at a chosen point.
/// </summary>
internal sealed class HookedInvoiceRepository : IPremiumInvoiceRepository
{
    private readonly IPremiumInvoiceRepository _inner;

    public HookedInvoiceRepository(IPremiumInvoiceRepository inner) => _inner = inner;

    /// <summary>Runs before each UpdateAsync with the 1-based save number.</summary>
    public Func<int, PremiumInvoice, Task>? BeforeUpdate { get; set; }

    public int Saves { get; private set; }

    public async Task<PremiumInvoice> UpdateAsync(PremiumInvoice invoice)
    {
        var n = ++Saves;
        if (BeforeUpdate != null)
            await BeforeUpdate(n, invoice);
        return await _inner.UpdateAsync(invoice);
    }

    public Task<PremiumInvoice?> GetByIdAsync(string id) => _inner.GetByIdAsync(id);
    public Task<IEnumerable<PremiumInvoice>> GetByGroupNumberAsync(string groupNumber) => _inner.GetByGroupNumberAsync(groupNumber);
    public Task<IEnumerable<PremiumInvoice>> GetByInvoiceNumberAsync(string invoiceNumber) => _inner.GetByInvoiceNumberAsync(invoiceNumber);
    public Task<IEnumerable<PremiumInvoice>> GetByBillingPeriodAsync(DateTime billingPeriodStart) => _inner.GetByBillingPeriodAsync(billingPeriodStart);
    public Task<IEnumerable<PremiumInvoice>> GetByStatusAsync(InvoiceStatus status) => _inner.GetByStatusAsync(status);
    public Task<IEnumerable<PremiumInvoice>> SearchAsync(string? groupNumber = null, DateTime? periodFrom = null, DateTime? periodTo = null,
        InvoiceStatus? status = null, int page = 1, int pageSize = 50) => _inner.SearchAsync(groupNumber, periodFrom, periodTo, status, page, pageSize);
    public Task<IEnumerable<PremiumInvoice>> GetOverdueAsync() => _inner.GetOverdueAsync();
    public Task<IEnumerable<PremiumInvoice>> ListByMemberAsync(string memberId, int take = 12) => _inner.ListByMemberAsync(memberId, take);
    public Task<PremiumInvoice> CreateAsync(PremiumInvoice invoice) => _inner.CreateAsync(invoice);
    public Task DeleteAsync(string id) => _inner.DeleteAsync(id);
}
