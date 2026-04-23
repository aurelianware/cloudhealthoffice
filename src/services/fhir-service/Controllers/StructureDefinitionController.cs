using FhirService.Services;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// Serves CHO-authored FHIR conformance resources (StructureDefinitions,
/// CodeSystems, ValueSets) at FHIR-canonical read/search endpoints.
///
/// Intentionally anonymous: FHIR metadata and conformance resources are
/// conventionally public so clients can discover what a server supports
/// before authenticating — same posture as
/// <see cref="MetadataController"/>. This deviates from
/// <c>PasController</c>'s <c>[Authorize]</c> posture; the deviation is
/// deliberate, not an oversight.
/// </summary>
[Route("fhir/r4")]
public class StructureDefinitionController : FhirControllerBase
{
    private readonly IChoFhirArtifactRegistry _registry;

    public StructureDefinitionController(IChoFhirArtifactRegistry registry)
    {
        _registry = registry;
    }

    // ── StructureDefinition ──────────────────────────────────────────────────

    /// <summary>GET /fhir/r4/StructureDefinition/{id}</summary>
    [HttpGet("StructureDefinition/{id}")]
    [Produces("application/fhir+json")]
    public IActionResult GetStructureDefinition(string id)
    {
        var sd = _registry.GetStructureDefinition(id);
        return sd is null
            ? FhirNotFound("StructureDefinition", id)
            : Ok(sd);
    }

    /// <summary>GET /fhir/r4/StructureDefinition — Bundle of all CHO-authored profiles and extensions.</summary>
    [HttpGet("StructureDefinition")]
    [Produces("application/fhir+json")]
    public IActionResult SearchStructureDefinitions()
        => Ok(BuildSearchsetBundle(_registry.AllStructureDefinitions));

    // ── CodeSystem ───────────────────────────────────────────────────────────

    [HttpGet("CodeSystem/{id}")]
    [Produces("application/fhir+json")]
    public IActionResult GetCodeSystem(string id)
    {
        var cs = _registry.GetCodeSystem(id);
        return cs is null
            ? FhirNotFound("CodeSystem", id)
            : Ok(cs);
    }

    [HttpGet("CodeSystem")]
    [Produces("application/fhir+json")]
    public IActionResult SearchCodeSystems()
        => Ok(BuildSearchsetBundle(_registry.AllCodeSystems));

    // ── ValueSet ─────────────────────────────────────────────────────────────

    [HttpGet("ValueSet/{id}")]
    [Produces("application/fhir+json")]
    public IActionResult GetValueSet(string id)
    {
        var vs = _registry.GetValueSet(id);
        return vs is null
            ? FhirNotFound("ValueSet", id)
            : Ok(vs);
    }

    [HttpGet("ValueSet")]
    [Produces("application/fhir+json")]
    public IActionResult SearchValueSets()
        => Ok(BuildSearchsetBundle(_registry.AllValueSets));

    // ── helpers ──────────────────────────────────────────────────────────────

    private Bundle BuildSearchsetBundle<T>(IReadOnlyList<T> resources) where T : Resource
    {
        var baseUrl = FhirBaseUrl;
        return new Bundle
        {
            Id = Guid.NewGuid().ToString(),
            Type = Bundle.BundleType.Searchset,
            Timestamp = DateTimeOffset.UtcNow,
            Total = resources.Count,
            Entry = [.. resources.Select(r => new Bundle.EntryComponent
            {
                FullUrl = $"{baseUrl}/{r.TypeName}/{r.Id}",
                Resource = r,
                Search = new Bundle.SearchComponent { Mode = Bundle.SearchEntryMode.Match },
            })],
        };
    }
}
