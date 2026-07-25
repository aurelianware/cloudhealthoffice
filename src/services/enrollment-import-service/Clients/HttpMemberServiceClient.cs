using System.Net;
using System.Net.Http.Json;

namespace EnrollmentImportService.Clients;

/// <summary>Thrown when member-service rejects or fails a write. Distinct from a
/// simple bool so callers can log/report the actual status and body.</summary>
public sealed class MemberServiceException(string message, HttpStatusCode statusCode) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public class HttpMemberServiceClient : IMemberServiceClient
{
    public const string HttpClientName = "MemberServiceWrite";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpMemberServiceClient> _logger;

    public HttpMemberServiceClient(IHttpClientFactory httpClientFactory, ILogger<HttpMemberServiceClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<bool> ExistsAsync(string tenantId, string memberId, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/members/{Uri.EscapeDataString(memberId)}");
        request.Headers.Add("X-Tenant-ID", tenantId);

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task CreateAsync(string tenantId, CreateMemberRequestDto request, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/members")
        {
            Content = JsonContent.Create(request)
        };
        httpRequest.Headers.Add("X-Tenant-ID", tenantId);

        using var response = await client.SendAsync(httpRequest, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogWarning("member-service rejected member creation for {MemberId}: {Status} {Body}",
                SanitizeForLog(request.MemberId), response.StatusCode, SanitizeForLog(body));
            throw new MemberServiceException(
                $"member-service create failed for {request.MemberId}: {response.StatusCode}", response.StatusCode);
        }
    }

    public async Task UpdateAsync(string tenantId, string memberId, UpdateMemberRequestDto request, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/members/{Uri.EscapeDataString(memberId)}")
        {
            Content = JsonContent.Create(request)
        };
        httpRequest.Headers.Add("X-Tenant-ID", tenantId);

        using var response = await client.SendAsync(httpRequest, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogWarning("member-service rejected member update for {MemberId}: {Status} {Body}",
                SanitizeForLog(memberId), response.StatusCode, SanitizeForLog(body));
            throw new MemberServiceException(
                $"member-service update failed for {memberId}: {response.StatusCode}", response.StatusCode);
        }
    }

    public async Task TerminateAsync(string tenantId, string memberId, TerminateMemberRequestDto request, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/members/{Uri.EscapeDataString(memberId)}/terminate")
        {
            Content = JsonContent.Create(request)
        };
        httpRequest.Headers.Add("X-Tenant-ID", tenantId);

        using var response = await client.SendAsync(httpRequest, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogWarning("member-service rejected member termination for {MemberId}: {Status} {Body}",
                SanitizeForLog(memberId), response.StatusCode, SanitizeForLog(body));
            throw new MemberServiceException(
                $"member-service terminate failed for {memberId}: {response.StatusCode}", response.StatusCode);
        }
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
