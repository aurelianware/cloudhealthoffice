using PremiumBillingService.Models;
using PremiumBillingService.Repositories;

namespace PremiumBillingService.Services;

/// <summary>
/// Read-modify-write of an invoice under optimistic concurrency. When the save
/// finds that someone else changed the invoice, it re-reads it and applies the
/// change again to the fresh copy, up to <see cref="MaxAttempts"/> times; after
/// that the <see cref="ConcurrencyConflictException"/> reaches the caller.
/// </summary>
public static class InvoiceWrites
{
    public const int MaxAttempts = 5;

    /// <param name="first">The copy already read, if any (saves one read).</param>
    /// <param name="change">
    /// Applies the change to a fresh copy and returns true to save it, or false
    /// when there is nothing (left) to do. It may throw to refuse the change
    /// (for example when a re-read shows the invoice was voided meanwhile).
    /// </param>
    /// <returns>The saved invoice, the unchanged invoice when nothing was to do, or null when it no longer exists.</returns>
    public static async Task<PremiumInvoice?> UpdateWithRetryAsync(
        IPremiumInvoiceRepository repository, string invoiceId, PremiumInvoice? first, Func<PremiumInvoice, bool> change)
    {
        var invoice = first ?? await repository.GetByIdAsync(invoiceId);
        for (var attempt = 1; ; attempt++)
        {
            if (invoice == null)
                return null;
            if (!change(invoice))
                return invoice;
            try
            {
                return await repository.UpdateAsync(invoice);
            }
            catch (ConcurrencyConflictException) when (attempt < MaxAttempts)
            {
                invoice = await repository.GetByIdAsync(invoiceId);
            }
        }
    }
}
