using System.Text.Json.Serialization;

namespace CloudHealthOffice.Infrastructure.Gateways.Stedi.Models;

/// <summary>
/// Transport DTO for the Stedi real-time eligibility (270/271) JSON request.
/// Shapes match the Stedi Healthcare API
/// (<c>POST /change/medicalnetwork/eligibility/v3</c>).
///
/// These types are Stedi-specific and live only in the Stedi infrastructure
/// implementation. They must never be exposed from a Cloud Health Office
/// domain/service contract — the <c>StediEligibilityMapper</c> is the only
/// boundary that touches both these and the canonical models.
///
/// <c>JsonIgnoreCondition.WhenWritingNull</c> keeps optional fields out of the
/// serialized request.
/// </summary>
internal sealed class StediEligibilityRequestDto
{
    /// <summary>Stedi payer id (X12 trading partner / payer identifier).</summary>
    [JsonPropertyName("tradingPartnerServiceId")]
    public string TradingPartnerServiceId { get; set; } = string.Empty;

    [JsonPropertyName("provider")]
    public StediProviderDto Provider { get; set; } = new();

    [JsonPropertyName("subscriber")]
    public StediSubscriberDto Subscriber { get; set; } = new();

    [JsonPropertyName("encounter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StediEncounterDto? Encounter { get; set; }

    /// <summary>Caller correlation id echoed back by Stedi; non-PHI.</summary>
    [JsonPropertyName("externalPatientId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExternalPatientId { get; set; }
}

internal sealed class StediProviderDto
{
    [JsonPropertyName("npi")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Npi { get; set; }

    [JsonPropertyName("organizationName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OrganizationName { get; set; }

    [JsonPropertyName("lastName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastName { get; set; }

    [JsonPropertyName("firstName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FirstName { get; set; }
}

internal sealed class StediSubscriberDto
{
    [JsonPropertyName("memberId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MemberId { get; set; }

    [JsonPropertyName("firstName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FirstName { get; set; }

    [JsonPropertyName("lastName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastName { get; set; }

    /// <summary>Date of birth in Stedi's YYYYMMDD format.</summary>
    [JsonPropertyName("dateOfBirth")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DateOfBirth { get; set; }

    [JsonPropertyName("groupNumber")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GroupNumber { get; set; }
}

internal sealed class StediEncounterDto
{
    [JsonPropertyName("serviceTypeCodes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ServiceTypeCodes { get; set; }

    /// <summary>Single date of service (YYYYMMDD).</summary>
    [JsonPropertyName("dateOfService")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DateOfService { get; set; }

    /// <summary>Start of a service date range (YYYYMMDD).</summary>
    [JsonPropertyName("beginningDateOfService")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BeginningDateOfService { get; set; }

    /// <summary>End of a service date range (YYYYMMDD).</summary>
    [JsonPropertyName("endDateOfService")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EndDateOfService { get; set; }
}
