namespace CloudHealthOffice.Portal.Services;

// ── AR Service ──────────────────────────────────────────────────────────────

public interface IArService
{
    // GL Accounts
    Task<List<GlAccountSummary>> GetAccountsAsync(string? accountType = null, string? lob = null, string? status = null);
    Task<GlAccountSummary?> GetAccountByIdAsync(string id);
    Task<string> CreateAccountAsync(GlAccountSummary account);
    Task UpdateAccountAsync(string id, GlAccountSummary account);
    Task ActivateAccountAsync(string id);
    Task DeactivateAccountAsync(string id);

    // Balances
    Task<List<ArBalanceSummary>> GetBalancesAsync(string? accountId = null, DateTime? period = null, bool? isReconciled = null);
    Task<ArBalanceSummary?> GetBalanceByIdAsync(string id);
    Task<List<ArBalanceSummary>> GetBalancesByAccountAsync(string accountId);
    Task<ArAgingSummary> GetAgingSummaryAsync();
    Task ReconcileBalanceAsync(string id);

    // Cash Posting
    Task<List<CashPostingSummary>> GetCashPostingsAsync(string? payerType = null, string? status = null, DateTime? dateFrom = null, DateTime? dateTo = null);
    Task<CashPostingSummary?> GetCashPostingByIdAsync(string id);
    Task<string> CreateCashPostingAsync(CashPostingSummary posting);
    Task ApplyCashPostingAsync(string id);
    Task VoidCashPostingAsync(string id);

    // Adjustments
    Task<List<ArAdjustmentSummary>> GetAdjustmentsAsync(string? type = null, string? status = null, DateTime? period = null, string? accountId = null);
    Task<ArAdjustmentSummary?> GetAdjustmentByIdAsync(string id);
    Task<string> CreateAdjustmentAsync(ArAdjustmentSummary adjustment);
    Task ApproveAdjustmentAsync(string id);
    Task RejectAdjustmentAsync(string id, string reason);
    Task PostAdjustmentAsync(string id);
    Task ReverseAdjustmentAsync(string id);

    // Batch Rules
    Task<List<ArBatchRuleSummary>> GetBatchRulesAsync(string? trigger = null, string? status = null);
    Task<ArBatchRuleSummary?> GetBatchRuleByIdAsync(string id);
    Task<string> CreateBatchRuleAsync(ArBatchRuleSummary rule);
    Task UpdateBatchRuleAsync(string id, ArBatchRuleSummary rule);
    Task<ArBatchRuleTestResult> TestBatchRuleAsync(string id, decimal sampleAmount);

    /// <summary>
    /// Member-scoped AR rollup consumed by the portal Member Details dialog
    /// (AR tab). Read-only; no payments initiated from this surface.
    /// </summary>
    Task<MemberArSummary?> GetMemberArSummaryAsync(string memberId);
}
