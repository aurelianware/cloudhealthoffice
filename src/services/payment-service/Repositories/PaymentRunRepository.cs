using PaymentService.Models;

namespace PaymentService.Repositories;

public interface IPaymentRunRepository
{
    Task<PaymentRun?> GetByIdAsync(string id);
    Task<PaymentRun?> GetByPaymentRunNumberAsync(string paymentRunNumber);
    Task<IEnumerable<PaymentRun>> SearchAsync(DateTime from, DateTime to, PaymentRunStatus? status = null);
    Task<PaymentRun> CreateAsync(PaymentRun paymentRun);
    Task<PaymentRun> UpdateAsync(PaymentRun paymentRun);
    Task DeleteAsync(string id);
}
