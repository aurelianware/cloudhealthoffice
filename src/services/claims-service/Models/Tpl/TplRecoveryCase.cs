using MongoDB.Bson.Serialization.Attributes;

namespace ClaimsService.Models.Tpl;

/// <summary>
/// One Medicaid TPL recovery: money the plan paid on a claim that another
/// payer owed, to be recovered from that payer (or the provider). Opened for
/// pay-and-chase lines when a claim is paid, and for paid claims that TPL
/// found later covers retroactively. One case per claim and origin.
/// </summary>
public sealed class TplRecoveryCase
{
    [BsonId]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string TenantId { get; set; } = string.Empty;

    /// <summary>Idempotency key: claim id plus origin.</summary>
    public string CaseKey { get; set; } = string.Empty;

    public string ClaimId { get; set; } = string.Empty;
    public string? ClaimNumber { get; set; }
    public string MemberId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;

    public TplRecoveryOrigin Origin { get; set; }

    public List<TplRecoveryLine> Lines { get; set; } = new();

    /// <summary>The TPL resources to recover from (coverage-service ids and carriers).</summary>
    public List<TplRecoveryResource> Resources { get; set; } = new();

    public DateTime ServiceDateFrom { get; set; }
    public DateTime PaymentDate { get; set; }

    /// <summary>What Medicaid paid on the lines: the amount to recover.</summary>
    public decimal AmountPaid { get; set; }

    public decimal AmountRecovered { get; set; }

    public decimal AmountOutstanding => Math.Max(0m, AmountPaid - AmountRecovered - AmountWrittenOff);

    public decimal AmountWrittenOff { get; set; }

    public TplRecoveryStatus Status { get; set; } = TplRecoveryStatus.Open;

    /// <summary>The last day the plan may keep what it recovers (California:
    /// 12 months after payment, APL 21-002). Null when the state sets no window.</summary>
    public DateTime? RetentionDeadline { get; set; }

    /// <summary>When the carrier or provider agreed to a repayment plan.
    /// Agreed on or before <see cref="RetentionDeadline"/>, the plan keeps
    /// recoveries after the deadline too.</summary>
    public DateTime? RepaymentPlanAgreedAt { get; set; }

    public DateTime? TransferredToStateAt { get; set; }

    public List<TplRecoveryEvent> Events { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool HasActiveRepaymentPlan =>
        RepaymentPlanAgreedAt is { } agreed && (RetentionDeadline is null || agreed.Date <= RetentionDeadline.Value.Date);

    public bool IsClosed => Status is TplRecoveryStatus.Recovered or TplRecoveryStatus.WrittenOff or TplRecoveryStatus.TransferredToState;
}

public sealed class TplRecoveryLine
{
    public int LineNumber { get; set; }
    public string? ProcedureCode { get; set; }
    public decimal PaidAmount { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class TplRecoveryResource
{
    public string TplPolicyId { get; set; } = string.Empty;
    public string CarrierName { get; set; } = string.Empty;
    public string? CarrierCode { get; set; }
    public string? PolicyNumber { get; set; }
}

public sealed class TplRecoveryEvent
{
    public TplRecoveryEventType Type { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    public decimal? Amount { get; set; }
    public string? Reference { get; set; }
    public string? Note { get; set; }
    public string? By { get; set; }
}

public enum TplRecoveryOrigin
{
    /// <summary>Paid under a pay-and-chase rule at adjudication.</summary>
    PayAndChase,
    /// <summary>TPL found after payment that covered the service date.</summary>
    Retroactive,
}

public enum TplRecoveryStatus
{
    Open,
    /// <summary>A bill or recovery request went to the carrier or provider.</summary>
    Billed,
    PartiallyRecovered,
    Recovered,
    WrittenOff,
    /// <summary>The plan's window closed; the state recovers and keeps the money
    /// (California: DHCS TPLRD from month 13).</summary>
    TransferredToState,
}

public enum TplRecoveryEventType
{
    Opened,
    Billed,
    PaymentReceived,
    RepaymentPlanAgreed,
    CarrierDenied,
    WrittenOff,
    TransferredToState,
    Note,
}
