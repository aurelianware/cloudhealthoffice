using FhirService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// Public adapter-mode inventory. Buyer demos and diligence packets should
/// start here so every subsequent FHIR call is labeled Demo, Hybrid, or Live.
/// Anonymous, so it carries modes and labels only: no tenant id and no
/// backing implementation per resource (see <see cref="FhirAdapterStatusReport.Public"/>).
/// </summary>
[Route("fhir/r4")]
[AllowAnonymous]
public sealed class AdapterStatusController : FhirControllerBase
{
    private readonly IFhirAdapterStatusService _status;

    public AdapterStatusController(IFhirAdapterStatusService status)
    {
        _status = status;
    }

    /// <summary>GET /fhir/r4/adapter-status</summary>
    [HttpGet("adapter-status")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(FhirAdapterStatusReport), 200)]
    public IActionResult GetAdapterStatus() => Ok(_status.GetStatus().Public());
}
