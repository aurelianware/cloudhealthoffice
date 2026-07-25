using System.Net;
using System.Net.Http.Json;

namespace EnrollmentImportService.Clients;

public sealed class SponsorServiceException(string message, HttpStatusCode statusCode) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public class HttpSponsorServiceClient : ISponsorServiceClient
{
    public const string HttpClientName = "SponsorServiceWrite";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpSponsorServiceClient> _logger;

    public HttpSponsorServiceClient(IHttpClientFactory httpClientFactory, ILogger<HttpSponsorServiceClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<bool> ExistsAsync(string tenantId, string groupNumber, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/sponsors/{Uri.EscapeDataString(groupNumber)}");
        request.Headers.Add("X-Tenant-ID", tenantId);

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task CreateAsync(string tenantId, CreateSponsorRequestDto request, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sponsors")
        {
            Content = JsonContent.Create(request)
        };
        httpRequest.Headers.Add("X-Tenant-ID", tenantId);

        using var response = await client.SendAsync(httpRequest, ct).ConfigureAwait(false);

        // A 409 here means another concurrent import already created this
        // sponsor (group number) between our existence check and this call —
        // not a real failure, the sponsor exists either way.
        if (response.StatusCode == HttpStatusCode.Conflict) return;

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogWarning("sponsor-service rejected sponsor creation for {GroupNumber}: {Status} {Body}",
                SanitizeForLog(request.GroupNumber), response.StatusCode, SanitizeForLog(body));
            throw new SponsorServiceException(
                $"sponsor-service create failed for {request.GroupNumber}: {response.StatusCode}", response.StatusCode);
        }
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
