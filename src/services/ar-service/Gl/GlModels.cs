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

    /// <summary>The unreversed remainder of an excluded payment paid another way: Dr claims payable, Cr cash.</summary>
    RemainderReissue,

    /// <summary>An operator's correction: the mirror image of an earlier entry.</summary>
    Reversal,

    /// <summary>
    /// A payment reversed before its NACHA file was sent and left out of the re-pinned file:
    /// Dr claims payable (never to be paid), Cr provider receivable (the receivable its
    /// reversal run opened is cancelled; the recoupment already reversed the expense).
    /// </summary>
    PaymentVoid,

    /// <summary>Bank-confirmed settlement: Dr ACH in transit, Cr cash.</summary>
    AchSettlement,
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

    /// <summary>Why (reversals, re-dated parked events, reposts).</summary>
    public string? Reason { get; set; }

    /// <summary>
    /// For a repost (<c>{key}:r{n}</c>): the entry of the same business key it replaces, which
    /// was reversed first. Null for the first entry of a key.
    /// </summary>
    public string? RepostOf { get; set; }
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

    /// <summary>
    /// The run was executed before the tenant's GL cutover (<c>GlPosting:Tenants:{t}:CutoverDate</c>):
    /// its accounting is in the opening balances. Registered and visible, never posted
    /// unless a finance user retries it with includeBeforeCutover and a reason.
    /// </summary>
    PreCutover,

    /// <summary>
    /// Not posted automatically (ProviderRefundOwed): an open item in the reconciliation until
    /// finance posts it by hand and dismisses the event with a reason naming the posting.
    /// </summary>
    ManualPostingRequired,

    /// <summary>
    /// It depends on another event that has not settled yet (a void on its recoupment, a
    /// transmission without a run date on the run's accrual): re-checked automatically when that
    /// event posts or parks PreCutover (<see cref="GlSourceEvent.WaitingForEventId"/>).
    /// </summary>
    AwaitingSourceEvent,
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

    /// <summary>While parked AwaitingSourceEvent: the event whose settling re-checks this one.</summary>
    public string? WaitingForEventId { get; set; }
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

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GlPeriodStatus
{
    Open,
    Closed,
}

/// <summary>A post in flight in a period: the close waits for it (or for its expiry).</summary>
[BsonIgnoreExtraElements]
public sealed class GlPostLease
{
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
}

/// <summary>
/// An accounting period (yyyy-MM) of a tenant. A post reserves a lease in it while the
/// period is open and releases it after its write; the close is one conditional write that
/// succeeds only while no live lease is held, so a post and the close serialize: a post
/// either lands before the close or is refused (parked) after it. Closing is final here.
/// At close the period's extract control (SHA-256 and totals) is recorded; a later extract
/// that differs is refused as an integrity failure.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class GlPeriod
{
    /// <summary>{tenant}:{yyyy-MM}.</summary>
    [BsonId] public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Period { get; set; } = string.Empty;
    public GlPeriodStatus Status { get; set; }
    public List<GlPostLease> Leases { get; set; } = new();
    public DateTime? ClosedAt { get; set; }
    public string? ClosedBy { get; set; }
    public string? Reason { get; set; }

    /// <summary>The extract's SHA-256 recorded at close (<see cref="GlExtractControl.Sha256"/>).</summary>
    public string? ExtractSha256 { get; set; }
    public int ExtractEntryCount { get; set; }
    public int ExtractLineCount { get; set; }
    [BsonRepresentation(MongoDB.Bson.BsonType.Decimal128)] public decimal ExtractTotalDebit { get; set; }
    [BsonRepresentation(MongoDB.Bson.BsonType.Decimal128)] public decimal ExtractTotalCredit { get; set; }

    public static string KeyFor(string tenantId, string period) => $"{tenantId}:{period}";
}

public enum GlPeriodCloseOutcome
{
    Closed,
    AlreadyClosed,

    /// <summary>A post holds a live lease in the period: nothing changed; try again.</summary>
    Busy,
}
