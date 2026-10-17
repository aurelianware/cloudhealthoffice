using PremiumBillingService.Models;

namespace PremiumBillingService.Repositories;

public interface IPremiumInvoiceRepository
{
    Task<PremiumInvoice?> GetByIdAsync(string id);
    Task<IEnumerable<PremiumInvoice>> GetByGroupNumberAsync(string groupNumber);

    /// <summary>
    /// Invoices with this invoice number (normally one; a reissued period can
    /// leave a voided one beside it). Used to match remittance references.
    /// </summary>
    Task<IEnumerable<PremiumInvoice>> GetByInvoiceNumberAsync(string invoiceNumber);
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

    /// <summary>
    /// Inserts the invoice. Throws <see cref="InvoiceAlreadyExistsException"/>
    /// when an invoice with the same id exists (rated invoices have a
    /// deterministic id per group and period, so a second generation of the
    /// same invoice cannot create a duplicate).
    /// </summary>
    Task<PremiumInvoice> CreateAsync(PremiumInvoice invoice);

    /// <summary>
    /// Saves the invoice if nobody saved it since it was read (same
    /// <see cref="PremiumInvoice.Version"/>). Otherwise
    /// throws <see cref="ConcurrencyConflictException"/>: re-read and retry.
    /// </summary>
    Task<PremiumInvoice> UpdateAsync(PremiumInvoice invoice);
    Task DeleteAsync(string id);

    /// <summary>
    /// <see cref="CreateAsync"/> fenced by the group's billing lease: nothing is
    /// written unless <paramref name="lease"/> still holds it (it is renewed).
    /// Throws <see cref="BillingLeaseLostException"/> otherwise. Stores that can
    /// do both writes atomically override this (Mongo on a replica set: a
    /// transaction); the default renews, then writes.
    /// </summary>
    async Task<PremiumInvoice> CreateFencedAsync(PremiumInvoice invoice, IBillingLeaseRepository leases, BillingLeaseHandle lease,
        DateTime now, TimeSpan duration)
    {
        if (!await leases.RenewAsync(lease, now, duration))
            throw new BillingLeaseLostException(lease.GroupNumber);
        return await CreateAsync(invoice);
    }

    /// <summary><see cref="UpdateAsync"/> fenced by the group's billing lease (see <see cref="CreateFencedAsync"/>).</summary>
    async Task<PremiumInvoice> UpdateFencedAsync(PremiumInvoice invoice, IBillingLeaseRepository leases, BillingLeaseHandle lease,
        DateTime now, TimeSpan duration)
    {
        if (!await leases.RenewAsync(lease, now, duration))
            throw new BillingLeaseLostException(lease.GroupNumber);
        return await UpdateAsync(invoice);
    }
}

/// <summary>An invoice with this id already exists.</summary>
public sealed class InvoiceAlreadyExistsException : Exception
{
    public InvoiceAlreadyExistsException(string invoiceId)
        : base($"Invoice {invoiceId} already exists")
    {
        InvoiceId = invoiceId;
    }

    public string InvoiceId { get; }
}
