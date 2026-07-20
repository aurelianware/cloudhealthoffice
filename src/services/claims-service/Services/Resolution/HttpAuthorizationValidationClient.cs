using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaimsService.Services;

namespace ClaimsService.Services.Resolution;

/// <summary>
/// HTTP-backed prior-authorization validation client. 404 and transport
/// failures return null so adjudication can distinguish "known invalid auth"
/// from "no authoritative auth signal available".
/// </summary>
public sealed class HttpAuthorizationValidationClient : IAuthorizationValidationClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpAuthorizationValidationClient> _logger;

    public HttpAuthorizationValidationClient(
        IHttpClientFactory httpClientFactory,
        ILogger<HttpAuthorizationValidationClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<AuthorizationValidationResult?> ValidateAsync(
        string tenantId,
        string authorizationNumber,
        string? procedureCode,
        DateTime serviceDate,
        string? providerNpi,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(authorizationNumber))
        {
            return null;
        }

        try
        {
            var client = _httpClientFactory.CreateClient(UpstreamClientNames.AuthorizationService);
            var encodedAuth = Uri.EscapeDataString(authorizationNumber.Trim());
            var serviceDateQuery = serviceDate.ToUniversalTime()
                .ToString("o", CultureInfo.InvariantCulture);
            var url = $"/api/authorizations/{encodedAuth}/validate" +
                      $"?serviceDate={Uri.EscapeDataString(serviceDateQuery)}";

            if (!string.IsNullOrWhiteSpace(procedureCode))
            {
                url += $"&procedureCode={Uri.EscapeDataString(procedureCode.Trim())}";
            }

            if (!string.IsNullOrWhiteSpace(providerNpi))
            {
                url += $"&providerNpi={Uri.EscapeDataString(providerNpi.Trim())}";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Tenant-ID", tenantId);

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Authorization service returned {StatusCode} validating authorization {AuthorizationNumber}",
                    response.StatusCode,
                    SanitizeForLog(authorizationNumber));
                return null;
            }

            return await response.Content
                .ReadFromJsonAsync<AuthorizationValidationResult>(JsonOptions, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException
                                    or TaskCanceledException
                                    or JsonException)
        {
            _logger.LogWarning(
                ex,
                "Authorization validation lookup failed for authorization {AuthorizationNumber} tenant {Tenant}",
                SanitizeForLog(authorizationNumber),
                SanitizeForLog(tenantId));
            return null;
        }
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
