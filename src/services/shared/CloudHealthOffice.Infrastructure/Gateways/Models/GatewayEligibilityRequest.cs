namespace CloudHealthOffice.Infrastructure.Gateways.Models;

/// <summary>
/// Vendor-neutral Cloud Health Office eligibility request handed to an
/// <see cref="Capabilities.IEligibilityGateway"/>.
///
/// This is a Cloud Health Office canonical model — independent of Stedi JSON,
/// Availity payloads, and raw X12. A vendor adapter is responsible for
/// translating this into the transport format it needs; no vendor DTO is ever
/// substituted for it.
/// </summary>
public sealed class GatewayEligibilityRequest
{
    /// <summary>Tenant on whose behalf the inquiry is made. Required.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Subscriber/member identifier as known to Cloud Health Office. Required
    /// unless <see cref="Subscriber"/>.<see cref="GatewayEligibilityPerson.MemberId"/>
    /// is set. Prefer <see cref="Subscriber"/> for new callers.
    /// </summary>
    public string SubscriberId { get; set; } = string.Empty;

    /// <summary>
    /// Optional distinct member id. Historically overloaded for "the patient
    /// when not the subscriber." It is <b>not</b> used to emit a dependent
    /// inquiry — set <see cref="Patient"/> for that. Kept for backward
    /// compatibility; do not treat a populated <see cref="MemberId"/> alone
    /// as a dependent request.
    /// </summary>
    public string? MemberId { get; set; }

    /// <summary>Optional group number for routing/disambiguation.</summary>
    public string? GroupNumber { get; set; }

    /// <summary>Rendering/servicing provider NPI.</summary>
    public string ProviderNpi { get; set; } = string.Empty;

    /// <summary>Optional rendering provider organization name.</summary>
    public string? ProviderOrganizationName { get; set; }

    /// <summary>X12 service type code being inquired about (default 30 = health benefit plan coverage).</summary>
    public string ServiceTypeCode { get; set; } = "30";

    /// <summary>Service date the eligibility is evaluated against.</summary>
    public DateOnly ServiceDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>Optional end of a service date range.</summary>
    public DateOnly? ServiceDateTo { get; set; }

    // Subscriber demographics — some payers require these to match a member.
    // Prefer <see cref="Subscriber"/>; these remain as a flat shorthand.
    public string? SubscriberFirstName { get; set; }
    public string? SubscriberLastName { get; set; }
    public DateOnly? SubscriberDateOfBirth { get; set; }

    /// <summary>
    /// Policyholder / insured. When set, its member id and demographics take
    /// precedence over the flat <see cref="SubscriberId"/> /
    /// <see cref="SubscriberFirstName"/> fields.
    /// </summary>
    public GatewayEligibilityPerson? Subscriber { get; set; }

    /// <summary>
    /// Person receiving services. Omit or set
    /// <see cref="GatewayEligibilityPerson.Relationship.Self"/> when the
    /// subscriber is the patient. Populate with a distinct identity (name
    /// and/or DOB) to request dependent eligibility.
    /// </summary>
    public GatewayEligibilityPerson? Patient { get; set; }

    /// <summary>Optional payer identifier for routing at the clearinghouse.</summary>
    public string? PayerId { get; set; }

    /// <summary>Optional payer display name.</summary>
    public string? PayerName { get; set; }

    /// <summary>
    /// Correlation id tying this inquiry to the originating request/trace.
    /// Surfaced on <see cref="GatewayTransactionMetadata.CorrelationId"/>.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>Subscriber member id from <see cref="Subscriber"/> or <see cref="SubscriberId"/>.</summary>
    public string ResolveSubscriberMemberId() =>
        FirstNonBlank(Subscriber?.MemberId, SubscriberId) ?? string.Empty;

    public string? ResolveSubscriberFirstName() =>
        FirstNonBlank(Subscriber?.FirstName, SubscriberFirstName);

    public string? ResolveSubscriberLastName() =>
        FirstNonBlank(Subscriber?.LastName, SubscriberLastName);

    public DateOnly? ResolveSubscriberDateOfBirth() =>
        Subscriber?.DateOfBirth ?? SubscriberDateOfBirth;

    /// <summary>
    /// True when this request represents a dependent inquiry (patient is
    /// present, has identity, and is not the subscriber).
    /// </summary>
    public bool IsDependentInquiry()
    {
        var patient = Patient;
        if (patient is null || !patient.HasIdentity || patient.IsSelf)
        {
            return false;
        }

        var subscriberId = ResolveSubscriberMemberId();
        var sameMemberId = !string.IsNullOrWhiteSpace(patient.MemberId) &&
            string.Equals(patient.MemberId, subscriberId, StringComparison.OrdinalIgnoreCase);
        var sameName = string.Equals(patient.FirstName, ResolveSubscriberFirstName(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(patient.LastName, ResolveSubscriberLastName(), StringComparison.OrdinalIgnoreCase);
        var sameDob = patient.DateOfBirth is { } pdob &&
            ResolveSubscriberDateOfBirth() is { } sdob &&
            pdob == sdob;

        // Identical to the subscriber with no extra identity → self coverage.
        if (sameMemberId && (sameName || (!patient.DateOfBirth.HasValue && ResolveSubscriberDateOfBirth() is null)))
        {
            return false;
        }

        if (sameName && sameDob && string.IsNullOrWhiteSpace(patient.MemberId))
        {
            return false;
        }

        return true;
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
