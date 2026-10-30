using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudHealthOffice.Infrastructure.Logging;
using FhirService.Middleware;
using FhirService.Models;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Primitives;
using Task = System.Threading.Tasks.Task;

namespace FhirService.Services.ChoNative;

/// <summary>
/// Patient, Coverage, Encounter and Claim from CHO's own services (ADR 018 for
/// the first two, ADR 024 for the others): member-service, coverage-service,
/// encounter-service and claims-service own their projections; this adapter
/// turns FHIR search into their structured queries and parses what they return.
///
/// Identity and tenancy: the named clients carry the caller's tenant
/// (<see cref="TenantHeaderPropagationHandler"/>) and the right credential (the
/// shared outbound token handler forwards a CHO caller's token; for a SMART
/// caller <c>SmartCallerOutboundHandler</c> sends fhir-service's own service
/// token). The upstream filters every query by the token's tenant. A tenant
/// argument that differs from the request's tenant is refused outright.
///
/// Failure: upstream 404 is "not found" (null); 5xx, transport failure,
/// timeout or an unreadable body raise <see cref="FhirUpstreamException"/>
/// (FHIR 502); unsupported search input raises
/// <see cref="FhirSearchNotSupportedException"/> (FHIR 400).
///
/// Upstream paths are root-relative (<c>/api/v1/...</c>) so a configured base
/// URL that carries a path (<c>http://member-service/api</c>, as the Kubernetes
/// manifests have it) cannot shift them.
///
/// Patient, Coverage, Encounter and Claim are served; the router never sends
/// anything else here.
/// </summary>
public sealed class ChoNativeFhirDataAdapter
{
    public const string MemberServiceClient = "ChoNativeMemberService";
    public const string CoverageServiceClient = "ChoNativeCoverageService";
    public const string EncounterServiceClient = "ChoNativeEncounterService";
    public const string ClaimsServiceClient = "ChoNativeClaimsService";

    /// <summary>The identifier system under which CHO exposes the member id.</summary>
    public const string MemberIdSystem = "urn:cho:member-id";

    /// <summary>The code system of Coverage.type in coverage-service's projection.</summary>
    public const string LineOfBusinessSystem = "http://fhir.cloudhealthoffice.com/CodeSystem/cho-line-of-business";

    /// <summary>member-service's FHIR Patient search; its <c>memberId</c> filter is what confines a patient-bound search.</summary>
    public const string PatientSearchRoute = "/api/v1/members/fhir/Patient";

    /// <summary>coverage-service's FHIR Coverage search; its <c>memberId</c> filter is what confines a patient-bound search.</summary>
    public const string CoverageSearchRoute = "/api/v1/coverage/fhir/Coverage";

    /// <summary>
    /// encounter-service's FHIR Encounter search; its <c>memberId</c> filter is
    /// what confines a patient-bound search. The prefix has no <c>v1</c>
    /// segment because encounter-service's own API has none.
    /// </summary>
    public const string EncounterSearchRoute = "/api/encounters/fhir/Encounter";

    /// <summary>claims-service's FHIR Claim search; its <c>memberId</c> filter is what confines a patient-bound search.</summary>
    public const string ClaimSearchRoute = "/fhir/Claim";

    /// <summary>The code system of Encounter.type in encounter-service's projection.</summary>
    public const string EncounterTypeSystem = "urn:cho:encounter-type";

    /// <summary>The identifier system of Encounter.identifier (the submitter's control number).</summary>
    public const string EncounterControlNumberSystem = "urn:cho:encounter-control-number";

    /// <summary>The code system of Encounter.class.</summary>
    public const string ActCodeSystem = "http://terminology.hl7.org/CodeSystem/v3-ActCode";

    /// <summary>
    /// The one <c>_revinclude</c> accepted: US Core requires a server to take
    /// <c>Provenance:target</c> on every resource it searches. CHO keeps no
    /// Provenance resources, so the include it asks for is empty and the
    /// results are exactly those of the search without it.
    /// </summary>
    public const string ProvenanceRevInclude = "Provenance:target";

