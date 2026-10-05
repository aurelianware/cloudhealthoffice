using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Security;
using PremiumBillingService.Models;

namespace PremiumBillingService.Clients;

/// <summary>
/// Where premium billing gets a sponsor's bank details for auto-debit (EFT/ACH
/// drafts and NACHA debit files).
/// </summary>
public interface ISponsorBankAccountSource
{
    Task<SponsorBankAccountLookup> GetAsync(string tenantId, string groupNumber, CancellationToken cancellationToken = default);
}

public enum SponsorBankAccountLookupStatus
{
    /// <summary>The sponsor's bank details are known.</summary>
    Found,

    /// <summary>
    /// The system of record says the sponsor is not enrolled in auto-debit.
    /// A normal state: the sponsor pays another way.
    /// </summary>
    NotEnrolled,

    /// <summary>
    /// Nobody could say whether the sponsor is enrolled or what its account is.
    /// Never a normal state: the item needs attention.
    /// </summary>
    Unavailable
}

public sealed record SponsorBankAccountLookup(
    SponsorBankAccountLookupStatus Status, SponsorBankAccount? Account, string? Reason)
{
    public static SponsorBankAccountLookup Found(SponsorBankAccount account) =>
        account.EftEnabled
            ? new(SponsorBankAccountLookupStatus.Found, account, null)
            : new(SponsorBankAccountLookupStatus.NotEnrolled, account, null);

    public static SponsorBankAccountLookup NotEnrolled() => new(SponsorBankAccountLookupStatus.NotEnrolled, null, null);

    public static SponsorBankAccountLookup Unavailable(string reason) =>
        new(SponsorBankAccountLookupStatus.Unavailable, null, reason);
}

/// <summary>
/// A source with nothing behind it: every lookup answers
/// <see cref="SponsorBankAccountLookupStatus.Unavailable"/>, so a draft is
/// refused loudly as needing attention. No longer registered in production
/// (see <see cref="HttpSponsorBankAccountSource"/>); kept for tests and as
/// the fail-closed behaviour.
/// </summary>
public sealed class UnavailableSponsorBankAccountSource : ISponsorBankAccountSource
{
    public const string Reason =
        "Sponsor bank details are unavailable: no sponsor bank-account source is configured, " +
        "so auto-debit cannot be performed. Needs attention.";

    public Task<SponsorBankAccountLookup> GetAsync(string tenantId, string groupNumber, CancellationToken cancellationToken = default)
        => Task.FromResult(SponsorBankAccountLookup.Unavailable(Reason));
}

/// <summary>
/// The production source: sponsor-service's service-only full read,
/// <c>GET /api/v1/internal/sponsors/{group}/bank-account</c>, which returns the
/// sponsor's <em>active approved</em> account (dual control in sponsor-service)
/// and admits only premium-billing-service's service token.
///
/// <para>
/// The call always carries this service's own token for the record's tenant,
/// never the caller's: sponsor-service refuses a user's token there whatever
/// its permissions. EftDraftService calls this only after it has checked the
/// user who releases the debit (payments:approve, user token, maker-checker),
/// so the service token fetches numbers for a debit a user already released.
/// </para>
///
/// <list type="bullet">
///   <item>200 with <c>eftEnabled: true</c>: <see cref="SponsorBankAccountLookupStatus.Found"/>.</item>
///   <item>200 with <c>eftEnabled: false</c>: <see cref="SponsorBankAccountLookupStatus.NotEnrolled"/> (a normal skip).</item>
///   <item>404 (no approved account, unknown sponsor), a refusal (401/403),
///   any other answer, or no answer: <see cref="SponsorBankAccountLookupStatus.Unavailable"/>
///   with the reason, so the item needs attention.</item>
/// </list>
/// Account numbers are never logged.
/// </summary>
public sealed class HttpSponsorBankAccountSource : ISponsorBankAccountSource
{
    public const string HttpClientName = "SponsorBankAccounts";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IChoServiceTokenSource? _tokens;
    private readonly ILogger<HttpSponsorBankAccountSource> _logger;

    public HttpSponsorBankAccountSource(
        IHttpClientFactory httpClientFactory,
        IServiceProvider services,
        ILogger<HttpSponsorBankAccountSource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _tokens = ChoServiceTokens.Resolve(services);
        _logger = logger;
    }

    public async Task<SponsorBankAccountLookup> GetAsync(string tenantId, string groupNumber, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(groupNumber))
            return SponsorBankAccountLookup.Unavailable(
                "Sponsor bank details unavailable: no tenant or group number to look up. Needs attention.");

        if (_tokens == null)
        {
            _logger.LogError("Sponsor bank details for group {GroupNumber} cannot be fetched: ChoAuth:ServiceToken is not configured",
                Sanitize(groupNumber));
            return SponsorBankAccountLookup.Unavailable(
                "Sponsor bank details unavailable: premium-billing-service has no service token configured (ChoAuth:ServiceToken). Needs attention.");
        }

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"/api/v1/internal/sponsors/{Uri.EscapeDataString(groupNumber)}/bank-account");
            request.Headers.Add(TenantMiddleware.TenantHeaderName, tenantId);
            // Always this service's own token. Set here, the shared outbound
            // handler leaves it alone instead of forwarding the user's token.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokens.GetTokenAsync(tenantId, cancellationToken));

            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var account = await response.Content.ReadFromJsonAsync<SponsorBankAccount>(JsonOptions, cancellationToken);
                return account == null
                    ? SponsorBankAccountLookup.Unavailable(
                        $"sponsor-service returned an empty bank account for sponsor {groupNumber}. Needs attention.")
                    : SponsorBankAccountLookup.Found(account);
            }

            var body = await SafeReadAsync(response, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Sponsor {GroupNumber} in tenant {TenantId} has no approved bank account in sponsor-service: {Body}",
                    Sanitize(groupNumber), Sanitize(tenantId), body);
                return SponsorBankAccountLookup.Unavailable(
                    $"Sponsor {groupNumber} has no approved bank account in sponsor-service (a pending change must be approved " +
                    $"by a user with payments:approve): {body}. Needs attention.");
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _logger.LogError("sponsor-service refused the bank-account read for sponsor {GroupNumber} in tenant {TenantId} " +
                                 "({StatusCode}); check service authentication",
                    Sanitize(groupNumber), Sanitize(tenantId), (int)response.StatusCode);
                return SponsorBankAccountLookup.Unavailable(
                    $"sponsor-service refused the bank-account read for sponsor {groupNumber} " +
                    $"({(int)response.StatusCode} {response.StatusCode}). Needs attention.");
            }

            _logger.LogError("sponsor-service answered {StatusCode} to the bank-account read for sponsor {GroupNumber} in tenant {TenantId}",
                (int)response.StatusCode, Sanitize(groupNumber), Sanitize(tenantId));
            return SponsorBankAccountLookup.Unavailable(
                $"sponsor-service answered {(int)response.StatusCode} {response.StatusCode} to the bank-account read " +
                $"for sponsor {groupNumber}: {body}. Needs attention.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogError("The bank-account read for sponsor {GroupNumber} in tenant {TenantId} failed: {Error}",
                Sanitize(groupNumber), Sanitize(tenantId), ex.GetType().Name);
            return SponsorBankAccountLookup.Unavailable(
                $"The bank-account read for sponsor {groupNumber} from sponsor-service failed ({ex.GetType().Name}). Needs attention.");
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
