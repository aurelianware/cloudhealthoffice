using System.Net;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;

namespace TenantService.Hierarchy;

/// <summary>A benefit plan's program, from benefit-plan-service.</summary>
public interface IBenefitPlanProgramLookup
{
    /// <summary>
    /// The program id the plan carries, or null when the plan is unknown or
    /// carries none. Throws when benefit-plan-service could not be asked, so a
    /// caller is never told "no program" because a service was down.
    /// </summary>
    Task<string?> GetProgramIdAsync(string tenantId, string planId, CancellationToken ct = default);

    /// <summary>
    /// The plan ids of every plan in a program, active or not. Empty when the
    /// program has none. Throws when benefit-plan-service could not be asked,
    /// for the same reason as <see cref="GetProgramIdAsync"/>: a caller
    /// narrowing a list by program must not be handed "no plans" because a
    /// service was down.
    /// </summary>
    Task<IReadOnlyList<string>> GetPlanIdsForProgramAsync(string tenantId, string programId, CancellationToken ct = default);
}

/// <summary>
/// Calls benefit-plan-service's <c>GET api/v1/plans/{planId}</c> with
/// tenant-service's own service identity, not the caller's token — the same
/// reasoning as <see cref="Configuration.HttpSponsorLineOfBusinessLookup"/>:
/// following the hierarchy is this service's job, and a caller allowed to read
/// a program need not be allowed to read benefit plans. The token is minted
/// for the tenant in <c>X-Tenant-ID</c>, which is always the caller's.
///
/// <para>
/// The plan → program link lives on the plan, in benefit-plan-service, because
/// that is the service that owns plans. tenant-service follows it here so that
/// every consumer asks one service one question (plan-configuration.md §7),
/// rather than each one joining two records for itself.
/// </para>
/// </summary>
public sealed class HttpBenefitPlanProgramLookup(IHttpClientFactory factory) : IBenefitPlanProgramLookup
{
    public const string HttpClientName = "benefit-plan-service";

    public async Task<string?> GetProgramIdAsync(string tenantId, string planId, CancellationToken ct = default)
    {
        var client = factory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/plans/{Uri.EscapeDataString(planId)}");
        request.Headers.Add("X-Tenant-ID", tenantId);
        request.UseChoServiceIdentity();
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (!string.Equals(property.Name, "programId", StringComparison.OrdinalIgnoreCase)) continue;
            var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        return null;
    }

    /// <summary>
    /// Reads <c>GET api/v1/plans?program={programId}&amp;activeOnly=false</c> and
    /// keeps the plan ids. Every plan of the program comes back whole, which is
    /// more than this needs; a projection on that endpoint is the obvious
    /// improvement once something reads it often enough to matter.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetPlanIdsForProgramAsync(string tenantId, string programId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(programId)) return [];

        var client = factory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"api/v1/plans?activeOnly=false&program={Uri.EscapeDataString(programId.Trim())}");
        request.Headers.Add("X-Tenant-ID", tenantId);
        request.UseChoServiceIdentity();
        using var response = await client.SendAsync(request, ct);
        // A 404 here would be the route missing, not an empty program: the list
        // endpoint answers an empty array for a program with no plans, so a 404
        // is a failure and is not read as "none".
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        var ids = new List<string>();
        foreach (var plan in doc.RootElement.EnumerateArray())
        {
            if (plan.ValueKind != JsonValueKind.Object) continue;
            if (!plan.TryGetProperty("planId", out var id) || id.ValueKind != JsonValueKind.String) continue;
            var value = id.GetString();
            if (!string.IsNullOrWhiteSpace(value)) ids.Add(value.Trim());
        }
        return ids;
    }
}
