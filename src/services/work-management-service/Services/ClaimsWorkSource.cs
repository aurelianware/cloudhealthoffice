using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Events;

namespace WorkManagementService.Services;

/// <summary>One page of claims-service's <c>GET /api/claims/work-queue/snapshots</c>.</summary>
public sealed class ClaimWorkSnapshotPage
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public DateTime ObservedAt { get; set; }
    public List<ClaimWorkEvent> Items { get; set; } = new();
}

/// <summary>The pended claims of a tenant, as claims-service sees them now (the reconcile source).</summary>
public interface IClaimsWorkSource
{
    Task<ClaimWorkSnapshotPage> GetPendedPageAsync(string tenantId, int page, int pageSize, CancellationToken ct = default);
}

/// <summary>
/// HTTP client for claims-service. Inside an admin request the caller's CHO
/// token is forwarded; from the background reconcile, the shared outbound
/// handler mints this service's own token for the tenant named in
/// <c>X-Tenant-ID</c>. claims-service takes the tenant from the token.
/// </summary>
public sealed class HttpClaimsWorkSource : IClaimsWorkSource
{
    public const string HttpClientName = "claims-service";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public HttpClaimsWorkSource(IHttpClientFactory factory) => _http = factory.CreateClient(HttpClientName);

    public async Task<ClaimWorkSnapshotPage> GetPendedPageAsync(string tenantId, int page, int pageSize, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/claims/work-queue/snapshots?page={page}&pageSize={pageSize}");
        request.Headers.Add("X-Tenant-ID", tenantId);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ClaimWorkSnapshotPage>(Json, ct)
               ?? throw new InvalidOperationException("claims-service returned an empty snapshot page.");
    }
}
