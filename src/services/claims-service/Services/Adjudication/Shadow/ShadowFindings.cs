using System.Globalization;
using System.Text;
using MongoDB.Bson.Serialization.Attributes;

namespace ClaimsService.Services.Adjudication.Shadow;

// ─────────────────────────────────────────────────────────────────
// The recovery worklist: one finding per claim where a shadow run
// says the core paid the wrong amount, with the plan's review of it.
// CHO never writes to the core and never contacts a provider: the
// plan's team reviews each finding, runs approved adjustments through
// its own mass-adjustment process, and records what came back here.
// See docs/integration/SHADOW-ADJUDICATION.md#recovery-worklist.
// ─────────────────────────────────────────────────────────────────

/// <summary>Which way the core's payment differs from CHO's.</summary>
public static class ShadowFindingDirection
{
    /// <summary>The core paid more than CHO would have: a possible overpayment, to recover.</summary>
    public const string CorePaidMore = "CorePaidMore";

    /// <summary>The core paid less: a possible underpayment, to pay the provider.</summary>
    public const string CorePaidLess = "CorePaidLess";
}

/// <summary>Where the plan's review of a finding stands.</summary>
public static class ShadowFindingStatus
{
    /// <summary>Not reviewed yet.</summary>
    public const string New = "New";

    /// <summary>The plan's reviewer agrees the payment was wrong and an adjustment should go through.</summary>
    public const string Approved = "Approved";

    /// <summary>The reviewer found the core was right (or the finding not worth pursuing).</summary>
    public const string Rejected = "Rejected";

    /// <summary>The adjustment is in the plan's own process: a recoupment request, offset or reprocessing.</summary>
    public const string InRecovery = "InRecovery";

    /// <summary>The money came back (or, for an underpayment, was paid). <see cref="ShadowFinding.RecoveredAmount"/> says how much.</summary>
    public const string Recovered = "Recovered";

    /// <summary>The provider disputed or appealed the adjustment.</summary>
    public const string Appealed = "Appealed";

    public static readonly IReadOnlyList<string> All = [New, Approved, Rejected, InRecovery, Recovered, Appealed];

    private static readonly Dictionary<string, string[]> Next = new(StringComparer.Ordinal)
    {
        [New] = [Approved, Rejected],
        [Approved] = [InRecovery, Recovered, Rejected],
        [InRecovery] = [Recovered, Appealed, Rejected],
        [Appealed] = [InRecovery, Recovered, Rejected],
        [Rejected] = [New],
        [Recovered] = [],
    };

    /// <summary>The statuses a finding in <paramref name="from"/> can move to.</summary>
    public static IReadOnlyList<string> AllowedFrom(string from) =>
        Next.TryGetValue(from, out var next) ? next : [];

    public static bool CanMove(string from, string to) => AllowedFrom(from).Contains(to, StringComparer.Ordinal);

