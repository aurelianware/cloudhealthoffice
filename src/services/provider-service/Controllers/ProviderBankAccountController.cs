using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Security;
using ProviderService.Services;

namespace ProviderService.Controllers;

/// <summary>
/// Provider bank accounts under dual control. Mounted at
/// <c>api/v1/providers</c> and <c>api/providers</c> (capitation-service reads
/// the legacy prefix).
///
/// <list type="bullet">
///   <item>Proposing a change (<c>PUT npi/{npi}/bank-account</c> or
///         <c>POST npi/{npi}/bank-account-changes</c>) needs
///         <c>providers:write</c>. The change is pending; the active account
///         is unchanged (202).</item>
///   <item>Approving or rejecting needs <c>payments:approve</c> and a user
///         token. The user who proposed a change cannot approve it (403
///         "Separation of duties"); there is no per-tenant override.</item>
///   <item>The masked read (<c>GET npi/{npi}/bank-account</c>, used by
///         capitation-service) returns the approved account only, never a
///         pending one. Reviewers read the pending change masked.</item>
/// </list>
/// Full routing, account and tax numbers are never returned by any of these.
/// </summary>
[ApiController]
[Route("api/v1/providers")]
[Route("api/providers")]
[Produces("application/json")]
public class ProviderBankAccountController : ControllerBase
{
    public const string SeparationOfDutiesType = "https://cloudhealthoffice.com/problems/bank-account-separation-of-duties";

    private readonly IProviderRepository _providers;
    private readonly IProviderBankAccountChangeService _changes;
    private readonly ILogger<ProviderBankAccountController> _logger;

    public ProviderBankAccountController(
        IProviderRepository providers,
        IProviderBankAccountChangeService changes,
        ILogger<ProviderBankAccountController> logger)
    {
        _providers = providers;
        _changes = changes;
        _logger = logger;
    }

