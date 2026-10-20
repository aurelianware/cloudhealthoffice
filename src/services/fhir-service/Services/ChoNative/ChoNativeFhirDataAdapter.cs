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
/// Patient and Coverage from CHO's own services (ADR 018): member-service and
/// coverage-service own the projections; this adapter turns FHIR search into
/// their structured queries and parses what they return.
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
/// Only Patient and Coverage are served; the router never sends anything else here.
/// </summary>
public sealed class ChoNativeFhirDataAdapter
{
    public const string MemberServiceClient = "ChoNativeMemberService";
    public const string CoverageServiceClient = "ChoNativeCoverageService";

    /// <summary>The identifier system under which CHO exposes the member id.</summary>
    public const string MemberIdSystem = "urn:cho:member-id";

    /// <summary>The code system of Coverage.type in coverage-service's projection.</summary>
    public const string LineOfBusinessSystem = "http://fhir.cloudhealthoffice.com/CodeSystem/cho-line-of-business";

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
        var body = await GetAsync(MemberServiceClient, "Patient", $"/api/v1/members/{Uri.EscapeDataString(id)}/fhir", ct);
        return body is null ? null : ParseResource<Patient>("Patient", body);
    }

    public async Task<(IReadOnlyList<Patient> Items, int Total)> SearchPatientsAsync(
        PatientSearchParams search, string tenantId, CancellationToken ct)
    {
        RequireRequestTenant(tenantId);
        var q = new QueryBuilder();

        // _id and a member-id identifier both name the member; both given must agree.
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
        return await SearchAsync<Patient>(MemberServiceClient, "Patient", "/api/v1/members/fhir/Patient" + q, ct);
    }

    // ── Coverage ─────────────────────────────────────────────────────────────

    public async Task<Coverage?> GetCoverageAsync(string id, string tenantId, CancellationToken ct)
    {
        RequireRequestTenant(tenantId);
        var body = await GetAsync(CoverageServiceClient, "Coverage", $"/api/v1/coverage/{Uri.EscapeDataString(id)}/fhir", ct);
        return body is null ? null : ParseResource<Coverage>("Coverage", body);
    }

    public async Task<(IReadOnlyList<Coverage> Items, int Total)> SearchCoverageAsync(
        CoverageSearchParams search, string tenantId, CancellationToken ct)
    {
        RequireRequestTenant(tenantId);
        var q = new QueryBuilder();
        q.Add("coverageId", search.Id);

        // patient and beneficiary both name the member (AND): when both are
        // given they must agree. search.Patient may already be the SMART-bound
        // patient the controller pinned.
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
        return await SearchAsync<Coverage>(CoverageServiceClient, "Coverage", "/api/v1/coverage/fhir/Coverage" + q, ct);
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

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
    private async Task<string?> GetAsync(string clientName, string resource, string path, CancellationToken ct)
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
            _logger.LogWarning(ex, "{Resource} upstream unreachable for {Path}", resource, LogSanitizer.Sanitize(path));
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
                _logger.LogWarning("{Resource} upstream answered {Status} for {Path}",
                    resource, (int)response.StatusCode, LogSanitizer.Sanitize(path));
                throw new FhirUpstreamException(resource, $"{resource} upstream is unavailable.");
            }
            return await response.Content.ReadAsStringAsync(ct);
        }
    }

    private async Task<(IReadOnlyList<T> Items, int Total)> SearchAsync<T>(
        string clientName, string resource, string path, CancellationToken ct) where T : Resource
    {
        var body = await GetAsync(clientName, resource, path, ct)
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
