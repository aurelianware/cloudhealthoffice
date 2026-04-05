using FhirService.Models;
using FhirService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// Internal API for managing CRD code classifications per tenant.
/// Updates are visible immediately to all CRD evaluations.
/// </summary>
[Route("api/v1/crd")]
[Authorize]
[Produces("application/json")]
public class CrdConfigController : FhirControllerBase
{
    private readonly ICrdService _crdService;
    private readonly ILogger<CrdConfigController> _logger;

    public CrdConfigController(
        ICrdService crdService,
        ILogger<CrdConfigController> logger)
    {
        _crdService = crdService;
        _logger = logger;
    }

    /// <summary>GET /api/v1/crd/code-classification — get current classification for caller's tenant</summary>
    [HttpGet("code-classification")]
    public IActionResult GetClassification()
    {
        var classification = _crdService.GetClassificationOrNull(TenantId)
            ?? _crdService.GetClassification("default");

        return Ok(classification);
    }

    /// <summary>PUT /api/v1/crd/code-classification — update classification for caller's tenant</summary>
    [HttpPut("code-classification")]
    public IActionResult SetClassification([FromBody] CrdCodeClassification classification)
    {
        _crdService.SetClassification(TenantId, classification);

        _logger.LogInformation(
            "CRD code classification updated for tenant {TenantId}: auth={AuthCount}, approved={ApprovedCount}, doc={DocCount}",
            SanitizeForLog(TenantId),
            classification.AuthRequiredCodes.Count,
            classification.AutoApprovedCodes.Count,
            classification.DocumentationRequiredCodes.Count);

        return Ok(classification);
    }
}
