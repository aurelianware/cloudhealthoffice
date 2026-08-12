using PaymentService.Models;

namespace PaymentService.Repositories;

public interface IPaymentRepository
{
    Task<Payment?> GetByIdAsync(string id);
    Task<Payment?> GetByCheckNumberAsync(string checkNumber);
    Task<IEnumerable<Payment>> GetByClaimIdAsync(string claimId);
    Task<IEnumerable<Payment>> SearchAsync(
        DateTime? paymentDateFrom,
        DateTime? paymentDateTo,
        string? payerId,
        PaymentStatus? status,
        int page = 1,
        int pageSize = 50);
    Task<PaymentsSummary> GetPaymentsSummaryAsync(DateTime from, DateTime to);
    Task<Payment> CreateAsync(Payment payment);
    Task<Payment> UpdateAsync(Payment payment);
    Task DeleteAsync(string id);
}