    /// <summary>CHO's 837 transaction variants, as Encounter.type codes.</summary>
    private static readonly string[] EncounterTypes = ["professional", "institutional", "dental"];

    /// <summary>
    /// FHIR Encounter statuses encounter-service's projection can emit. Any
    /// other status in the R4 value set is a valid code that selects nothing,
    /// so it answers an empty bundle rather than a 400.
    /// </summary>
    private static readonly string[] EncounterStatuses = ["finished", "entered-in-error"];

    /// <summary>The full R4 Encounter status value set, for telling a bad code from an unused one.</summary>
    private static readonly string[] AllEncounterStatuses =
        ["planned", "arrived", "triaged", "in-progress", "onleave", "finished", "cancelled", "entered-in-error", "unknown"];

    /// <summary>FHIR Claim statuses claims-service's projection can emit.</summary>
    private static readonly string[] ClaimStatuses = ["active", "cancelled"];

    /// <summary>The full R4 fm-status value set.</summary>
    private static readonly string[] AllClaimStatuses = ["active", "cancelled", "draft", "entered-in-error"];

    /// <summary>The deepest offset the CHO search endpoints accept.</summary>
    public const int MaxOffset = 10_000;

    private static readonly string[] LinesOfBusiness = ["Commercial", "Medicare", "Medicaid", "Exchange", "TRICARE", "VA"];

    private static readonly FhirJsonParser Parser = new(new ParserSettings { PermissiveParsing = false });

    private readonly IHttpClientFactory _clients;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<ChoNativeFhirDataAdapter> _logger;

    public ChoNativeFhirDataAdapter(
        IHttpClientFactory clients, IHttpContextAccessor http, ILogger<ChoNativeFhirDataAdapter> logger)
    {
        _clients = clients;
        _http = http;
        _logger = logger;
    }

    // ── Patient ──────────────────────────────────────────────────────────────

    public async Task<Patient?> GetPatientAsync(string id, string tenantId, CancellationToken ct)
    {
        RequireRequestTenant(tenantId);
        var body = await GetAsync(MemberServiceClient, "Patient", $"/api/v1/members/{Uri.EscapeDataString(id)}/fhir", "/api/v1/members/{memberId}/fhir", ct);
        return body is null ? null : ParseResource<Patient>("Patient", body);
    }

    public async Task<(IReadOnlyList<Patient> Items, int Total)> SearchPatientsAsync(
        PatientSearchParams search, string tenantId, CancellationToken ct)
    {
        RequireRequestTenant(tenantId);
        RejectUnsupportedParameters(PatientParameters);
        RejectRevIncludesOtherThanProvenance();
        var q = new QueryBuilder();

        // _id and a member-id identifier both name the member; both given must
        // agree. The value used is search.Id, which the controller may have
        // pinned to a patient-bound token's member; the raw query is only
        // checked for repeats and OR lists.
        FhirSearchTranslation.Single("_id", Values("_id", search.Id));
        var memberId = search.Id;
        if (FhirSearchTranslation.Single("identifier", Values("identifier", search.Identifier)) is { } identifier)
        {
            var value = FhirSearchTranslation.ParseToken("identifier", identifier, MemberIdSystem);
            if (memberId is not null && !string.Equals(memberId, value, StringComparison.Ordinal))
                return (Array.Empty<Patient>(), 0);
            memberId = value;
        }
        q.Add("memberId", memberId);
        q.Add("family", FhirSearchTranslation.Single("family", Values("family", search.Family)));
        q.Add("given", FhirSearchTranslation.Single("given", Values("given", search.Given)));
        q.Add("name", FhirSearchTranslation.Single("name", Values("name", search.Name)));

        if (FhirSearchTranslation.Single("gender", Values("gender", search.Gender)) is { } gender)
        {
            var code = FhirSearchTranslation.ParseToken("gender", gender, "http://hl7.org/fhir/administrative-gender").ToLowerInvariant();
            if (code is not ("male" or "female" or "other" or "unknown"))
                throw new FhirSearchNotSupportedException($"'{code}' is not an administrative-gender code.");
            q.Add("gender", code);
        }

        if (!AddRange(q, "birthDate", FhirSearchTranslation.ParseDates("birthdate", Values("birthdate", search.BirthDate)), dateOnly: true)
            || !AddRange(q, "lastUpdated", FhirSearchTranslation.ParseDates("_lastUpdated", Values("_lastUpdated", search.LastUpdated)), dateOnly: false))
        {
            return (Array.Empty<Patient>(), 0);
        }

        q.AddPage(search.Page, search.Count);
        return await SearchAsync<Patient>(MemberServiceClient, "Patient", PatientSearchRoute, PatientSearchRoute + q, ct);
    }

