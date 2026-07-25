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

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
