using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Responders.Models;

/// <summary>
/// Vendor-neutral inbound eligibility inquiry (270-equivalent) received when
/// Cloud Health Office is the payer / information source.
///
/// Independent of Stedi JSON, Availity payloads, raw X12, and FHIR
/// CoverageEligibilityRequest. Transport adapters translate an external
/// format into this shape; no vendor DTO is substituted for it.
///
/// <see cref="ClaimedTenantId"/> is untrusted caller input and is never used
/// to select a tenant. Routing uses <see cref="PayerId"/>,
/// <see cref="TradingPartnerId"/>, and <see cref="AuthenticatedEndpointId"/>.
/// </summary>
public sealed class PayerEligibilityInquiry
{
    /// <summary>Caller-supplied transaction id, when present. Echoed on the response.</summary>
    public string? TransactionId { get; set; }

    /// <summary>Inbound correlation / trace id. Echoed on the response.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>External network transaction id, when the adapter has one.</summary>
    public string? ExternalTransactionId { get; set; }

    /// <summary>
    /// Tenant id asserted by the caller. Ignored for routing; never trusted.
    /// </summary>
    public string? ClaimedTenantId { get; set; }

    /// <summary>External payer identifier as known to the submitting network.</summary>
    public string? PayerId { get; set; }

    public string? PayerName { get; set; }

    /// <summary>Trading-partner / interchange identifier, when distinct from <see cref="PayerId"/>.</summary>
    public string? TradingPartnerId { get; set; }

    /// <summary>
    /// Identity of the authenticated inbound endpoint / integration. Set by the
    /// transport adapter from its trust boundary, never from the request body.
    /// </summary>
    public string? AuthenticatedEndpointId { get; set; }

    /// <summary>Adapter that produced this inquiry (e.g. "canonical", "x12"). Non-PHI.</summary>
    public string? AdapterName { get; set; }

    public PayerEligibilityProvider? RequestingProvider { get; set; }

    /// <summary>Policyholder / insured. Required for a valid inquiry.</summary>
    public GatewayEligibilityPerson? Subscriber { get; set; }

    /// <summary>
    /// Person receiving services. Omit or mark
    /// <see cref="GatewayEligibilityPerson.Relationship.Self"/> when the
    /// subscriber is the patient.
    /// </summary>
    public GatewayEligibilityPerson? Patient { get; set; }

    /// <summary>X12 service type codes. Default is 30 (health benefit plan coverage).</summary>
    public List<string> ServiceTypeCodes { get; set; } = new() { ServiceTypeCode.HealthBenefitPlanCoverage };

    /// <summary>
    /// Date of service the eligibility is evaluated against. Required; left
    /// at <c>default</c> when omitted so the responder can reject a missing
    /// date rather than silently substituting today.
    /// </summary>
    public DateOnly DateOfService { get; set; }

    public PayerEligibilitySourceMetadata? SourceMetadata { get; set; }

    public bool IsDependentInquiry()
    {
        var patient = Patient;
        if (patient is null || !patient.HasIdentity || patient.IsSelf)
        {
            return false;
        }

        var subscriberId = Subscriber?.MemberId;
        var sameMemberId = !string.IsNullOrWhiteSpace(patient.MemberId) &&
            string.Equals(patient.MemberId, subscriberId, StringComparison.OrdinalIgnoreCase);
        var sameName = string.Equals(patient.FirstName, Subscriber?.FirstName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(patient.LastName, Subscriber?.LastName, StringComparison.OrdinalIgnoreCase);
        var sameDob = patient.DateOfBirth is { } pdob &&
            Subscriber?.DateOfBirth is { } sdob &&
            pdob == sdob;

        if (sameMemberId && (sameName || (patient.DateOfBirth is null && Subscriber?.DateOfBirth is null)))
        {
            return false;
        }

        if (sameName && sameDob && string.IsNullOrWhiteSpace(patient.MemberId))
        {
            return false;
        }

        return true;
    }

    public string PrimaryServiceTypeCode()
    {
        foreach (var code in ServiceTypeCodes)
        {
            if (!string.IsNullOrWhiteSpace(code))
            {
                return code.Trim();
            }
        }

        return ServiceTypeCode.HealthBenefitPlanCoverage;
    }
}

/// <summary>Rendering / servicing provider on an inbound eligibility inquiry.</summary>
public sealed class PayerEligibilityProvider
{
    public string? Npi { get; set; }

    public string? OrganizationName { get; set; }

    public bool HasIdentity =>
        !string.IsNullOrWhiteSpace(Npi) || !string.IsNullOrWhiteSpace(OrganizationName);
}

/// <summary>
/// Non-PHI origin metadata. Must not carry raw request payloads, member
/// identifiers, or demographics.
/// </summary>
public sealed class PayerEligibilitySourceMetadata
{
    /// <summary>Inbound network / adapter name (e.g. "canonical", "x12", "stedi-planned").</summary>
    public string? Network { get; set; }

    public string? InterchangeControlNumber { get; set; }
}

/// <summary>Well-known X12 service type codes the responder understands.</summary>
public static class ServiceTypeCode
{
    /// <summary>30 — Health Benefit Plan Coverage (generic inquiry).</summary>
    public const string HealthBenefitPlanCoverage = "30";
}
