// ─────────────────────────────────────────────────────────────────
// ADDITION to AdjudicationController.cs
//
// Drop this endpoint into the existing AdjudicationController class
// alongside the existing /adjudicate, /resolve-rates, /calculate-benefits
// endpoints. It replaces the Argo workflow's Python validate-provider-step.
//
// Constructor additions required:
//   private readonly IEnrollmentDecisionGate _enrollmentGate;
//
//   public AdjudicationController(
//       IClaimRoutingService scrubEngine,
//       IBenefitCalculationEngine benefitEngine,
//       IRateResolutionService rateEngine,
//       INcciEditService ncciEngine,
//       IEnrollmentDecisionGate enrollmentGate,     ← ADD
//       ILogger<AdjudicationController> logger)
//   {
//       ...
//       _enrollmentGate = enrollmentGate;           ← ADD
//   }
// ─────────────────────────────────────────────────────────────────

using CloudHealthOffice.ProviderEnrollmentService.Abstractions;
using CloudHealthOffice.ProviderEnrollmentService.Models;
using Microsoft.AspNetCore.Mvc;

// ── Endpoint ──────────────────────────────────────────────────────

// POST /api/v1/adjudication/validate-provider-enrollment
//
// Called by Argo workflow Step 3 (validate-provider) in parallel with
// verify-coverage and validate-codes. Replaces the Python inline script
// that only checked credentialingStatus and providerStatus.
//
// This endpoint gates on active Medicaid enrollment state — the Python
// script had no visibility into PEMS or any state enrollment system.

[HttpPost("validate-provider-enrollment")]
[ProducesResponseType(typeof(ProviderEnrollmentValidationResponse), StatusCodes.Status200OK)]
[ProducesResponseType(StatusCodes.Status400BadRequest)]
public async Task<ActionResult<ProviderEnrollmentValidationResponse>> ValidateProviderEnrollment(
    [FromBody] ProviderEnrollmentValidationRequest request,
    CancellationToken ct)
{
    using var span = ChoActivitySource.StartActivity(
        "claim.validate-provider-enrollment",
        System.Diagnostics.ActivityKind.Internal,
        tenantId: TenantId,
        claimId: request.ClaimId);

    if (string.IsNullOrEmpty(request.ProviderNpi))
        return BadRequest("ProviderNpi is required.");

    // Map string LOB from the Argo workflow payload to the enrollment service enum
    var lob = request.LineOfBusiness?.ToUpperInvariant() switch
    {
        "MEDICAID" or "STAR" or "STARPLUS" or "STARKIDS" => LineOfBusiness.Medicaid,
        "CHIP"                                            => LineOfBusiness.CHIP,
        "MARKETPLACE" or "EXCHANGE"                       => LineOfBusiness.Marketplace,
        "MEDICARE"                                        => LineOfBusiness.Medicare,
        _                                                 => LineOfBusiness.None
    };

    var gateResult = await _enrollmentGate.EvaluateAsync(
        npi:         request.ProviderNpi,
        taxonomy:    request.ProviderTaxonomy ?? string.Empty,
        stateCode:   request.StateCode ?? "TX",
        serviceDate: request.ServiceDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
        lob:         lob,
        ct:          ct);

    span?.SetTag("cho.enrollment.passed",      gateResult.Passed);
    span?.SetTag("cho.enrollment.denial_code", gateResult.DenialCode ?? string.Empty);

    _logger.LogInformation(
        "Provider enrollment validation: NPI={Npi} State={State} LOB={Lob} Passed={Passed}",
        SanitizeForLog(request.ProviderNpi),
        SanitizeForLog(request.StateCode),
        SanitizeForLog(request.LineOfBusiness),
        gateResult.Passed);

    return Ok(new ProviderEnrollmentValidationResponse
    {
        ClaimId    = request.ClaimId,
        Status     = gateResult.Passed ? "APPROVED" : "DENIED",
        DenialCode = gateResult.DenialCode,
        Reason     = gateResult.DenialReason,
        // CARC 185: Provider not enrolled in Medicaid
        Carc       = gateResult.Passed ? null : "185"
    });
}

// ── Request / Response types ──────────────────────────────────────
// Add these to BenefitPlanService.Models or a new AdjudicationModels.cs

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
