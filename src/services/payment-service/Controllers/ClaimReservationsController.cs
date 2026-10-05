using Microsoft.AspNetCore.Mvc;
using PaymentService.Models;
using PaymentService.Services;

namespace PaymentService.Controllers;

/// <summary>
/// Claim reservations of payment and reversal runs. Reads need payments:read
/// (Program.cs default) and see the caller's tenant only (from the token).
/// Releasing one is on the run:
/// POST /api/{paymentruns|reversalruns}/{id}/reservations/{claimId}/release.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class ClaimReservationsController : ControllerBase
{
    private readonly IReservationReconciliationService _reconciliation;

    public ClaimReservationsController(IReservationReconciliationService reconciliation)
        => _reconciliation = reconciliation;

    /// <summary>
    /// Reservations reconciliation could not release safely (a payment or 835
    /// exists, the run is stuck, or the state cannot be classified), each with
    /// its reason and the path a second approver releases it at.
    /// </summary>
    [HttpGet("needs-attention")]
    [ProducesResponseType(typeof(IEnumerable<ReservationNeedingAttentionView>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<ReservationNeedingAttentionView>>> NeedsAttention()
        => Ok(await _reconciliation.ListNeedingAttentionAsync());
}
