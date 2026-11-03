using ClaimsService.Services.Adjudication.Shadow;
using CloudHealthOffice.FeeScheduleEngine.Models;

namespace ClaimsService.Services.RetroRate;

/// <summary>
/// Body of <c>POST /api/v1/claims/retro-rate/impact-runs</c>: the fee schedule
/// change to measure. Names the schedule, and optionally the revision the
/// change stored (its <see cref="FeeScheduleChange.NewRevision"/>); without
/// one, the schedule's latest change is used.
/// </summary>
public class RetroRateImpactRunRequest
{
    public string FeeScheduleId { get; set; } = string.Empty;
    public long? Revision { get; set; }

    /// <summary>Most claims to re-price; capped by <see cref="RetroRateOptions.MaxClaimsPerRun"/>.</summary>
    public int? Limit { get; set; }
}

/// <summary>How a paid claim was tied to the change.</summary>
public static class RetroRateMatch
{
    /// <summary>A line records that it was priced on the schedule at an older revision.</summary>
    public const string Provenance = "Provenance";

    /// <summary>
    /// A state supplemental payment on the claim came from the schedule
    /// (<c>adjudicationResult.supplementalPayments</c>).
    /// </summary>
    public const string Supplemental = "Supplemental";

    /// <summary>
    /// The claim has no pricing provenance (priced before it was recorded), is
    /// on the plan the schedule is the default for, and bills a changed code.
    /// A broader match: the claim may have been priced on a provider contract.
    /// </summary>
    public const string PlanFallback = "PlanFallback";

    /// <summary>
    /// A new schedule took effect retroactively for the claim's plan, and the
    /// claim was priced on another schedule before it existed.
    /// </summary>
    public const string NewSchedule = "NewSchedule";
}

/// <summary>What re-pricing would do to a paid claim.</summary>
public static class RetroRateDirection
{
    /// <summary>The plan would pay more: an additional payment is owed.</summary>
    public const string Underpaid = "Underpaid";

    /// <summary>The plan would pay less: a recoupment, if the plan opts in.</summary>
    public const string Overpaid = "Overpaid";

    /// <summary>Paid amount within tolerance; nothing to adjust.</summary>
    public const string Unchanged = "Unchanged";

    /// <summary>Re-pricing pended, rejected or denied; the claim needs a person.</summary>
    public const string Undecided = "Undecided";

    public static readonly IReadOnlyList<string> All = [Underpaid, Overpaid, Unchanged, Undecided];
}

