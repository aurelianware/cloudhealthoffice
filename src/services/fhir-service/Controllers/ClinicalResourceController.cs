using FhirService.Models;
using FhirService.Services;
using FhirService.Services.Clinical;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// FHIR R4 read and search for the USCDI clinical resource types Cloud Health
/// Office serves (CMS-0057-F PAT-02):
///
///   GET /fhir/r4/{Type}/{id}
///   GET /fhir/r4/{Type}?patient=Patient/{id}        (or ?subject=, where R4 defines it)
///
/// ONE CONTROLLER, TWELVE TYPES, ON PURPOSE. Every clinical type has the same
/// member binding, the same authorization boundary, and the same store; twelve
/// near-identical controllers would be twelve places for one of those to drift,
/// and the drift would be invisible until a resource was served without a check.
/// The route constraint below lists exactly the inventory
/// (<see cref="ClinicalResourceInventory"/>) — no catch-all — and a structural
/// test pins the two together, so this controller cannot silently start
/// answering for a type nobody authorized.
///
/// WHY IT CANNOT BYPASS THE AUTHORIZATION LAYERS. It is reached through
/// <c>/fhir/r4</c> like every other resource, so <c>SmartScopeEnforcementMiddleware</c>
/// has already required an authenticated token with
/// <c>patient|user|system/{Type}.read</c>, <c>TenantMiddleware</c> has already
/// established the tenant, and the globally registered
/// <c>ProviderAccessAuthorizationFilter</c> has already required attribution and
/// an active ProviderAccess consent for any provider- or backend-shaped caller.
/// None of that is re-implemented here and none of it is opt-in.
///
/// WHAT THIS CONTROLLER ADDS is the last control, the one only it can apply: the
/// member the caller is authorized for is carried into the storage query, so a
/// resource id alone never reaches another member's data. See
/// <see cref="ClinicalResourceService"/>.
/// </summary>
[Route("fhir/r4")]
public class ClinicalResourceController : FhirControllerBase
{
    /// <summary>
    /// The clinical types this controller answers for, as a route constraint.
    /// It has to be a compile-time literal, so
    /// <c>ClinicalResourceInventory.RouteAlternation</c> is what a test compares
    /// it against — the inventory stays the source of truth and this string
    /// cannot drift from it unnoticed.
    /// </summary>
    private const string ClinicalTypes =
        "AllergyIntolerance|CarePlan|CareTeam|Condition|Device|DiagnosticReport|Goal|"
        + "Immunization|MedicationDispense|MedicationRequest|Observation|Procedure";

    /// <summary>
    /// FHIR's own id character set. It excludes <c>$</c>, so an operation path
    /// can never be routed here and mistaken for a resource id.
    ///
    /// Two route-template rules shape how it is written: <c>[</c> and <c>]</c>
    /// are token delimiters and must be doubled, and a <c>{n,m}</c> quantifier
    /// cannot appear at all because braces delimit route parameters — so the
    /// length bound is a separate <c>maxlength</c> constraint. ASP.NET anchors an
    /// inline regex constraint itself, so no <c>^</c>/<c>$</c> is written here.
    /// </summary>
    private const string FhirId = "[[A-Za-z0-9.-]]+";

    private readonly IClinicalResourceService _clinical;
    private readonly FhirBundleBuilder _bundleBuilder;

    public ClinicalResourceController(
        IClinicalResourceService clinical, FhirBundleBuilder bundleBuilder)
    {
        _clinical = clinical;
        _bundleBuilder = bundleBuilder;
    }

    /// <summary>GET /fhir/r4/{ClinicalType}/{id} — read one clinical resource.</summary>
    [HttpGet("{resourceType:regex(" + ClinicalTypes + ")}/{id:regex(" + FhirId + "):maxlength(64)}")]
    [ProducesResponseType(typeof(Resource), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 403)]
    [ProducesResponseType(typeof(OperationOutcome), 404)]
    public async Task<IActionResult> Read(
        string resourceType, string id, [FromQuery] ClinicalSearchParams search, CancellationToken ct)
    {
        var type = ClinicalResourceInventory.Canonicalize(resourceType);
        if (type is null) return FhirNotFound(resourceType, id);

        // A read accepts a member context too: it is how a PROVIDER names the
        // member the Provider Access filter has already authorized them for. A
        // patient-context token's own binding still wins over anything on the
        // query string.
        var binding = ResolveMemberBinding(type, search);
        if (binding.Contradictory)
            return FhirBadRequest("patient and subject name different members.");

        var result = await _clinical.ReadAsync(BuildContext(binding.MemberId), type, id, ct);

        return result.Outcome switch
        {
            ClinicalAccessOutcome.Granted => Ok(result.Resource),
            ClinicalAccessOutcome.NotAuthorized => ClinicalForbidden(),
            _ => FhirNotFound(type, id),
        };
    }

