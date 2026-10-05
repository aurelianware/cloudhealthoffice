using CloudHealthOffice.FieldProtection;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using SponsorService.Models;
using SponsorService.Repositories;
using SponsorService.Services;

namespace SponsorService.Controllers;

/// <summary>
/// Sponsor bank accounts (what premium billing auto-debits) under dual control.
/// <list type="bullet">
///   <item>Propose (<c>POST {group}/bank-account-changes</c>): billing:run or
///         enrollment:process. The change is pending (202); the active account
///         is unchanged. The first account is pending too.</item>
///   <item>Approve / reject: payments:approve, user tokens only; the proposer
///         cannot approve (403 "Separation of duties"); a stale approval is 409.
///         Cancel: billing:run or enrollment:process.</item>
///   <item>Reads are masked (last 4): billing:read, payments:read or
///         enrollment:read (and payments:approve for the review reads).</item>
/// </list>
/// Full numbers are returned only by <see cref="SponsorBankAccountDebitController"/>,
/// to premium-billing-service's service token.
/// </summary>
[ApiController]
[Route("api/v1/sponsors/{groupNumber}")]
[Produces("application/json")]
public class SponsorBankAccountsController : ControllerBase
{
    public const string SeparationOfDutiesType = "https://cloudhealthoffice.com/problems/bank-account-separation-of-duties";
    private const string ProposePermission = "billing:run,enrollment:process";
    private const string ReadPermission = "billing:read,payments:read,enrollment:read";
    private const string ReviewPermission = "payments:approve,billing:read,payments:read,enrollment:read";

    private readonly ISponsorRepository _sponsors;
    private readonly ISponsorBankAccountService _accounts;
    private readonly ICurrentActor _actor;
    private readonly ILogger<SponsorBankAccountsController> _logger;

    public SponsorBankAccountsController(
        ISponsorRepository sponsors,
        ISponsorBankAccountService accounts,
        ICurrentActor actor,
        ILogger<SponsorBankAccountsController> logger)
    {
        _sponsors = sponsors;
        _accounts = accounts;
        _actor = actor;
        _logger = logger;
    }