    /// <summary>The status as stored, for a name in any case; null when it is not one.</summary>
    public static string? Parse(string? value) =>
        All.FirstOrDefault(s => string.Equals(s, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// One claim on the recovery worklist. Its id is the tenant and the claim
/// chain, so a later run of the same claim updates it instead of adding a
/// second finding. While a finding is <see cref="ShadowFindingStatus.New"/>
/// a later run refreshes its amounts; once reviewed, a later run only records
/// whether the claim still differs.
/// </summary>
[BsonIgnoreExtraElements]
public class ShadowFinding
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;

    public string ClaimId { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public string? SourceSystem { get; set; }
    public string? SourceClaimId { get; set; }
    public string ClaimType { get; set; } = string.Empty;
    public DateTime ServiceDateFrom { get; set; }
    public DateTime? CorePaidDate { get; set; }
    public string? BillingProviderNpi { get; set; }

    public string Direction { get; set; } = ShadowFindingDirection.CorePaidMore;
    public string Kind { get; set; } = string.Empty;
    public decimal CorePaid { get; set; }
    public decimal ChoPaid { get; set; }

    /// <summary>|CHO paid − core paid|: what is to recover (or to pay).</summary>
    public decimal Amount { get; set; }

    /// <summary>CHO paid − core paid: the adjustment the plan would make. Negative for an overpayment.</summary>
    public decimal AdjustmentAmount { get; set; }

    /// <summary>The CHO stage behind the difference (Pricing, BenefitCalculation, ...).</summary>
    public string Stage { get; set; } = string.Empty;

    /// <summary>CHO's first stage finding, for the reviewer.</summary>
    public string? Reason { get; set; }

    public string? CoreDenialCode { get; set; }
    public string? ChoDenialCode { get; set; }

    /// <summary>The authorization disagreement, when the difference is one (<see cref="ShadowAuthorizationMismatch"/>).</summary>
    public string? AuthorizationFinding { get; set; }

    /// <summary>The run and result the amounts come from.</summary>
    public string RunId { get; set; } = string.Empty;
    public string ResultId { get; set; } = string.Empty;

    public DateTime FirstFoundAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }

    /// <summary>False when the latest run that compared the claim found no difference any more.</summary>
    public bool StillDiffers { get; set; } = true;

    public string Status { get; set; } = ShadowFindingStatus.New;
    public DateTime? StatusChangedAtUtc { get; set; }
    public string? StatusChangedBy { get; set; }
    public string? Note { get; set; }

    /// <summary>Recovered (or, for an underpayment, paid), once the plan records it.</summary>
    public decimal? RecoveredAmount { get; set; }

    /// <summary>Every status change, oldest first.</summary>
    public List<ShadowFindingEvent> History { get; set; } = [];

    public static string IdFor(string tenantId, string claimId) => $"{tenantId}:{claimId}";
}

public class ShadowFindingEvent
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string? By { get; set; }
    public DateTime AtUtc { get; set; }
    public string? Note { get; set; }
    public decimal? RecoveredAmount { get; set; }
}

/// <summary>Body of <c>POST .../findings/{id}/status</c>.</summary>
public class ShadowFindingStatusRequest
{
    public string Status { get; set; } = string.Empty;
    public string? Note { get; set; }

    /// <summary>Required to move to Recovered: what came back (or was paid), at most the finding's amount.</summary>
    public decimal? RecoveredAmount { get; set; }
}

// ─────────────────────────────────────────────────────────────────
// Recoupment lookback
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// How far back the plan may recoup an overpayment: a default for the plan
/// and an override per provider (billing NPI), as the provider contracts set
/// it. CHO does not know these limits on its own; the plan enters them.
/// </summary>
[BsonIgnoreExtraElements]
public class ShadowRecoveryLookbackSettings
{
    /// <summary>The tenant id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Days from the core's paid date; null when the plan has not said.</summary>
    public int? DefaultDays { get; set; }

    public List<ShadowProviderLookback> Providers { get; set; } = [];

    public DateTime? UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }

    /// <summary>The provider's override, else the plan's default.</summary>
    public (int? Days, string? ContractNumber) For(string? npi)
    {
        var p = string.IsNullOrWhiteSpace(npi) ? null
            : Providers.FirstOrDefault(x => string.Equals(x.Npi, npi.Trim(), StringComparison.Ordinal));
        return p is null ? (DefaultDays, null) : (p.Days, p.ContractNumber);
    }

    public List<string> Validate()
    {
        var errors = new List<string>();
        if (DefaultDays is < 1 or > 3650) errors.Add("defaultDays must be between 1 and 3650.");
        foreach (var p in Providers)
        {
            if (string.IsNullOrWhiteSpace(p.Npi) || p.Npi.Trim().Length != 10 || !p.Npi.Trim().All(char.IsAsciiDigit))
                errors.Add($"'{p.Npi}' is not a 10-digit NPI.");
            if (p.Days is < 1 or > 3650) errors.Add($"{p.Npi}: days must be between 1 and 3650.");
        }
        var duplicates = Providers.GroupBy(p => p.Npi?.Trim(), StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key);
        errors.AddRange(duplicates.Select(d => $"{d} is listed more than once."));
        return errors;
    }
}

public class ShadowProviderLookback
{
    public string Npi { get; set; } = string.Empty;
    public string? ContractNumber { get; set; }
    public int Days { get; set; }
}

/// <summary>Where a finding stands against the recoupment lookback.</summary>
public static class ShadowLookbackState
{
    /// <summary>The deadline has passed: recovery may no longer be allowed under the contract.</summary>
    public const string Past = "Past";

    /// <summary>Within 30 days of the deadline.</summary>
    public const string DueSoon = "DueSoon";

    public const string Within = "Within";

