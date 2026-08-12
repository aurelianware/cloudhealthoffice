using System.Text.Json.Serialization;

namespace BenefitPlanService.Models.Estimate;

/// <summary>
/// How authoritative a payment estimate is.
///
/// <para>
/// Prospective adjudication normally returns <see cref="Simulation"/>: the
/// result is a read-only projection of what CHO's own engines expect, not a
/// payment guarantee. It is only reported as <see cref="AuthoritativePayer"/>
/// when the tenant's operating-mode configuration establishes CHO as the
/// authoritative adjudication system for the claim type/line of business.
/// <see cref="PayerEstimate"/> is reserved for a future source where an
/// external payer returns an estimate. Deliberately no "guaranteed payment"
/// value exists — an estimate is never a guarantee.
/// </para>
/// </summary>
public enum EstimateAuthority
{
    /// <summary>Read-only projection from CHO's simulation engines. Not a payment guarantee.</summary>
    Simulation,

    /// <summary>An estimate returned by an external payer connection (future).</summary>
    PayerEstimate,

    /// <summary>
    /// CHO is the authoritative adjudication engine for this claim type/LOB
    /// per tenant operating mode; the estimate reflects the same engine that
    /// will adjudicate the real claim (subject to eligibility/accumulator
    /// changes at claim time).
    /// </summary>
    AuthoritativePayer
}

/// <summary>Severity of an estimate message/reason.</summary>
public enum EstimateMessageSeverity
{
    /// <summary>Informational — explains how an amount was derived.</summary>
    Info,

    /// <summary>Warning — something may affect the final payment (e.g. prior auth).</summary>
    Warning,

    /// <summary>Denial — the line is not expected to pay as submitted.</summary>
    Denial
}

/// <summary>Deterministic, rule-based quality signal for the estimate.</summary>
public enum EstimateConfidenceLevel
{
    High,
    Medium,
    Low,
    InsufficientData
}

/// <summary>
/// Provider-facing prospective payment estimate. Every monetary field uses
/// <see cref="decimal"/> and claim-level totals equal the sum of the
/// corresponding line-level amounts.
/// </summary>
public record PaymentEstimateResponse
{
    /// <summary>Echoes <see cref="PaymentEstimateRequest.RequestId"/>.</summary>
    public string? RequestId { get; init; }

    /// <summary>
    /// Overall status: "estimated" when the estimate was produced, or
    /// "insufficient_data" when required inputs (e.g. the benefit plan)
    /// could not be resolved.
    /// </summary>
    public string Status { get; init; } = "estimated";

    /// <summary>How authoritative the estimate is. Normally <see cref="EstimateAuthority.Simulation"/>.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EstimateAuthority Authority { get; init; } = EstimateAuthority.Simulation;

    /// <summary>ISO currency code. Always "USD" today.</summary>
    public string Currency { get; init; } = "USD";

    /// <summary>Claim-level totals — the element-wise sum of the line amounts.</summary>
    public EstimateTotals Totals { get; init; } = new();

    /// <summary>Per-line estimate detail.</summary>
    public List<EstimateLine> Lines { get; init; } = [];

    /// <summary>Claim-level warnings (prior auth, provider integrity, missing data, …).</summary>
    public List<EstimateMessage> Warnings { get; init; } = [];

    /// <summary>Deterministic confidence/result-quality signal.</summary>
    public EstimateConfidence Confidence { get; init; } = new();

    /// <summary>Human-readable disclaimer. Always present.</summary>
    public string Disclaimer { get; init; } =
        "Estimate only. Final payment depends on eligibility, benefits, accumulators, " +
        "coordination of benefits, other claims, authorization state, and claim state " +
        "at adjudication time.";
}

/// <summary>Claim-level estimate totals.</summary>
public record EstimateTotals
{
    public decimal BilledAmount { get; init; }
    public decimal AllowedAmount { get; init; }
    public decimal ContractualAdjustment { get; init; }
    public decimal PayerResponsibility { get; init; }
    public decimal PatientResponsibility { get; init; }
    public decimal DeductibleAmount { get; init; }
    public decimal CopayAmount { get; init; }
    public decimal CoinsuranceAmount { get; init; }
}

/// <summary>Line-level estimate detail.</summary>
public record EstimateLine
{
    public int LineNumber { get; init; }
    public string ProcedureCode { get; init; } = default!;

    public decimal BilledAmount { get; init; }
    public decimal AllowedAmount { get; init; }
    public decimal ContractualAdjustment { get; init; }
    public decimal PayerResponsibility { get; init; }
    public decimal PatientResponsibility { get; init; }
    public decimal DeductibleAmount { get; init; }
    public decimal CopayAmount { get; init; }
    public decimal CoinsuranceAmount { get; init; }

    /// <summary>
    /// Line outcome: "payable", "not_covered", "denied", or "needs_review".
    /// </summary>
    public string Status { get; init; } = "payable";

    /// <summary>Echoed dental tooth number, when supplied on the request.</summary>
    public string? ToothNumber { get; init; }

    /// <summary>Structured, stable-coded reasons explaining this line's estimate.</summary>
    public List<EstimateMessage> Messages { get; init; } = [];
}

/// <summary>
/// A stable-coded, human-readable reason. Codes are intended to be stable
/// enough for a provider-facing UI to switch on; descriptions never contain
/// stack traces or internal implementation detail.
/// </summary>
public record EstimateMessage
{
    public string Code { get; init; } = default!;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EstimateMessageSeverity Severity { get; init; } = EstimateMessageSeverity.Info;

    public string Description { get; init; } = default!;
}

/// <summary>
/// Rule-based (not AI-derived) confidence signal. The level is derived
/// deterministically from the data actually available during the estimate.
/// </summary>
public record EstimateConfidence
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EstimateConfidenceLevel Level { get; init; } = EstimateConfidenceLevel.High;

    /// <summary>Positive facts that supported the estimate.</summary>
    public List<string> Reasons { get; init; } = [];

    /// <summary>Inputs that were missing or unresolved, lowering confidence.</summary>
    public List<string> MissingData { get; init; } = [];
}
