using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudHealthOffice.Infrastructure.Logging;
using FhirService.Middleware;

namespace FhirService.Services.ChoNative;

/// <summary>A member as member-service projects it, with the subscriber a Patient does not carry.</summary>
public sealed record DirectoryMember(string MemberId, string? SubscriberMemberId, JsonObject Patient);

/// <summary>A bounded candidate list: <see cref="Complete"/> is false when the bound cut it short.</summary>
public sealed record DirectoryMembers(IReadOnlyList<DirectoryMember> Members, bool Complete);

/// <summary>
/// The member, coverage and claims reads Payer-to-Payer needs from CHO's own
/// services (ADR 020): members by member id, by subscriber and by birth date
/// from member-service; a member's coverages from coverage-service; a member's
/// ExplanationOfBenefit resources from claims-service. Every result is the
/// owning service's own projection.
///
/// Identity, tenancy and failure as <see cref="ChoNativeFhirDataAdapter"/>:
/// the named clients carry the request's tenant and fhir-service's service
/// identity for a SMART caller; a tenant argument other than the request's is
/// refused; a 5xx, a transport failure or an unreadable body raises
/// <see cref="FhirUpstreamException"/> (FHIR 502), never an empty answer. Logs
/// name route templates, never member ids or names.
/// </summary>
public sealed class ChoNativeMemberDirectory
{
    /// <summary>The page size used against member-service and coverage-service (their maximum).</summary>
    public const int PageSize = 100;

    /// <summary>The page size used against claims-service's EOB search (its maximum).</summary>
    public const int EobPageSize = 200;

    /// <summary>Most EOB pages read for one member: 10,000 EOBs.</summary>
    public const int MaxEobPages = 50;

    private readonly IHttpClientFactory _clients;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<ChoNativeMemberDirectory> _logger;

    public ChoNativeMemberDirectory(
        IHttpClientFactory clients, IHttpContextAccessor http, ILogger<ChoNativeMemberDirectory> logger)
    {
        _clients = clients;
        _http = http;
        _logger = logger;
    }

    /// <summary>The member with exactly this member id, or none.</summary>
    public async Task<DirectoryMember?> FindByMemberIdAsync(string tenantId, string memberId, CancellationToken ct = default)
    {
        RequireRequestTenant(tenantId);
        var page = await MembersPageAsync($"memberId={Escape(memberId)}", 0, 2, ct);
        return page.Members.FirstOrDefault(m => string.Equals(m.MemberId, memberId, StringComparison.Ordinal));
    }

    /// <summary>The members whose subscriber may be <paramref name="subscriberMemberId"/>, up to <paramref name="max"/>.</summary>
    public Task<DirectoryMembers> FindBySubscriberAsync(
        string tenantId, string subscriberMemberId, int max, CancellationToken ct = default)
    {
        RequireRequestTenant(tenantId);
        return MembersAsync($"subscriberMemberId={Escape(subscriberMemberId)}", max, ct);
    }

    /// <summary>The members born on <paramref name="birthDate"/>, up to <paramref name="max"/>.</summary>
    public Task<DirectoryMembers> FindByBirthDateAsync(
        string tenantId, DateOnly birthDate, int max, CancellationToken ct = default)
    {
        RequireRequestTenant(tenantId);
        var from = birthDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var to = birthDate.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return MembersAsync($"birthDateFrom={from}&birthDateTo={to}", max, ct);
    }

    /// <summary>Every coverage of the member, as coverage-service projects it.</summary>
    public async Task<IReadOnlyList<JsonObject>> GetCoveragesAsync(string tenantId, string memberId, CancellationToken ct = default)
    {
        RequireRequestTenant(tenantId);
        var all = new List<JsonObject>();
        for (var offset = 0; ; offset += PageSize)
        {
            var body = await GetAsync(ChoNativeFhirDataAdapter.CoverageServiceClient, "Coverage",
                $"{ChoNativeFhirDataAdapter.CoverageSearchRoute}?memberId={Escape(memberId)}&offset={offset}&limit={PageSize}",
                ChoNativeFhirDataAdapter.CoverageSearchRoute, ct);
            var (total, items) = ReadPage("Coverage", body);
            foreach (var item in items)
            {
                var resource = item?["resource"] as JsonObject ?? throw Unreadable("Coverage");
                all.Add(resource.DeepClone().AsObject());
            }
            if (items.Count == 0 || offset + PageSize >= total || offset + PageSize > ChoNativeFhirDataAdapter.MaxOffset) break;
        }
        return all;
    }