    /// <summary>No lookback entered for the provider or plan, or no paid date on the claim.</summary>
    public const string Unknown = "Unknown";

    /// <summary>An underpayment: the lookback limits recoupment, not paying a provider.</summary>
    public const string NotApplicable = "NotApplicable";

    public const int DueSoonDays = 30;
}

/// <summary>A finding as the worklist shows it: the stored finding and its lookback, as of today.</summary>
public class ShadowFindingView
{
    public required ShadowFinding Finding { get; init; }
    public string LookbackState { get; init; } = ShadowLookbackState.Unknown;
    public int? LookbackDays { get; init; }
    public DateTime? LookbackDeadline { get; init; }
    public int? DaysLeft { get; init; }
    public string? ContractNumber { get; init; }
    public IReadOnlyList<string> NextStatuses { get; init; } = [];

    public static ShadowFindingView For(ShadowFinding f, ShadowRecoveryLookbackSettings? lookback, DateTime todayUtc)
    {
        var (days, contract) = lookback?.For(f.BillingProviderNpi) ?? (null, null);
        DateTime? deadline = days is { } d && f.CorePaidDate is { } paid ? paid.Date.AddDays(d) : null;
        int? left = deadline is { } dl ? (int)Math.Floor((dl - todayUtc.Date).TotalDays) : null;
        var state = f.Direction == ShadowFindingDirection.CorePaidLess ? ShadowLookbackState.NotApplicable
            : left is null ? ShadowLookbackState.Unknown
            : left < 0 ? ShadowLookbackState.Past
            : left <= ShadowLookbackState.DueSoonDays ? ShadowLookbackState.DueSoon
            : ShadowLookbackState.Within;
        return new ShadowFindingView
        {
            Finding = f,
            LookbackState = state,
            LookbackDays = days,
            LookbackDeadline = deadline,
            DaysLeft = left,
            ContractNumber = contract,
            NextStatuses = ShadowFindingStatus.AllowedFrom(f.Status),
        };
    }
}

// ─────────────────────────────────────────────────────────────────
// From a run's results to findings
// ─────────────────────────────────────────────────────────────────

public static class ShadowFindingBuilder
{
    /// <summary>
    /// Whether a result is a payment difference worth a finding: CHO decided,
    /// the core's answer was final, and the payments differ by more than the
    /// run's tolerance.
    /// </summary>
    public static bool IsFinding(ShadowAdjudicationResult r, decimal tolerance) =>
        r.Kind is not (ShadowDiscrepancyKind.NotComparable or ShadowDiscrepancyKind.ChoUndecided or ShadowDiscrepancyKind.Match)
        && r.AbsolutePaidVariance > Math.Max(0m, tolerance);

    /// <summary>
    /// Folds one run's results into the existing findings: new differences are
    /// added, findings not yet reviewed are refreshed, reviewed ones only record
    /// that they were seen again, and a claim the run compared that no longer
    /// differs is marked so. Returns the findings to save.
    /// </summary>
    public static List<ShadowFinding> Merge(
        ShadowAdjudicationRun run, IEnumerable<ShadowAdjudicationResult> results,
        IReadOnlyDictionary<string, ShadowFinding> existing, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(run);
        var changed = new List<ShadowFinding>();
        foreach (var r in results)
        {
            if (r.Kind is ShadowDiscrepancyKind.NotComparable or ShadowDiscrepancyKind.ChoUndecided) continue;
            var id = ShadowFinding.IdFor(run.TenantId, r.ClaimId);
            existing.TryGetValue(id, out var found);

            if (!IsFinding(r, run.Tolerance))
            {
                if (found is { StillDiffers: true })
                {
                    found.StillDiffers = false;
                    found.LastSeenAtUtc = nowUtc;
                    changed.Add(found);
                }
                continue;
            }

            if (found is null)
            {
                found = new ShadowFinding
                {
                    Id = id, TenantId = run.TenantId, ClaimId = r.ClaimId, FirstFoundAtUtc = nowUtc,
                    Status = ShadowFindingStatus.New,
                };
                Refresh(found, r);
            }
            else if (found.Status == ShadowFindingStatus.New)
            {
                Refresh(found, r);
            }
            found.StillDiffers = true;
            found.LastSeenAtUtc = nowUtc;
            changed.Add(found);
        }
        return changed;
    }

