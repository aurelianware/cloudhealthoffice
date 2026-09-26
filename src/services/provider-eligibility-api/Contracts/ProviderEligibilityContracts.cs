using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace ProviderEligibilityApi.Contracts;

/// <summary>
/// Eligibility inquiry from a provider application. Tenant is never part of the
/// body; it comes from the authenticated request.
/// </summary>
public sealed class ProviderEligibilityCheckRequest
{
    /// <summary>Payer identifier as listed by <c>GET /api/v1/payers</c> (id or payer id alias).</summary>
    public string? PayerId { get; set; }

    public ProviderEligibilityProvider? Provider { get; set; }

    /// <summary>The policyholder. Member id, name and date of birth are required.</summary>
    public ProviderEligibilityPerson? Subscriber { get; set; }

    /// <summary>
    /// The patient when they are not the subscriber (a dependent). Omit when the
    /// patient is the subscriber.
    /// </summary>
    public ProviderEligibilityPerson? Patient { get; set; }

    public string? GroupNumber { get; set; }

    /// <summary>X12 service type code. Defaults to 30 (health benefit plan coverage); 35 is dental care.</summary>
    public string? ServiceTypeCode { get; set; }

    /// <summary>Date coverage is evaluated for. Defaults to today (UTC).</summary>
    public DateOnly? ServiceDate { get; set; }

    /// <summary>Caller's correlation id, echoed back and logged. Must not contain PHI.</summary>
    public string? CorrelationId { get; set; }
}

public sealed class ProviderEligibilityProvider
{
    public string? Npi { get; set; }
    public string? OrganizationName { get; set; }
}

public sealed class ProviderEligibilityPerson
{
    /// <summary>Required for the subscriber; optional for a dependent.</summary>
    public string? MemberId { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public DateOnly? DateOfBirth { get; set; }

    /// <summary>For a dependent: spouse, child or other.</summary>
    public string? RelationshipToSubscriber { get; set; }
}

/// <summary>
/// Normalized eligibility result. Deliberately omits member identifiers and
/// demographics: the caller already holds them and they are not echoed back.
/// </summary>
public sealed class ProviderEligibilityCheckResponse
{
    /// <summary>Completed, Rejected (payer business rejection) or Failed.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Vendor-neutral error category; <c>None</c> on success.</summary>
    public string ErrorCategory { get; set; } = nameof(GatewayErrorCategory.None);

    /// <summary>Operator-safe explanation for rejected or failed checks.</summary>
    public string? Message { get; set; }

    public bool Eligible { get; set; }

    /// <summary>Active, Inactive or Unknown.</summary>
    public string CoverageStatus { get; set; } = nameof(GatewayCoverageStatus.Unknown);

    public string? PlanName { get; set; }
    public string? PlanId { get; set; }
    public string? GroupNumber { get; set; }
    public DateOnly? CoverageStart { get; set; }
    public DateOnly? CoverageEnd { get; set; }
    public List<GatewayEligibilityBenefit> Benefits { get; set; } = new();

    public string? CorrelationId { get; set; }
    public DateTimeOffset CheckedAtUtc { get; set; }
}

public sealed class ProviderPayerSummary
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string> PayerIds { get; set; } = new();

    /// <summary>Supported, EnrollmentRequired or NotSupported for 270/271.</summary>
    public string Eligibility { get; set; } = string.Empty;
}

public sealed class ValidationErrorResponse
{
    public string Error { get; set; } = "The eligibility request is invalid.";
    public Dictionary<string, string> Fields { get; set; } = new();
}
