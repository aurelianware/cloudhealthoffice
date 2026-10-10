using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace ArService.Gl;

/// <summary>
/// What a journal line does in the chart: a posting role a tenant maps to one of its
/// <c>GlAccount</c> numbers (<c>GlPosting:Tenants:{tenant}:Accounts:{Role}</c>).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GlPostingRole
{
    /// <summary>Medical claims expense (incurred at payment-run execution).</summary>
    ClaimsExpense,

    /// <summary>Claims payable to providers (accrued at execution, relieved when the file is transmitted).</summary>
    ClaimsPayable,

    /// <summary>
    /// ACH in transit: the bank has the file, the money has not settled. Map it to the cash
    /// account until bank settlement events exist (then settlement moves it to <see cref="Cash"/>).
    /// </summary>
    AchInTransit,

    /// <summary>Provider overpayments receivable (from reversal runs; recovered by later payment offsets).</summary>
    ProviderReceivable,

    /// <summary>Operating cash (settlement seam: not posted to yet).</summary>
    Cash,
}

/// <summary>Why an entry exists.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GlEntryKind
{
    /// <summary>Payment run: Dr claims expense (gross), Cr claims payable (net), Cr provider receivable (offsets).</summary>
    ClaimsAccrual,

    /// <summary>Reversal run: Dr provider receivable, Cr claims expense.</summary>
    ClaimsRecoupment,

    /// <summary>NACHA file at the bank: Dr claims payable, Cr ACH in transit.</summary>
    AchTransmission,

    /// <summary>An operator's correction: the mirror image of an earlier entry.</summary>
    Reversal,
}

/// <summary>One side of one account. Exactly one of Debit / Credit is positive; amounts are money (2 decimals).</summary>
[BsonIgnoreExtraElements]
public sealed class GlJournalLine
{
    public int LineNumber { get; set; }
    public GlPostingRole Role { get; set; }
    public string AccountId { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;

    [BsonRepresentation(MongoDB.Bson.BsonType.Decimal128)] public decimal Debit { get; set; }
    [BsonRepresentation(MongoDB.Bson.BsonType.Decimal128)] public decimal Credit { get; set; }
    public string? Memo { get; set; }
}

/// <summary>
/// A balanced, append-only journal entry. The id is the entry's business key
/// (<see cref="GlJournal.KeyFor"/>: kind + source document), so an event delivered twice,
/// or two events about the same run, can never post twice. Never updated or deleted:
/// a correction is a new <see cref="GlEntryKind.Reversal"/> entry naming
/// <see cref="ReversesEntryId"/>.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class GlJournalEntry
{
    [BsonId] public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public GlEntryKind Kind { get; set; }

    /// <summary>The source event's id (for a reversal: the reversed entry's).</summary>
    public string SourceEventId { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;

    /// <summary>Payment run / reversal run id the entry is about.</summary>
    public string SourceDocumentId { get; set; } = string.Empty;

    /// <summary>Human reference (run number, file reference).</summary>
    public string SourceReference { get; set; } = string.Empty;

    /// <summary>The accounting (effective) date: decides the period.</summary>
    public DateTime EntryDate { get; set; }

    /// <summary>yyyy-MM of <see cref="EntryDate"/>.</summary>
    public string Period { get; set; } = string.Empty;

    /// <summary>When it was recorded here.</summary>
    public DateTime PostedAt { get; set; }

    /// <summary>The service or user that caused it.</summary>
    public string PostedBy { get; set; } = string.Empty;

    public string? LineOfBusiness { get; set; }
    public string Description { get; set; } = string.Empty;
    public List<GlJournalLine> Lines { get; set; } = new();

    [BsonRepresentation(MongoDB.Bson.BsonType.Decimal128)] public decimal TotalDebit { get; set; }
    [BsonRepresentation(MongoDB.Bson.BsonType.Decimal128)] public decimal TotalCredit { get; set; }

    /// <summary>For a <see cref="GlEntryKind.Reversal"/>: the entry it mirrors (unique: an entry is reversed at most once).</summary>
    public string? ReversesEntryId { get; set; }

    /// <summary>Why (reversals, re-dated parked events).</summary>
    public string? Reason { get; set; }
}

/// <summary>Where an ingested source event stands.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GlSourceEventStatus
{
    /// <summary>Its entry is in the journal.</summary>
    Posted,

    /// <summary>Held, never dropped: see <see cref="GlSourceEvent.ParkReason"/>. Retried by an operator.</summary>
    Parked,

    /// <summary>Nothing to post (e.g. every amount zero). Kept for the audit trail.</summary>
    NothingToPost,

    /// <summary>A parked event a finance user decided not to post, with a reason (e.g. a duplicate transmission).</summary>
    Dismissed,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GlParkReason
{
    /// <summary>A role has no account mapped, or the mapped account is missing, inactive or not effective.</summary>
    NeedsMapping,

    /// <summary>The entry date falls in a closed period.</summary>
    ClosedPeriod,

    /// <summary>The run already has an entry of this kind from another event (e.g. a second transmitted file for one run).</summary>
    DuplicateBusinessKey,

    /// <summary>The payload cannot be posted as is (amounts negative, not money, inconsistent totals).</summary>
    InvalidPayload,
}

/// <summary>
/// The register of every source event received (one per event id): its payload verbatim,
/// a hash of it, and what became of it. The register may change (parked → posted); the
/// journal never does.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class GlSourceEvent
{
    /// <summary>{tenant}:{eventId}.</summary>
    [BsonId] public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;

    /// <summary>SHA-256 of <see cref="PayloadJson"/>: a redelivery with another payload under the same id is refused.</summary>
    public string PayloadSha256 { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
    public GlSourceEventStatus Status { get; set; }
    public GlParkReason? ParkReason { get; set; }
    public string? ParkDetail { get; set; }
    public string? EntryId { get; set; }
    public int Attempts { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public string? LastAttemptBy { get; set; }

    /// <summary>The source document (run) and the totals the event claims, for reconciliation.</summary>
    public string SourceDocumentId { get; set; } = string.Empty;
    public string SourceReference { get; set; } = string.Empty;
    [BsonRepresentation(MongoDB.Bson.BsonType.Decimal128)] public decimal ClaimedAmount { get; set; }

    /// <summary>Optimistic concurrency for status changes.</summary>
    public string Version { get; set; } = string.Empty;

    public static string KeyFor(string tenantId, string eventId) => $"{tenantId}:{eventId}";
}

/// <summary>A closed accounting period (yyyy-MM) of a tenant. Closing is final here; nothing posts into it.</summary>
[BsonIgnoreExtraElements]
public sealed class GlClosedPeriod
{
    /// <summary>{tenant}:{yyyy-MM}.</summary>
    [BsonId] public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Period { get; set; } = string.Empty;
    public DateTime ClosedAt { get; set; }
    public string ClosedBy { get; set; } = string.Empty;
    public string? Reason { get; set; }

    public static string KeyFor(string tenantId, string period) => $"{tenantId}:{period}";
}
