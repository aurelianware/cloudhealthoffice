using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Work;

namespace WorkManagementService.Services;

/// <summary>Enrollment exceptions that need a specialist now, as enrollment-import-service sees them (the reconcile source).</summary>
public interface IEnrollmentWorkSource
{
    Task<EnrollmentWorkSnapshotPage> GetWorkablePageAsync(string tenantId, int page, int pageSize, CancellationToken ct = default);
}

/// <summary>
/// HTTP client for enrollment-import-service's
/// <c>GET api/v1/enrollment/exceptions/work-snapshots</c>. Same token
/// handling as <see cref="HttpClaimsWorkSource"/>: the caller's token inside a
/// request, this service's own token for the tenant in X-Tenant-ID from the
/// background reconcile.
/// </summary>
public sealed class HttpEnrollmentWorkSource : IEnrollmentWorkSource
{
    public const string HttpClientName = "enrollment-import-service";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public HttpEnrollmentWorkSource(IHttpClientFactory factory) => _http = factory.CreateClient(HttpClientName);

    public async Task<EnrollmentWorkSnapshotPage> GetWorkablePageAsync(string tenantId, int page, int pageSize, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/enrollment/exceptions/work-snapshots?page={page}&pageSize={pageSize}");
        request.Headers.Add("X-Tenant-ID", tenantId);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<EnrollmentWorkSnapshotPage>(Json, ct)
               ?? throw new InvalidOperationException("enrollment-import-service returned an empty snapshot page.");
    }
}
