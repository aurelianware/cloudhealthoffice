using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Security;

namespace ProviderService.Controllers;

/// <summary>
/// The payee's tax identity for information returns: payment-service reads
/// the provider's TIN and legal name to total its payments by TIN for the
/// 1099-MISC (box 6, medical and health care payments). Only
/// payment-service's service token is accepted; every user token and every
/// other service is refused (403). The tenant comes from the token. The TIN
/// is never logged.
/// </summary>
[ApiController]
[Route("api/v1/internal/providers/npi/{npi}/tax-identity")]
[RequireServiceClient(PaymentClientId)]
[Produces("application/json")]
public sealed class ProviderTaxIdentityController : ControllerBase
{
    public const string PaymentClientId = "payment-service";
    public static readonly EventId TaxIdentityReadEvent = new(4708, "ProviderTaxIdentityRead");

    private readonly IProviderRepository _providers;
    private readonly ILogger<ProviderTaxIdentityController> _logger;

    public ProviderTaxIdentityController(IProviderRepository providers, ILogger<ProviderTaxIdentityController> logger)
    {
        _providers = providers;
        _logger = logger;
    }

    /// <summary>The provider's TIN, TIN type and legal name; 404 for an unknown NPI.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ProviderTaxIdentity), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get([FromRoute] string npi)
    {
        var provider = await _providers.GetByNPIAsync(npi);
        if (provider == null)
            return NotFound(new { code = "ProviderNotFound", error = $"Provider with NPI {npi} not found" });

        _logger.LogInformation(TaxIdentityReadEvent,
            "AUDIT provider tax identity read for information returns: NPI {Npi} in tenant {TenantId} by service {Client}; TIN on file {HasTin}",
            LogSanitizer.Sanitize(npi), LogSanitizer.Sanitize(this.TokenTenantId()),
            LogSanitizer.Sanitize(ChoPrincipal.ServiceClientId(User)), !string.IsNullOrWhiteSpace(provider.TaxId));

        return Ok(ProviderTaxIdentity.From(provider));
    }
}

/// <summary>What a 1099 needs about the payee.</summary>
public sealed class ProviderTaxIdentity
{
    public string Npi { get; set; } = string.Empty;
    public string? TaxId { get; set; }

    /// <summary>EIN for an organization, SSN for an individual (from the provider type when no W-9 type is recorded).</summary>
    public TaxIdType? TaxIdType { get; set; }

    public string LegalName { get; set; } = string.Empty;
    public bool W9OnFile { get; set; }

    public static ProviderTaxIdentity From(Provider p) => new()
    {
        Npi = p.NPI,
        TaxId = string.IsNullOrWhiteSpace(p.TaxId) ? null : p.TaxId.Trim(),
        TaxIdType = p.BankAccount?.TaxIdType
                    ?? (p.ProviderType == ProviderType.Individual ? Models.TaxIdType.SSN : Models.TaxIdType.EIN),
        LegalName = !string.IsNullOrWhiteSpace(p.OrganizationName)
            ? p.OrganizationName!
            : string.Join(" ", new[] { p.FirstName, p.MiddleName, p.LastName }.Where(s => !string.IsNullOrWhiteSpace(s))),
        W9OnFile = p.BankAccount?.W9OnFile ?? false,
    };
}