    /// <summary>GET /fhir/r4/{ClinicalType} — search one member's clinical resources.</summary>
    [HttpGet("{resourceType:regex(" + ClinicalTypes + ")}")]
    [ProducesResponseType(typeof(Bundle), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 400)]
    [ProducesResponseType(typeof(OperationOutcome), 403)]
    public async Task<IActionResult> Search(
        string resourceType, [FromQuery] ClinicalSearchParams search, CancellationToken ct)
    {
        var type = ClinicalResourceInventory.Canonicalize(resourceType);
        if (type is null) return FhirNotFound(resourceType, string.Empty);

        var entry = ClinicalResourceInventory.Find(type)!;

        // Only advertise what is honoured, and only honour what is advertised: a
        // `subject` on a type whose R4 definition has no such parameter is a
        // request CHO cannot answer, and saying so beats quietly ignoring it and
        // returning a Bundle that looks like an answer.
        if (!entry.SupportsSubjectSearch && !string.IsNullOrWhiteSpace(search.Subject))
            return FhirBadRequest($"{type} does not support the 'subject' search parameter; use 'patient'.");

        var binding = ResolveMemberBinding(type, search);
        if (binding.Contradictory)
            return FhirBadRequest("patient and subject name different members.");

        var count = ClampPageSize(search.Count);
        var page = ClampPage(search.Page);

        var result = await _clinical.SearchAsync(
            BuildContext(binding.MemberId), type, binding.RequestedMemberId, search.Id, page, count, ct);

        return result.Outcome switch
        {
            ClinicalAccessOutcome.Granted => Ok(_bundleBuilder.Build(
                result.Resources, result.Total, page, count, type, FhirBaseUrl, RawQueryString)),
            ClinicalAccessOutcome.InvalidRequest => FhirBadRequest("The search could not be interpreted."),
            _ => ClinicalForbidden(),
        };
    }

    /// <summary>
    /// Which member this request is about, and which member the caller may read.
    ///
    /// For a PATIENT-context token the answer is the token's own binding and
    /// nothing else — a query parameter cannot widen it. For a provider or
    /// backend token it is the member named on the request, which the Provider
    /// Access filter has already tested for attribution and consent before this
    /// action body ran; a provider-shaped call that named no member never gets
    /// here.
    /// </summary>
    private MemberBinding ResolveMemberBinding(string resourceType, ClinicalSearchParams search)
    {
        var entry = ClinicalResourceInventory.Find(resourceType);

        // Taken from the BOUND model, not re-read from the query string: one
        // source for one input, so what a unit test drives and what a request
        // carries cannot diverge.
        var patient = Normalize(search.Patient);
        var subject = entry?.SupportsSubjectSearch == true ? Normalize(search.Subject) : null;

        // Two member parameters that disagree is a malformed search, not a
        // licence to pick one.
        if (patient is not null && subject is not null
            && !string.Equals(patient, subject, StringComparison.Ordinal))
            return new MemberBinding(null, null, Contradictory: true);

        var requested = patient ?? subject;

        // SmartPatientId is set only for patient-scoped tokens, and it wins:
        // the member reading their own record cannot ask for anybody else's.
        var authorized = SmartPatientId ?? requested;

        return new MemberBinding(authorized, requested, Contradictory: false);
    }

    private ClinicalAccessContext BuildContext(string? authorizedMemberId) => new()
    {
        TenantId = TenantId,
        AuthorizedMemberId = authorizedMemberId,
        CallerId = ResolveCallerId(),
        IsPatientContext = SmartPatientId is not null,
    };

    /// <summary>The caller, from the validated token. Never from a header or the query string.</summary>
    private string? ResolveCallerId()
    {
        foreach (var claim in new[] { "sub", System.Security.Claims.ClaimTypes.NameIdentifier, "client_id", "azp" })
        {
            var value = User?.FindFirst(claim)?.Value;
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return User?.Identity?.Name;
    }

    /// <summary>
    /// The same uniform refusal the Provider Access layer gives, for the same
    /// reason: "you may not read this member" and "there is no such member" must
    /// not be distinguishable, or the difference becomes an enumeration oracle.
    /// The category is in the audit line.
    /// </summary>
    private IActionResult ClinicalForbidden()
        => StatusCode(403, new OperationOutcome
        {
            Issue =
            [
                new OperationOutcome.IssueComponent
                {
                    Severity = OperationOutcome.IssueSeverity.Error,
                    Code = OperationOutcome.IssueType.Forbidden,
                    Diagnostics = "Access to this member's clinical data is not authorized for this request.",
                },
            ],
        });

    /// <summary>Accepts <c>Patient/123</c> and <c>123</c> alike, per FHIR reference search.</summary>
    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();
        return trimmed.StartsWith("Patient/", StringComparison.OrdinalIgnoreCase)
            ? trimmed["Patient/".Length..]
            : trimmed;
    }

    private readonly record struct MemberBinding(
        string? MemberId, string? RequestedMemberId, bool Contradictory);
}