    private static void Refresh(ShadowFinding f, ShadowAdjudicationResult r)
    {
        f.ClaimNumber = r.ClaimNumber;
        f.SourceSystem = r.SourceSystem;
        f.SourceClaimId = r.SourceClaimId;
        f.ClaimType = r.ClaimType;
        f.ServiceDateFrom = r.ServiceDateFrom;
        f.CorePaidDate = r.CorePaidDate;
        f.BillingProviderNpi = r.BillingProviderNpi;
        f.Direction = r.PaidVariance < 0 ? ShadowFindingDirection.CorePaidMore : ShadowFindingDirection.CorePaidLess;
        f.Kind = r.Kind;
        f.CorePaid = r.CorePaid;
        f.ChoPaid = r.ChoPaid;
        f.Amount = r.AbsolutePaidVariance;
        f.AdjustmentAmount = r.PaidVariance;
        f.Stage = ShadowRunReportBuilder.DrivingStage(r);
        f.Reason = r.ChoStageFindings.FirstOrDefault();
        f.CoreDenialCode = r.CoreDenialCode;
        f.ChoDenialCode = r.ChoDenialCode;
        f.AuthorizationFinding = ShadowAuthorizationMismatch.For(r);
        f.RunId = r.RunId;
        f.ResultId = r.Id;
    }

    /// <summary>Applies a reviewer's status change, or says why it cannot be made.</summary>
    public static string? Move(ShadowFinding f, ShadowFindingStatusRequest request, string? actor, DateTime nowUtc)
    {
        var to = ShadowFindingStatus.Parse(request.Status);
        if (to is null)
            return $"status must be one of: {string.Join(", ", ShadowFindingStatus.All)}.";
        if (!ShadowFindingStatus.CanMove(f.Status, to))
        {
            var allowed = ShadowFindingStatus.AllowedFrom(f.Status);
            return allowed.Count == 0
                ? $"A {f.Status} finding is closed."
                : $"A {f.Status} finding can move to {string.Join(" or ", allowed)}, not {to}.";
        }
        if (to == ShadowFindingStatus.Recovered)
        {
            if (request.RecoveredAmount is not { } amount || amount <= 0m)
                return "recoveredAmount is required to record a recovery, and must be more than zero.";
            if (amount > f.Amount)
                return $"recoveredAmount cannot be more than the finding's {f.Amount.ToString("0.00", CultureInfo.InvariantCulture)}.";
        }
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is { Length: > 2000 }) return "note is limited to 2,000 characters.";

        f.History.Add(new ShadowFindingEvent
        {
            From = f.Status, To = to, By = actor, AtUtc = nowUtc, Note = note,
            RecoveredAmount = to == ShadowFindingStatus.Recovered ? request.RecoveredAmount : null,
        });
        f.Status = to;
        f.StatusChangedAtUtc = nowUtc;
        f.StatusChangedBy = actor;
        if (note is not null) f.Note = note;
        if (to == ShadowFindingStatus.Recovered) f.RecoveredAmount = request.RecoveredAmount;
        if (to == ShadowFindingStatus.New) f.RecoveredAmount = null;
        return null;
    }
}

// ─────────────────────────────────────────────────────────────────
// Summary: by status, by provider, against the lookback
// ─────────────────────────────────────────────────────────────────

public class ShadowFindingSummary
{
    public int Findings { get; init; }

    /// <summary>Possible overpayments found (every status but Rejected).</summary>
    public ShadowFindingTotal Overpayments { get; init; } = new();

    /// <summary>Possible underpayments found (every status but Rejected).</summary>
    public ShadowFindingTotal Underpayments { get; init; } = new();

    /// <summary>Approved, in recovery, appealed or recovered: what the plan's review agreed with.</summary>
    public ShadowFindingTotal Confirmed { get; init; } = new();

    /// <summary>Recorded as recovered (or paid), with <see cref="ShadowFindingTotal.Recovered"/>.</summary>
    public ShadowFindingTotal Recovered { get; init; } = new();

    /// <summary>Recovered of confirmed overpayment dollars (0 to 1); null when none are confirmed.</summary>
    public decimal? RecoveryRate { get; init; }

    public List<ShadowFindingStatusRow> ByStatus { get; init; } = [];

