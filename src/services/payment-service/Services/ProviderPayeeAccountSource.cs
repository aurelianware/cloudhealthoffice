using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Middleware;

namespace PaymentService.Services;

/// <summary>
/// Where a FFS payment's EFT account comes from: provider-service's
/// <em>active approved</em> bank account (dual control there), never a value
/// from a claim, a request or configuration.
/// </summary>
public interface IProviderPayeeAccountSource
{
    /// <summary>False when no source is wired: execution then leaves payment methods as the run's.</summary>
    bool IsConfigured { get; }

    Task<PayeeAccountLookup> GetAsync(string tenantId, string providerNpi, CancellationToken cancellationToken = default);
}

public enum PayeeAccountLookupStatus
{
    /// <summary>An approved account with EFT enabled, both numbers and a payee TIN.</summary>
    Eft,

    /// <summary>
    /// Definitively no approved EFT account: none approved (only a pending
    /// change, or none at all), EFT not enabled, or no payee TIN. Paid by check.
    /// </summary>
    NoEftAccount,

    /// <summary>
    /// The answer is not known (provider-service down, refused, unreadable).
    /// Never turned into a check: the caller stops with nothing issued or recorded.
    /// </summary>
    Unavailable,
}

/// <summary>The approved EFT account of a payee. Holds full numbers: never logged or serialized to a response.</summary>
public sealed class PayeeEftAccount
{
    public string RoutingNumber { get; init; } = string.Empty;
    public string AccountNumber { get; init; } = string.Empty;
    public bool IsSavings { get; init; }
    public string TaxId { get; init; } = string.Empty;
    public string? AccountHolderName { get; init; }
    public string? ProviderId { get; init; }

    /// <summary>The TIN with every non-digit removed (the payee key compares these).</summary>
    public string TaxIdDigits => new(TaxId.Where(char.IsDigit).ToArray());
}

public sealed record PayeeAccountLookup(PayeeAccountLookupStatus Status, PayeeEftAccount? Account, string? Reason)
{
    public static PayeeAccountLookup Eft(PayeeEftAccount account) => new(PayeeAccountLookupStatus.Eft, account, null);
    public static PayeeAccountLookup NoEft(string reason) => new(PayeeAccountLookupStatus.NoEftAccount, null, reason);
    public static PayeeAccountLookup Unavailable(string reason) => new(PayeeAccountLookupStatus.Unavailable, null, reason);
}

/// <summary>
/// provider-service's service-only payee read,
/// <c>GET /api/v1/internal/providers/npi/{npi}/payee-account</c>, which admits
/// only payment-service's service token. Called on the
/// <see cref="HttpClientName"/> client, which carries payment-service's own
/// token only inside an open <see cref="RunExecutionGrant"/> (an approved run
/// execution or EFT file generation), never the user's.
/// </summary>
public sealed class HttpProviderPayeeAccountSource : IProviderPayeeAccountSource
{
    public const string HttpClientName = "ProviderPayeeAccounts";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpProviderPayeeAccountSource> _logger;

    public HttpProviderPayeeAccountSource(IHttpClientFactory httpClientFactory, ILogger<HttpProviderPayeeAccountSource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public bool IsConfigured => true;

    public async Task<PayeeAccountLookup> GetAsync(string tenantId, string providerNpi, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(providerNpi))
            return PayeeAccountLookup.NoEft("no payee NPI to look up");

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"/api/v1/internal/providers/npi/{Uri.EscapeDataString(providerNpi)}/payee-account");
            request.Headers.Add(TenantMiddleware.TenantHeaderName, tenantId);

            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var body = await response.Content.ReadFromJsonAsync<PayeeAccountResponse>(JsonOptions, cancellationToken);
                if (body == null)
                    return PayeeAccountLookup.Unavailable($"provider-service returned an empty payee account for NPI {providerNpi}");
                if (!body.EftEnabled || string.IsNullOrEmpty(body.RoutingNumber) || string.IsNullOrEmpty(body.AccountNumber))
                    return PayeeAccountLookup.NoEft("the approved bank account is not enabled for EFT");
                if (string.IsNullOrWhiteSpace(body.PayeeTaxId))
                    return PayeeAccountLookup.NoEft("the approved bank account records no payee TIN");
                return PayeeAccountLookup.Eft(new PayeeEftAccount
                {
                    RoutingNumber = body.RoutingNumber,
                    AccountNumber = body.AccountNumber,
                    IsSavings = string.Equals(body.AccountType, "Savings", StringComparison.OrdinalIgnoreCase),
                    TaxId = body.PayeeTaxId!,
                    AccountHolderName = body.AccountHolderName,
                    ProviderId = body.ProviderId,
                });
            }

            var error = await SafeReadAsync(response, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound && error.Contains("NoApprovedAccount", StringComparison.Ordinal))
                return PayeeAccountLookup.NoEft("no approved bank account (a pending change needs approval by a user with payments:approve)");
            if (response.StatusCode == HttpStatusCode.NotFound && error.Contains("ProviderNotFound", StringComparison.Ordinal))
                return PayeeAccountLookup.NoEft("the payee NPI is not a provider in provider-service");

            _logger.LogError("provider-service answered {StatusCode} to the payee-account read for NPI {Npi} in tenant {TenantId}",
                (int)response.StatusCode, Sanitize(providerNpi), Sanitize(tenantId));
            return PayeeAccountLookup.Unavailable(
                $"provider-service answered {(int)response.StatusCode} {response.StatusCode} to the payee-account read for NPI {providerNpi}");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogError("The payee-account read for NPI {Npi} in tenant {TenantId} failed: {Error}",
                Sanitize(providerNpi), Sanitize(tenantId), ex.GetType().Name);
            return PayeeAccountLookup.Unavailable($"the payee-account read for NPI {providerNpi} failed ({ex.GetType().Name})");
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

    private sealed class PayeeAccountResponse
    {
        public string? ProviderId { get; set; }
        public bool EftEnabled { get; set; }
        public string? RoutingNumber { get; set; }
        public string? AccountNumber { get; set; }
        public string? AccountType { get; set; }
        public string? AccountHolderName { get; set; }
        public string? PayeeTaxId { get; set; }
    }
}

/// <summary>
/// No provider-service configured (<c>ProviderService:BaseUrl</c> unset).
/// Execution keeps the run's payment method; EFT file generation refuses.
/// </summary>
public sealed class UnconfiguredProviderPayeeAccountSource : IProviderPayeeAccountSource
{
    public const string Reason =
        "No provider bank-account source is configured (ProviderService:BaseUrl); EFT accounts cannot be read.";

    public bool IsConfigured => false;

    public Task<PayeeAccountLookup> GetAsync(string tenantId, string providerNpi, CancellationToken cancellationToken = default)
        => Task.FromResult(PayeeAccountLookup.Unavailable(Reason));
}
