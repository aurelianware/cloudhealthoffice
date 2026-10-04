using CloudHealthOffice.FieldProtection;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using SponsorService.Models;
using SponsorService.Repositories;
using SponsorService.Services;

namespace SponsorService.Controllers;

/// <summary>
/// The one place full sponsor routing and account numbers leave
/// sponsor-service: premium-billing-service reads the <em>active approved</em>
/// account to build an auto-debit, after a user with payments:approve (and not
/// the invoice's maker) has released the debit there. Only
/// premium-billing-service's service token is accepted; every user token,
/// TenantAdmin included, and every other service is refused (403). The tenant
/// comes from the token.
/// </summary>
[ApiController]
[Route("api/v1/internal/sponsors/{groupNumber}/bank-account")]
[RequireServiceClient(PremiumBillingClientId)]
[Produces("application/json")]
public class SponsorBankAccountDebitController : ControllerBase
{
    public const string PremiumBillingClientId = "premium-billing-service";
    public static readonly EventId FullReadEvent = new(4816, "SponsorBankAccountFullRead");

    private readonly ISponsorRepository _sponsors;
    private readonly ISponsorBankAccountService _accounts;
    private readonly ICurrentActor _actor;
    private readonly ILogger<SponsorBankAccountDebitController> _logger;

    public SponsorBankAccountDebitController(
        ISponsorRepository sponsors,
        ISponsorBankAccountService accounts,
        ICurrentActor actor,
        ILogger<SponsorBankAccountDebitController> logger)
    {
        _sponsors = sponsors;
        _accounts = accounts;
        _actor = actor;
        _logger = logger;
    }

    /// <summary>
    /// The active approved account with full numbers. 404 with
    /// <c>code: "NoApprovedAccount"</c> when none is approved (a pending
    /// change is never returned) or <c>"SponsorNotFound"</c>. A sponsor not
    /// enrolled in auto-debit is answered without numbers.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(SponsorBankAccountDetails), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetForDebit([FromRoute] string groupNumber, CancellationToken ct)
    {
        var tenant = _actor.TenantId;
        var sponsor = await _sponsors.GetByGroupNumberAsync(tenant, groupNumber);
        if (sponsor == null)
            return NotFound(new { code = "SponsorNotFound", error = $"Sponsor with group number '{groupNumber}' not found" });

        SponsorBankAccountDetails? active;
        try
        {
            active = await _accounts.GetActiveAsync(sponsor, ct);
        }
        catch (FieldProtectionException ex)
        {
            _logger.LogError(ex, "Sponsor bank account for {GroupNumber} in tenant {TenantId} could not be decrypted",
                Sanitize(groupNumber), Sanitize(tenant));
            return Problem(title: "Bank-account encryption unavailable", detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (active == null)
        {
            return NotFound(new
            {
                code = "NoApprovedAccount",
                error = $"Sponsor {groupNumber} has no approved bank account (a pending change needs approval by a user with payments:approve)"
            });
        }

        var enrolled = active.EftEnabled;
        _logger.LogInformation(FullReadEvent,
            "AUDIT sponsor bank account read for debit: sponsor {GroupNumber} in tenant {TenantId} by service {Client}; " +
            "enrolled {Enrolled}; numbers returned {Returned}",
            Sanitize(groupNumber), Sanitize(tenant), Sanitize(_actor.UserId), enrolled, enrolled);

        // Not enrolled: premium billing skips the sponsor and needs no numbers.
        return Ok(enrolled ? active : SponsorBankAccountMasking.Mask(active));
    }

    private static string Sanitize(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
