using System.Text.Json.Serialization;

namespace CloudHealthOffice.Infrastructure.ReferenceData.Payers.Stedi.DTOs;

/// <summary>
/// Transport DTO for a single payer on Stedi's List Payers JSON API
/// (<c>GET /2024-04-01/payers</c> on <c>https://payers.us.stedi.com</c>).
/// Internal to infrastructure — never part of a domain contract.
///
/// Fields match the published Stedi response; unknown properties are ignored.
/// </summary>
internal sealed class StediPayerListResponseDto
{
    [JsonPropertyName("items")]
    public List<StediPayerDto>? Items { get; set; }

    [JsonPropertyName("nextPageToken")]
    public string? NextPageToken { get; set; }
}

internal sealed class StediPayerDto
{
    [JsonPropertyName("stediId")]
    public string? StediId { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("primaryPayerId")]
    public string? PrimaryPayerId { get; set; }

    [JsonPropertyName("conciseName")]
    public string? ConciseName { get; set; }

    [JsonPropertyName("aliases")]
    public List<string>? Aliases { get; set; }

    [JsonPropertyName("names")]
    public List<string>? Names { get; set; }

    [JsonPropertyName("coverageTypes")]
    public List<string>? CoverageTypes { get; set; }

    [JsonPropertyName("operatingStates")]
    public List<string>? OperatingStates { get; set; }

    [JsonPropertyName("programs")]
    public List<string>? Programs { get; set; }

    [JsonPropertyName("parentPayerGroupId")]
    public string? ParentPayerGroupId { get; set; }

    [JsonPropertyName("parentPayerGroupName")]
    public string? ParentPayerGroupName { get; set; }

    [JsonPropertyName("employerIdentificationNumbers")]
    public List<string>? EmployerIdentificationNumbers { get; set; }

    [JsonPropertyName("transactionSupport")]
    public StediTransactionSupportDto? TransactionSupport { get; set; }

    [JsonPropertyName("enrollment")]
    public StediEnrollmentDto? Enrollment { get; set; }

    [JsonPropertyName("urls")]
    public StediPayerUrlsDto? Urls { get; set; }
}

internal sealed class StediTransactionSupportDto
{
    [JsonPropertyName("eligibilityCheck")]
    public string? EligibilityCheck { get; set; }

    [JsonPropertyName("claimStatus")]
    public string? ClaimStatus { get; set; }

    [JsonPropertyName("claimSubmission")]
    public string? ClaimSubmission { get; set; }

    [JsonPropertyName("claimPayment")]
    public string? ClaimPayment { get; set; }

    [JsonPropertyName("coordinationOfBenefits")]
    public string? CoordinationOfBenefits { get; set; }

    [JsonPropertyName("dentalClaimSubmission")]
    public string? DentalClaimSubmission { get; set; }

    [JsonPropertyName("institutionalClaimSubmission")]
    public string? InstitutionalClaimSubmission { get; set; }

    [JsonPropertyName("professionalClaimSubmission")]
    public string? ProfessionalClaimSubmission { get; set; }

    [JsonPropertyName("unsolicitedClaimAttachment")]
    public string? UnsolicitedClaimAttachment { get; set; }
}

internal sealed class StediEnrollmentDto
{
    [JsonPropertyName("ptanRequired")]
    public bool? PtanRequired { get; set; }

    [JsonPropertyName("transactionEnrollmentProcesses")]
    public Dictionary<string, StediEnrollmentProcessDto>? TransactionEnrollmentProcesses { get; set; }
}

internal sealed class StediEnrollmentProcessDto
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("timeframe")]
    public string? Timeframe { get; set; }

    [JsonPropertyName("requestedEffectiveDate")]
    public string? RequestedEffectiveDate { get; set; }
}

internal sealed class StediPayerUrlsDto
{
    [JsonPropertyName("website")]
    public string? Website { get; set; }
}
