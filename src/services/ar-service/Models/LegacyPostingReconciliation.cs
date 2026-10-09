namespace ArService.Models;

/// <summary>
/// Finance's review of a cash posting that was applied before applying credited AR balances.
/// <para>
/// Before PR #1271, <c>cash-postings/{id}/apply</c> set the posting's status and
/// <c>AppliedAmount</c> but credited no <see cref="ArBalance"/>. Such a posting has
/// applications with an amount and no <see cref="CashApplication.PostedEntryId"/>. Applying it
/// now would credit them for the first time, and finance may already have corrected those
/// balances by hand. Until finance has decided per application, apply and void refuse it
/// (409 <c>LegacyPostingRequiresReconciliation</c>). The decision is carried out by
/// <c>tools/ArLegacyPostingReconciliation</c>; see
/// docs/operations/AR-LEGACY-POSTING-RECONCILIATION.md.
/// </para>
/// </summary>
public class LegacyPostingReconciliation
{
    public LegacyReconciliationStatus Status { get; set; } = LegacyReconciliationStatus.PendingReview;

    /// <summary>The finance reviewer named in the reviewed CSV.</summary>
    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }

    /// <summary>The finance ticket the review is recorded under.</summary>
    public string? Ticket { get; set; }
    public string? Note { get; set; }

    /// <summary>Who ran the reconciliation tool, and when.</summary>
    public string? ReconciledBy { get; set; }
    public DateTime? ReconciledAt { get; set; }

    /// <summary>SHA-256 of the reviewed CSV that was executed.</summary>
    public string? CsvSha256 { get; set; }

    /// <summary>The decision for each legacy application.</summary>
    public List<LegacyApplicationDecision> Applications { get; set; } = new();
}

public class LegacyApplicationDecision
{
    /// <summary>Index of the application in <see cref="CashPosting.Applications"/>.</summary>
    public int ApplicationIndex { get; set; }
    public string ArBalanceId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public LegacyReconciliationDecision Decision { get; set; }

    /// <summary><c>cash-{posting}-{index}</c> when credited, <c>manual-{posting}-{index}</c> when not.</summary>
    public string PostedEntryId { get; set; } = string.Empty;
}

public enum LegacyReconciliationStatus
{
    /// <summary>Not yet reviewed: apply and void are refused.</summary>
    PendingReview = 1,

    /// <summary>Every legacy application was decided and carried out.</summary>
    Reconciled = 2
}

public enum LegacyReconciliationDecision
{
    /// <summary>Finance already corrected the balance by hand: mark posted, credit nothing.</summary>
    CorrectedManually = 1,

    /// <summary>The balance was never corrected: credit it now, as apply does.</summary>
    ApplyCredit = 2
}
