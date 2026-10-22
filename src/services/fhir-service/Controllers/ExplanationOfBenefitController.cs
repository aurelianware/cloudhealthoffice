using FhirService.Services.Identity;
using System.Text;
using FhirService.Services;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// FHIR R4 ExplanationOfBenefit resource — read and search (capability
/// 5.11). Thin proxy over claims-service's
/// <c>FhirExplanationOfBenefitController</c>; claims-service owns the
/// canonical CHO projection. Mirrors the BP 5.8
/// <see cref="InsurancePlanController"/> proxy and the Provider 5.7-5.9
/// <see cref="ProviderDirectoryController"/> proxies — fhir-service
/// remains the single FHIR façade for external consumers while each
/// domain service owns its own projection.
///
/// <para>
/// SMART patient binding is enforced upstream by
/// <see cref="Middleware.SmartScopeEnforcementMiddleware"/>: it rejects
/// requests where an explicit <c>patient</c> param does not match the
/// bound patient and stores the bound id in
/// <c>HttpContext.Items["SmartPatientId"]</c> for the controller to
/// auto-inject. CMS-0057-F Patient Access requires EOBs searchable by
/// patient with one of those two paths satisfied.
/// </para>
/// </summary>
[Route("fhir/r4")]
// Two owners behind one resource type (see PriorAuthorizationPath): claims EOBs
// need claims:read, prior-authorization EOBs need authorizations:read. The
// policy admits either, and each branch then checks the one it actually needs
// (RequireChoPermission), so a CHO caller holding only one of them cannot read
// the other's data.
[FhirAccess(smart: true, cho: "claims:read,authorizations:read")]
public class ExplanationOfBenefitController : FhirControllerBase
{
    /// <summary>
    /// Back-compat alias used by tests that referenced the
    /// controller-local constant before capability 5.11 introduced the
    /// shared <see cref="UpstreamClientNames.ClaimsService"/> name. New
    /// code should use the shared constant directly.
    /// </summary>
    public const string ClaimsServiceClientName = UpstreamClientNames.ClaimsService;

    /// <summary>
    /// The <c>use</c> search value that selects prior authorizations rather than
    /// claims. Da Vinci PDex represents a prior authorization as an
    /// <c>ExplanationOfBenefit</c> with <c>use = preauthorization</c>, and
    /// CMS-0057-F's PA-data requirement is served through it (gap matrix G-31,
    /// and the PA half of G-33 and G-35).
    /// </summary>
    public const string PreauthorizationUse = "preauthorization";

    /// <summary>
    /// authorization-service's prior-authorization EOB route. Owned there
    /// because the record is owned there, exactly as claims-service owns the
    /// claim projection; fhir-service adds nothing but the façade.
    /// </summary>
    internal const string PriorAuthorizationPath = "fhir/ExplanationOfBenefit/prior-authorization";

    /// <summary>
    /// Resource-id prefix authorization-service puts on a prior-authorization
    /// EOB (<c>pa-{authorizationNumber}</c>). Claim EOB ids are claim-version
    /// ids and never start with it, so a read routes to the owning service from
    /// the id alone without asking both.
    /// </summary>
    internal const string PriorAuthorizationIdPrefix = "pa-";

    /// <summary>
    /// The Da Vinci PDex profile the prior-authorization projection is shaped
    /// toward, advertised in the CapabilityStatement. It names what CHO is
    /// implementing; it is not a conformance claim, because no profile
    /// validation has run against the projection (authorization-service's
    /// <c>PriorAuthorizationEobProjector</c> lists what is missing).
    /// </summary>
    public const string PdexPriorAuthorizationProfile =
        "http://hl7.org/fhir/us/davinci-pdex/StructureDefinition/pdex-prior-authorization";

    /// <summary>CHO permission a claims-EOB request needs.</summary>
    private const string ClaimsReadPermission = "claims:read";

    /// <summary>CHO permission a prior-authorization-EOB request needs.</summary>
    private const string AuthorizationsReadPermission = "authorizations:read";

    private readonly HttpClient _claimsServiceClient;
    private readonly HttpClient _authorizationServiceClient;
    private readonly ILogger<ExplanationOfBenefitController> _logger;