    // ── Coverage ─────────────────────────────────────────────────────────────

    public async Task<Coverage?> GetCoverageAsync(string id, string tenantId, CancellationToken ct)
    {
        RequireRequestTenant(tenantId);
        var body = await GetAsync(CoverageServiceClient, "Coverage", $"/api/v1/coverage/{Uri.EscapeDataString(id)}/fhir", "/api/v1/coverage/{id}/fhir", ct);
        return body is null ? null : ParseResource<Coverage>("Coverage", body);
    }

    public async Task<(IReadOnlyList<Coverage> Items, int Total)> SearchCoverageAsync(
        CoverageSearchParams search, string tenantId, CancellationToken ct)
    {
        RequireRequestTenant(tenantId);
        RejectUnsupportedParameters(CoverageParameters);
        var q = new QueryBuilder();
        FhirSearchTranslation.Single("_id", Values("_id", search.Id));
        q.Add("coverageId", search.Id);

        // patient and beneficiary both name the member (AND): when both are
        // given they must agree. search.Patient may already be the SMART-bound
        // patient the controller pinned.
        FhirSearchTranslation.Single("patient", Values("patient", search.Patient));
        var patient = search.Patient is null ? null : FhirSearchTranslation.ParsePatientReference("patient", search.Patient);
        var beneficiary = FhirSearchTranslation.Single("beneficiary", Values("beneficiary", search.Beneficiary)) is { } b
            ? FhirSearchTranslation.ParsePatientReference("beneficiary", b)
            : null;
        if (patient is not null && beneficiary is not null && !string.Equals(patient, beneficiary, StringComparison.Ordinal))
            return (Array.Empty<Coverage>(), 0);
        q.Add("memberId", patient ?? beneficiary);

        foreach (var status in FhirSearchTranslation.AnyOf("status", Values("status", search.Status)))
        {
            var code = FhirSearchTranslation.ParseToken("status", status, "http://hl7.org/fhir/fm-status").ToLowerInvariant();
            if (code is not ("active" or "cancelled" or "draft" or "entered-in-error"))
                throw new FhirSearchNotSupportedException($"'{code}' is not a Coverage status.");
            q.Add("status", code);
        }

        foreach (var type in FhirSearchTranslation.AnyOf("type", Values("type", search.Type)))
        {
            var code = FhirSearchTranslation.ParseToken("type", type, LineOfBusinessSystem);
            var lob = LinesOfBusiness.FirstOrDefault(l => string.Equals(l, code, StringComparison.OrdinalIgnoreCase))
                      ?? throw new FhirSearchNotSupportedException(
                          $"'{code}' is not a CHO line of business ({string.Join(", ", LinesOfBusiness)}).");
            q.Add("lineOfBusiness", lob);
        }

        if (!AddRange(q, "lastUpdated", FhirSearchTranslation.ParseDates("_lastUpdated", Values("_lastUpdated", search.LastUpdated)), dateOnly: false))
            return (Array.Empty<Coverage>(), 0);

        q.AddPage(search.Page, search.Count);
        return await SearchAsync<Coverage>(CoverageServiceClient, "Coverage", CoverageSearchRoute, CoverageSearchRoute + q, ct);
    }


