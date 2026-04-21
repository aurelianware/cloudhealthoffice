using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using ClaimsService.Fhir;
using ClaimsService.Models;
using ClaimsService.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace ClaimsService.Controllers;

/// <summary>
/// v1 claims API used by the portal Member Details dialog. Exposes a
/// member-scoped search that projects each matching <see cref="Claim"/> into a
/// FHIR R4 <c>ExplanationOfBenefit</c> resource — the payer-facing 835-shaped
/// representation the Claims tab consumes. Paired with the legacy
/// <c>/api/claims</c> surface (which stays as-is for existing callers).
/// </summary>
[ApiController]
[Route("api/v1/claims")]
[Produces("application/json")]
public class ClaimsV1Controller : ControllerBase
{
    private readonly IClaimRepository _claimRepository;
    private readonly IExplanationOfBenefitProjector _eobProjector;
    private readonly ILogger<ClaimsV1Controller> _logger;

    public ClaimsV1Controller(
        IClaimRepository claimRepository,
        IExplanationOfBenefitProjector eobProjector,
        ILogger<ClaimsV1Controller> logger)
    {
        _claimRepository = claimRepository;
        _eobProjector = eobProjector;
        _logger = logger;
    }

    /// <summary>
    /// Search claims for a member. Returns a small wrapper
    /// <c>{ total, page, pageSize, resources[] }</c> where <c>resources</c> is
    /// a FHIR <c>ExplanationOfBenefit</c> array (one per matching claim).
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(EobSearchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<EobSearchResponse>> SearchMemberClaims(
        [FromQuery, Required] string memberId,
        [FromQuery] DateTime? serviceDateFrom = null,
        [FromQuery] DateTime? serviceDateTo = null,
        [FromQuery] ClaimStatus? status = null,
        [FromQuery] string? providerNPI = null,
        [FromQuery] ClaimType? claimType = null,
        [FromQuery] decimal? amountMin = null,
        [FromQuery] decimal? amountMax = null,
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery, Range(1, 100)] int pageSize = 20)
    {
        if (string.IsNullOrWhiteSpace(memberId))
            return BadRequest(new { error = "memberId is required" });

        _logger.LogInformation(
            "v1 claims member search: member={Member}, status={Status}, type={Type}, amount=[{Min},{Max}]",
            SanitizeForLog(memberId), status, claimType, amountMin, amountMax);

        var (claims, total) = await _claimRepository.SearchForMemberAsync(
            memberId, serviceDateFrom, serviceDateTo, status,
            providerNPI, claimType, amountMin, amountMax,
            page, pageSize);

        var resources = new JsonArray();
        foreach (var claim in claims)
        {
            resources.Add(_eobProjector.Project(claim));
        }

        return Ok(new EobSearchResponse
        {
            Total = total,
            Page = page,
            PageSize = pageSize,
            Resources = resources
        });
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

/// <summary>
/// Wrapper for FHIR ExplanationOfBenefit search results. Kept intentionally
/// small — pagination metadata plus the FHIR resource array as a JsonNode so
/// we can project without taking on the Hl7.Fhir.R4 transitive dep graph.
/// </summary>
public class EobSearchResponse
{
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public JsonArray Resources { get; set; } = new();
}