    /// <summary>
    /// The provider's active (approved) bank account, masked: last 4 digits
    /// only. Used by capitation-service to choose the disbursement method.
    /// A pending change is never returned here.
    /// </summary>
    [HttpGet("npi/{npi}/bank-account")]
    [RequirePermission("providers:read,payments:read")]
    [ProducesResponseType(typeof(ProviderBankAccount), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProviderBankAccount>> GetBankAccount(string npi, CancellationToken ct)
    {
        _logger.LogInformation("Fetching bank account for provider NPI: {NPI}", Sanitize(npi));

        var provider = await _providers.GetByNPIAsync(npi);
        if (provider == null)
        {
            return NotFound($"Provider with NPI {npi} not found");
        }

        var active = await _changes.GetActiveAccountAsync(provider, ct);
        if (active == null)
        {
            return NotFound($"No approved bank account on file for provider NPI {npi}");
        }

        return Ok(BankAccountMasking.Mask(active));
    }

    /// <summary>
    /// Propose a new bank account. Legacy route: it used to replace the
    /// account directly; it now records a pending change (202) that a second
    /// user with payments:approve must approve.
    /// </summary>
    [HttpPut("npi/{npi}/bank-account")]
    [ProducesResponseType(typeof(BankAccountChangeView), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult<BankAccountChangeView>> UpsertBankAccount(
        string npi, [FromBody] ProviderBankAccount bankAccount, CancellationToken ct)
        => ProposeAsync(npi, bankAccount, "PUT bank-account", ct);

    /// <summary>Propose a new bank account (pending until approved).</summary>
    [HttpPost("npi/{npi}/bank-account-changes")]
    [ProducesResponseType(typeof(BankAccountChangeView), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult<BankAccountChangeView>> ProposeBankAccountChange(
        string npi, [FromBody] ProviderBankAccount bankAccount, CancellationToken ct)
        => ProposeAsync(npi, bankAccount, "POST bank-account-changes", ct);

    /// <summary>The pending change for review, masked.</summary>
    [HttpGet("npi/{npi}/bank-account-changes/pending")]
    [RequirePermission("payments:approve,providers:read")]
    [ProducesResponseType(typeof(BankAccountChangeView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BankAccountChangeView>> GetPendingChange(string npi, CancellationToken ct)
    {
        var provider = await _providers.GetByNPIAsync(npi);
        if (provider == null)
        {
            return NotFound($"Provider with NPI {npi} not found");
        }

        var pending = await _changes.GetPendingAsync(provider, ct);
        return pending == null
            ? NotFound($"No pending bank-account change for provider NPI {npi}")
            : Ok(BankAccountChangeView.From(pending));
    }

    /// <summary>Every bank-account change of the provider, newest first, masked (the history).</summary>
    [HttpGet("npi/{npi}/bank-account-changes")]
    [RequirePermission("payments:approve,providers:read")]
    [ProducesResponseType(typeof(IEnumerable<BankAccountChangeView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<BankAccountChangeView>>> ListChanges(string npi, CancellationToken ct)
    {
        var provider = await _providers.GetByNPIAsync(npi);
        if (provider == null)
        {
            return NotFound($"Provider with NPI {npi} not found");
        }

        var changes = await _changes.ListChangesAsync(provider, ct);
        return Ok(changes.Select(BankAccountChangeView.From).ToList());
    }

    /// <summary>
    /// Approve a pending change: the proposed account becomes the active one
    /// in one conditional write. 403 when the approver proposed it or is a
    /// service; 409 when the change is no longer pending or the active
    /// account changed since it was proposed.
    /// </summary>
    [HttpPost("npi/{npi}/bank-account-changes/{changeId}/approve")]
    [RequirePermission("payments:approve")]
    [ProducesResponseType(typeof(BankAccountChangeView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult<BankAccountChangeView>> Approve(
        string npi, string changeId, [FromBody] BankAccountChangeDecisionRequest? body, CancellationToken ct)
        => DecideAsync(npi, (provider, actor) => _changes.ApproveAsync(provider, changeId, actor, body?.Reason, ct));

    /// <summary>Reject a pending change. The active account is unchanged.</summary>
    [HttpPost("npi/{npi}/bank-account-changes/{changeId}/reject")]
    [RequirePermission("payments:approve")]
    [ProducesResponseType(typeof(BankAccountChangeView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult<BankAccountChangeView>> Reject(
        string npi, string changeId, [FromBody] BankAccountChangeDecisionRequest? body, CancellationToken ct)
        => DecideAsync(npi, (provider, actor) => _changes.RejectAsync(provider, changeId, actor, body?.Reason, ct));

    /// <summary>Withdraw a pending change (providers:write). The active account is unchanged.</summary>
    [HttpPost("npi/{npi}/bank-account-changes/{changeId}/cancel")]
    [ProducesResponseType(typeof(BankAccountChangeView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult<BankAccountChangeView>> Cancel(
        string npi, string changeId, [FromBody] BankAccountChangeDecisionRequest? body, CancellationToken ct)
        => DecideAsync(npi, (provider, actor) => _changes.CancelAsync(provider, changeId, actor.UserId, body?.Reason, ct));

    private async Task<ActionResult<BankAccountChangeView>> ProposeAsync(
        string npi, ProviderBankAccount bankAccount, string source, CancellationToken ct)
    {
        _logger.LogInformation("Bank-account change proposed for provider NPI: {NPI}", Sanitize(npi));

        var provider = await _providers.GetByNPIAsync(npi);
        if (provider == null)
        {
            return NotFound($"Provider with NPI {npi} not found");
        }

        try
        {
            var change = await _changes.ProposeAsync(provider, bankAccount, this.TokenActorId(), source, ct);
            return StatusCode(StatusCodes.Status202Accepted, BankAccountChangeView.From(change));
        }
        catch (BankAccountChangeConflictException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    private async Task<ActionResult<BankAccountChangeView>> DecideAsync(
        string npi, Func<Provider, BankAccountActor, Task<PendingBankAccountChange>> decide)
    {
        var provider = await _providers.GetByNPIAsync(npi);
        if (provider == null)
        {
            return NotFound($"Provider with NPI {npi} not found");
        }

        var actor = new BankAccountActor(this.TokenActorId(), ChoPrincipal.IsService(User));
        try
        {
            var change = await decide(provider, actor);
            return Ok(BankAccountChangeView.From(change));
        }
        catch (BankAccountChangeNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (BankAccountChangeForbiddenException ex)
        {
            return Problem(type: SeparationOfDutiesType, title: ex.Title, detail: ex.Message,
                statusCode: StatusCodes.Status403Forbidden);
        }
        catch (BankAccountChangeConflictException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    private static string Sanitize(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
