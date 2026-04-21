namespace ArService.Models;

/// <summary>
/// Read-only member-scoped AR rollup consumed by the portal Member Details
/// dialog. Aggregates across all <see cref="ArBalance"/> documents for the
/// tenant, filtering <see cref="ArPostingEntry"/> rows by MemberId.
/// </summary>
public class MemberArSummary
{
    public string MemberId { get; set; } = string.Empty;

    /// <summary>
    /// Sum of (debits - credits) across all posting entries tagged to this
    /// member. Positive means the member owes; negative means credit balance.
    /// </summary>
    public decimal CurrentBalance { get; set; }

    public AgedBuckets Aged { get; set; } = new();

    /// <summary>Most recent charges (debits), up to <c>RecentLimit</c>.</summary>
    public List<ArChargeRow> RecentCharges { get; set; } = new();

    /// <summary>Most recent payments (credits), up to <c>RecentLimit</c>.</summary>
    public List<ArPaymentRow> RecentPayments { get; set; } = new();

    public DateTime AsOfUtc { get; set; } = DateTime.UtcNow;

    public const int RecentLimit = 10;
}

/// <summary>
/// Aging buckets matching the member-linkage-tabs spec:
/// 0-30 / 31-60 / 61-90 / 91+ days outstanding. The model's per-balance
/// buckets (<see cref="ArBalance.Current"/>, etc.) get rolled into these at
/// member granularity.
/// </summary>
public class AgedBuckets
{
    public decimal Bucket0_30 { get; set; }
    public decimal Bucket31_60 { get; set; }
    public decimal Bucket61_90 { get; set; }
    public decimal Bucket91Plus { get; set; }

    public decimal Total => Bucket0_30 + Bucket31_60 + Bucket61_90 + Bucket91Plus;
}

public class ArChargeRow
{
    public string EntryId { get; set; } = string.Empty;
    public DateTime PostedAt { get; set; }
    public decimal Amount { get; set; }
    public ArPostingSource Source { get; set; }
    public string? SourceReferenceNumber { get; set; }
    public string? Memo { get; set; }
}

public class ArPaymentRow
{
    public string EntryId { get; set; } = string.Empty;
    public DateTime PostedAt { get; set; }
    public decimal Amount { get; set; }
    public ArPostingSource Source { get; set; }
    public string? SourceReferenceNumber { get; set; }
    public string? Memo { get; set; }
}
