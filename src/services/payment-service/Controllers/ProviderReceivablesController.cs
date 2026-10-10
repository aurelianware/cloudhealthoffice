using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using PaymentService.Models;
using PaymentService.Services;

namespace PaymentService.Controllers;

/// <summary>
/// The provider receivable ledger (payments:read). Receivables are opened by reversal
/// runs whose 835 nets below zero and recovered by payment runs. The one write is the
/// release of a receivable held for the void of an unsent payment, once a person confirmed
/// the payment was paid (payments:approve, a user, reason). The tenant comes from the token.
/// </summary>
[ApiController]
[Route("api/receivables")]
[Produces("application/json")]
public class ProviderReceivablesController : ControllerBase
{
    private readonly IProviderReceivableLedger _ledger;
    private readonly ICurrentActor _actor;
    private readonly TimeProvider _time;

    public ProviderReceivablesController(IProviderReceivableLedger ledger, ICurrentActor actor, TimeProvider time)
    {
        _ledger = ledger;
        _actor = actor;
        _time = time;
    }

    /// <summary>Receivables, oldest first, optionally for one provider NPI and/or status.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ProviderReceivableRecord>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<ProviderReceivableRecord>>> Search(
        [FromQuery] string? providerNpi = null, [FromQuery] ReceivableStatus? status = null)
        => Ok(await _ledger.SearchAsync(_actor.TenantId, providerNpi, status));

    /// <summary>Outstanding receivables by aging bucket (0-30, 31-60, 61-90, 91-120, over 120 days).</summary>
    [HttpGet("aging")]
    [ProducesResponseType(typeof(ReceivableAgingReport), StatusCodes.Status200OK)]
    public async Task<ActionResult<ReceivableAgingReport>> Aging(
        [FromQuery] DateTime? asOf = null, [FromQuery] string? providerNpi = null)
        => Ok(await _ledger.GetAgingAsync(_actor.TenantId, asOf ?? _time.GetUtcNow().UtcDateTime, providerNpi));

    /// <summary>One receivable with its full ledger history.</summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ProviderReceivableRecord), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProviderReceivableRecord>> Get(string id)
    {
        var record = await _ledger.GetAsync(_actor.TenantId, id);
        return record == null ? NotFound() : Ok(record);
    }

    /// <summary>
    /// A PendingVoid receivable (its reversed payments' NACHA file had not been sent) is
    /// released for recovery: the payments were confirmed paid (e.g. a file sent outside this
    /// system). Audited. 409 when it is not held.
    /// </summary>
    [HttpPost("{id}/release")]
    [RequirePermission("payments:approve")]
    [ProducesResponseType(typeof(ProviderReceivableRecord), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProviderReceivableRecord>> Release(string id, [FromBody] ReleaseReceivableRequest? request)
    {
        if (_actor.IsService)
            return Problem(title: "User required", detail: "A person releases a held receivable, not a service token.",
                statusCode: StatusCodes.Status403Forbidden);
        if (string.IsNullOrWhiteSpace(request?.Reason))
            return Problem(title: "Reason required", detail: "Say how the payment was confirmed paid.", statusCode: StatusCodes.Status400BadRequest);
        try
        {
            return Ok(await _ledger.ActivateAsync(_actor.TenantId, id, _actor.UserId, request.Reason));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Problem(title: "Not held", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}

public class ReleaseReceivableRequest
{
    public string? Reason { get; set; }
}
