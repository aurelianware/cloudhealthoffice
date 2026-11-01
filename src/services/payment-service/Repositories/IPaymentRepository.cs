using PaymentService.Models;

namespace PaymentService.Repositories;

public interface IPaymentRepository
{
    Task<Payment?> GetByIdAsync(string id);
    Task<Payment?> GetByCheckNumberAsync(string checkNumber);
    Task<IEnumerable<Payment>> GetByClaimIdAsync(string claimId);

    /// <summary>
    /// A payee's payments (checks and EFTs to that NPI), newest first: the
    /// provider record's Payments tab.
    /// </summary>
    Task<IReadOnlyList<Payment>> GetByPayeeNpiAsync(string payeeNpi, int limit)
        => throw new NotSupportedException("Payments by payee need the MongoDB payment store.");

    /// <summary>
    /// Of <paramref name="claimIds"/>, those that already appear in a payment of
    /// the current tenant: a payment issued for them (<paramref name="reversal"/>
    /// false) or a reversal payment recouping them (true). Payment-service's own
    /// records are authoritative for "already paid", whatever the claim's status
    /// in claims-service.
    /// </summary>
    Task<IReadOnlyCollection<string>> GetClaimIdsWithPaymentAsync(IReadOnlyCollection<string> claimIds, bool reversal);
    Task<IEnumerable<Payment>> SearchAsync(
        DateTime? paymentDateFrom,
        DateTime? paymentDateTo,
        string? payerId,
        PaymentStatus? status,
        int page = 1,
        int pageSize = 50);
    Task<PaymentsSummary> GetPaymentsSummaryAsync(DateTime from, DateTime to);

    /// <summary>
    /// Payments this plan issued (by a payment or reversal run) dated in
    /// [<paramref name="from"/>, <paramref name="before"/>), excluding any
    /// voided before its file reached the bank: the 1099 basis.
    /// </summary>
    Task<IReadOnlyList<Services.InformationReturns.IssuedPayment>> GetIssuedPaymentsAsync(DateTime from, DateTime before, CancellationToken ct = default)
        => throw new NotSupportedException("Issued payments need the MongoDB payment store.");
    Task<Payment> CreateAsync(Payment payment);
    Task<Payment> UpdateAsync(Payment payment);
    Task DeleteAsync(string id);
}