    /// <summary>
    /// The member's ExplanationOfBenefit resources, as claims-service projects
    /// them, up to <see cref="MaxEobPages"/> pages.
    /// </summary>
    public async Task<IReadOnlyList<JsonObject>> GetExplanationsOfBenefitAsync(
        string tenantId, string memberId, CancellationToken ct = default)
    {
        RequireRequestTenant(tenantId);
        const string route = "fhir/ExplanationOfBenefit";
        var all = new List<JsonObject>();
        for (var page = 1; page <= MaxEobPages; page++)
        {
            var body = await GetAsync(UpstreamClientNames.ClaimsService, "ExplanationOfBenefit",
                $"{route}?patient={Escape(memberId)}&_count={EobPageSize}&_page={page}", route, ct);
            JsonArray entries;
            long total;
            try
            {
                var bundle = JsonNode.Parse(body) as JsonObject ?? throw new FormatException("not an object");
                if (bundle["resourceType"]?.GetValue<string>() != "Bundle") throw new FormatException("not a Bundle");
                total = bundle["total"]?.GetValue<long>() ?? 0;
                entries = bundle["entry"] as JsonArray ?? [];
            }
            catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
            {
                throw Unreadable("ExplanationOfBenefit", ex);
            }

            foreach (var entry in entries)
            {
                var resource = entry?["resource"] as JsonObject ?? throw Unreadable("ExplanationOfBenefit");
                // Defence in depth: claims-service filters by patient; anything
                // else in the answer is never exported as this member's.
                var patient = resource["patient"]?["reference"]?.GetValue<string>();
                if (!string.Equals(patient, $"Patient/{memberId}", StringComparison.Ordinal))
                {
                    _logger.LogError("ExplanationOfBenefit search returned a record for another patient; the export is refused.");
                    throw new FhirUpstreamException("ExplanationOfBenefit",
                        "ExplanationOfBenefit upstream returned records outside the member.");
                }
                all.Add(resource.DeepClone().AsObject());
            }
            if (entries.Count == 0 || (long)page * EobPageSize >= total) break;
        }
        return all;
    }

    private async Task<DirectoryMembers> MembersAsync(string filter, int max, CancellationToken ct)
    {
        var all = new List<DirectoryMember>();
        for (var offset = 0; offset < max; offset += PageSize)
        {
            var page = await MembersPageAsync(filter, offset, Math.Min(PageSize, max - offset), ct);
            all.AddRange(page.Members);
            if (page.Members.Count == 0 || offset + page.Members.Count >= page.Total)
                return new DirectoryMembers(all, Complete: true);
        }
        // The bound was reached with members still unread.
        return new DirectoryMembers(all, Complete: false);
    }

    private async Task<(IReadOnlyList<DirectoryMember> Members, long Total)> MembersPageAsync(
        string filter, int offset, int limit, CancellationToken ct)
    {
        var body = await GetAsync(ChoNativeFhirDataAdapter.MemberServiceClient, "Patient",
            $"{ChoNativeFhirDataAdapter.PatientSearchRoute}?{filter}&offset={offset}&limit={limit}",
            ChoNativeFhirDataAdapter.PatientSearchRoute, ct);
        var (total, items) = ReadPage("Patient", body);
        var members = new List<DirectoryMember>(items.Count);
        foreach (var item in items)
        {
            try
            {
                var memberId = item?["memberId"]?.GetValue<string>();
                var resource = item?["resource"] as JsonObject;
                if (string.IsNullOrEmpty(memberId) || resource is null) throw new FormatException("item without a member");
                var subscriber = item!["subscriberMemberId"]?.GetValue<string>();
                members.Add(new DirectoryMember(memberId, string.IsNullOrWhiteSpace(subscriber) ? null : subscriber,
                    resource.DeepClone().AsObject()));
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                throw Unreadable("Patient", ex);
            }
        }
        return (members, total);
    }

    private static (long Total, JsonArray Items) ReadPage(string resource, string body)
    {
        try
        {
            var page = JsonNode.Parse(body) as JsonObject ?? throw new FormatException("not an object");
            var total = page["total"]?.GetValue<long>() ?? throw new FormatException("missing total");
            var items = page["items"] as JsonArray ?? throw new FormatException("missing items");
            return (total, items);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            throw Unreadable(resource, ex);
        }
    }

    private static FhirUpstreamException Unreadable(string resource, Exception? inner = null) =>
        new(resource, $"{resource} upstream returned an unreadable response.", inner);

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private void RequireRequestTenant(string tenantId)
    {
        var requestTenant = _http.HttpContext?.GetTenantId();
        if (string.IsNullOrEmpty(tenantId) || (requestTenant is not null && !string.Equals(requestTenant, tenantId, StringComparison.Ordinal)))
        {
            _logger.LogError(
                "CHO-native member directory read refused: tenant argument {ParamTenant} does not match the request tenant {RequestTenant}.",
                LogSanitizer.Sanitize(tenantId), LogSanitizer.Sanitize(requestTenant));
            throw new InvalidOperationException("The tenant argument does not match the request's tenant.");
        }
    }

    /// <summary>The body of a 2xx answer. A 404 means the search route is missing upstream: an outage, not "none".</summary>
    private async Task<string> GetAsync(string clientName, string resource, string path, string route, CancellationToken ct)
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
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("{Resource} upstream answered {Status} at {Route}", resource, (int)response.StatusCode, route);
                throw new FhirUpstreamException(resource, response.StatusCode == HttpStatusCode.NotFound
                    ? $"{resource} search endpoint was not found upstream."
                    : $"{resource} upstream is unavailable.");
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
}
