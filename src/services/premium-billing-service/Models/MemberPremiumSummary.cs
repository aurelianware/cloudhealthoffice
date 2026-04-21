namespace PremiumBillingService.Models;

/// <summary>
/// Premium billing rollup for a single member. Invoices are sponsor-scoped,
/// so we derive the member view by selecting invoices whose
/// <see cref="InvoiceLineItem.MemberId"/> matches the requested member.
/// </summary>
public class MemberPremiumSummary
{
    public string MemberId { get; set; } = string.Empty;

    /// <summary>The most recent invoice containing this member.</summary>
    public InvoiceView? CurrentInvoice { get; set; }

    /// <summary>
    /// Projected next bill date, derived from the current invoice as
    /// <c>CurrentInvoice.BillingPeriodEnd + 1 day</c>. When a future-dated
    /// invoice has already been generated, it will be the newest invoice and
    /// therefore already the <see cref="CurrentInvoice"/>, so this rule
    /// covers both cases.
    /// </summary>
    public DateTime? NextBillDate { get; set; }

    /// <summary>
    /// True when the sponsor BillingInfo indicates ACH/EFT draft. Derived at
    /// the controller layer since autopay is a sponsor-level config rather
    /// than an invoice attribute.
    /// </summary>
    public bool AutopayEnabled { get; set; }

    public GracePeriodState Grace { get; set; } = new();

    public List<InvoiceView> Last12 { get; set; } = new();
}

/// <summary>
/// Projected view of an invoice optimized for the portal Premium tab — keeps
/// the line-item arrays off the wire since the tab only shows totals.
/// </summary>
public class InvoiceView
{
    public string Id { get; set; } = string.Empty;
    public string InvoiceNumber { get; set; } = string.Empty;
    public string GroupNumber { get; set; } = string.Empty;
    public string SponsorName { get; set; } = string.Empty;
    public DateTime BillingPeriodStart { get; set; }
    public DateTime BillingPeriodEnd { get; set; }
    public DateTime DueDate { get; set; }
    public InvoiceStatus Status { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal TotalPaid { get; set; }
    public decimal BalanceDue { get; set; }
    public bool IsAptcSubsidized { get; set; }
    public decimal AptcMonthlyAmount { get; set; }
    public GraceType GraceType { get; set; }
}

/// <summary>
/// Computed grace-period state for the member's current invoice. The portal
/// uses this to decide whether to render the grace-period alert and what
/// copy to show.
/// </summary>
public class GracePeriodState
{
    public bool IsInGrace { get; set; }
    public GraceType GraceType { get; set; }
    public int DaysRemaining { get; set; }
    public DateTime? ExpiresOn { get; set; }
}
