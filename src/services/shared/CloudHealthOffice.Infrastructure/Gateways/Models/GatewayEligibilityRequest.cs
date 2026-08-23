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

    /// <summary>Subscriber/member identifier as known to Cloud Health Office. Required.</summary>
    public string SubscriberId { get; set; } = string.Empty;

    /// <summary>Optional distinct member id when checking a dependent.</summary>
    public string? MemberId { get; set; }

    /// <summary>Optional group number for routing/disambiguation.</summary>
    public string? GroupNumber { get; set; }

    /// <summary>Rendering/servicing provider NPI.</summary>
    public string ProviderNpi { get; set; } = string.Empty;

    /// <summary>X12 service type code being inquired about (default 30 = health benefit plan coverage).</summary>
    public string ServiceTypeCode { get; set; } = "30";

    /// <summary>Service date the eligibility is evaluated against.</summary>
    public DateOnly ServiceDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>Optional end of a service date range.</summary>
    public DateOnly? ServiceDateTo { get; set; }

    // Subscriber demographics — some payers require these to match a member.
    public string? SubscriberFirstName { get; set; }
    public string? SubscriberLastName { get; set; }
    public DateOnly? SubscriberDateOfBirth { get; set; }

    /// <summary>Optional payer identifier for routing at the clearinghouse.</summary>
    public string? PayerId { get; set; }

    /// <summary>Optional payer display name.</summary>
    public string? PayerName { get; set; }

    /// <summary>
    /// Correlation id tying this inquiry to the originating request/trace.
    /// Surfaced on <see cref="GatewayTransactionMetadata.CorrelationId"/>.
    /// </summary>
    public string? CorrelationId { get; set; }
}