/// <summary>
/// One paid claim's dry-run re-price: what it was paid, what it would be paid
/// on the schedule as it stands now, and the shadow comparison behind it.
/// Nothing on the claim changes.
/// </summary>
public class RetroRateImpactResult
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;

    public string ClaimId { get; set; } = string.Empty;

    /// <summary>The claim version document that was read (what a reprocessing run adjusts).</summary>
    public string ClaimDocumentId { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public string ClaimStatus { get; set; } = string.Empty;
    public DateTime ServiceDateFrom { get; set; }
    public string? BillingProviderNpi { get; set; }
    public string? BenefitPlanId { get; set; }

    /// <summary>A <see cref="RetroRateMatch"/> value.</summary>
    public string Match { get; set; } = string.Empty;

    /// <summary>A <see cref="RetroRateDirection"/> value.</summary>
    public string Direction { get; set; } = string.Empty;

    /// <summary>Lines billed with a code the change touched (all lines when the change touched every code).</summary>
    public List<int> AffectedLines { get; set; } = [];

    /// <summary>The revision of the schedule the claim was priced on, when its lines record one.</summary>
    public long? PricedOnRevision { get; set; }

    public decimal PaidAllowed { get; set; }
    public decimal RepricedAllowed { get; set; }
    public decimal Paid { get; set; }
    public decimal Repriced { get; set; }
    public decimal PaidMemberLiability { get; set; }
    public decimal RepricedMemberLiability { get; set; }

    /// <summary>Repriced − Paid: positive is owed to the provider, negative would be recouped.</summary>
    public decimal NetPaidDelta { get; set; }

    /// <summary>|NetPaidDelta|, stored so results sort largest first.</summary>
    public decimal AbsoluteNetPaidDelta { get; set; }

    public decimal MemberLiabilityDelta { get; set; }

    /// <summary>Why re-pricing did not decide, when <see cref="Direction"/> is Undecided.</summary>
    public string? PendCode { get; set; }
    public string? PendReason { get; set; }

    /// <summary>Line by line, paid against re-priced (the shadow comparison's lines).</summary>
    public List<ShadowLineComparison> Lines { get; set; } = [];

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>One billing provider's totals in a run.</summary>
public class RetroRateProviderTotal
{
    public string BillingProviderNpi { get; set; } = string.Empty;
    public int Claims { get; set; }
    public decimal Underpaid { get; set; }
    public decimal Overpaid { get; set; }
    public decimal Net { get; set; }
}

/// <summary>A dry-run impact report for one fee schedule change.</summary>
public class RetroRateImpactRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = string.Empty;
    public string? RequestedBy { get; set; }

    public string FeeScheduleId { get; set; } = string.Empty;
    public string FeeScheduleName { get; set; } = string.Empty;
    public string ChangeId { get; set; } = string.Empty;
    public string ChangeKind { get; set; } = string.Empty;
    public long NewRevision { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? TermDate { get; set; }
    public bool AllCodes { get; set; }
    public int ChangedCodeCount { get; set; }

    /// <summary>Paid claims in the date range and plan scope that were read.</summary>
    public int CandidatesRead { get; set; }

    /// <summary>True when there were more candidates than the run's limit; run again with a higher limit.</summary>
    public bool Truncated { get; set; }

    public int ClaimsAffected { get; set; }
    public Dictionary<string, int> CountsByDirection { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> CountsByMatch { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Sum owed to providers (Underpaid claims).</summary>
    public decimal TotalUnderpaid { get; set; }

    /// <summary>Sum the plan overpaid (Overpaid claims, as a positive number).</summary>
    public decimal TotalOverpaid { get; set; }

    public decimal NetPaidDelta { get; set; }
    public decimal NetMemberLiabilityDelta { get; set; }

    /// <summary>Largest providers by absolute net, at most 25.</summary>
    public List<RetroRateProviderTotal> TopProviders { get; set; } = [];

    public List<string> Failed { get; set; } = [];

    /// <summary>Plain-language caveats for whoever reads the report.</summary>
    public List<string> Notes { get; set; } = [];

    public decimal Tolerance { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }
}

/// <summary>Settings under <c>Adjudication:RetroRate</c>.</summary>
public class RetroRateOptions
{
    public const string SectionName = "Adjudication:RetroRate";

    /// <summary>Most paid claims one impact run re-prices.</summary>
    public int MaxClaimsPerRun { get; set; } = 2000;

    /// <summary>A paid difference at or below this is Unchanged.</summary>
    public decimal Tolerance { get; set; } = 0.01m;

    /// <summary>
    /// A reprocessing run must be approved by someone other than the person
    /// who proposed it (two-person rule). On by default.
    /// </summary>
    public bool RequireSecondApprover { get; set; } = true;

    /// <summary>
    /// Per-tenant recoupment opt-in. Without an entry, overpayments are never
    /// recouped: underpayments are paid, overpaid claims are left as paid.
    /// </summary>
    public Dictionary<string, RetroRateTenantOptions> Tenants { get; set; } = new(StringComparer.Ordinal);

    public RetroRateTenantOptions For(string tenantId) =>
        Tenants.TryGetValue(tenantId, out var t) ? t : new RetroRateTenantOptions();
}

/// <summary>The candidate query for <see cref="ClaimsService.Repositories.IClaimRepository.FindRetroRateCandidatesAsync"/>.</summary>
public sealed record RetroRateCandidateQuery
{
    public required string FeeScheduleId { get; init; }

    /// <summary>Plans whose claims are candidates even without provenance (default-for and supplemental plans).</summary>
    public IReadOnlyList<string> PlanKeys { get; init; } = [];

    /// <summary>First date of service in scope.</summary>
    public required DateTime ServiceFrom { get; init; }

    /// <summary>Dates of service before this are in scope; null = open.</summary>
    public DateTime? ServiceBefore { get; init; }
}
