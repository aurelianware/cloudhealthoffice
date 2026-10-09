using MongoDB.Bson.Serialization.Attributes;

namespace ArLegacyPostingReconciliation;

/// <summary>
/// One reconciled legacy application, in collection <c>ar_legacy_reconciliation_audit</c> of the
/// tenant's database. The id is fixed per application, so there is one record per application
/// however often the tool runs. It is written as <see cref="AuditState.Intended"/> before
/// anything changes and set to <see cref="AuditState.Completed"/> once the posting is saved.
/// </summary>
public sealed class LegacyReconciliationAudit
{
    public const string Collection = "ar_legacy_reconciliation_audit";

    public static string IdFor(string tenantId, string postingId, int index) => $"{tenantId}:{postingId}:{index}";

    [BsonId] public string Id { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public AuditState State { get; set; }
    public string Operator { get; set; } = string.Empty;
    public DateTime At { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string CsvSha256 { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string PostingId { get; set; } = string.Empty;
    public string PostingNumber { get; set; } = string.Empty;
    public int ApplicationIndex { get; set; }
    public string ArBalanceId { get; set; } = string.Empty;
    public string Decision { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string PostedEntryId { get; set; } = string.Empty;
    /// <summary>True when this run added the credit entry; false when it was already on the balance or the decision credits nothing.</summary>
    public bool Credited { get; set; }
    public BalanceSnapshot? Before { get; set; }
    public BalanceSnapshot? After { get; set; }
    public string? Reviewer { get; set; }
    public string? ReviewedAt { get; set; }
    public string? Ticket { get; set; }
    public string? Note { get; set; }
}

public enum AuditState { Intended = 1, Completed = 2 }

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

/// <summary>One execute run, in collection <c>ar_legacy_reconciliation_runs</c> of the base database.</summary>
public sealed class LegacyReconciliationRun
{
    public const string Collection = "ar_legacy_reconciliation_runs";

    [BsonId] public string Id { get; set; } = string.Empty;
    public string Operator { get; set; } = string.Empty;
    public string CsvSha256 { get; set; } = string.Empty;
    public string CsvFileName { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? LogFile { get; set; }
    public List<PostingOutcome> Postings { get; set; } = new();
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
