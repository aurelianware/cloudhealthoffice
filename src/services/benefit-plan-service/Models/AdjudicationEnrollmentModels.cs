namespace BenefitPlanService.Models;

public record ProviderEnrollmentValidationRequest
{
    public string? ClaimId              { get; init; }
    public required string ProviderNpi  { get; init; }
    public string? ProviderTaxonomy     { get; init; }
    public string? StateCode            { get; init; }
    public DateOnly? ServiceDate        { get; init; }
    public string? LineOfBusiness       { get; init; }
}

public record ProviderEnrollmentValidationResponse
{
    public string? ClaimId      { get; init; }
    public required string Status { get; init; }  // "APPROVED" | "DENIED"
    public string? DenialCode   { get; init; }
    public string? Reason       { get; init; }
    public string? Carc         { get; init; }    // CARC 185 on denial
}
