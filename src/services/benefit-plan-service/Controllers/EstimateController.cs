using System.Diagnostics;
using BenefitPlanService.Middleware;
using BenefitPlanService.Models.Estimate;
using BenefitPlanService.Services;
using CloudHealthOffice.Infrastructure.Observability;
using Microsoft.AspNetCore.Mvc;

namespace BenefitPlanService.Controllers;

/// <summary>
/// Provider-facing <b>prospective adjudication</b> (claim payment estimate)
/// endpoint. A provider application submits a proposed set of services
/// <em>before</em> a real claim exists and receives the expected allowed
/// amount, payer payment, patient responsibility, contractual adjustment and
/// per-line detail.
///
/// <para>
/// This is <b>not</b> real claim adjudication. It runs the same pricing and
/// benefit engines in a read-only simulation mode and never persists a claim,
/// payment, accumulator, counter, remittance, workflow, or authorization
/// state. See <c>docs/architecture/prospective-adjudication.md</c>.
/// </para>
///
/// <para>
/// Tenant context is taken from the authenticated request (JWT claim or
/// <c>X-Tenant-ID</c> header via <see cref="TenantMiddleware"/>); a tenant id
/// in the request body can never override it.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/adjudication")]
public class EstimateController : ControllerBase
{
    private readonly IPaymentEstimateService _estimateService;
    private readonly ILogger<EstimateController> _logger;

    public EstimateController(
        IPaymentEstimateService estimateService,
        ILogger<EstimateController> logger)
    {
        _estimateService = estimateService;
        _logger = logger;
    }

    private string TenantId => HttpContext.GetTenantId()
        ?? throw new InvalidOperationException("Tenant context missing");

    /// <summary>
    /// Produce a prospective payment estimate for a proposed set of services.
    /// Read-only: no financial state is created or modified.
    /// </summary>
    [HttpPost("estimate")]
    [ProducesResponseType(typeof(PaymentEstimateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaymentEstimateResponse>> Estimate(
        [FromBody] PaymentEstimateRequest request,
        CancellationToken ct)
    {
        if (request is null)
            return BadRequest(new { error = "Request body is required" });
        if (string.IsNullOrWhiteSpace(request.MemberId))
            return BadRequest(new { error = "MemberId is required" });
        if (request.BenefitPlanId == Guid.Empty)
            return BadRequest(new { error = "BenefitPlanId is required" });
        if (string.IsNullOrWhiteSpace(request.ProviderNpi))
            return BadRequest(new { error = "ProviderNpi is required" });
        if (request.Lines is null || request.Lines.Count == 0)
            return BadRequest(new { error = "At least one service line is required" });
        if (request.Lines.Any(l => l.LineNumber <= 0))
            return BadRequest(new { error = "Each service line requires a positive LineNumber" });
        if (request.Lines.Select(l => l.LineNumber).Distinct().Count() != request.Lines.Count)
            return BadRequest(new { error = "Service line numbers must be unique" });

        using var span = ChoActivitySource.StartActivity(
            "adjudication.estimate",
            ActivityKind.Internal,
            tenantId: TenantId,
            memberId: request.MemberId,
            claimType: request.ClaimType);
        span?.SetTag("cho.estimate.line_count", request.Lines.Count);
        span?.SetTag("cho.benefit_plan_id", request.BenefitPlanId.ToString());

        var estimate = await _estimateService.EstimateAsync(TenantId, request, ct);

        span?.SetTag("cho.estimate.status", estimate.Status);
        span?.SetTag("cho.estimate.authority", estimate.Authority.ToString());
        span?.SetTag("cho.estimate.confidence", estimate.Confidence.Level.ToString());

        return Ok(estimate);
    }
}
