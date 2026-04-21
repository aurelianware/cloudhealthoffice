using ArService.Models;
using ArService.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace ArService.Controllers;

/// <summary>
/// Member-scoped read of the tenant's AR ledger. Used by the portal Member
/// Details dialog (AR tab). Aggregates posting entries tagged with the given
/// <c>memberId</c> across every <see cref="ArBalance"/> document for the
/// tenant; strictly read-only — no payments initiated from this surface.
/// </summary>
[ApiController]
[Route("api/v1/members")]
[Produces("application/json")]
public class MembersArController : ControllerBase
{
    private readonly IArBalanceRepository _balanceRepository;
    private readonly ILogger<MembersArController> _logger;

    public MembersArController(
        IArBalanceRepository balanceRepository,
        ILogger<MembersArController> logger)
    {
        _balanceRepository = balanceRepository;
        _logger = logger;
    }

    /// <summary>
    /// Member AR summary — current balance, aged buckets, recent charges and
    /// payments. Empty result (zero balance, empty lists) if the member has
    /// no posting activity — never 404 on a valid member.
    /// </summary>
    [HttpGet("{memberId}/ar-summary")]
    [ProducesResponseType(typeof(MemberArSummary), StatusCodes.Status200OK)]
    public async Task<ActionResult<MemberArSummary>> GetArSummary(string memberId)
    {
        if (string.IsNullOrWhiteSpace(memberId))
            return BadRequest(new { error = "memberId is required" });

        _logger.LogInformation("AR summary lookup for member {MemberId}", SanitizeForLog(memberId));

        var balances = (await _balanceRepository.GetBalancesContainingMemberAsync(memberId)).ToList();
        var summary = BuildSummary(memberId, balances, DateTime.UtcNow);
        return Ok(summary);
    }

    /// <summary>
    /// Build a <see cref="MemberArSummary"/> from the raw balances. Public
    /// and static so unit tests can drive it without MongoDB.
    /// </summary>
    public static MemberArSummary BuildSummary(
        string memberId,
        IReadOnlyList<ArBalance> balances,
        DateTime asOfUtc)
    {
        var summary = new MemberArSummary
        {
            MemberId = memberId,
            AsOfUtc = asOfUtc
        };

        var memberEntries = balances
            .SelectMany(b => b.PostingEntries.Where(e => e.MemberId == memberId))
            .ToList();

        foreach (var e in memberEntries)
        {
            summary.CurrentBalance += e.DebitAmount - e.CreditAmount;
            var ageDays = (asOfUtc - e.PostedAt).TotalDays;
            var net = e.DebitAmount - e.CreditAmount;

            if (ageDays <= 30)        summary.Aged.Bucket0_30   += net;
            else if (ageDays <= 60)   summary.Aged.Bucket31_60  += net;
            else if (ageDays <= 90)   summary.Aged.Bucket61_90  += net;
            else                      summary.Aged.Bucket91Plus += net;
        }

        summary.RecentCharges = memberEntries
            .Where(e => e.DebitAmount > 0)
            .OrderByDescending(e => e.PostedAt)
            .Take(MemberArSummary.RecentLimit)
            .Select(e => new ArChargeRow
            {
                EntryId = e.EntryId,
                PostedAt = e.PostedAt,
                Amount = e.DebitAmount,
                Source = e.Source,
                SourceReferenceNumber = e.SourceReferenceNumber,
                Memo = e.Memo
            })
            .ToList();

        summary.RecentPayments = memberEntries
            .Where(e => e.CreditAmount > 0)
            .OrderByDescending(e => e.PostedAt)
            .Take(MemberArSummary.RecentLimit)
            .Select(e => new ArPaymentRow
            {
                EntryId = e.EntryId,
                PostedAt = e.PostedAt,
                Amount = e.CreditAmount,
                Source = e.Source,
                SourceReferenceNumber = e.SourceReferenceNumber,
                Memo = e.Memo
            })
            .ToList();

        return summary;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
