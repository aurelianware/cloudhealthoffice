using Microsoft.AspNetCore.Mvc;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;

namespace PremiumBillingService.Controllers;

/// <summary>
/// Member-scoped premium rollup for the portal Member Details dialog. Pulls
/// the member's recent invoices (selected via
/// <see cref="IPremiumInvoiceRepository.ListByMemberAsync"/>), picks the most
/// recent as the current invoice, and derives grace-period state.
/// </summary>
[ApiController]
[Route("api/v1/members")]
[Produces("application/json")]
public class MembersPremiumController : ControllerBase
{
    private readonly IPremiumInvoiceRepository _invoiceRepository;
    private readonly ILogger<MembersPremiumController> _logger;

    public MembersPremiumController(
        IPremiumInvoiceRepository invoiceRepository,
        ILogger<MembersPremiumController> logger)
    {
        _invoiceRepository = invoiceRepository;
        _logger = logger;
    }

    /// <summary>
    /// Returns current invoice, next bill date, autopay flag, grace-period
    /// state (APTC-aware), and the last 12 invoices for the member.
    /// </summary>
    [HttpGet("{memberId}/premium-summary")]
    [ProducesResponseType(typeof(MemberPremiumSummary), StatusCodes.Status200OK)]
    public async Task<ActionResult<MemberPremiumSummary>> GetPremiumSummary(string memberId)
    {
        if (string.IsNullOrWhiteSpace(memberId))
            return BadRequest(new { error = "memberId is required" });

        _logger.LogInformation("Premium summary lookup for member {MemberId}", SanitizeForLog(memberId));

        var invoices = (await _invoiceRepository.ListByMemberAsync(memberId, take: 12)).ToList();
        var summary = Build(memberId, invoices, DateTime.UtcNow);
        return Ok(summary);
    }

    /// <summary>
    /// Shape the raw invoices into a summary. Public and static so unit tests
    /// can exercise the grace-period math without MongoDB.
    /// </summary>
    public static MemberPremiumSummary Build(
        string memberId,
        IReadOnlyList<PremiumInvoice> invoices,
        DateTime nowUtc)
    {
        var summary = new MemberPremiumSummary { MemberId = memberId };
        if (invoices.Count == 0) return summary;

        var ordered = invoices
            .OrderByDescending(i => i.BillingPeriodStart)
            .ToList();

        summary.Last12 = ordered.Select(ProjectView).ToList();
        var current = ordered[0];
        summary.CurrentInvoice = ProjectView(current);
        summary.NextBillDate = current.BillingPeriodEnd.AddDays(1);

        // Autopay heuristic: the most recent payment on the current invoice
        // used ACH/EFT. Sponsor-level BillingInfo would be more authoritative,
        // but that would require an out-of-process hop; the payment trail on
        // the invoice itself is already available and reflects reality.
        var latestPayment = current.Payments
            .OrderByDescending(p => p.PaymentDate)
            .FirstOrDefault();
        summary.AutopayEnabled =
            latestPayment?.PaymentMethod?.StartsWith("ACH", StringComparison.OrdinalIgnoreCase) == true;

        summary.Grace = ComputeGrace(current, nowUtc);
        return summary;
    }

    public static GracePeriodState ComputeGrace(PremiumInvoice invoice, DateTime nowUtc)
    {
        var state = new GracePeriodState { GraceType = invoice.GraceType };

        // Grace only applies when there's still money owed and the invoice
        // hasn't already closed out (Paid/Voided/WriteOff).
        if (invoice.BalanceDue <= 0m) return state;
        if (invoice.Status is InvoiceStatus.Paid or InvoiceStatus.Voided or InvoiceStatus.WriteOff)
            return state;

        // Grace window opens on DueDate; closes on GracePeriodExpires.
        // APTC regime uses a 3-month (≈90-day) statutory window; if the model
        // didn't persist GracePeriodExpires for some reason, compute it from
        // DueDate so the endpoint always has a definitive answer.
        var expires = invoice.GracePeriodExpires ?? (invoice.GraceType == GraceType.AptcThreeMonth
            ? invoice.DueDate.AddDays(90)
            : invoice.DueDate.AddDays(invoice.GracePeriodDays));

        if (nowUtc < invoice.DueDate) return state;
        if (nowUtc > expires) return state;

        state.IsInGrace = true;
        state.ExpiresOn = expires;
        state.DaysRemaining = Math.Max(0, (int)Math.Ceiling((expires - nowUtc).TotalDays));
        return state;
    }

    private static InvoiceView ProjectView(PremiumInvoice invoice) => new()
    {
        Id = invoice.Id,
        InvoiceNumber = invoice.InvoiceNumber,
        GroupNumber = invoice.GroupNumber,
        SponsorName = invoice.SponsorName,
        BillingPeriodStart = invoice.BillingPeriodStart,
        BillingPeriodEnd = invoice.BillingPeriodEnd,
        DueDate = invoice.DueDate,
        Status = invoice.Status,
        TotalAmount = invoice.TotalAmount,
        TotalPaid = invoice.TotalPaid,
        BalanceDue = invoice.BalanceDue,
        IsAptcSubsidized = invoice.IsAptcSubsidized,
        AptcMonthlyAmount = invoice.AptcMonthlyAmount,
        GraceType = invoice.GraceType
    };

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
