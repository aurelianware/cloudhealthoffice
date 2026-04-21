using ArService.Models;

namespace ArService.Repositories;

public interface IGlAccountRepository
{
    Task<GlAccount?> GetByIdAsync(string id);
    Task<IEnumerable<GlAccount>> SearchAsync(GlAccountType? accountType = null, LineOfBusiness? lob = null,
        GlAccountStatus? status = null, int page = 1, int pageSize = 50);
    Task<GlAccount> CreateAsync(GlAccount account);
    Task<GlAccount> UpdateAsync(GlAccount account);
}

public interface IArBalanceRepository
{
    Task<ArBalance?> GetByIdAsync(string id);
    Task<IEnumerable<ArBalance>> SearchAsync(string? accountId = null, DateTime? period = null,
        bool? isReconciled = null, int page = 1, int pageSize = 50);
    Task<IEnumerable<ArBalance>> GetByAccountIdAsync(string accountId);
    Task<ArBalance> CreateAsync(ArBalance balance);
    Task<ArBalance> UpdateAsync(ArBalance balance);

    /// <summary>
    /// Return every balance whose <c>PostingEntries</c> contains at least one
    /// entry tagged to <paramref name="memberId"/>. The controller aggregates
    /// these in memory — volumes are small (balances keyed by account+period
    /// intersected with a single member) so this is fine. Scales to tens of
    /// balances per member per plan year.
    /// </summary>
    Task<IEnumerable<ArBalance>> GetBalancesContainingMemberAsync(string memberId);
}

public interface ICashPostingRepository
{
    Task<CashPosting?> GetByIdAsync(string id);
    Task<IEnumerable<CashPosting>> SearchAsync(PayerType? payerType = null, CashPostingStatus? status = null,
        DateTime? dateFrom = null, DateTime? dateTo = null, int page = 1, int pageSize = 50);
    Task<CashPosting> CreateAsync(CashPosting posting);
    Task<CashPosting> UpdateAsync(CashPosting posting);
}

public interface IArAdjustmentRepository
{
    Task<ArAdjustment?> GetByIdAsync(string id);
    Task<IEnumerable<ArAdjustment>> SearchAsync(ArAdjustmentType? type = null, ArAdjustmentStatus? status = null,
        DateTime? period = null, string? glAccountId = null, int page = 1, int pageSize = 50);
    Task<ArAdjustment> CreateAsync(ArAdjustment adjustment);
    Task<ArAdjustment> UpdateAsync(ArAdjustment adjustment);
}

public interface IArBatchRuleRepository
{
    Task<ArBatchRule?> GetByIdAsync(string id);
    Task<IEnumerable<ArBatchRule>> SearchAsync(BatchRuleTrigger? trigger = null,
        BatchRuleStatus? status = null, int page = 1, int pageSize = 50);
    Task<ArBatchRule> CreateAsync(ArBatchRule rule);
    Task<ArBatchRule> UpdateAsync(ArBatchRule rule);
}
