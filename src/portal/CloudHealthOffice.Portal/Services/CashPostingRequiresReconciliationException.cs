namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// ar-service refused to apply or void a cash posting (409
/// <c>LegacyPostingRequiresReconciliation</c>): it was applied before applying credited AR
/// balances, and finance must reconcile it first. The service is up; this is not an outage.
/// </summary>
public sealed class CashPostingRequiresReconciliationException : InvalidOperationException
{
    public const string Code = "LegacyPostingRequiresReconciliation";

    public string PostingId { get; }

    public CashPostingRequiresReconciliationException(string postingId, string action)
        : base($"This cash posting cannot be {action} yet: it was applied before AR balances were credited on apply, " +
               "and finance must reconcile it first. Ask the AR team to complete the legacy cash posting reconciliation for it.")
    {
        PostingId = postingId;
    }
}
