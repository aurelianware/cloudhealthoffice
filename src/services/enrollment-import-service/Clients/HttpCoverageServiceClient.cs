using System.Net;
using System.Net.Http.Json;

namespace EnrollmentImportService.Clients;

public sealed class CoverageServiceException(string message, HttpStatusCode statusCode) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public class HttpCoverageServiceClient : ICoverageServiceClient
{
    public const string HttpClientName = "CoverageServiceWrite";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpCoverageServiceClient> _logger;

    public HttpCoverageServiceClient(IHttpClientFactory httpClientFactory, ILogger<HttpCoverageServiceClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task CreateAsync(string tenantId, CreateCoverageRequestDto request, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/coverage")
        {
            Content = JsonContent.Create(request)
        };
        httpRequest.Headers.Add("X-Tenant-ID", tenantId);

        using var response = await client.SendAsync(httpRequest, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogWarning("coverage-service rejected coverage creation for member {MemberId}: {Status} {Body}",
                SanitizeForLog(request.MemberId), response.StatusCode, SanitizeForLog(body));
            throw new CoverageServiceException(
                $"coverage-service create failed for {request.MemberId}: {response.StatusCode}", response.StatusCode);
        }
    }

    public async Task<IReadOnlyList<CoverageRecordDto>> GetMemberCoverageAsync(
        string tenantId, string memberId, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/coverage/member/{Uri.EscapeDataString(memberId)}/history?includeTerminated=true");
        httpRequest.Headers.Add("X-Tenant-ID", tenantId);

        using var response = await client.SendAsync(httpRequest, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }
        await EnsureSuccessAsync(response, "history lookup", memberId, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<List<CoverageRecordDto>>(cancellationToken: ct).ConfigureAwait(false)
            ?? [];
    }

    public async Task UpdateAsync(string tenantId, string coverageId, UpdateCoverageRequestDto request, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/coverage/{Uri.EscapeDataString(coverageId)}")
        {
            Content = JsonContent.Create(request)
        };
        httpRequest.Headers.Add("X-Tenant-ID", tenantId);

        using var response = await client.SendAsync(httpRequest, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "update", coverageId, ct).ConfigureAwait(false);
    }

    public async Task TerminateAsync(
        string tenantId, string coverageId, DateTime terminationDate, string? reasonCode, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var url = $"/api/v1/coverage/{Uri.EscapeDataString(coverageId)}?terminationDate={terminationDate:yyyy-MM-dd}";
        if (!string.IsNullOrEmpty(reasonCode))
        {
            url += $"&reasonCode={Uri.EscapeDataString(reasonCode)}";
        }
        using var httpRequest = new HttpRequestMessage(HttpMethod.Delete, url);
        httpRequest.Headers.Add("X-Tenant-ID", tenantId);

        using var response = await client.SendAsync(httpRequest, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "termination", coverageId, ct).ConfigureAwait(false);
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, string id, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        _logger.LogWarning("coverage-service rejected coverage {Operation} for {Id}: {Status} {Body}",
            operation, SanitizeForLog(id), response.StatusCode, SanitizeForLog(body));
        throw new CoverageServiceException(
            $"coverage-service {operation} failed for {id}: {response.StatusCode}", response.StatusCode);
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