    public ExplanationOfBenefitController(
        IHttpClientFactory httpClientFactory,
        ILogger<ExplanationOfBenefitController> logger)
    {
        _claimsServiceClient = httpClientFactory.CreateClient(UpstreamClientNames.ClaimsService);
        _authorizationServiceClient = httpClientFactory.CreateClient(UpstreamClientNames.AuthorizationService);
        _logger = logger;
    }

    /// <summary>
    /// Enforces the permission the branch actually needs for a CHO caller. The
    /// controller policy admits <c>claims:read</c> OR
    /// <c>authorizations:read</c> because one resource type has two owners;
    /// without this a caller holding only one could read the other's data. A
    /// SMART caller is governed by its scopes and the confinement checks, not
    /// by CHO permissions, so it passes through.
    /// </summary>
    private IActionResult? RequireChoPermission(string permission)
    {
        if (!FhirService.Services.Identity.FhirCallerSchemes.IsCho(User)) return null;
        if (CloudHealthOffice.Infrastructure.Security.ChoPrincipal.HasPermission(User, permission)) return null;

        _logger.LogWarning(
            "ExplanationOfBenefit request refused: CHO caller lacks {Permission}",
            permission);
        return FhirForbidden("Not permitted to read this ExplanationOfBenefit.");
    }

    /// <summary>
    /// GET /fhir/r4/ExplanationOfBenefit/{id} — read EOB by claim version
    /// id. Proxies to claims-service /fhir/ExplanationOfBenefit/{id}; the
    /// upstream owns the canonical CHO projection and resolves
    /// adjustment chains to their head version (capability 5.11
    /// Decision 11). Path id is URL-encoded so embedded slashes / spaces
    /// don't bypass the upstream route binding.
    /// </summary>
    [HttpGet("ExplanationOfBenefit/{id}")]
    [Produces("application/fhir+json")]
    public Task<IActionResult> ReadEob(string id, CancellationToken ct)
    {
        // Both branches apply the same patient confinement: the upstream answers
        // for the tenant, not for a patient, so a patient-bound token (or a
        // provider just authorized for one member) may only see a resource
        // whose patient is that member.
        var confinement = AuthorizedMemberId is null
            ? (Func<string, bool>?)null
            : body => !IsOutsidePatientContext(EobPatientReference(body));

        if (IsPriorAuthorizationId(id))
        {
            if (RequireChoPermission(AuthorizationsReadPermission) is { } refused) return Task.FromResult(refused);

            return ProxyAuthorizationServiceAsync(
                "ExplanationOfBenefit",
                $"{PriorAuthorizationPath}/{Uri.EscapeDataString(id)}",
                ct,
                confinement);
        }

        if (RequireChoPermission(ClaimsReadPermission) is { } claimsRefused) return Task.FromResult(claimsRefused);

        return ProxyClaimsServiceAsync(
            "ExplanationOfBenefit",
            $"fhir/ExplanationOfBenefit/{Uri.EscapeDataString(id)}",
            ct,
            confinement);
    }

    /// <summary>
    /// True when the id names a prior-authorization EOB, which
    /// authorization-service owns. Claim EOB ids are claim-version ids and
    /// never carry the prefix.
    /// </summary>
    internal static bool IsPriorAuthorizationId(string? id)
        => !string.IsNullOrWhiteSpace(id)
           && id.Trim().StartsWith(PriorAuthorizationIdPrefix, StringComparison.Ordinal);

