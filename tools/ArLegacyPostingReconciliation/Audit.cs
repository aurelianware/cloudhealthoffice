using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace ArLegacyPostingReconciliation;

/// <summary>
/// One legacy application as one execute run handled it, in collection
/// <c>ar_legacy_reconciliation_audit</c> of the tenant's database.
/// <para>
/// Append-only: the id is per run (<c>{runId}:{tenant}:{posting}:{index}</c>), so a later run
/// never touches an earlier run's record; the record is inserted before anything changes and
/// afterwards only gains <see cref="Events"/> (and the summary fields <see cref="State"/>,
/// <see cref="Credited"/>, <see cref="After"/> and <see cref="CompletedAt"/>). The credit is
/// recorded as intended before the balance is saved and as made immediately after the save,
/// so a run that stops anywhere leaves a record of what it did.
/// </para>
/// </summary>
[BsonIgnoreExtraElements]
public sealed class LegacyReconciliationAudit
{
    public const string Collection = "ar_legacy_reconciliation_audit";

    public static string IdFor(string runId, string tenantId, string postingId, int index) => $"{runId}:{tenantId}:{postingId}:{index}";

    [BsonId] public string Id { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public AuditState State { get; set; }
    public string Operator { get; set; } = string.Empty;
    public DateTime At { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string CsvSha256 { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string Database { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string PostingId { get; set; } = string.Empty;
    public string PostingNumber { get; set; } = string.Empty;
    public int ApplicationIndex { get; set; }
    public string ArBalanceId { get; set; } = string.Empty;
    /// <summary>The balance did not exist (only a CORRECTED_MANUALLY decision is carried out then).</summary>
    public bool BalanceMissing { get; set; }
    public string Decision { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string PostedEntryId { get; set; } = string.Empty;
    /// <summary>True once this run's save of the credit entry succeeded; never set back.</summary>
    public bool Credited { get; set; }
    public BalanceSnapshot? Before { get; set; }
    public BalanceSnapshot? After { get; set; }
    public string? Reviewer { get; set; }
    public string? ReviewedAt { get; set; }
    public string? Ticket { get; set; }
    public string? Note { get; set; }
    /// <summary>What happened, in order. Only ever appended to.</summary>
    public List<AuditEvent> Events { get; set; } = new();
}

public enum AuditState { Intended = 1, Completed = 2, Failed = 3 }

public sealed class AuditEvent
{
    /// <summary>Recorded before anything changed.</summary>
    public const string Intended = "Intended";
    /// <summary>About to save the balance with the credit entry.</summary>
    public const string CreditIntended = "CreditIntended";
    /// <summary>The balance save with the credit entry succeeded.</summary>
    public const string Credited = "Credited";
    /// <summary>The balance was changed by someone else meanwhile; re-read and retried.</summary>
    public const string ConcurrencyRetry = "ConcurrencyRetry";
    /// <summary>The credit entry was already on the balance (an earlier, interrupted run made it).</summary>
    public const string AlreadyOnBalance = "AlreadyOnBalance";
    /// <summary>The posting was saved with its posted ids and reconciliation record.</summary>
    public const string PostingSaved = "PostingSaved";
    /// <summary>The posting changed while being reconciled and was not saved.</summary>
    public const string PostingSaveConflict = "PostingSaveConflict";
    /// <summary>An error stopped the run for this posting.</summary>
    public const string Failed = "Failed";
    /// <summary>A later run found the posting reconciled by this record's run.</summary>
    public const string ConfirmedByLaterRun = "ConfirmedByLaterRun";

    public DateTime At { get; set; }
    public string Event { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public BalanceSnapshot? Balance { get; set; }

    public static AuditEvent Of(string @event, string? detail = null, BalanceSnapshot? balance = null) =>
        new() { At = DateTime.UtcNow, Event = @event, Detail = detail, Balance = balance };
}

public sealed class BalanceSnapshot
{
    public decimal ClosingBalance { get; set; }
    public decimal TotalCredits { get; set; }
    public decimal SponsorBalance { get; set; }
    public decimal MemberBalance { get; set; }
    public long Version { get; set; }

    public static BalanceSnapshot Of(ArService.Models.ArBalance b) => new()
    {
        ClosingBalance = b.ClosingBalance, TotalCredits = b.TotalCredits,
        SponsorBalance = b.SponsorBalance, MemberBalance = b.MemberBalance, Version = b.Version
    };

    public override string ToString() =>
        $"closing {ClosingBalance} credits {TotalCredits} (sponsor {SponsorBalance}, member {MemberBalance})";
}

/// <summary>
/// One execute run, in collection <c>ar_legacy_reconciliation_runs</c> of the base database.
/// Inserted when the run starts and appended to as it goes (<see cref="Actions"/> after every
/// balance save, <see cref="Postings"/> after every posting), so a run that stops part-way
/// still shows what it did.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class LegacyReconciliationRun
{
    public const string Collection = "ar_legacy_reconciliation_runs";

    [BsonId] public string Id { get; set; } = string.Empty;
    public RunState State { get; set; }
    public string Operator { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string Database { get; set; } = string.Empty;
    public bool UseTenantScoping { get; set; }
    public string CsvSha256 { get; set; } = string.Empty;
    public string CsvFileName { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? LogFile { get; set; }
    /// <summary>The ar-service build the capability marker named when the run started.</summary>
    public string? ServiceBuild { get; set; }
    /// <summary>The posting being worked on; cleared when its outcome is recorded.</summary>
    public string? CurrentPosting { get; set; }
    public List<RunAction> Actions { get; set; } = new();
    public List<PostingOutcome> Postings { get; set; } = new();

    public static UpdateDefinitionBuilder<LegacyReconciliationRun> Update => Builders<LegacyReconciliationRun>.Update;
}

public enum RunState { Running = 1, Finished = 2 }

/// <summary>A balance change the run made.</summary>
public sealed class RunAction
{
    public DateTime At { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string PostingId { get; set; } = string.Empty;
    public string ArBalanceId { get; set; } = string.Empty;
    public List<string> EntryIds { get; set; } = new();
    public decimal Amount { get; set; }
    public BalanceSnapshot? After { get; set; }
}

public sealed class PostingOutcome
{
    public string TenantId { get; set; } = string.Empty;
    public string PostingId { get; set; } = string.Empty;
    public OutcomeKind Outcome { get; set; }
    public List<string> Reasons { get; set; } = new();
}

public enum OutcomeKind
{
    /// <summary>Would be (dry-run) or was (execute) reconciled.</summary>
    Reconciled = 1,
    /// <summary>Already reconciled with the same decisions: nothing to do.</summary>
    AlreadyReconciled = 2,
    /// <summary>No decision in the CSV: left for later.</summary>
    Skipped = 3,
    /// <summary>The CSV does not match the live data, or the decision cannot be carried out.</summary>
    Refused = 4,
    /// <summary>Execution failed part-way; safe to run again.</summary>
    Failed = 5
}
