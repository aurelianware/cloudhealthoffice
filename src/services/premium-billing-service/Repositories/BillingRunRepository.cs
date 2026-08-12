using PremiumBillingService.Models;

namespace PremiumBillingService.Repositories;

public interface IBillingRunRepository
{
    Task<BillingRun?> GetByIdAsync(string id);
    Task<BillingRun?> GetByBillingRunNumberAsync(string billingRunNumber);
    Task<IEnumerable<BillingRun>> SearchAsync(DateTime? from, DateTime? to, BillingRunStatus? status = null);
    Task<BillingRun> CreateAsync(BillingRun billingRun);
    Task<BillingRun> UpdateAsync(BillingRun billingRun);
    Task DeleteAsync(string id);
}