    /// <summary>The EOB's <c>patient.reference</c>; null when absent or unreadable.</summary>
    internal static string? EobPatientReference(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("patient", out var patient)
                   && patient.ValueKind == System.Text.Json.JsonValueKind.Object
                   && patient.TryGetProperty("reference", out var reference)
                   && reference.ValueKind == System.Text.Json.JsonValueKind.String
                ? reference.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// GET /fhir/r4/ExplanationOfBenefit?patient=&amp;_id=&amp;_count=&amp;_page=
    /// — search EOBs. Forwards the FHIR search query string to
    /// claims-service /fhir/ExplanationOfBenefit unchanged after
    /// auto-injecting the SMART-bound patient id when the caller did
    /// not supply <c>patient</c>. CMS-0057-F requires patient context on
    /// EOB search; if neither <c>patient</c> nor <c>_id</c> nor a SMART
    /// binding is available we short-circuit to a FHIR 400 rather than
    /// forwarding a request that's guaranteed to fail upstream.
    /// </summary>
    [HttpGet("ExplanationOfBenefit")]
    [Produces("application/fhir+json")]
    public Task<IActionResult> SearchEobs(CancellationToken ct = default)
    {
        var query = HttpContext.Request.Query;
        // Normalize FHIR-typed reference form — `patient=Patient/123` is
        // equivalent to `patient=123` per FHIR search semantics, and
        // claims-service stores raw member ids. Without the strip, an
        // upstream search for `Patient/{id}` silently returns empty.
        // SmartPatientId is already normalized by the middleware so it
        // doesn't need the same treatment, but mirror it here for symmetry.
        var explicitPatient = StripPatientPrefix(query["patient"].FirstOrDefault());
        var explicitId = query["_id"].FirstOrDefault();

        // SMART scope enforcement middleware already rejects a mismatched
        // explicit patient param and surfaces the bound patient id via
        // SmartPatientId. Auto-inject when the caller didn't provide one
        // so patient-app callers don't need to know their own member id.
        // A request confined to one member (patient binding, or the member
        // Provider Access just authorized) is pinned to that member, whatever
        // else the query says.
        var effectivePatient = AuthorizedMemberId
            ?? (string.IsNullOrEmpty(explicitPatient) ? null : explicitPatient);

        var leavesConfinement = AuthorizedMemberId is null
            ? (Func<string, bool>?)null
            : body => BundlePatientReferences(body) is not { } patients || patients.Any(IsOutsidePatientContext);

        // PDex serves a prior authorization as an ExplanationOfBenefit with
        // use=preauthorization, so that is the discriminator between the two
        // owners of this resource type. A search WITHOUT `use` is the claims
        // search it has always been — the two result sets are not merged,
        // because one bundle cannot be paged coherently across two upstreams
        // with independent cursors. The CapabilityStatement says so, and names
        // `use` as the parameter that selects prior authorizations.
        if (WantsPriorAuthorizations(query))
        {
            if (RequireChoPermission(AuthorizationsReadPermission) is { } refused) return Task.FromResult(refused);

            if (string.IsNullOrEmpty(effectivePatient))
            {
                return Task.FromResult(FhirBadRequest(
                    "Prior-authorization ExplanationOfBenefit search requires the patient search parameter. " +
                    "Provide a patient-scoped token or an explicit patient search parameter."));
            }

            var priorAuthPath = PriorAuthorizationPath + BuildPriorAuthorizationQueryString(query, effectivePatient);
            return ProxyAuthorizationServiceAsync(
                "ExplanationOfBenefit", priorAuthPath, ct, leavesConfinement: leavesConfinement);
        }

        if (RequireChoPermission(ClaimsReadPermission) is { } claimsRefused) return Task.FromResult(claimsRefused);

        if (string.IsNullOrEmpty(effectivePatient) && string.IsNullOrEmpty(explicitId))
        {
            return Task.FromResult(FhirBadRequest(
                "ExplanationOfBenefit search requires either the patient or _id search parameter. " +
                "Provide a patient-scoped token or an explicit patient search parameter."));
        }

        var path = "fhir/ExplanationOfBenefit" + BuildUpstreamQueryString(query, effectivePatient);
        return ProxyClaimsServiceAsync("ExplanationOfBenefit", path, ct,
            leavesConfinement: leavesConfinement);
    }

    /// <summary>
    /// True when the search asks for prior authorizations: any <c>use</c> value
    /// of <c>preauthorization</c>. A <c>use</c> naming something else (a claim,
    /// a predetermination) is NOT a prior-auth search and falls through to the
    /// claims upstream, which decides what it supports.
    /// </summary>
    internal static bool WantsPriorAuthorizations(IQueryCollection query)
        => query["use"].Any(v =>
            string.Equals(v?.Trim(), PreauthorizationUse, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The query string for authorization-service's prior-authorization route.
    /// Unlike the claims proxy this does NOT forward every inbound parameter:
    /// <c>use</c> is consumed here (it selected this upstream and means nothing
    /// downstream), and the route takes <c>patient</c>, <c>status</c>,
    /// <c>_count</c> and <c>_page</c>. Forwarding the rest would imply support
    /// this path does not have.
    /// </summary>
    private static string BuildPriorAuthorizationQueryString(
        IQueryCollection inbound, string effectivePatient)
    {
        var sb = new StringBuilder();
        var first = true;
        AppendParam(sb, ref first, "patient", effectivePatient);

        foreach (var name in new[] { "status", "_count", "_page" })
        {
            var value = inbound[name].FirstOrDefault();
            if (!string.IsNullOrEmpty(value))
                AppendParam(sb, ref first, name, value);
        }

        return sb.ToString();
    }

    /// <summary>
    /// The <c>patient.reference</c> of every entry in a search Bundle (null
    /// for an entry without one); null when the body is not a Bundle, which a
    /// confined search treats as leaving its scope.
    /// </summary>
    internal static IReadOnlyList<string?>? BundlePatientReferences(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || !root.TryGetProperty("resourceType", out var type) || type.GetString() != "Bundle")
                return null;
            if (!root.TryGetProperty("entry", out var entries))
                return [];
            if (entries.ValueKind != System.Text.Json.JsonValueKind.Array)
                return null;

            var references = new List<string?>();
            foreach (var entry in entries.EnumerateArray())
            {
                references.Add(entry.ValueKind == System.Text.Json.JsonValueKind.Object
                               && entry.TryGetProperty("resource", out var resource)
                    ? EobPatientReference(resource.GetRawText())
                    : null);
            }
            return references;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string? StripPatientPrefix(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        const string prefix = "Patient/";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..]
            : value;
    }

    /// <summary>
    /// Rewrites the inbound query string with the effective (possibly
    /// auto-injected) patient parameter. Preserves all other params
    /// verbatim so future search-parameter additions in claims-service
    /// don't require a fhir-service-side change.
    /// </summary>
    private static string BuildUpstreamQueryString(
        IQueryCollection inbound, string? effectivePatient)
    {
        var sb = new StringBuilder();
        var first = true;
        var patientWritten = false;

        foreach (var (key, values) in inbound)
        {
            if (string.Equals(key, "patient", StringComparison.OrdinalIgnoreCase))
            {
                // Replace with the effective value (may be the original
                // value or the SMART-bound id).
                if (!string.IsNullOrEmpty(effectivePatient))
                {
                    AppendParam(sb, ref first, "patient", effectivePatient);
                    patientWritten = true;
                }
                continue;
            }

            foreach (var value in values)
            {
                if (value is null) continue;
                AppendParam(sb, ref first, key, value);
            }
        }

        if (!patientWritten && !string.IsNullOrEmpty(effectivePatient))
        {
            AppendParam(sb, ref first, "patient", effectivePatient);
        }

        return sb.Length == 0 ? string.Empty : sb.ToString();
    }

    private static void AppendParam(StringBuilder sb, ref bool first, string key, string value)
    {
        sb.Append(first ? '?' : '&');
        first = false;
        sb.Append(Uri.EscapeDataString(key));
        sb.Append('=');
        sb.Append(Uri.EscapeDataString(value));
    }

    private Task<IActionResult> ProxyClaimsServiceAsync(
        string resourceLabel,
        string path,
        CancellationToken ct,
        Func<string, bool>? successBodyAllowed = null,
        Func<string, bool>? leavesConfinement = null)
        => ProxyUpstreamServiceAsync(
            _claimsServiceClient,
            "claims-service",
            resourceLabel,
            path,
            _logger,
            ct,
            successBodyAllowed,
            leavesConfinement);

    private Task<IActionResult> ProxyAuthorizationServiceAsync(
        string resourceLabel,
        string path,
        CancellationToken ct,
        Func<string, bool>? successBodyAllowed = null,
        Func<string, bool>? leavesConfinement = null)
        => ProxyUpstreamServiceAsync(
            _authorizationServiceClient,
            "authorization-service",
            resourceLabel,
            path,
            _logger,
            ct,
            successBodyAllowed,
            leavesConfinement);
}