    /// <summary>Open overpayments (not rejected or recovered) whose lookback has passed, or ends within 30 days.</summary>
    public ShadowFindingTotal PastLookback { get; init; } = new();
    public ShadowFindingTotal LookbackDueSoon { get; init; } = new();

    /// <summary>Open overpayments with no lookback entered for their provider or plan.</summary>
    public ShadowFindingTotal LookbackUnknown { get; init; } = new();

    /// <summary>Billing providers, the most open overpayment dollars first.</summary>
    public List<ShadowFindingProviderRow> ByProvider { get; init; } = [];
}

public class ShadowFindingTotal
{
    public int Findings { get; set; }
    public decimal Amount { get; set; }
    public decimal Recovered { get; set; }
}

public class ShadowFindingStatusRow
{
    public string Status { get; set; } = string.Empty;
    public int Findings { get; set; }
    public decimal Amount { get; set; }
    public decimal Recovered { get; set; }
}

public class ShadowFindingProviderRow
{
    public string BillingProviderNpi { get; set; } = string.Empty;
    public string? ContractNumber { get; set; }
    public int Findings { get; set; }
    public decimal CorePaidMore { get; set; }
    public decimal CorePaidLess { get; set; }

    /// <summary>Overpayment dollars not yet rejected or recovered.</summary>
    public decimal OpenOverpayments { get; set; }
    public decimal Confirmed { get; set; }
    public decimal Recovered { get; set; }
    public int PastLookback { get; set; }

    /// <summary>The earliest lookback deadline among its open overpayments.</summary>
    public DateTime? NextDeadline { get; set; }
}

public static class ShadowFindingSummaryBuilder
{
    private static readonly string[] ConfirmedStatuses =
        [ShadowFindingStatus.Approved, ShadowFindingStatus.InRecovery, ShadowFindingStatus.Appealed, ShadowFindingStatus.Recovered];

    public static ShadowFindingSummary Build(IReadOnlyList<ShadowFindingView> views, int topProviders = 25)
    {
        ArgumentNullException.ThrowIfNull(views);
        var live = views.Where(v => v.Finding.Status != ShadowFindingStatus.Rejected).ToList();
        var openOver = live.Where(v => v.Finding.Direction == ShadowFindingDirection.CorePaidMore
                                       && v.Finding.Status != ShadowFindingStatus.Recovered).ToList();
        var confirmed = views.Where(v => ConfirmedStatuses.Contains(v.Finding.Status)).ToList();
        var confirmedOver = confirmed.Where(v => v.Finding.Direction == ShadowFindingDirection.CorePaidMore).ToList();
        var recoveredOver = confirmedOver.Sum(v => v.Finding.RecoveredAmount ?? 0m);

        return new ShadowFindingSummary
        {
            Findings = views.Count,
            Overpayments = Total(live.Where(v => v.Finding.Direction == ShadowFindingDirection.CorePaidMore)),
            Underpayments = Total(live.Where(v => v.Finding.Direction == ShadowFindingDirection.CorePaidLess)),
            Confirmed = Total(confirmed),
            Recovered = Total(views.Where(v => v.Finding.Status == ShadowFindingStatus.Recovered)),
            RecoveryRate = confirmedOver.Count == 0 || confirmedOver.Sum(v => v.Finding.Amount) == 0m
                ? null
                : Math.Round(recoveredOver / confirmedOver.Sum(v => v.Finding.Amount), 4),
            ByStatus = ShadowFindingStatus.All.Select(s =>
            {
                var t = Total(views.Where(v => v.Finding.Status == s));
                return new ShadowFindingStatusRow { Status = s, Findings = t.Findings, Amount = t.Amount, Recovered = t.Recovered };
            }).ToList(),
            PastLookback = Total(openOver.Where(v => v.LookbackState == ShadowLookbackState.Past)),
            LookbackDueSoon = Total(openOver.Where(v => v.LookbackState == ShadowLookbackState.DueSoon)),
            LookbackUnknown = Total(openOver.Where(v => v.LookbackState == ShadowLookbackState.Unknown)),
            ByProvider = views
                .GroupBy(v => v.Finding.BillingProviderNpi ?? "unknown", StringComparer.Ordinal)
                .Select(g =>
                {
                    var list = g.ToList();
                    var notRejected = list.Where(v => v.Finding.Status != ShadowFindingStatus.Rejected).ToList();
                    var open = notRejected.Where(v => v.Finding.Direction == ShadowFindingDirection.CorePaidMore
                                                      && v.Finding.Status != ShadowFindingStatus.Recovered).ToList();
                    return new ShadowFindingProviderRow
                    {
                        BillingProviderNpi = g.Key,
                        ContractNumber = list.Select(v => v.ContractNumber).FirstOrDefault(c => c is not null),
                        Findings = list.Count,
                        CorePaidMore = notRejected.Where(v => v.Finding.Direction == ShadowFindingDirection.CorePaidMore).Sum(v => v.Finding.Amount),
                        CorePaidLess = notRejected.Where(v => v.Finding.Direction == ShadowFindingDirection.CorePaidLess).Sum(v => v.Finding.Amount),
                        OpenOverpayments = open.Sum(v => v.Finding.Amount),
                        Confirmed = list.Where(v => ConfirmedStatuses.Contains(v.Finding.Status)).Sum(v => v.Finding.Amount),
                        Recovered = list.Sum(v => v.Finding.RecoveredAmount ?? 0m),
                        PastLookback = open.Count(v => v.LookbackState == ShadowLookbackState.Past),
                        NextDeadline = open.Where(v => v.LookbackState is ShadowLookbackState.DueSoon or ShadowLookbackState.Within)
                            .Select(v => v.LookbackDeadline).Min(),
                    };
                })
                .OrderByDescending(r => r.OpenOverpayments)
                .ThenByDescending(r => r.CorePaidMore + r.CorePaidLess)
                .ThenBy(r => r.BillingProviderNpi, StringComparer.Ordinal)
                .Take(Math.Clamp(topProviders, 1, 500))
                .ToList(),
        };
    }

