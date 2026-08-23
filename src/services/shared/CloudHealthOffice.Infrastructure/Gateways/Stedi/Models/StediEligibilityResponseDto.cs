using System.Text.Json.Serialization;

namespace CloudHealthOffice.Infrastructure.Gateways.Stedi.Models;

/// <summary>
/// Transport DTO for the Stedi real-time eligibility (271) JSON response.
/// Only the subset of fields Cloud Health Office currently normalizes is
/// modelled; unknown fields are ignored by the deserializer.
///
/// Stedi-specific and infrastructure-only — never surfaced from a domain
/// contract. Mapped to <c>GatewayEligibilityResponse</c> by
/// <c>StediEligibilityMapper</c>.
/// </summary>
internal sealed class StediEligibilityResponseDto
{
    [JsonPropertyName("meta")]
    public StediMetaDto? Meta { get; set; }

    [JsonPropertyName("controlNumber")]
    public string? ControlNumber { get; set; }

    [JsonPropertyName("tradingPartnerServiceId")]
    public string? TradingPartnerServiceId { get; set; }

    [JsonPropertyName("payer")]
    public StediEntityDto? Payer { get; set; }

    [JsonPropertyName("planInformation")]
    public StediPlanInformationDto? PlanInformation { get; set; }

    [JsonPropertyName("planStatus")]
    public List<StediPlanStatusDto>? PlanStatus { get; set; }

    [JsonPropertyName("benefitsInformation")]
    public List<StediBenefitInformationDto>? BenefitsInformation { get; set; }

    [JsonPropertyName("planDateInformation")]
    public StediPlanDateInformationDto? PlanDateInformation { get; set; }

    [JsonPropertyName("errors")]
    public List<StediErrorDto>? Errors { get; set; }

    [JsonPropertyName("subscriber")]
    public StediEligibilityPartyDto? Subscriber { get; set; }

    [JsonPropertyName("dependents")]
    public List<StediEligibilityPartyDto>? Dependents { get; set; }
}

/// <summary>Subscriber or dependent identity as returned on a 271 JSON body.</summary>
internal sealed class StediEligibilityPartyDto
{
    [JsonPropertyName("memberId")]
    public string? MemberId { get; set; }

    [JsonPropertyName("firstName")]
    public string? FirstName { get; set; }

    [JsonPropertyName("lastName")]
    public string? LastName { get; set; }

    [JsonPropertyName("dateOfBirth")]
    public string? DateOfBirth { get; set; }

    [JsonPropertyName("relationToSubscriber")]
    public string? RelationToSubscriber { get; set; }
}

internal sealed class StediMetaDto
{
    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }

    [JsonPropertyName("applicationMode")]
    public string? ApplicationMode { get; set; }
}

internal sealed class StediEntityDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("entityIdentifier")]
    public string? EntityIdentifier { get; set; }
}

internal sealed class StediPlanInformationDto
{
    [JsonPropertyName("groupNumber")]
    public string? GroupNumber { get; set; }

    [JsonPropertyName("groupDescription")]
    public string? GroupDescription { get; set; }

    [JsonPropertyName("planNumber")]
    public string? PlanNumber { get; set; }
}

internal sealed class StediPlanStatusDto
{
    /// <summary>EB01-style status code, e.g. "1" active, "6" inactive.</summary>
    [JsonPropertyName("statusCode")]
    public string? StatusCode { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("planDetails")]
    public string? PlanDetails { get; set; }

    [JsonPropertyName("serviceTypeCodes")]
    public List<string>? ServiceTypeCodes { get; set; }

    [JsonPropertyName("coverageLevelCode")]
    public string? CoverageLevelCode { get; set; }
}

internal sealed class StediBenefitInformationDto
{
    /// <summary>Benefit type code (EB01), e.g. "1","C","A","B","G".</summary>
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("serviceTypeCodes")]
    public List<string>? ServiceTypeCodes { get; set; }

    [JsonPropertyName("coverageLevelCode")]
    public string? CoverageLevelCode { get; set; }

    [JsonPropertyName("timeQualifierCode")]
    public string? TimeQualifierCode { get; set; }

    [JsonPropertyName("timeQualifier")]
    public string? TimeQualifier { get; set; }

    [JsonPropertyName("benefitAmount")]
    public string? BenefitAmount { get; set; }

    [JsonPropertyName("benefitPercent")]
    public string? BenefitPercent { get; set; }

    [JsonPropertyName("benefitQuantity")]
    public string? BenefitQuantity { get; set; }

    [JsonPropertyName("inPlanNetworkIndicatorCode")]
    public string? InPlanNetworkIndicatorCode { get; set; }

    [JsonPropertyName("authOrCertIndicator")]
    public string? AuthOrCertIndicator { get; set; }

    [JsonPropertyName("benefitsDateInformation")]
    public StediBenefitDateInformationDto? BenefitsDateInformation { get; set; }

    [JsonPropertyName("additionalInformation")]
    public List<StediAdditionalInformationDto>? AdditionalInformation { get; set; }
}

internal sealed class StediAdditionalInformationDto
{
    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

internal sealed class StediBenefitDateInformationDto
{
    [JsonPropertyName("benefitBegin")]
    public string? BenefitBegin { get; set; }

    [JsonPropertyName("benefitEnd")]
    public string? BenefitEnd { get; set; }

    [JsonPropertyName("eligibilityBegin")]
    public string? EligibilityBegin { get; set; }

    [JsonPropertyName("eligibilityEnd")]
    public string? EligibilityEnd { get; set; }
}

internal sealed class StediPlanDateInformationDto
{
    [JsonPropertyName("eligibilityBegin")]
    public string? EligibilityBegin { get; set; }

    [JsonPropertyName("eligibilityEnd")]
    public string? EligibilityEnd { get; set; }

    [JsonPropertyName("planBegin")]
    public string? PlanBegin { get; set; }

    [JsonPropertyName("planEnd")]
    public string? PlanEnd { get; set; }
}

internal sealed class StediErrorDto
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>AAA-style follow-up action code, when present.</summary>
    [JsonPropertyName("followupAction")]
    public string? FollowupAction { get; set; }

    [JsonPropertyName("location")]
    public string? Location { get; set; }
}
