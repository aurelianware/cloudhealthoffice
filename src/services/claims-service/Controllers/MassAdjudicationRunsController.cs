using ClaimsService.Models;
using ClaimsService.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace ClaimsService.Controllers;

[ApiController]
[Route("api/mass-adjudication/runs")]
public sealed class MassAdjudicationRunsController : ControllerBase
{
    private readonly IMassAdjudicationRunRepository _repository;
    private readonly ILogger<MassAdjudicationRunsController> _logger;

    public MassAdjudicationRunsController(
        IMassAdjudicationRunRepository repository,
        ILogger<MassAdjudicationRunsController> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<MassAdjudicationRunSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<MassAdjudicationRunSummary>>> List(
        [FromQuery] int limit = 25,
        CancellationToken ct = default)
    {
        var runs = await _repository.ListAsync(GetTenantId(), limit, ct);
        return Ok(runs);
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(MassAdjudicationRunSummary), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MassAdjudicationRunSummary>> Get(string id, CancellationToken ct = default)
    {
        var run = await _repository.GetAsync(GetTenantId(), id, ct);
        return run is null ? NotFound() : Ok(run);
    }

    [HttpGet("{id}/claims")]
    [ProducesResponseType(typeof(IReadOnlyList<MassAdjudicationClaimResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<MassAdjudicationClaimResult>>> ListClaimResults(
        string id,
        [FromQuery] string? outcome = null,
        [FromQuery] string? validationStatus = null,
        [FromQuery] string? paymentStatus = null,
        [FromQuery] int limit = 250,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var run = await _repository.GetAsync(tenantId, id, ct);
        if (run is null)
        {
            return NotFound();
        }

        var results = await _repository.ListClaimResultsAsync(
            tenantId,
            id,
            outcome,
            validationStatus,
            paymentStatus,
            run.PaymentTolerance,
            limit,
            ct);
        return Ok(results);
    }

    [HttpPost]
    [ProducesResponseType(typeof(MassAdjudicationRunSummary), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<MassAdjudicationRunSummary>> Create(
        [FromBody] MassAdjudicationRunSummary? summary,
        CancellationToken ct = default)
    {
        if (summary?.Run is null)
        {
            return BadRequest(new { error = "Run payload is required" });
        }

        var tenantId = GetTenantId();
        summary.Run.TenantId = tenantId;

        var saved = await _repository.SaveAsync(summary, ct);
        _logger.LogInformation(
            "Saved mass adjudication run {RunId} for tenant {TenantId}: {Processed}/{Total} processed, {Failures} platform failures",
            saved.Id,
            SanitizeForLog(tenantId),
            saved.Processed,
            saved.TotalClaims,
            saved.PlatformFailures);

        return CreatedAtAction(nameof(Get), new { id = saved.Id }, saved);
    }

    private string GetTenantId()
    {
        var tenantId = HttpContext?.Items["TenantId"]?.ToString();
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new InvalidOperationException("TenantId not found in HttpContext. Ensure tenant middleware is configured.");
        }

        return tenantId;
    }

    private static string SanitizeForLog(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
