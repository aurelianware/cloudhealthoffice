using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Security;

namespace CapitationService.Services;

/// <summary>
/// Where capitation gets a provider's full routing and account numbers for a
/// NACHA credit. Called only after the releasing user passed the release
/// checks (payments:approve and separation of duties).
/// </summary>
public interface IProviderBankAccountSource
{
    Task<ProviderBankAccountLookup> GetForDisbursementAsync(string tenantId, string providerNpi, CancellationToken cancellationToken = default);
}

/// <summary>The full numbers, or why they are not available (the disbursement needs attention).</summary>
public sealed record ProviderBankAccountLookup(ProviderBankAccountDto? Account, string? Reason)
{
    public bool Found => Account != null;

    public static ProviderBankAccountLookup Of(ProviderBankAccountDto account) => new(account, null);

    public static ProviderBankAccountLookup Unavailable(string reason) => new(null, reason);
}

/// <summary>
/// provider-service's service-only full read,
/// <c>GET /api/v1/internal/providers/npi/{npi}/bank-account</c>, which returns
/// the provider's <em>active approved</em> account (dual control in
/// provider-service) and admits only capitation-service's service token.
///
/// <para>
/// The call always carries this service's own token for the statement's
/// tenant, minted here on the dedicated <see cref="HttpClientName"/> client,
/// never the releasing user's: provider-service refuses a user's token there
/// whatever its permissions. The token is only attached for a configured CHO
/// host (<see cref="ChoOutboundHosts"/>).
/// </para>
///
/// <list type="bullet">
///   <item>200 with EFT enabled and both numbers: found.</item>
///   <item>200 without numbers (EFT not enabled), 404 (no approved account,
///   unknown provider), a refusal (401/403), any other answer, or no answer:
///   unavailable with the reason, so the disbursement needs attention.</item>
/// </list>
/// Account numbers are never logged.
/// </summary>
public sealed class HttpProviderBankAccountSource : IProviderBankAccountSource
{
    public const string HttpClientName = "ProviderBankAccounts";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ChoTokenIssuer? _issuer;
    private readonly ChoAuthOptions? _authOptions;
    private readonly ChoOutboundHosts? _hosts;
    private readonly ILogger<HttpProviderBankAccountSource> _logger;

    public HttpProviderBankAccountSource(
        IHttpClientFactory httpClientFactory,
        IServiceProvider services,
        ILogger<HttpProviderBankAccountSource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _issuer = services.GetService<ChoTokenIssuer>();
        _authOptions = services.GetService<ChoAuthOptions>();
        _hosts = services.GetService<ChoOutboundHosts>();
        _logger = logger;
    }

    public async Task<ProviderBankAccountLookup> GetForDisbursementAsync(
        string tenantId, string providerNpi, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(providerNpi))
            return ProviderBankAccountLookup.Unavailable(
                "Provider bank details unavailable: no tenant or NPI to look up. Needs attention.");

        var clientId = _authOptions?.ServiceToken?.ClientId;
        if (_issuer == null || string.IsNullOrEmpty(clientId))
        {
            _logger.LogError("Bank details for provider {NPI} cannot be fetched: ChoAuth:ServiceToken is not configured", Sanitize(providerNpi));
            return ProviderBankAccountLookup.Unavailable(
                "Provider bank details unavailable: capitation-service has no service token configured (ChoAuth:ServiceToken). Needs attention.");
        }

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"/api/v1/internal/providers/npi/{Uri.EscapeDataString(providerNpi)}/bank-account");
            if (_hosts?.IsChoService(new Uri(client.BaseAddress ?? new Uri("http://invalid"), request.RequestUri!)) != true)
            {
                _logger.LogError("The provider-service URL {Host} is not a configured CHO host; no service token is sent",
                    client.BaseAddress?.Host);
                return ProviderBankAccountLookup.Unavailable(
                    "Provider bank details unavailable: ProviderService:BaseUrl is not a configured CHO host (ChoAuth:Outbound:Hosts). Needs attention.");
            }
            request.Headers.Add(TenantMiddleware.TenantHeaderName, tenantId);
            // Always this service's own token. Set here, the shared outbound
            // handler leaves it alone instead of forwarding the user's token.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _issuer.IssueServiceToken(clientId, tenantId));

            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var account = await response.Content.ReadFromJsonAsync<ProviderBankAccountDto>(JsonOptions, cancellationToken);
                if (account == null)
                    return ProviderBankAccountLookup.Unavailable(
                        $"provider-service returned an empty bank account for provider {providerNpi}. Needs attention.");
                if (!account.EftEnabled || string.IsNullOrEmpty(account.RoutingNumber) || string.IsNullOrEmpty(account.AccountNumber))
                    return ProviderBankAccountLookup.Unavailable(
                        $"Provider {providerNpi}'s approved bank account has no EFT routing and account numbers. Needs attention.");
                return ProviderBankAccountLookup.Of(account);
            }

            var body = await SafeReadAsync(response, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Provider {NPI} in tenant {TenantId} has no approved bank account in provider-service: {Body}",
                    Sanitize(providerNpi), Sanitize(tenantId), body);
                return ProviderBankAccountLookup.Unavailable(
                    $"Provider {providerNpi} has no approved bank account in provider-service (a pending change must be approved " +
                    $"by a user with payments:approve): {body}. Needs attention.");
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _logger.LogError("provider-service refused the bank-account read for provider {NPI} in tenant {TenantId} " +
                                 "({StatusCode}); check service authentication",
                    Sanitize(providerNpi), Sanitize(tenantId), (int)response.StatusCode);
                return ProviderBankAccountLookup.Unavailable(
                    $"provider-service refused the bank-account read for provider {providerNpi} " +
                    $"({(int)response.StatusCode} {response.StatusCode}). Needs attention.");
            }

            _logger.LogError("provider-service answered {StatusCode} to the bank-account read for provider {NPI} in tenant {TenantId}",
                (int)response.StatusCode, Sanitize(providerNpi), Sanitize(tenantId));
            return ProviderBankAccountLookup.Unavailable(
                $"provider-service answered {(int)response.StatusCode} {response.StatusCode} to the bank-account read " +
                $"for provider {providerNpi}: {body}. Needs attention.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogError("The bank-account read for provider {NPI} in tenant {TenantId} failed: {Error}",
                Sanitize(providerNpi), Sanitize(tenantId), ex.GetType().Name);
            return ProviderBankAccountLookup.Unavailable(
                $"The bank-account read for provider {providerNpi} from provider-service failed ({ex.GetType().Name}). Needs attention.");
        }
    }

    /// <summary>Error bodies only (never a 200 body, which holds numbers), trimmed.</summary>
    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            body = body.Replace("\r", string.Empty).Replace("\n", " ");
            return body.Length > 300 ? body[..300] : body;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Sanitize(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}

/// <summary>
/// No full-number source: every lookup needs attention. For hosts and tests
/// that do not wire provider-service; never silently "no account".
/// </summary>
public sealed class UnavailableProviderBankAccountSource : IProviderBankAccountSource
{
    public const string Reason =
        "Provider bank details are unavailable: no provider bank-account source is configured. Needs attention.";

    public Task<ProviderBankAccountLookup> GetForDisbursementAsync(string tenantId, string providerNpi, CancellationToken cancellationToken = default)
        => Task.FromResult(ProviderBankAccountLookup.Unavailable(Reason));
}