    // ── Encounter ────────────────────────────────────────────────────────────

    public async Task<Encounter?> GetEncounterAsync(string id, string tenantId, CancellationToken ct)
    {
        RequireRequestTenant(tenantId);
        var body = await GetAsync(EncounterServiceClient, "Encounter",
            $"/api/encounters/{Uri.EscapeDataString(id)}/fhir", "/api/encounters/{id}/fhir", ct);
        return body is null ? null : ParseResource<Encounter>("Encounter", body);
    }

    public async Task<(IReadOnlyList<Encounter> Items, int Total)> SearchEncountersAsync(
        EncounterSearchParams search, string tenantId, CancellationToken ct)
    {
        RequireRequestTenant(tenantId);
        RejectUnsupportedParameters(EncounterParameters);
        RejectRevIncludesOtherThanProvenance();
        var q = new QueryBuilder();

        FhirSearchTranslation.Single("_id", Values("_id", search.Id));
        q.Add("encounterId", search.Id);

        if (FhirSearchTranslation.Single("identifier", Values("identifier", null)) is { } identifier)
            q.Add("controlNumber", FhirSearchTranslation.ParseToken("identifier", identifier, EncounterControlNumberSystem));

        // encounter-service matches the class codes through the place of
        // service they are derived from; one it never emits matches nothing.
        foreach (var encounterClass in FhirSearchTranslation.AnyOf("class", Values("class", null)))
            q.Add("encounterClass", FhirSearchTranslation.ParseToken("class", encounterClass, ActCodeSystem));

        // search.Patient may already be the SMART-bound patient the controller
        // pinned; it is what confines the search to one member upstream.
        FhirSearchTranslation.Single("patient", Values("patient", search.Patient));
        q.Add("memberId", search.Patient is null ? null : FhirSearchTranslation.ParsePatientReference("patient", search.Patient));

        if (ProjectedStatus("status", Values("status", search.Status), AllEncounterStatuses, EncounterStatuses,
                "http://hl7.org/fhir/encounter-status", "an Encounter status") is { } statusOutcome)
        {
            if (statusOutcome.MatchesNothing) return (Array.Empty<Encounter>(), 0);
            q.Add("status", statusOutcome.Code);
        }

        foreach (var type in FhirSearchTranslation.AnyOf("type", Values("type", search.Type)))
        {
            var code = FhirSearchTranslation.ParseToken("type", type, EncounterTypeSystem);
            var matched = EncounterTypes.FirstOrDefault(t => string.Equals(t, code, StringComparison.OrdinalIgnoreCase))
                          ?? throw new FhirSearchNotSupportedException(
                              $"'{code}' is not a CHO encounter type ({string.Join(", ", EncounterTypes)}).");
            // The upstream binds the CHO EncounterType enum, whose names are
            // the codes with an initial capital.
            q.Add("encounterType", char.ToUpperInvariant(matched[0]) + matched[1..]);
        }

        if (!AddRange(q, "serviceDate", FhirSearchTranslation.ParseDates("date", Values("date", search.Date)), dateOnly: false)
            || !AddRange(q, "lastUpdated", FhirSearchTranslation.ParseDates("_lastUpdated", Values("_lastUpdated", search.LastUpdated)), dateOnly: false))
        {
            return (Array.Empty<Encounter>(), 0);
        }

        q.AddPage(search.Page, search.Count);
        return await SearchAsync<Encounter>(EncounterServiceClient, "Encounter", EncounterSearchRoute, EncounterSearchRoute + q, ct);
    }

    // ── Claim ────────────────────────────────────────────────────────────────

    public async Task<Claim?> GetClaimAsync(string id, string tenantId, CancellationToken ct)
    {
        RequireRequestTenant(tenantId);
        var body = await GetAsync(ClaimsServiceClient, "Claim",
            $"/fhir/Claim/{Uri.EscapeDataString(id)}", "/fhir/Claim/{id}", ct);
        return body is null ? null : ParseResource<Claim>("Claim", body);
    }

