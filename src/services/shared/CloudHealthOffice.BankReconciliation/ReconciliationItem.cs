using MongoDB.Bson.Serialization.Attributes;

namespace CloudHealthOffice.BankReconciliation;

public enum ReconciliationStatus
{
    /// <summary>Claimed by a reconciliation run that has not finished deciding (crash recovery re-decides it after a lease).</summary>
    Pending,

    /// <summary>The bank line matched money CHO already recorded (an 820/lockbox payment, a NACHA file). Nothing new was posted.</summary>
    Matched,

    /// <summary>Strong match to one open invoice for its exact balance: applied through cash application.</summary>
    Applied,

    /// <summary>Waiting for a person: no match, or only a weak one. Never applied automatically.</summary>
    Suspense,

    /// <summary>An ACH return: always waits for a person.</summary>
    Return,

    /// <summary>Out of scope for this service (status codes, other transaction types, test groups).</summary>
    Ignored,

    /// <summary>A person resolved a suspense or return item.</summary>
    Resolved,

    /// <summary>A person dismissed a suspense or return item.</summary>
    Dismissed,
}

/// <summary>
/// What one service decided for one bank transaction. One per (service,
/// transaction), created once: re-ingesting a file never decides a line
/// twice. Items in <see cref="ReconciliationStatus.Suspense"/> or
/// <see cref="ReconciliationStatus.Return"/> are the work queue.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class ReconciliationItem
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string TransactionId { get; set; } = string.Empty;
    public string StatementId { get; set; } = string.Empty;

    public string TypeCode { get; set; } = string.Empty;
    public Bai2.Bai2Direction Direction { get; set; }
    public long AmountMinor { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public DateTime AsOfDate { get; set; }
    public string MaskedAccount { get; set; } = string.Empty;
    public string? BankReference { get; set; }
    public string? CustomerReference { get; set; }

    /// <summary>What kind of line this is to the service: PremiumCash, LockboxDeposit, NachaSettlement, AchReturn, Other.</summary>
    public string Kind { get; set; } = string.Empty;

    public ReconciliationStatus Status { get; set; }

    /// <summary>Why it is in suspense, or how it matched.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>What it matched: RemittanceBatch, PremiumInvoice, PaymentFileTransmission.</summary>
    public string? MatchedEntityType { get; set; }

    public List<string> MatchedEntityIds { get; set; } = [];

    /// <summary>Possible matches shown to the person working a suspense item (never applied automatically).</summary>
    public List<string> Candidates { get; set; } = [];

    public DateTime CreatedAt { get; set; }

    /// <summary>When the item's event reached the <see cref="IBankReconciliationEventSink"/>; null while pending.</summary>
    public DateTime? EventPublishedAt { get; set; }

    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
    public string? ResolutionNote { get; set; }

    /// <summary>Changes on every write; updates are conditional on it.</summary>
    public string Version { get; set; } = Guid.NewGuid().ToString("N");

    public bool IsOpen => Status is ReconciliationStatus.Suspense or ReconciliationStatus.Return;

    public static string IdFor(string service, string transactionId) => $"{service}:{transactionId}";

    /// <summary>A copy whose lists are its own.</summary>
    public ReconciliationItem Copy()
    {
        var copy = (ReconciliationItem)MemberwiseClone();
        copy.MatchedEntityIds = [.. MatchedEntityIds];
        copy.Candidates = [.. Candidates];
        return copy;
    }
}
