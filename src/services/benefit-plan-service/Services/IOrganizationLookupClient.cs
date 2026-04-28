using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace BenefitPlanService.Services;

/// <summary>
/// Cross-service read client for <c>Organization</c> (the canonical
/// payer-defined network from provider-service capability 5.3). Used by
/// benefit-plan-service to resolve <c>NetworkTier.NetworkId</c>
/// references at write time so operators get an early failure when a
/// plan references a network that doesn't exist or has been terminated.
///
/// <para>
/// HTTP-only contract via the named <c>HttpClient("ProviderService")</c>
/// registered in <c>Program.cs</c>. No project reference; mirrors
/// <see cref="HttpProviderIntegrityGate"/> from capability 5.10.
/// </para>
///
/// <para>
/// Capability 5.5 ships only <see cref="GetOrganizationAsync"/>. The
/// per-claim membership check (<c>IsProviderInNetworkAsync</c>) and its
/// in-process cache deferred to the capability that actually consumes
/// it — see plan-first record in
/// <c>docs/architecture/network-tier-organization-reference.md</c>.
/// </para>
/// </summary>
public interface IOrganizationLookupClient
{
    /// <summary>
    /// Returns the head version of the network identified by
    /// <paramref name="networkId"/> (the chain key
    /// <c>Organization.OrganizationId</c>), or <c>null</c> when the
    /// network does not exist or provider-service is unreachable.
    /// Failures are logged at warning and surfaced as null so callers
    /// can apply their own policy (warn-and-continue at write time
    /// today).
    /// </summary>
    Task<OrganizationLookupResult?> GetOrganizationAsync(
        string networkId, CancellationToken ct = default);
}

/// <summary>
/// Subset of the provider-service <c>Organization</c> wire shape that
/// benefit-plan-service actually needs at the moment. Bound by the
/// <c>HttpClient.GetFromJsonAsync</c> deserializer; unknown wire fields
/// are ignored.
/// </summary>
public sealed record OrganizationLookupResult
{
    [JsonPropertyName("organizationId")]
    public string OrganizationId { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("effectiveDate")]
    public DateTime EffectiveDate { get; init; }

    [JsonPropertyName("terminationDate")]
    public DateTime? TerminationDate { get; init; }
}

/// <summary>
/// HTTP-backed <see cref="IOrganizationLookupClient"/> against
/// <c>provider-service</c> via the same named client used by
/// <see cref="HttpProviderIntegrityGate"/>.
/// </summary>
public class HttpOrganizationLookupClient : IOrganizationLookupClient
{
    public const string ProviderServiceClientName = HttpProviderIntegrityGate.ProviderServiceClientName;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpOrganizationLookupClient> _logger;

    public HttpOrganizationLookupClient(
        IHttpClientFactory httpClientFactory,
        ILogger<HttpOrganizationLookupClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<OrganizationLookupResult?> GetOrganizationAsync(
        string networkId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(networkId))
        {
            return null;
        }

        try
        {
            var client = _httpClientFactory.CreateClient(ProviderServiceClientName);
            var encoded = Uri.EscapeDataString(networkId);
            using var response = await client.GetAsync($"api/v1/networks/{encoded}", ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Provider service returned {StatusCode} for network {NetworkId}",
                    response.StatusCode, SanitizeForLog(networkId));
                return null;
            }

            return await response.Content.ReadFromJsonAsync<OrganizationLookupResult>(ct);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-driven cancellation must propagate. Swallowing this
            // would surface as an `unresolved` outcome and obscure the
            // actual cancellation signal from the orchestrator.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // HttpRequestException → transport / DNS failure.
            // TaskCanceledException without ct.IsCancellationRequested →
            // HttpClient request timeout (the typed client's Timeout
            // surfaces as TaskCanceledException). Treat both as
            // "unresolved" so the caller's policy (warn + continue at
            // write time today) applies.
            _logger.LogWarning(ex,
                "Provider service unreachable for network {NetworkId}; treating as unresolved",
                SanitizeForLog(networkId));
            return null;
        }
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
