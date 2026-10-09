using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Security;
using ProviderService.Services;

namespace ProviderService.Controllers;

/// <summary>
/// The fee-for-service counterpart of <see cref="ProviderBankAccountDisbursementController"/>:
/// payment-service reads the provider's <em>active approved</em> account (dual
/// control in provider-service) to build the NACHA CCD+ credit file of an
/// executed payment run. Only payment-service's service token is accepted;
/// every user token, TenantAdmin and PlatformAdmin included, and every other
/// service (capitation-service too) is refused (403). The tenant comes from the
/// token.
///
/// <para>
/// Unlike the capitation read, the answer carries the payee TIN recorded with
/// the approved account (<see cref="ProviderBankAccount.TaxId"/>): a FFS NACHA
/// entry is one per payee, keyed on TIN plus account, so two NPIs billing under
/// one TIN into one account get one credit. payment-service keeps it in memory
/// only and records its last four digits. Neither the TIN nor the numbers are
/// logged.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/internal/providers/npi/{npi}/payee-account")]
[RequireServiceClient(PaymentClientId)]
[Produces("application/json")]
public class ProviderPayeeAccountController : ControllerBase
{
    public const string PaymentClientId = "payment-service";
    public static readonly EventId PayeeReadEvent = new(4707, "ProviderPayeeAccountFullRead");

    private readonly IProviderRepository _providers;
    private readonly IProviderBankAccountChangeService _changes;
    private readonly ILogger<ProviderPayeeAccountController> _logger;

    public ProviderPayeeAccountController(
        IProviderRepository providers,
        IProviderBankAccountChangeService changes,
        ILogger<ProviderPayeeAccountController> logger)
    {
        _providers = providers;
        _changes = changes;
        _logger = logger;
    }

    /// <summary>
    /// The active approved account with full routing and account numbers and
    /// the payee TIN. 404 <c>NoApprovedAccount</c> when none is approved (a
    /// pending change is never returned) and <c>ProviderNotFound</c> for an
    /// unknown NPI. An account without EFT enabled is answered without numbers
    /// (payment-service pays that payee by check). 503 when the numbers cannot
    /// be decrypted.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ProviderPayeeAccountDetails), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetPayeeAccount([FromRoute] string npi, CancellationToken ct)
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
            _logger.LogError(PayeeReadEvent,
                "AUDIT provider payee account read refused: provider {ProviderId} (NPI {Npi}) in tenant {TenantId} " +
                "by service {Client}; the numbers could not be decrypted",
                Sanitize(provider.ProviderId), Sanitize(npi), Sanitize(tenant), Sanitize(caller));
            return Problem(title: "Bank-account encryption unavailable",
                detail: "The provider's bank numbers could not be decrypted.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var enrolled = active.EftEnabled;
        _logger.LogInformation(PayeeReadEvent,
            "AUDIT provider payee account read for FFS EFT: provider {ProviderId} (NPI {Npi}) in tenant {TenantId} " +
            "by service {Client}; EFT enabled {Enrolled}; numbers returned {Returned}",
            Sanitize(provider.ProviderId), Sanitize(npi), Sanitize(tenant), Sanitize(caller), enrolled, enrolled);

        return Ok(ProviderPayeeAccountDetails.From(provider, active, includeNumbers: enrolled));
    }

    private static string Sanitize(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}

/// <summary>
/// What payment-service needs to credit a FFS payee. A type of its own: the
/// response serializer masks every <see cref="ProviderBankAccount"/>.
/// </summary>
public sealed class ProviderPayeeAccountDetails
{
    public string ProviderId { get; set; } = string.Empty;
    public string? ProviderNpi { get; set; }
    public bool EftEnabled { get; set; }
    public DisbursementMethod PreferredDisbursementMethod { get; set; }
    public string? RoutingNumber { get; set; }
    public string? AccountNumber { get; set; }
    public BankAccountType AccountType { get; set; }
    public string? AccountHolderName { get; set; }
    public string? RoutingNumberLast4 { get; set; }
    public string? AccountNumberLast4 { get; set; }

    /// <summary>The payee TIN recorded with the approved account; null when none was recorded.</summary>
    public string? PayeeTaxId { get; set; }

    public TaxIdType? PayeeTaxIdType { get; set; }

    public static ProviderPayeeAccountDetails From(Provider provider, ProviderBankAccount account, bool includeNumbers)
    {
        var masked = BankAccountMasking.Mask(account)!;
        return new ProviderPayeeAccountDetails
        {
            ProviderId = provider.ProviderId,
            ProviderNpi = provider.NPI,
            EftEnabled = account.EftEnabled,
            PreferredDisbursementMethod = account.PreferredDisbursementMethod,
            RoutingNumber = includeNumbers ? account.RoutingNumber : null,
            AccountNumber = includeNumbers ? account.AccountNumber : null,
            AccountType = account.AccountType,
            AccountHolderName = account.AccountHolderName,
            RoutingNumberLast4 = masked.RoutingNumberLast4,
            AccountNumberLast4 = masked.AccountNumberLast4,
            PayeeTaxId = includeNumbers ? account.TaxId : null,
            PayeeTaxIdType = account.TaxIdType,
        };
    }
}
