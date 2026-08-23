namespace CloudHealthOffice.Infrastructure.Gateways.Models;

/// <summary>
/// Vendor-neutral outbound claim submission request (837-equivalent) handed to
/// an <see cref="Capabilities.IClaimSubmissionGateway"/>.
///
/// Independent of Stedi JSON, Availity payloads, and raw X12. A vendor adapter
/// translates this into the transport format it needs. This is a submission
/// projection — not the claims-service domain aggregate and not an
/// adjudication record.
/// </summary>
public sealed class GatewayClaimSubmissionRequest
{
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Cloud Health Office claim identifier (patient control number).</summary>
    public string ClaimId { get; set; } = string.Empty;

    /// <summary>
    /// Claim version / submission generation. Distinct versions (and frequency
    /// codes 7/8) produce distinct idempotency keys so corrected claims can
    /// be sent intentionally.
    /// </summary>
    public int ClaimVersion { get; set; } = 1;

    public GatewayClaimType ClaimType { get; set; } = GatewayClaimType.Professional;

    /// <summary>X12 CLM05-3: 1 original, 7 replacement, 8 void.</summary>
    public string FrequencyCode { get; set; } = "1";

    public string? PayerId { get; set; }

    public string? PayerName { get; set; }

    public GatewayClaimProvider? BillingProvider { get; set; }

    public GatewayClaimProvider? RenderingProvider { get; set; }

    public GatewayClaimProvider? ReferringProvider { get; set; }

    public GatewayEligibilityPerson? Subscriber { get; set; }

    public GatewayEligibilityPerson? Patient { get; set; }

    public string? GroupNumber { get; set; }

    public string PlaceOfServiceCode { get; set; } = "11";

    public DateOnly? ServiceDateFrom { get; set; }

    public DateOnly? ServiceDateTo { get; set; }

    public decimal TotalCharge { get; set; }

    public List<GatewayClaimDiagnosis> Diagnoses { get; set; } = new();

    public List<GatewayClaimLine> ServiceLines { get; set; } = new();

    public string? PriorAuthorizationNumber { get; set; }

    public string? ReferralNumber { get; set; }

    /// <summary>837I type of bill (e.g. 111). Required for institutional claims.</summary>
    public string? TypeOfBill { get; set; }

    public DateOnly? AdmissionDate { get; set; }

    public DateOnly? DischargeDate { get; set; }

    public string? PatientStatusCode { get; set; }

    public string? CorrelationId { get; set; }

    /// <summary>
    /// Optional caller-supplied idempotency key. When omitted the gateway
    /// derives one from tenant + claim id + version + type + frequency.
    /// </summary>
    public string? IdempotencyKey { get; set; }

    public string ResolveIdempotencyKey()
    {
        if (!string.IsNullOrWhiteSpace(IdempotencyKey))
        {
            return IdempotencyKey.Trim();
        }

        return $"{TenantId}|{ClaimId}|{ClaimVersion}|{ClaimType}|{FrequencyCode}";
    }

    public HealthcareTransactionType TransactionType() => ClaimType switch
    {
        GatewayClaimType.Institutional => HealthcareTransactionType.InstitutionalClaim837I,
        GatewayClaimType.Dental => HealthcareTransactionType.DentalClaim837D,
        _ => HealthcareTransactionType.ProfessionalClaim837P
    };
}

public enum GatewayClaimType
{
    Professional = 1,
    Institutional = 2,
    Dental = 3
}

public sealed class GatewayClaimProvider
{
    public string? Npi { get; set; }

    public string? OrganizationName { get; set; }

    public string? LastName { get; set; }

    public string? FirstName { get; set; }

    public string? EmployerId { get; set; }

    public string? TaxonomyCode { get; set; }

    public string? Address1 { get; set; }

    public string? Address2 { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public string? PostalCode { get; set; }

    public string? Phone { get; set; }

    public bool HasNpi => !string.IsNullOrWhiteSpace(Npi);
}

public sealed class GatewayClaimDiagnosis
{
    public string Code { get; set; } = string.Empty;

    /// <summary>ABK principal, ABF secondary.</summary>
    public string Qualifier { get; set; } = "ABK";

    public int PointerNumber { get; set; }
}

public sealed class GatewayClaimLine
{
    public int LineNumber { get; set; }

    public string ProcedureCode { get; set; } = string.Empty;

    public List<string> Modifiers { get; set; } = new();

    public List<int> DiagnosisPointers { get; set; } = new();

    public decimal Units { get; set; } = 1;

    public decimal ChargeAmount { get; set; }

    public DateOnly? ServiceDateFrom { get; set; }

    public DateOnly? ServiceDateTo { get; set; }

    public string? PlaceOfServiceCode { get; set; }

    /// <summary>837I revenue code.</summary>
    public string? RevenueCode { get; set; }

    /// <summary>837D tooth number, when modeled.</summary>
    public string? ToothNumber { get; set; }

    public string? ToothSurface { get; set; }

    public string? OralCavity { get; set; }

    public string? Quadrant { get; set; }

    public string LineItemControlNumber() => LineNumber > 0 ? LineNumber.ToString() : "1";
}
