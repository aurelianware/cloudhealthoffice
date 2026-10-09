using ArService.Models;

namespace ArService.Ledger;

/// <summary>
/// The cash-posting ledger rules shared by <c>CashPostingController</c> and the legacy
/// reconciliation tool (tools/ArLegacyPostingReconciliation): entry ids, the credit entry an
/// application posts, how an entry moves a balance, and which postings are legacy.
/// </summary>
public static class CashPostingLedger
{
    /// <summary>Error code returned (409) for a legacy posting finance has not reconciled.</summary>
    public const string LegacyPostingRequiresReconciliation = "LegacyPostingRequiresReconciliation";

    public const string RunbookPath = "docs/operations/AR-LEGACY-POSTING-RECONCILIATION.md";

    /// <summary>Prefix of the sentinel <see cref="CashApplication.PostedEntryId"/> for an
    /// application finance corrected by hand: no such entry exists on any balance.</summary>
    public const string ManualEntryPrefix = "manual-";

    /// <summary>The fixed id of the credit entry for application <paramref name="index"/>.</summary>
    public static string CreditEntryId(string postingId, int index) => $"cash-{postingId}-{index}";

    /// <summary>The sentinel posted id for an application corrected by hand (never credited here).</summary>
    public static string ManualEntryId(string postingId, int index) => $"{ManualEntryPrefix}{postingId}-{index}";

    public static bool IsManualEntryId(string? postedEntryId) =>
        postedEntryId != null && postedEntryId.StartsWith(ManualEntryPrefix, StringComparison.Ordinal);

    /// <summary>
    /// True for a posting applied by the code before PR #1271: it is
    /// <see cref="CashPostingStatus.PartiallyApplied"/> or <see cref="CashPostingStatus.Applied"/>,
    /// has at least one application with <c>AmountApplied &gt; 0</c>, and no application has a
    /// <see cref="CashApplication.PostedEntryId"/>.
    /// <para>
    /// The old code never set <c>PostedEntryId</c>; the current code sets it on every application
    /// with an amount when it applies. So a posting that was applied and carries no posted id at
    /// all was applied by the old code. A posting with some posted ids and some unposted
    /// applications was applied by the current code and later given more applications; those are
    /// not legacy and apply credits only the new ones. Pending postings were never applied, and
    /// voided ones can be neither applied nor voided again, so neither is legacy.
    /// </para>
    /// </summary>
    public static bool IsLegacy(CashPosting posting) =>
        (posting.Status == CashPostingStatus.PartiallyApplied || posting.Status == CashPostingStatus.Applied)
        && posting.Applications.Any(a => a.AmountApplied > 0)
        && posting.Applications.All(a => a.PostedEntryId == null);

    /// <summary>
    /// The applications of a legacy posting that need a decision: those with an amount.
    /// </summary>
    public static IEnumerable<(CashApplication Application, int Index)> LegacyApplications(CashPosting posting) =>
        posting.Applications.Select((a, i) => (a, i)).Where(x => x.a.AmountApplied > 0 && x.a.PostedEntryId == null);

    /// <summary>
    /// Apply and void must refuse this posting until finance has reconciled it. Reconciling
    /// gives every legacy application a posted id, so a reconciled posting is no longer legacy.
    /// </summary>
    public static bool RequiresLegacyReconciliation(CashPosting posting) => IsLegacy(posting);

    /// <summary>The credit entry application <paramref name="index"/> posts to its balance.</summary>
    public static ArPostingEntry CreditEntry(CashPosting posting, int index, string? postedBy, DateTime postedAt, string? memo = null)
    {
        var application = posting.Applications[index];
        return new ArPostingEntry
        {
            EntryId = CreditEntryId(posting.Id, index),
            Source = ArPostingSource.CashReceipt,
            SourceReferenceId = posting.Id,
            SourceReferenceNumber = posting.PostingNumber,
            CreditAmount = application.AmountApplied,
            PostedAt = postedAt,
            PostedBy = postedBy,
            Memo = memo ?? application.Memo,
            MemberId = posting.PayerType == PayerType.Member ? posting.PayerReferenceId : null
        };
    }

    /// <summary>
    /// Adds the entry to the balance and moves its totals: debits and credits,
    /// the sponsor or member split by payer, and the closing balance.
    /// </summary>
    public static void Post(ArBalance balance, ArPostingEntry entry, PayerType payerType)
    {
        balance.PostingEntries.Add(entry);
        balance.TotalDebits += entry.DebitAmount;
        balance.TotalCredits += entry.CreditAmount;
        var net = entry.DebitAmount - entry.CreditAmount;
        if (payerType == PayerType.Member)
        {
            balance.MemberDebits += entry.DebitAmount;
            balance.MemberCredits += entry.CreditAmount;
            balance.MemberBalance += net;
        }
        else if (payerType == PayerType.Sponsor)
        {
            balance.SponsorDebits += entry.DebitAmount;
            balance.SponsorCredits += entry.CreditAmount;
            balance.SponsorBalance += net;
        }
        balance.ClosingBalance = balance.OpeningBalance + balance.TotalDebits - balance.TotalCredits;
    }
}
