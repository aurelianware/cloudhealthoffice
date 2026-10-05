using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Security;
using ProviderService.Services;

namespace ProviderService.Controllers;

/// <summary>
/// The one place full provider routing and account numbers leave
/// provider-service: capitation-service reads the <em>active approved</em>
/// account to build a NACHA credit, after a user with payments:approve (and
/// not the statement's maker) has released the disbursement there. Only
/// capitation-service's service token is accepted; every user token,
/// TenantAdmin and PlatformAdmin included, and every other service is refused
/// (403). The tenant comes from the token. The bank tax id is never returned.
/// </summary>
[ApiController]
[Route("api/v1/internal/providers/npi/{npi}/bank-account")]
[RequireServiceClient(CapitationClientId)]
[Produces("application/json")]
public class ProviderBankAccountDisbursementController : ControllerBase
{
    public const string CapitationClientId = "capitation-service";
    public static readonly EventId FullReadEvent = new(4706, "ProviderBankAccountFullRead");

    private readonly IProviderRepository _providers;
    private readonly IProviderBankAccountChangeService _changes;
    private readonly ILogger<ProviderBankAccountDisbursementController> _logger;

    public ProviderBankAccountDisbursementController(
        IProviderRepository providers,
        IProviderBankAccountChangeService changes,
        ILogger<ProviderBankAccountDisbursementController> logger)
    {
        _providers = providers;
        _changes = changes;
        _logger = logger;
    }

    /// <summary>
    /// The active approved account with full routing and account numbers.
    /// 404 <c>NoApprovedAccount</c> when none is approved (a pending change is
    /// never returned) and <c>ProviderNotFound</c> for an unknown NPI. An
    /// account without EFT enabled is answered without numbers. 503 when the
    /// numbers cannot be decrypted.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ProviderBankAccountDisbursementDetails), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetForDisbursement([FromRoute] string npi, CancellationToken ct)
    {
        var tenant = this.TokenTenantId();
        var caller = ChoPrincipal.ServiceClientId(User);

        var provider = await _providers.GetByNPIAsync(npi);
        if (provider == null)
            return NotFound(new { code = "ProviderNotFound", error = $"Provider with NPI {npi} not found" });
        if (string.IsNullOrEmpty(provider.TenantId)) provider.TenantId = tenant;

        var active = await _changes.GetActiveAccountAsync(provider, ct);
        if (active == null)
        {
            return NotFound(new
            {
                code = "NoApprovedAccount",
                error = $"Provider {npi} has no approved bank account (a pending change needs approval by a user with payments:approve)"
            });
        }

        if (ProviderBankAccountProtection.HasCiphertext(active))
        {
            _logger.LogError(FullReadEvent,
                "AUDIT provider bank account read for disbursement refused: provider {ProviderId} (NPI {Npi}) in tenant {TenantId} " +
                "by service {Client}; the numbers could not be decrypted",
                Sanitize(provider.ProviderId), Sanitize(npi), Sanitize(tenant), Sanitize(caller));
            return Problem(title: "Bank-account encryption unavailable",
                detail: "The provider's bank numbers could not be decrypted.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var enrolled = active.EftEnabled;
        _logger.LogInformation(FullReadEvent,
            "AUDIT provider bank account read for disbursement: provider {ProviderId} (NPI {Npi}) in tenant {TenantId} " +
            "by service {Client}; EFT enabled {Enrolled}; numbers returned {Returned}",
            Sanitize(provider.ProviderId), Sanitize(npi), Sanitize(tenant), Sanitize(caller), enrolled, enrolled);

        return Ok(ProviderBankAccountDisbursementDetails.From(provider, active, includeNumbers: enrolled));
    }

    private static string Sanitize(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}

/// <summary>
/// What capitation-service needs to pay a provider. A type of its own: the
/// response serializer masks every <see cref="ProviderBankAccount"/>.
/// </summary>
public sealed class ProviderBankAccountDisbursementDetails
{
    public string ProviderId { get; set; } = string.Empty;
    public string? ProviderNpi { get; set; }
    public bool EftEnabled { get; set; }
    public DisbursementMethod PreferredDisbursementMethod { get; set; }
    public string? RoutingNumber { get; set; }
    public string? AccountNumber { get; set; }
    public BankAccountType AccountType { get; set; }
    public string? AccountHolderName { get; set; }
    public string? StripeConnectedAccountId { get; set; }
    public string? RoutingNumberLast4 { get; set; }
    public string? AccountNumberLast4 { get; set; }

    public static ProviderBankAccountDisbursementDetails From(Provider provider, ProviderBankAccount account, bool includeNumbers)
    {
        var masked = BankAccountMasking.Mask(account)!;
        return new ProviderBankAccountDisbursementDetails
        {
            ProviderId = provider.ProviderId,
            ProviderNpi = provider.NPI,
            EftEnabled = account.EftEnabled,
            PreferredDisbursementMethod = account.PreferredDisbursementMethod,
            RoutingNumber = includeNumbers ? account.RoutingNumber : null,
            AccountNumber = includeNumbers ? account.AccountNumber : null,
            AccountType = account.AccountType,
            AccountHolderName = account.AccountHolderName,
            StripeConnectedAccountId = account.StripeConnectedAccountId,
            RoutingNumberLast4 = masked.RoutingNumberLast4,
            AccountNumberLast4 = masked.AccountNumberLast4,
        };
    }
}