    public async Task<(IReadOnlyList<Claim> Items, int Total)> SearchClaimsAsync(
        ClaimSearchParams search, string tenantId, CancellationToken ct)
    {
        RequireRequestTenant(tenantId);
        RejectUnsupportedParameters(ClaimParameters);
        var q = new QueryBuilder();

        FhirSearchTranslation.Single("_id", Values("_id", search.Id));
        q.Add("claimVersionId", search.Id);

        FhirSearchTranslation.Single("patient", Values("patient", search.Patient));
        q.Add("memberId", search.Patient is null ? null : FhirSearchTranslation.ParsePatientReference("patient", search.Patient));

        if (ProjectedStatus("status", Values("status", search.Status), AllClaimStatuses, ClaimStatuses,
                "http://hl7.org/fhir/fm-status", "a Claim status") is { } statusOutcome)
        {
            if (statusOutcome.MatchesNothing) return (Array.Empty<Claim>(), 0);
            q.Add("status", statusOutcome.Code);
        }

        // Every claim this projection serves has use = claim: a prior
        // authorization is a different record, projected by
        // authorization-service as a PDex prior-authorization EOB. Asking for
        // any other use selects nothing here rather than quietly widening.
        foreach (var use in FhirSearchTranslation.AnyOf("use", Values("use", search.Use)))
        {
            var code = FhirSearchTranslation.ParseToken("use", use, "http://hl7.org/fhir/claim-use").ToLowerInvariant();
            if (code is not ("claim" or "preauthorization" or "predetermination"))
                throw new FhirSearchNotSupportedException($"'{code}' is not a Claim use.");
            if (code != "claim") return (Array.Empty<Claim>(), 0);
        }

        if (!AddRange(q, "created", FhirSearchTranslation.ParseDates("created", Values("created", search.Created)), dateOnly: false)
            || !AddRange(q, "lastUpdated", FhirSearchTranslation.ParseDates("_lastUpdated", Values("_lastUpdated", search.LastUpdated)), dateOnly: false))
        {
            return (Array.Empty<Claim>(), 0);
        }

        q.AddPage(search.Page, search.Count);
        return await SearchAsync<Claim>(ClaimsServiceClient, "Claim", ClaimSearchRoute, ClaimSearchRoute + q, ct);
    }

