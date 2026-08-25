using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Posts a stored, matched 835 onto claim financials and member accumulators.
/// Does not invent remittances, change 277CA or 276/277, or reconcile EFT.
/// </summary>
public interface IRemittancePoster
{
    Task<RemittancePostResult> PostAsync(
        RemittancePostRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class RemittancePostRequest
{
    public string ReceiptId { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;
}

public sealed class RemittancePostResult
{
    public bool Replay { get; init; }

    public RemittanceLifecycleStatus Status { get; init; }

    public string RemittanceId { get; init; } = string.Empty;

    public string ReceiptId { get; init; } = string.Empty;

    public string TenantId { get; init; } = string.Empty;

    public int ClaimsPosted { get; init; }

    public int AccumulatorsApplied { get; init; }

    public GatewayErrorCategory ErrorCategory { get; init; }

    public string? ErrorMessage { get; init; }
}

public sealed class RemittanceClaimPost
{
    public string TenantId { get; init; } = string.Empty;

    public string ClaimId { get; init; } = string.Empty;

    public string RemittanceId { get; init; } = string.Empty;

    public decimal PaymentAmount { get; init; }

    public decimal PatientResponsibility { get; init; }

    public string? CheckNumber { get; init; }

    public DateTime PaymentDate { get; init; }

    public string? ControlNumber { get; init; }
}

public enum RemittanceClaimPostOutcome
{
    Posted = 0,
    AlreadyPosted,
    NotFound,
    Rejected,
    Failed
}

public sealed record RemittanceClaimPostResult(
    RemittanceClaimPostOutcome Outcome,
    string? ErrorMessage = null);

public sealed class RemittanceAccumulatorApply
{
    public string TenantId { get; init; } = string.Empty;

    public string MemberId { get; init; } = string.Empty;

    public string ClaimId { get; init; } = string.Empty;

    public string RemittanceId { get; init; } = string.Empty;

    public DateTime PlanYearStart { get; init; }

    public DateTime PlanYearEnd { get; init; }

    public decimal DeductibleDelta { get; init; }

    public decimal CopayDelta { get; init; }

    public decimal CoinsuranceDelta { get; init; }

    public decimal OopDelta { get; init; }
}

public enum RemittanceAccumulatorApplyOutcome
{
    Applied = 0,
    Duplicate,
    Skipped,
    Failed
}

public sealed record RemittanceAccumulatorApplyResult(
    RemittanceAccumulatorApplyOutcome Outcome,
    string? ErrorMessage = null);

public interface IClaimRemittancePostingSink
{
    Task<RemittanceClaimPostResult> PostAsync(
        RemittanceClaimPost request,
        CancellationToken cancellationToken = default);
}

public interface IRemittanceAccumulatorSink
{
    Task<RemittanceAccumulatorApplyResult> ApplyAsync(
        RemittanceAccumulatorApply request,
        CancellationToken cancellationToken = default);
}