    /// <summary>The approved account (masked) and the pending change (masked), if any.</summary>
    [HttpGet("bank-account")]
    [RequirePermission(ReadPermission)]
    [ProducesResponseType(typeof(SponsorBankAccountView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> GetBankAccount([FromRoute] string groupNumber, CancellationToken ct)
        => WithSponsorAsync(groupNumber, async sponsor =>
        {
            var record = await _accounts.GetAsync(sponsor, ct);
            var active = await _accounts.GetActiveAsync(sponsor, ct);
            var pending = record?.GetPending();
            return Ok(new SponsorBankAccountView
            {
                GroupNumber = sponsor.GroupNumber,
                Active = SponsorBankAccountMasking.Mask(active),
                ActiveApprovedBy = active == null ? null : record?.ActiveApprovedBy,
                ActiveApprovedAt = active == null ? null : record?.ActiveApprovedAt,
                Pending = pending == null ? null : SponsorBankAccountChangeView.From(pending),
            });
        });

    /// <summary>The pending change for review, masked.</summary>
    [HttpGet("bank-account-changes/pending")]
    [RequirePermission(ReviewPermission)]
    [ProducesResponseType(typeof(SponsorBankAccountChangeView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> GetPending([FromRoute] string groupNumber, CancellationToken ct)
        => WithSponsorAsync(groupNumber, async sponsor =>
        {
            var pending = (await _accounts.GetAsync(sponsor, ct))?.GetPending();
            return pending == null
                ? NotFound(new { error = $"No pending bank-account change for sponsor {groupNumber}" })
                : Ok(SponsorBankAccountChangeView.From(pending));
        });

    /// <summary>Every change, newest first, masked (the history).</summary>
    [HttpGet("bank-account-changes")]
    [RequirePermission(ReviewPermission)]
    [ProducesResponseType(typeof(IEnumerable<SponsorBankAccountChangeView>), StatusCodes.Status200OK)]
    public Task<IActionResult> ListChanges([FromRoute] string groupNumber, CancellationToken ct)
        => WithSponsorAsync(groupNumber, async sponsor =>
        {
            var record = await _accounts.GetAsync(sponsor, ct);
            var changes = record?.Changes.OrderByDescending(c => c.RequestedAt).Select(SponsorBankAccountChangeView.From).ToList()
                ?? new List<SponsorBankAccountChangeView>();
            return Ok(changes);
        });

    /// <summary>
    /// Propose the sponsor's account and auto-debit enrollment. Pending (202)
    /// until a different user with payments:approve approves it; a newer
    /// proposal supersedes a pending one.
    /// </summary>
    [HttpPost("bank-account-changes")]
    [RequirePermission(ProposePermission)]
    [ProducesResponseType(typeof(SponsorBankAccountChangeView), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Propose(
        [FromRoute] string groupNumber, [FromBody] ProposeSponsorBankAccountRequest request, CancellationToken ct)
        => WithSponsorAsync(groupNumber, async sponsor =>
        {
            var change = await _accounts.ProposeAsync(sponsor, request, _actor.UserId, ct);
            return StatusCode(StatusCodes.Status202Accepted, SponsorBankAccountChangeView.From(change));
        });

    /// <summary>
    /// Approve a pending change: it becomes the active account in one
    /// conditional write. 403 for the proposer or a service token; 409 when the
    /// change is no longer pending or the active account changed since.
    /// </summary>
    [HttpPost("bank-account-changes/{changeId}/approve")]
    [RequirePermission("payments:approve")]
    [ProducesResponseType(typeof(SponsorBankAccountChangeView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Approve(
        [FromRoute] string groupNumber, [FromRoute] string changeId,
        [FromBody] SponsorBankAccountDecisionRequest? body, CancellationToken ct)
        => WithSponsorAsync(groupNumber, async sponsor =>
            Ok(SponsorBankAccountChangeView.From(await _accounts.ApproveAsync(sponsor, changeId, Actor, body?.Reason, ct))));

    /// <summary>Reject a pending change. The active account is unchanged.</summary>
    [HttpPost("bank-account-changes/{changeId}/reject")]
    [RequirePermission("payments:approve")]
    [ProducesResponseType(typeof(SponsorBankAccountChangeView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Reject(
        [FromRoute] string groupNumber, [FromRoute] string changeId,
        [FromBody] SponsorBankAccountDecisionRequest? body, CancellationToken ct)
        => WithSponsorAsync(groupNumber, async sponsor =>
            Ok(SponsorBankAccountChangeView.From(await _accounts.RejectAsync(sponsor, changeId, Actor, body?.Reason, ct))));

    /// <summary>Withdraw a pending change. The active account is unchanged.</summary>
    [HttpPost("bank-account-changes/{changeId}/cancel")]
    [RequirePermission(ProposePermission)]
    [ProducesResponseType(typeof(SponsorBankAccountChangeView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Cancel(
        [FromRoute] string groupNumber, [FromRoute] string changeId,
        [FromBody] SponsorBankAccountDecisionRequest? body, CancellationToken ct)
        => WithSponsorAsync(groupNumber, async sponsor =>
            Ok(SponsorBankAccountChangeView.From(await _accounts.CancelAsync(sponsor, changeId, _actor.UserId, body?.Reason, ct))));

    private SponsorBankAccountActor Actor => new(_actor.UserId, _actor.IsService);

    private async Task<IActionResult> WithSponsorAsync(string groupNumber, Func<Sponsor, Task<IActionResult>> action)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var sponsor = await _sponsors.GetByGroupNumberAsync(_actor.TenantId, groupNumber);
        if (sponsor == null)
            return NotFound(new { error = $"Sponsor with group number '{groupNumber}' not found" });

        try
        {
            return await action(sponsor);
        }
        catch (SponsorBankAccountValidationException ex)
        {
            return BadRequest(new { error = "The bank account is not valid", errors = ex.Errors });
        }
        catch (SponsorBankAccountNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (SponsorBankAccountForbiddenException ex)
        {
            return Problem(type: SeparationOfDutiesType, title: ex.Title, detail: ex.Message,
                statusCode: StatusCodes.Status403Forbidden);
        }
        catch (SponsorBankAccountConflictException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (FieldProtectionException ex)
        {
            _logger.LogError(ex, "Sponsor bank-account data for sponsor {GroupNumber} could not be encrypted or decrypted",
                groupNumber.Replace("\r", string.Empty).Replace("\n", string.Empty));
            return Problem(title: "Bank-account encryption unavailable", detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