    /// <summary>
    /// A status token resolved against what the projection can actually emit.
    /// A code outside the resource's value set is a 400; a valid code the
    /// projection never emits selects nothing, so the caller answers an empty
    /// bundle rather than refusing a legitimate query. Null when no status was
    /// given.
    /// </summary>
    private static (string Code, bool MatchesNothing)? ProjectedStatus(
        string parameter, IEnumerable<string?> values, string[] valueSet, string[] emitted, string system, string description)
    {
        (string, bool)? outcome = null;
        foreach (var raw in FhirSearchTranslation.AnyOf(parameter, values))
        {
            var code = FhirSearchTranslation.ParseToken(parameter, raw, system).ToLowerInvariant();
            if (!valueSet.Contains(code, StringComparer.Ordinal))
                throw new FhirSearchNotSupportedException($"'{code}' is not {description}.");
            if (!emitted.Contains(code, StringComparer.Ordinal))
                return (code, true);
            // Repeated status parameters AND, and one record has one status, so
            // two different emitted codes can match nothing.
            if (outcome is { Item1: var earlier } && !string.Equals(earlier, code, StringComparison.Ordinal))
                return (code, true);
            outcome = (code, false);
        }
        return outcome;
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    /// <summary>Request parameters that never filter: paging and output format.</summary>
    private static readonly string[] ControlParameters = ["_count", "_page", "_format", "_pretty"];

    private static readonly HashSet<string> PatientParameters = new(
        ControlParameters.Concat(["_id", "_lastUpdated", "identifier", "family", "given", "name", "gender", "birthdate", "_revinclude"]),
        StringComparer.Ordinal);

    private static readonly HashSet<string> CoverageParameters = new(
        ControlParameters.Concat(["_id", "_lastUpdated", "patient", "beneficiary", "status", "type"]),
        StringComparer.Ordinal);

    private static readonly HashSet<string> EncounterParameters = new(
        ControlParameters.Concat(["_id", "_lastUpdated", "patient", "date", "status", "type", "identifier", "class", "_revinclude"]),
        StringComparer.Ordinal);

    private static readonly HashSet<string> ClaimParameters = new(
        ControlParameters.Concat(["_id", "_lastUpdated", "patient", "created", "status", "use"]),
        StringComparer.Ordinal);

    /// <summary>
    /// Refuses any parameter this server would not apply — an unknown name, a
    /// modifier (<c>family:exact</c>), <c>_include</c>, <c>_sort</c> — so a
    /// caller never receives a broader result than the query it sent (the
    /// bundle's self link echoes the query as given).
    /// </summary>
    private void RejectUnsupportedParameters(IReadOnlySet<string> supported)
    {
        var query = _http.HttpContext?.Request.Query;
        if (query is null) return;
        var unsupported = query.Keys.Where(k => !supported.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (unsupported.Count > 0)
        {
            throw new FhirSearchNotSupportedException(
                "Unsupported search parameter(s): " + string.Join(", ", unsupported)
                + ". Supported: " + string.Join(", ", supported.Except(ControlParameters).OrderBy(k => k, StringComparer.Ordinal)) + ".");
        }
    }

    /// <summary>
    /// Accepts <see cref="ProvenanceRevInclude"/> and refuses any other
    /// <c>_revinclude</c>, which would ask for resources this server does not
    /// return.
    /// </summary>
    private void RejectRevIncludesOtherThanProvenance()
    {
        var query = _http.HttpContext?.Request.Query;
        if (query is null || !query.TryGetValue("_revinclude", out StringValues values)) return;
        var other = values.SelectMany(v => (v ?? "").Split(','))
            .Where(v => !string.Equals(v.Trim(), ProvenanceRevInclude, StringComparison.Ordinal))
            .ToList();
        if (other.Count > 0)
        {
            throw new FhirSearchNotSupportedException(
                $"Unsupported _revinclude: {string.Join(", ", other)}. Supported: {ProvenanceRevInclude}.");
        }
    }

    /// <summary>
    /// Every value the request gave for a parameter. FHIR repeats a parameter
    /// to AND it (birthdate=ge1980&amp;birthdate=lt1990), which a bound string
    /// property cannot carry, so the raw query is read when there is one.
    /// </summary>
    private IEnumerable<string?> Values(string parameter, string? bound)
    {
        var query = _http.HttpContext?.Request.Query;
        if (query is not null && query.TryGetValue(parameter, out StringValues raw) && raw.Count > 0)
            return raw.ToArray();
        return [bound];
    }

    /// <summary>Adds a range; false when it can match nothing (the caller answers empty).</summary>
    private static bool AddRange(QueryBuilder q, string name, FhirSearchTranslation.DateRange? range, bool dateOnly)
    {
        if (range is not { } r) return true;
        if (r.IsEmpty) return false;
        if (dateOnly)
        {
            // Dates of birth are stored as dates: any instant within a day
            // selects the whole day.
            var from = r.From?.Date;
            var to = r.To is { } t ? (t.TimeOfDay == TimeSpan.Zero ? t.Date : t.Date.AddDays(1)) : (DateTime?)null;
            if (from is { } f && to is { } e && f >= e) return false;
            q.Add(name + "From", from?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            q.Add(name + "To", to?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        else
        {
            q.Add(name + "From", r.From is { } f ? FhirSearchTranslation.Iso(f) : null);
            q.Add(name + "To", r.To is { } t ? FhirSearchTranslation.Iso(t) : null);
        }
        return true;
    }

    private void RequireRequestTenant(string tenantId)
    {
        var requestTenant = _http.HttpContext?.GetTenantId();
        if (string.IsNullOrEmpty(tenantId) || (requestTenant is not null && !string.Equals(requestTenant, tenantId, StringComparison.Ordinal)))
        {
            _logger.LogError(
                "CHO-native FHIR read refused: tenant argument {ParamTenant} does not match the request tenant {RequestTenant}.",
                LogSanitizer.Sanitize(tenantId), LogSanitizer.Sanitize(requestTenant));
            throw new InvalidOperationException("The tenant argument does not match the request's tenant.");
        }
    }

    /// <summary>The body of a 2xx answer, or null on 404.</summary>
    /// <param name="route">The route template, for logs: the path itself can carry member ids and names.</param>
    private async Task<string?> GetAsync(string clientName, string resource, string path, string route, CancellationToken ct)
    {
        var client = _clients.CreateClient(clientName);
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(path, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            _logger.LogWarning(ex, "{Resource} upstream unreachable at {Route}", resource, route);
            throw new FhirUpstreamException(resource, $"{resource} upstream is unreachable.", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (!response.IsSuccessStatusCode)
            {
                // 5xx is an outage; 4xx means fhir-service sent something the
                // upstream refused (a contract or credential defect), which the
                // FHIR caller cannot fix either. Neither body is passed on.
                _logger.LogWarning("{Resource} upstream answered {Status} at {Route}",
                    resource, (int)response.StatusCode, route);
                throw new FhirUpstreamException(resource, $"{resource} upstream is unavailable.");
            }
            try
            {
                return await response.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "{Resource} upstream response body could not be read at {Route}", resource, route);
                throw new FhirUpstreamException(resource, $"{resource} upstream is unreachable.", ex);
            }
        }
    }

    private async Task<(IReadOnlyList<T> Items, int Total)> SearchAsync<T>(
        string clientName, string resource, string route, string path, CancellationToken ct) where T : Resource
    {
        var body = await GetAsync(clientName, resource, path, route, ct)
                   ?? throw new FhirUpstreamException(resource, $"{resource} search endpoint was not found upstream.");
        try
        {
            var page = JsonNode.Parse(body)?.AsObject()
                       ?? throw new FormatException("empty search response");
            var total = page["total"]?.GetValue<long>() ?? throw new FormatException("missing total");
            var items = page["items"]?.AsArray() ?? throw new FormatException("missing items");
            var resources = items
                .Select(i => i?["resource"]?.ToJsonString() ?? throw new FormatException("item without a resource"))
                .Select(json => ParseResource<T>(resource, json))
                .ToList();
            return (resources, (int)Math.Min(total, int.MaxValue));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "{Resource} upstream search response was unreadable", resource);
            throw new FhirUpstreamException(resource, $"{resource} upstream returned an unreadable response.", ex);
        }
    }

    private T ParseResource<T>(string resource, string json) where T : Resource
    {
        try
        {
            return Parser.Parse<T>(json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{Resource} upstream projection is not a valid FHIR {Resource}", resource, resource);
            throw new FhirUpstreamException(resource, $"{resource} upstream returned an unreadable response.", ex);
        }
    }

    /// <summary>A query string of the parameters that have values, escaped.</summary>
    private sealed class QueryBuilder
    {
        private readonly StringBuilder _sb = new();

        public void Add(string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            _sb.Append(_sb.Length == 0 ? '?' : '&')
               .Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value.Trim()));
        }

        /// <summary>FHIR's 1-based page of <paramref name="count"/> as an offset and limit.</summary>
        public void AddPage(int page, int count)
        {
            var size = Math.Max(count, 0);
            var offset = (long)(Math.Max(page, 1) - 1) * size;
            if (offset > MaxOffset)
                throw new FhirSearchNotSupportedException(
                    $"Pages deeper than {MaxOffset} results are not supported; narrow the search instead.");
            Add("offset", offset.ToString(CultureInfo.InvariantCulture));
            Add("limit", size.ToString(CultureInfo.InvariantCulture));
        }

        public override string ToString() => _sb.ToString();
    }
}
