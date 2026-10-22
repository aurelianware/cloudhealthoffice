using System.Text.Json.Nodes;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using AuthorizationService.Services.Fhir;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace AuthorizationService.Controllers;

/// <summary>
/// FHIR R4 <c>ExplanationOfBenefit</c> read and search for PRIOR
/// AUTHORIZATIONS (<c>use = preauthorization</c>) — the CMS-0057-F PA-data
/// requirement for Patient Access, Provider Access and Payer-to-Payer
/// (gap matrix G-31, and the PA half of G-33 and G-35).
///
/// <para>
/// authorization-service owns the projection because it owns the record. This
/// mirrors claims-service's <c>FhirExplanationOfBenefitController</c> exactly:
/// the domain service projects, and fhir-service stays a thin façade that
/// proxies here. There is no second prior-auth store and no FHIR-only copy of
/// a decision.
/// </para>
///
/// <para>
/// <b>Drugs are excluded</b> from everything this controller serves
/// (<see cref="PriorAuthorizationApiScope"/>). CMS-0057-F's PA-data APIs cover
/// medical prior authorization; a drug authorization is out of scope, and a
/// read of one answers 404 rather than disclosing it.
/// </para>
///
/// <para>
/// <b>Tenant and member scoping.</b> The tenant comes from the validated token
/// by way of the shared tenant middleware, never from a header or the query
/// string, and the repository filters on it. A search REQUIRES
/// <c>patient</c>: there is no tenant-wide prior-auth listing on this route,
/// because every consumer of it is acting for one member. Caller confinement
/// (a patient-bound SMART token, or the member Provider Access authorized) is
/// enforced in fhir-service, which also re-checks every resource it passes
/// back; this controller's own guarantee is that it never returns a record
/// belonging to another tenant or to a member other than the one asked for.
/// </para>
///
/// <para>
/// <b>Timeliness.</b> CMS-0057-F requires PA information to be available within
/// one business day of a status change. This reads the live authorization
/// record, so there is no propagation delay to manage — the API is current the
/// moment the record is.
/// </para>
/// </summary>
[ApiController]
[Route("fhir")]
[RequirePermission("authorizations:read")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class FhirPriorAuthorizationController : ControllerBase
{
    private const string FhirContentType = "application/fhir+json";
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    private readonly IAuthorizationRepository _repository;
    private readonly IPriorAuthorizationEobProjector _projector;
    private readonly ILogger<FhirPriorAuthorizationController> _logger;

    public FhirPriorAuthorizationController(
        IAuthorizationRepository repository,
        IPriorAuthorizationEobProjector projector,
        ILogger<FhirPriorAuthorizationController> logger)
    {
        _repository = repository;
        _projector = projector;
        _logger = logger;
    }

    /// <summary>
    /// GET /fhir/ExplanationOfBenefit/prior-authorization/{id} — read one
    /// prior-authorization EOB. <paramref name="id"/> is the FHIR resource id
    /// (<c>pa-{authorizationNumber}</c>); the bare authorization number is
    /// accepted too, so a caller holding a <c>preAuthRef</c> can read the
    /// resource without knowing the prefix.
    /// </summary>
    [HttpGet("ExplanationOfBenefit/prior-authorization/{id}")]
    [Produces(FhirContentType)]
    public async Task<IActionResult> ReadPriorAuthorization(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return Outcome(400, "invalid", "ExplanationOfBenefit id is required.");

        var number = PriorAuthorizationEobProjector.AuthorizationNumberFromResourceId(id) ?? id.Trim();

        Authorization? authorization;
        try
        {
            // Tenant-scoped in the repository: a number from another tenant
            // reads as not found, exactly as it does everywhere else.
            authorization = await _repository.GetByAuthorizationNumberAsync(number);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Prior-authorization ExplanationOfBenefit read failed for {Id}",
                LogSanitizer.Sanitize(id));
            return Outcome(500, "exception", "ExplanationOfBenefit read failed.");
        }

        // Out of CMS-0057-F scope is indistinguishable from absent, on purpose:
        // a 403 would tell the caller a drug authorization exists.
        if (authorization is null || !PriorAuthorizationApiScope.IsInScope(authorization))
            return Outcome(404, "not-found", "ExplanationOfBenefit not found.");

        return Fhir(200, _projector.Project(authorization));
    }

    /// <summary>
    /// GET /fhir/ExplanationOfBenefit/prior-authorization?patient=&amp;_count=&amp;_page=
    /// — search one member's prior authorizations, newest first.
    ///
    /// <para>
    /// Parameters honoured: <c>patient</c> (required), <c>status</c> (a FHIR
    /// <c>use=preauthorization</c> consumer filters on the authorization's own
    /// status; accepted as a CHO <see cref="AuthorizationStatus"/> name),
    /// <c>_count</c> (default 50, clamped to 200) and <c>_page</c> (1-based).
    /// Everything else is ignored rather than refused, so adding a parameter
    /// later is not a breaking change for a caller that already sends it.
    /// </para>
    /// </summary>
    [HttpGet("ExplanationOfBenefit/prior-authorization")]
    [Produces(FhirContentType)]
    public async Task<IActionResult> SearchPriorAuthorizations(
        [FromQuery] string? patient,
        [FromQuery] string? status,
        [FromQuery(Name = "_count")] int count = DefaultPageSize,
        [FromQuery(Name = "_page")] int page = 1,
        CancellationToken ct = default)
    {
        var normalizedPatient = StripPatientPrefix(patient);
        if (string.IsNullOrWhiteSpace(normalizedPatient))
        {
            return Outcome(400, "invalid",
                "Prior-authorization ExplanationOfBenefit search requires the patient search parameter.");
        }

        AuthorizationStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<AuthorizationStatus>(status.Trim(), ignoreCase: true, out var parsed))
                return Outcome(400, "invalid", "Unrecognised status value.");
            statusFilter = parsed;
        }

        var pageSize = Math.Clamp(count, 1, MaxPageSize);
        var pageNumber = Math.Max(1, page);

        IEnumerable<Authorization> records;
        try
        {
            records = await _repository.SearchAsync(
                memberId: normalizedPatient,
                providerNPI: null,
                serviceDateFrom: null,
                serviceDateTo: null,
                status: statusFilter,
                lineOfBusiness: null,
                page: pageNumber,
                pageSize: pageSize);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Prior-authorization ExplanationOfBenefit search failed.");
            return Outcome(500, "exception", "ExplanationOfBenefit search failed.");
        }

        var matched = records
            // Defence in depth: the repository filters by member, and a result
            // for anyone else means it did not. Dropping it here keeps this
            // controller's own guarantee true whatever the store does.
            .Where(a => string.Equals(a.MemberId, normalizedPatient, StringComparison.Ordinal))
            .Where(PriorAuthorizationApiScope.IsInScope)
            .OrderByDescending(a => a.SubmittedDate)
            .ThenBy(a => a.AuthorizationNumber, StringComparer.Ordinal)
            .ToList();

        return Fhir(200, BuildBundle(matched));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A searchset Bundle of the page's in-scope records.
    ///
    /// <para>
    /// <c>total</c> is the number of entries in THIS bundle, not a count across
    /// all pages. The repository's search returns a page without a total, and
    /// reporting a pre-filter count would overstate what the caller can see
    /// once drug authorizations are removed. A consumer paginates until a page
    /// comes back short, which is valid FHIR: <c>total</c> is optional in a
    /// searchset and the <c>Bundle.total</c> here is deliberately the page's.
    /// </para>
    /// </summary>
    private JsonObject BuildBundle(IReadOnlyList<Authorization> authorizations)
    {
        var entries = new JsonArray();
        foreach (var authorization in authorizations)
        {
            entries.Add(new JsonObject
            {
                ["resource"] = _projector.Project(authorization),
                ["search"] = new JsonObject { ["mode"] = "match" },
            });
        }

        return new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "searchset",
            ["total"] = authorizations.Count,
            ["entry"] = entries,
        };
    }

    private static string? StripPatientPrefix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var trimmed = value.Trim();
        const string prefix = "Patient/";
        return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[prefix.Length..]
            : trimmed;
    }

    private ContentResult Fhir(int status, JsonObject body)
        => new()
        {
            ContentType = FhirContentType,
            Content = body.ToJsonString(),
            StatusCode = status,
        };

    private ContentResult Outcome(int status, string code, string diagnostics)
        => Fhir(status, new JsonObject
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
        });
}
