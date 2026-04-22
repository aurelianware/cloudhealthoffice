using FhirService.Services;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// Serves CHO-authored FHIR OperationDefinitions. Anonymous for the same
/// reason as <see cref="StructureDefinitionController"/>: FHIR metadata
/// is conventionally public so clients can discover operations before
/// authenticating.
/// </summary>
[Route("fhir/r4")]
public class OperationDefinitionController : FhirControllerBase
{
    private readonly IChoFhirArtifactRegistry _registry;

    public OperationDefinitionController(IChoFhirArtifactRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>GET /fhir/r4/OperationDefinition/{id}</summary>
    [HttpGet("OperationDefinition/{id}")]
    [Produces("application/fhir+json")]
    public IActionResult GetOperationDefinition(string id)
    {
        var od = _registry.GetOperationDefinition(id);
        return od is null
            ? FhirNotFound("OperationDefinition", id)
            : Ok(od);
    }

    /// <summary>GET /fhir/r4/OperationDefinition — Bundle of all CHO-authored operations.</summary>
    [HttpGet("OperationDefinition")]
    [Produces("application/fhir+json")]
    public IActionResult SearchOperationDefinitions()
    {
        var all = _registry.AllOperationDefinitions;
        var baseUrl = FhirBaseUrl;
        return Ok(new Bundle
        {
            Id = Guid.NewGuid().ToString(),
            Type = Bundle.BundleType.Searchset,
            Timestamp = DateTimeOffset.UtcNow,
            Total = all.Count,
            Entry = [.. all.Select(od => new Bundle.EntryComponent
            {
                FullUrl = $"{baseUrl}/OperationDefinition/{od.Id}",
                Resource = od,
                Search = new Bundle.SearchComponent { Mode = Bundle.SearchEntryMode.Match },
            })],
        });
    }
}
