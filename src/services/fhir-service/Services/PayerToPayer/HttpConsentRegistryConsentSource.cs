using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Consent.Contracts;
using FhirService.Services.Consent;
using FhirService.Middleware;

namespace FhirService.Services.PayerToPayer;

/// <summary>
/// Production <see cref="IPayerToPayerConsentSource"/>: reads the member's
/// consent snapshots from consent-service, the authoritative registry.
///
/// It calls the registry's PHI-free authorization projection
/// (<c>GET api/v1/members/{memberId}/consents/authorization-snapshots</c>), so
/// deciding "may we disclose?" never pulls a member's narrative consent text
/// across a service boundary.
///
/// Follows the <see cref="HttpFhirAppealAdapter"/> pattern: a named client with
/// the tenant and correlation propagation handlers attached, so consent-service
/// sees the same tenant context the FHIR caller arrived with.
///
/// Fail-closed: anything other than a successful, parseable response yields NO
/// snapshots, and no snapshots means no authorization. A registry that is down
/// stops the exchange rather than waving it through.
/// </summary>
public sealed class HttpConsentRegistryConsentSource : IPayerToPayerConsentSource, IConsentSource
{
    public const string HttpClientName = "ChoConsentService";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly IHttpClientFactory _clientFactory;
    private readonly ILogger<HttpConsentRegistryConsentSource> _logger;

    public HttpConsentRegistryConsentSource(
        IHttpClientFactory clientFactory, ILogger<HttpConsentRegistryConsentSource> logger)
    {
        _clientFactory = clientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ConsentAuthorizationSnapshot>> GetConsentsAsync(
        string tenantId, string memberId, CancellationToken ct = default)
    {
        var client = _clientFactory.CreateClient(HttpClientName);
        var path = $"api/v1/members/{Uri.EscapeDataString(memberId)}/consents/authorization-snapshots";

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(path, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Category only: the exception can name internal hosts.
            _logger.LogWarning(
                "Consent registry unreachable for tenant={Tenant} ({Fault}); authorization will be denied.",
                Clean(tenantId), ex.GetType().Name);
            return Array.Empty<ConsentAuthorizationSnapshot>();
        }

        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.LogWarning(
                    "Consent registry returned {Status} for tenant={Tenant}; authorization will be denied.",
                    (int)response.StatusCode, Clean(tenantId));
                return Array.Empty<ConsentAuthorizationSnapshot>();
            }

            try
            {
                var payload = await response.Content
                    .ReadFromJsonAsync<ConsentSnapshotEnvelope>(JsonOptions, ct);
                return payload?.Items ?? (IReadOnlyList<ConsentAuthorizationSnapshot>)
                    Array.Empty<ConsentAuthorizationSnapshot>();
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                _logger.LogWarning(
                    "Consent registry response could not be read for tenant={Tenant}; authorization will be denied.",
                    Clean(tenantId));
                return Array.Empty<ConsentAuthorizationSnapshot>();
            }
        }
    }

    private sealed class ConsentSnapshotEnvelope
    {
        public List<ConsentAuthorizationSnapshot> Items { get; set; } = new();
    }

    /// <summary>Strips CR/LF so an id cannot forge a log entry (CWE-117).</summary>
    private static string Clean(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\r", string.Empty, StringComparison.Ordinal)
                   .Replace("\n", string.Empty, StringComparison.Ordinal);
}
