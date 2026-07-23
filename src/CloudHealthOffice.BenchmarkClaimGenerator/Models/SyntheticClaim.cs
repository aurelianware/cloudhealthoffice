namespace CloudHealthOffice.BenchmarkClaimGenerator.Models;

/// <summary>
/// Core model for a synthetic healthcare claim in the Million Claim Challenge corpus.
/// Each claim includes pre-computed expected adjudication outcomes for benchmarking.
/// </summary>
public class SyntheticClaim
{
    /// <summary>Unique claim identifier in format MCC-{type}-{seq:D7}.</summary>
    public string ClaimId { get; set; } = string.Empty;

    /// <summary>Claim type: Professional, Institutional, Dental, or EdgeCase.</summary>
    public string ClaimType { get; set; } = string.Empty;

    /// <summary>Edge case scenario type, null for non-edge-case claims.</summary>
    public EdgeCaseScenario? EdgeCase { get; set; }

    /// <summary>Date of service (or admission date for institutional).</summary>
    public DateTime DateOfService { get; set; }

    /// <summary>Date the claim was received by the payer.</summary>
    public DateTime DateReceived { get; set; }

    /// <summary>Member/subscriber information.</summary>
    public SyntheticMember Member { get; set; } = null!;

    /// <summary>Rendering provider (the provider who performed the service).</summary>
    public SyntheticProvider RenderingProvider { get; set; } = null!;

    /// <summary>Billing provider (the entity submitting the claim).</summary>
    public SyntheticProvider BillingProvider { get; set; } = null!;

    /// <summary>Place of service code (e.g., 11=Office, 21=Inpatient, 02=Telehealth).</summary>
    public string PlaceOfService { get; set; } = string.Empty;

    /// <summary>Benefit plan identifier.</summary>
    public string BenefitPlanId { get; set; } = string.Empty;

    /// <summary>Service lines on this claim.</summary>
    public List<ClaimLine> Lines { get; set; } = new();

    /// <summary>Primary ICD-10 diagnosis code.</summary>
    public string PrimaryDiagnosisCode { get; set; } = string.Empty;

    /// <summary>Secondary ICD-10 diagnosis codes.</summary>
    public List<string> SecondaryDiagnosisCodes { get; set; } = new();

    /// <summary>Frequency/type of bill code (for institutional claims).</summary>
    public string? FrequencyCode { get; set; }

    /// <summary>Bill type (for institutional claims, e.g., 0111).</summary>
    public string? BillType { get; set; }

    /// <summary>DRG code (for inpatient claims).</summary>
    public string? DrgCode { get; set; }

    /// <summary>Total billed charges across all lines.</summary>
    public decimal TotalCharges { get; set; }

    // CMS-0057-F Compliance Fields

    /// <summary>Prior authorization status (Required, OnFile, NotRequired, Expired).</summary>
    public string PriorAuthStatus { get; set; } = "NotRequired";

    /// <summary>Prior authorization number if applicable.</summary>
    public string? PriorAuthNumber { get; set; }

    /// <summary>Whether a FHIR resource has been generated for CMS-0057-F compliance.</summary>
    public bool FhirResourceGenerated { get; set; }

    /// <summary>Whether this claim is ready for payer-to-payer data exchange.</summary>
    public bool PayerToPayerReady { get; set; }

    /// <summary>
    /// X12 837 CLM11-1 related-causes code (AA=Auto Accident, EM=Employment,
    /// OA=Other Accident). Null when the service is unrelated to any
    /// accident/injury liability. Signals potential third-party/subrogation
    /// liability requiring investigation before payment.
    /// </summary>
    public string? RelatedCausesCode { get; set; }

    /// <summary>X12 837 DTP*439 accident date. Set only when <see cref="RelatedCausesCode"/> is set.</summary>
    public DateTime? AccidentDate { get; set; }

    /// <summary>Pre-computed expected adjudication outcome for benchmarking.</summary>
    public ExpectedOutcome ExpectedOutcome { get; set; } = null!;
}