    private static ShadowFindingTotal Total(IEnumerable<ShadowFindingView> views)
    {
        var list = views.ToList();
        return new ShadowFindingTotal
        {
            Findings = list.Count,
            Amount = list.Sum(v => v.Finding.Amount),
            Recovered = list.Sum(v => v.Finding.RecoveredAmount ?? 0m),
        };
    }
}

// ─────────────────────────────────────────────────────────────────
// Export for the plan's own mass adjustment
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// The worklist as a plain CSV the plan loads into (or maps to) its core's
/// mass-adjustment process. One row per claim, the core's own claim id first.
/// CHO does not format it for any one core's import; that layout is scoped
/// per plan when asked. Formula-safe for spreadsheets.
/// </summary>
public static class ShadowFindingCsv
{
    public static readonly string[] Header =
    [
        "FindingId", "SourceSystem", "SourceClaimId", "ClaimNumber", "ClaimType", "BillingProviderNpi", "ContractNumber",
        "ServiceDateFrom", "CorePaidDate", "Direction", "CorePaid", "ChoPaid", "AdjustmentAmount", "Stage", "Reason",
        "CoreDenialCode", "ChoDenialCode", "AuthorizationFinding", "Status", "StatusChangedBy", "StatusChangedAt",
        "RecoveredAmount", "LookbackDeadline", "LookbackState", "Note",
    ];

    public static string Write(IEnumerable<ShadowFindingView> views)
    {
        var sb = new StringBuilder();
        Append(sb, Header);
        foreach (var v in views)
        {
            var f = v.Finding;
            Append(sb,
            [
                f.Id, f.SourceSystem, f.SourceClaimId, f.ClaimNumber, f.ClaimType, f.BillingProviderNpi, v.ContractNumber,
                Date(f.ServiceDateFrom), Date(f.CorePaidDate), f.Direction, Money(f.CorePaid), Money(f.ChoPaid), Money(f.AdjustmentAmount),
                f.Stage, f.Reason, f.CoreDenialCode, f.ChoDenialCode, f.AuthorizationFinding, f.Status, f.StatusChangedBy,
                f.StatusChangedAtUtc?.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                f.RecoveredAmount is { } r ? Money(r) : null, Date(v.LookbackDeadline), v.LookbackState, f.Note,
            ]);
        }
        return sb.ToString();
    }

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string? Date(DateTime? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static void Append(StringBuilder sb, IReadOnlyList<string?> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(ShadowDiscrepancyCsv.Escape(fields[i]));
        }
        sb.Append("\r\n");
    }
}
