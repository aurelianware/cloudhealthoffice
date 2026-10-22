using System.Text.Json.Nodes;
using CareManagementService.Repositories;
using CareManagementService.Services.Fhir;
using Microsoft.AspNetCore.Mvc;

namespace CareManagementService.Controllers;

/// <summary>
/// Care plans, goals and care teams as FHIR R4, read-only.
///
/// care-management-service owns the record and so projects its own
/// resources, as authorization-service does for prior-authorization
/// ExplanationOfBenefit; fhir-service proxies rather than duplicating the
/// mapping. Nothing is stored twice, so a projection cannot drift from the
/// case it describes.
///
/// Searching is by member. A FHIR id here is the case id, which is how a
/// caller that read a Bundle comes back for one resource.
/// </summary>
[ApiController]
[Route("fhir")]
[Produces("application/fhir+json", "application/json")]
public sealed class FhirCarePlanController(ICareCaseRepository cases) : ControllerBase
{
    /// <summary>A member's care plans as a searchset Bundle.</summary>
    [HttpGet("CarePlan")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SearchCarePlans([FromQuery] string? patient, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(patient))
            return BadRequest(OperationOutcome("required", "A patient is required to search care plans."));

        var memberId = StripResourcePrefix(patient, "Patient");
        var resources = (await cases.ListByMemberAsync(memberId, ct))
            .Select(CarePlanProjector.ToCarePlan)
            .OfType<JsonObject>()
            .ToList();

        return FhirJson(Bundle(resources));
    }

    [HttpGet("CarePlan/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCarePlan(string id, CancellationToken ct)
    {
        if (await cases.GetByIdAsync(id, ct) is not { } careCase) return NotFoundOutcome("CarePlan");
        return CarePlanProjector.ToCarePlan(careCase) is { } plan ? FhirJson(plan) : NotFoundOutcome("CarePlan");
    }

    /// <summary>A member's goals across their care plans.</summary>
    [HttpGet("Goal")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SearchGoals([FromQuery] string? patient, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(patient))
            return BadRequest(OperationOutcome("required", "A patient is required to search goals."));

        var memberId = StripResourcePrefix(patient, "Patient");
        var resources = (await cases.ListByMemberAsync(memberId, ct))
            .SelectMany(CarePlanProjector.ToGoals)
            .ToList();

        return FhirJson(Bundle(resources));
    }

    [HttpGet("CareTeam/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCareTeam(string id, CancellationToken ct)
    {
        if (await cases.GetByIdAsync(id, ct) is not { } careCase) return NotFoundOutcome("CareTeam");
        return CarePlanProjector.ToCareTeam(careCase) is { } team ? FhirJson(team) : NotFoundOutcome("CareTeam");
    }

    [HttpGet("CareTeam")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SearchCareTeams([FromQuery] string? patient, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(patient))
            return BadRequest(OperationOutcome("required", "A patient is required to search care teams."));

        var memberId = StripResourcePrefix(patient, "Patient");
        var resources = (await cases.ListByMemberAsync(memberId, ct))
            .Select(CarePlanProjector.ToCareTeam)
            .OfType<JsonObject>()
            .ToList();

        return FhirJson(Bundle(resources));
    }

    /// <summary>Accepts both "Patient/123" and a bare id, as FHIR search references are written both ways.</summary>
    private static string StripResourcePrefix(string reference, string resourceType)
    {
        var prefix = resourceType + "/";
        return reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? reference[prefix.Length..]
            : reference;
    }

    private static JsonObject Bundle(IReadOnlyList<JsonObject> resources) => new()
    {
        ["resourceType"] = "Bundle",
        ["type"] = "searchset",
        ["total"] = resources.Count,
        ["entry"] = new JsonArray(resources
            .Select(r => (JsonNode)new JsonObject { ["resource"] = r.DeepClone() })
            .ToArray()),
    };

    private static JsonObject OperationOutcome(string code, string diagnostics) => new()
    {
        ["resourceType"] = "OperationOutcome",
        ["issue"] = new JsonArray
        {
            new JsonObject
            {
                ["severity"] = "error",
                ["code"] = code,
                ["diagnostics"] = diagnostics,
            },
        },
    };

    private IActionResult NotFoundOutcome(string resourceType) =>
        new ObjectResult(OperationOutcome("not-found", $"No {resourceType} with that id."))
        {
            StatusCode = StatusCodes.Status404NotFound,
            ContentTypes = { "application/fhir+json" },
        };

    private IActionResult FhirJson(JsonObject resource) =>
        new ObjectResult(resource) { ContentTypes = { "application/fhir+json" } };
}
