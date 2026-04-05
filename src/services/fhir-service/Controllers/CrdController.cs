using System.Diagnostics;
using FhirService.Models;
using FhirService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FhirService.Controllers;

/// <summary>
/// Da Vinci CRD (Coverage Requirements Discovery) controller.
/// Implements CDS Hooks 2.0 discovery and hook execution endpoints.
/// </summary>
[Route("cds-services")]
[Produces("application/json")]
public class CrdController : FhirControllerBase
{
    private readonly ICrdService _crdService;
    private readonly CrdConfig _config;
    private readonly ILogger<CrdController> _logger;

    private static readonly CrdDiscoveryResponse DiscoveryPayload = new()
    {
        Services = new List<CrdServiceDefinition>
        {
            new()
            {
                Hook = "order-select",
                Title = "CHO Coverage Requirements Discovery",
                Description = "Determines prior authorization requirements for ordered services",
                Id = "cho-order-select",
                Prefetch = new Dictionary<string, string>
                {
                    ["patient"] = "Patient/{{context.patientId}}",
                    ["coverage"] = "Coverage?patient={{context.patientId}}&status=active",
                },
            },
            new()
            {
                Hook = "order-sign",
                Title = "CHO Prior Authorization Check",
                Description = "Final prior authorization determination at order signing",
                Id = "cho-order-sign",
                Prefetch = new Dictionary<string, string>
                {
                    ["patient"] = "Patient/{{context.patientId}}",
                    ["coverage"] = "Coverage?patient={{context.patientId}}&status=active",
                },
            },
        },
    };

    private static readonly Dictionary<string, string> HookIdToHookType = new(StringComparer.Ordinal)
    {
        ["cho-order-select"] = "order-select",
        ["cho-order-sign"] = "order-sign",
    };

    public CrdController(
        ICrdService crdService,
        IOptions<CrdConfig> config,
        ILogger<CrdController> logger)
    {
        _crdService = crdService;
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>
    /// CDS Hooks discovery endpoint. Returns available CRD hooks.
    /// Must be publicly accessible per the CDS Hooks 2.0 spec.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    public IActionResult Discovery()
    {
        return Ok(DiscoveryPayload);
    }

    /// <summary>
    /// CDS Hooks execution endpoint. Evaluates coverage requirements for the given hook.
    /// </summary>
    [HttpPost("{hookId}")]
    [Authorize]
    public async Task<IActionResult> ExecuteHook(
        string hookId,
        [FromBody] CrdHookRequest request,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Validate hookId is known
        if (!HookIdToHookType.TryGetValue(hookId, out var expectedHookType))
        {
            _logger.LogWarning("CRD hook execution for unknown hookId: {HookId}",
                SanitizeForLog(hookId));
            return NotFound(new { error = $"Unknown hook: {hookId}" });
        }

        // Validate request
        if (string.IsNullOrWhiteSpace(request.HookInstance))
        {
            return BadRequest(new { error = "hookInstance is required" });
        }

        if (!string.Equals(request.Hook, expectedHookType, StringComparison.Ordinal))
        {
            return BadRequest(new { error = $"Hook type '{request.Hook}' does not match endpoint '{hookId}'" });
        }

        if (request.Context == null)
        {
            return BadRequest(new { error = "context is required" });
        }

        _logger.LogInformation(
            "CRD hook {HookId} received for tenant {TenantId}, patient {PatientId}",
            SanitizeForLog(hookId),
            SanitizeForLog(TenantId),
            SanitizeForLog(request.Context.PatientId ?? "unknown"));

        try
        {
            var result = await _crdService.EvaluateCoverageRequirementsAsync(
                request, TenantId, ct);

            _logger.LogInformation(
                "CRD hook {HookId} completed in {ElapsedMs}ms, codes={CodesEvaluated}, translations={Translations}",
                SanitizeForLog(hookId),
                sw.ElapsedMilliseconds,
                result.CodesEvaluated,
                result.TranslationsPerformed);

            return Ok(new CrdCardResponse { Cards = result.Cards });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CRD hook {HookId} failed after {ElapsedMs}ms",
                SanitizeForLog(hookId), sw.ElapsedMilliseconds);
            return StatusCode(500, new { error = "Internal error processing CRD request" });
        }
    }
}
