namespace CloudHealthOffice.BenchmarkClaimGenerator.Models;

/// <summary>
/// Pre-computed expected adjudication outcome for a synthetic claim.
/// Used as the answer key for the Million Claim Challenge benchmark.
/// </summary>
public class ExpectedOutcome
{
    /// <summary>Expected claim-level disposition (Paid, Denied, Pended).</summary>
    public string Disposition { get; set; } = string.Empty;

    /// <summary>CARC/RARC denial reason code if denied.</summary>
    public string? DenialReasonCode { get; set; }

    /// <summary>Expected allowed amount after fee schedule application.</summary>
    public decimal ExpectedAllowedAmount { get; set; }

    /// <summary>Expected paid amount after member cost-sharing.</summary>
    public decimal ExpectedPaidAmount { get; set; }

    /// <summary>Total expected member liability.</summary>
    public decimal ExpectedMemberLiability { get; set; }

    /// <summary>Expected copay amount.</summary>
    public decimal ExpectedCopay { get; set; }

    /// <summary>Expected coinsurance amount.</summary>
    public decimal ExpectedCoinsurance { get; set; }

    /// <summary>Expected deductible amount.</summary>
    public decimal ExpectedDeductible { get; set; }

    /// <summary>Expected DRG code (for institutional claims).</summary>
    public string? ExpectedDrgCode { get; set; }

    /// <summary>Per-line expected outcomes.</summary>
    public List<LineOutcome> LineOutcomes { get; set; } = new();

    /// <summary>Whether the claim is expected to be FHIR-compliant per CMS-0057-F.</summary>
    public bool ExpectedFhirCompliant { get; set; }

    /// <summary>Expected prior authorization decision (Approved, Denied, N/A).</summary>
    public string ExpectedPriorAuthDecision { get; set; } = "N/A";
}

/// <summary>
/// Expected adjudication outcome for a single claim line.
/// </summary>
public class LineOutcome
{
    /// <summary>Line number (1-based).</summary>
    public int LineNumber { get; set; }

    /// <summary>Line-level disposition (Paid, Denied, Pended).</summary>
    public string Disposition { get; set; } = string.Empty;

    /// <summary>Allowed amount for this line.</summary>
    public decimal AllowedAmount { get; set; }

    /// <summary>Paid amount for this line.</summary>
    public decimal PaidAmount { get; set; }

    /// <summary>CARC/RARC reason code for this line.</summary>
    public string? ReasonCode { get; set; }
}
