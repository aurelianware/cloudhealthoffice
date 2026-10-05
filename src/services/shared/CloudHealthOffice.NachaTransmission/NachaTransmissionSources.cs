using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.NachaTransmission;

/// <summary>Where a tenant's transmission settings come from.</summary>
public interface INachaTransmissionSettingsSource
{
    /// <summary>The tenant's settings, or null when none are configured.</summary>
    Task<NachaTransmissionSettings?> GetAsync(string tenantId, CancellationToken cancellationToken = default);
}

/// <summary>Reads Key Vault secrets by name (the service's managed identity).</summary>
public interface INachaSecretReader
{
    Task<string> GetSecretAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>
/// <c>configuration.paymentControls.nachaTransmission</c> from tenant-service's
/// tenant record, read with this service's own service token for the tenant
/// (never the releasing user's, who may not see secret names), and only from a
/// configured CHO host.
/// </summary>
public sealed class HttpNachaTransmissionSettingsSource : INachaTransmissionSettingsSource
{
    public const string HttpClientName = "NachaTransmissionTenantSettings";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IChoServiceTokenSource? _tokens;
    private readonly ChoOutboundHosts? _hosts;
    private readonly ILogger<HttpNachaTransmissionSettingsSource> _logger;

    public HttpNachaTransmissionSettingsSource(
        IHttpClientFactory httpClientFactory, IServiceProvider services, ILogger<HttpNachaTransmissionSettingsSource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _tokens = ChoServiceTokens.Resolve(services);
        _hosts = services.GetService<ChoOutboundHosts>();
        _logger = logger;
    }

    public async Task<NachaTransmissionSettings?> GetAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        if (_tokens == null)
            throw new NachaTransmissionException(
                "NACHA transmission settings cannot be read: this service has no service token configured (ChoAuth:ServiceToken).");

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/tenants/{Uri.EscapeDataString(tenantId)}");
        if (_hosts?.IsChoService(new Uri(client.BaseAddress ?? new Uri("http://invalid"), request.RequestUri!)) != true)
            throw new NachaTransmissionException(
                "NACHA transmission settings cannot be read: TenantService:BaseUrl is not a configured CHO host (ChoAuth:Outbound:Hosts).");
        request.Headers.Add(TenantMiddleware.TenantHeaderName, tenantId);
        try
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokens.GetTokenAsync(tenantId, cancellationToken));
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (!response.IsSuccessStatusCode)
                throw new NachaTransmissionException(
                    $"tenant-service answered {(int)response.StatusCode} to the NACHA transmission settings read.");

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return Parse(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning("NACHA transmission settings for tenant {TenantId} could not be read: {Error}", tenantId, ex.GetType().Name);
            throw new NachaTransmissionException(
                $"NACHA transmission settings could not be read from tenant-service ({ex.GetType().Name}).");
        }
    }

    /// <summary><c>configuration.paymentControls.nachaTransmission</c>, or null when absent.</summary>
    public static NachaTransmissionSettings? Parse(string tenantJson)
    {
        using var doc = JsonDocument.Parse(tenantJson);
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("configuration", out var configuration)
            && configuration.ValueKind == JsonValueKind.Object
            && configuration.TryGetProperty("paymentControls", out var controls)
            && controls.ValueKind == JsonValueKind.Object
            && controls.TryGetProperty("nachaTransmission", out var transmission)
            && transmission.ValueKind == JsonValueKind.Object)
        {
            return transmission.Deserialize<NachaTransmissionSettings>(JsonOptions);
        }

        return null;
    }
}

/// <summary>Key Vault secrets with the service's managed identity (<c>DefaultAzureCredential</c>).</summary>
public sealed class KeyVaultNachaSecretReader : INachaSecretReader
{
    private readonly SecretClient _client;

    public KeyVaultNachaSecretReader(Uri vaultUri, TokenCredential credential)
        => _client = new SecretClient(vaultUri, credential);

    public async Task<string> GetSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        var secret = await _client.GetSecretAsync(name, cancellationToken: cancellationToken);
        return secret.Value.Value;
    }
}

/// <summary>No vault configured: every read fails, so transmission falls back.</summary>
public sealed class UnconfiguredNachaSecretReader : INachaSecretReader
{
    public Task<string> GetSecretAsync(string name, CancellationToken cancellationToken = default)
        => throw new NachaTransmissionException(
            "No Key Vault is configured for NACHA transmission credentials (NachaTransmission:KeyVaultUri or SecretProvider:AzureKeyVaultUri).",
            notConfigured: true);
}
