namespace FhirService.Models;

/// <summary>
/// Result of auto-adjudication attempt by PasAutoAdjudicator.
/// </summary>
public class PasDecisionResult
{
    public bool HasDecision { get; set; }
    public string? Decision { get; set; }       // "approved" | "denied" | "modified"
    public string? DenialReasonCode { get; set; }
    public string? DenialReason { get; set; }
    public string? AuthorizationNumber { get; set; }
    public DateTime? EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public string? RuleName { get; set; }        // Which rule triggered the decision
    public long ElapsedMs { get; set; }

    /// <summary>
    /// Why a pended request was pended, when a rule says so; recorded as the
    /// record's pend reason. Null for the clinical-review default.
    /// </summary>
    public string? PendReason { get; set; }

    /// <summary>
    /// The line of business the deciding path evaluated (the record's); null
    /// when nothing was evaluated (disabled, time budget exceeded).
    /// </summary>
    public string? LineOfBusiness { get; set; }

    /// <summary>
    /// The service state (<c>Claim.item.locationAddress.state</c>) when the
    /// deciding path read it (the enrollment gate ran or the state rule engine
    /// was reached); null otherwise, including when the request named none.
    /// </summary>
    public string? StateCode { get; set; }

    /// <summary>
    /// For a <c>"modified"</c> decision: the preferred alternative certified in
    /// place of each requested service it covers.
    /// </summary>
    public List<PasAlternativeService>? Alternatives { get; set; }
}

/// <summary>A service certified in place of a requested one, and why.</summary>
public sealed record PasAlternativeService(PasPreferredAlternative Policy, FhirService.Services.PasProcedureCode Requested);

/// <summary>
/// A plan's preferred alternative for a service (site-of-care or
/// rent-not-buy policy): a request for <see cref="ServiceCode"/> is certified
/// as <see cref="AlternativeCode"/> instead, with the reason stated.
/// </summary>
public sealed class PasPreferredAlternative
{
    /// <summary>The requested service the policy covers.</summary>
    public string ServiceCode { get; set; } = string.Empty;

    /// <summary>Its code system; when absent, the code alone is matched.</summary>
    public string? ServiceCodeSystem { get; set; }

    public string AlternativeCode { get; set; } = string.Empty;
    public string? AlternativeSystem { get; set; }
    public string? AlternativeDisplay { get; set; }

    /// <summary>Why the requested service is not certified as asked.</summary>
    public string ReasonCode { get; set; } = "PREFERRED_ALTERNATIVE";
    public string Reason { get; set; } = string.Empty;

    public bool Covers(FhirService.Services.PasProcedureCode code)
        => string.Equals(code.Code, ServiceCode, StringComparison.Ordinal)
           && (string.IsNullOrEmpty(ServiceCodeSystem)
               || string.Equals(code.System, ServiceCodeSystem, StringComparison.Ordinal));
}

/// <summary>
/// Configuration for PAS auto-adjudication rules.
/// Bound from Cms0057:PasAutoAdjudication config section.
/// </summary>
public class PasAutoAdjudicationConfig
{
    public bool Enabled { get; set; } = true;
    public List<string> AutoApproveServiceTypes { get; set; } = new();
    public List<string> AutoDenyServiceTypes { get; set; } = new();
    public double GoldCardThreshold { get; set; } = 0.95;
    public decimal DollarThreshold { get; set; } = 500.00m;
    public int MaxResponseMs { get; set; } = 12000;

    /// <summary>
    /// The plan's preferred alternatives. A request every line of which one
    /// covers is certified as those alternatives (A2) instead of as asked.
    /// </summary>
    public List<PasPreferredAlternative> PreferredAlternatives { get; set; } = new();
}
