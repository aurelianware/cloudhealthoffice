using EnrollmentImportService.Models;
using EnrollmentImportService.Services;
using Microsoft.AspNetCore.Mvc;

namespace EnrollmentImportService.Controllers;

/// <summary>
/// Read-side API for individual 834 transactions persisted by the import path.
/// Consumed by member-service's <c>GET /api/v1/members/{id}/834-transactions</c>.
/// </summary>
[ApiController]
[Route("api/v1/enrollment")]
public class TransactionsController : ControllerBase
{
    private readonly IEnrollmentTransactionRepository _transactions;

    public TransactionsController(IEnrollmentTransactionRepository transactions)
    {
        _transactions = transactions;
    }

    /// <summary>
    /// List 834 transactions for a member, most recent first, capped at <c>limit</c>.
    /// </summary>
    [HttpGet("transactions")]
    [ProducesResponseType(typeof(List<EnrollmentTransaction>), 200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> ListTransactions(
        [FromHeader(Name = "X-Tenant-ID")] string tenantId,
        [FromQuery] string memberId,
        [FromQuery] int limit = 100)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return BadRequest("X-Tenant-ID header is required");
        if (string.IsNullOrWhiteSpace(memberId))
            return BadRequest("memberId query parameter is required");
        if (limit < 1 || limit > 500) limit = 100;

        var list = await _transactions.ListByMemberAsync(tenantId, memberId, limit);
        return Ok(list);
    }

    /// <summary>
    /// Most recent 834 transactions for the tenant, newest first — the
    /// admin-console read path (no memberId filter, unlike
    /// <see cref="ListTransactions"/> above).
    /// </summary>
    [HttpGet("transactions/recent")]
    [ProducesResponseType(typeof(List<EnrollmentTransaction>), 200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> ListRecentTransactions(
        [FromHeader(Name = "X-Tenant-ID")] string tenantId,
        [FromQuery] int limit = 100)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return BadRequest("X-Tenant-ID header is required");
        if (limit < 1 || limit > 500) limit = 100;

        var list = await _transactions.ListRecentAsync(tenantId, limit);
        return Ok(list);
    }
}
