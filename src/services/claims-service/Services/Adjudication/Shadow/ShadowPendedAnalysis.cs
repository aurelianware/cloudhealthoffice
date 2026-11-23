using ClaimsService.Models;
using ClaimsService.Repositories;
using Microsoft.Extensions.Options;

namespace ClaimsService.Services.Adjudication.Shadow;

/// <summary>
/// What CHO would have done with the claims the core pended, by the core's
/// pend reason: how many there were, how many CHO would have decided without
/// an examiner, how often CHO's decision matched the examiner's once a later
/// file brought it, and how long the claims sat pended. A lead for the plan's
/// own review of which pends could be automated, not a determination; it
/// states no cost per claim touched, because CHO does not know the plan's.
/// See docs/integration/SHADOW-ADJUDICATION.md#pended-claims.
/// </summary>
public sealed class ShadowPendedAnalysis
{
    public DateTime AsOfUtc { get; set; }

    /// <summary>Shadow results from this time on are read.</summary>
    public DateTime SinceUtc { get; set; }

    /// <summary>More pended claims were found than one report reads; the oldest were left out.</summary>
    public bool Truncated { get; set; }

    public ShadowPendReasonRow Total { get; set; } = new() { PendReasonCode = "All" };

    /// <summary>One row per core pend reason, most claims first.</summary>
    public List<ShadowPendReasonRow> ByPendReason { get; set; } = [];
}

public sealed class ShadowPendReasonRow
{
    /// <summary>The core's pend reason code; "(none)" when the file gave none.</summary>
    public string PendReasonCode { get; set; } = string.Empty;

    /// <summary>The description the file gave most often for the code.</summary>
    public string? PendReason { get; set; }

    /// <summary>Pended claims CHO ran.</summary>
    public int Claims { get; set; }

    /// <summary>CHO would have paid or denied the claim without an examiner.</summary>
    public int ChoWouldResolve { get; set; }
    public int ChoWouldPay { get; set; }
    public int ChoWouldDeny { get; set; }

    /// <summary>CHO would have pended too, or lacked data to decide.</summary>
    public int ChoWouldPend { get; set; }

    /// <summary>ChoWouldResolve ÷ Claims.</summary>
    public decimal? AutoResolveRate { get; set; }

    /// <summary>Why CHO would pend, as "CODE (n)", most first.</summary>
    public List<string> ChoPendCodes { get; set; } = [];

    /// <summary>A later file brought the examiner's final paid or denied decision.</summary>
    public int ExaminerDecided { get; set; }

    /// <summary>Claims CHO would have resolved and the examiner has decided: the ones agreement is measured on.</summary>
    public int Compared { get; set; }

    /// <summary>Same outcome, and for paid claims the same payment within the run's tolerance.</summary>
    public int Agreed { get; set; }
    public int ChoPaidExaminerDenied { get; set; }
    public int ChoDeniedExaminerPaid { get; set; }
    public int PaidAmountDiffers { get; set; }

    /// <summary>Agreed ÷ Compared.</summary>
    public decimal? AgreementRate { get; set; }

    /// <summary>Average days from pended (or received) to the examiner's decision.</summary>
    public decimal? AverageDaysPended { get; set; }

    /// <summary>No final decision has arrived yet.</summary>
    public int StillPended { get; set; }

    /// <summary>Days the oldest still-pended claim has waited, as of the report.</summary>
    public int? OldestStillPendedDays { get; set; }

    /// <summary>The core voided the claim after pending it.</summary>
    public int Voided { get; set; }

    /// <summary>
    /// Enough claims, mostly resolved by CHO, and agreeing with the examiner
    /// (see <see cref="ShadowPendedAnalysisBuilder"/>): worth the plan's review
    /// as a pend that could be automated.
    /// </summary>
    public bool AutomationCandidate { get; set; }
}

public static class ShadowPendedAnalysisBuilder
{
    public const string NoCode = "(none)";

    /// <summary>A reason needs this many pended claims to be a candidate.</summary>
    public const int CandidateMinClaims = 20;

    /// <summary>...this many of them compared with the examiner...</summary>
    public const int CandidateMinCompared = 10;

    /// <summary>...CHO resolving at least this share...</summary>
    public const decimal CandidateMinAutoResolveRate = 0.80m;

    /// <summary>...and agreeing with the examiner at least this often.</summary>
    public const decimal CandidateMinAgreementRate = 0.95m;

    /// <summary>CHO paid or denied the claim itself (a hold only for provider screening still counts).</summary>
    public static bool ChoResolved(ShadowAdjudicationResult r) =>
        r.ChoOutcome is ShadowOutcome.Paid or ShadowOutcome.Denied
        && (r.ChoPendCode is null || r.HeldForProviderScreening);

    /// <param name="pended">Shadow results of claims the core had pended; a claim's latest result is used.</param>
    /// <param name="current">Each claim's current version by claim id, where one was found.</param>
    public static ShadowPendedAnalysis Build(
        IEnumerable<ShadowAdjudicationResult> pended,
        IReadOnlyDictionary<string, Claim> current,
        decimal tolerance,
        DateTime asOfUtc,
        DateTime sinceUtc)
    {
        var latest = pended
            .Where(r => r.CorePended)
            .GroupBy(r => r.ClaimId, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(r => r.CreatedAtUtc).First())
            .ToList();

        var analysis = new ShadowPendedAnalysis
        {
            AsOfUtc = asOfUtc,
            SinceUtc = sinceUtc,
            Total = Row("All", latest, current, tolerance, asOfUtc),
        };
        analysis.Total.PendReason = null;
        analysis.ByPendReason = latest
            .GroupBy(r => r.CorePendCode ?? NoCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => Row(g.Key, g.ToList(), current, tolerance, asOfUtc))
            .OrderByDescending(r => r.Claims)
            .ThenBy(r => r.PendReasonCode, StringComparer.Ordinal)
            .ToList();
        return analysis;
    }

    private static ShadowPendReasonRow Row(
        string code, IReadOnlyList<ShadowAdjudicationResult> results, IReadOnlyDictionary<string, Claim> current,
        decimal tolerance, DateTime asOfUtc)
    {
        var row = new ShadowPendReasonRow
        {
            PendReasonCode = code,
            PendReason = results.Select(r => r.CorePendReason).Where(s => !string.IsNullOrWhiteSpace(s))
                .GroupBy(s => s!, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault(),
            Claims = results.Count,
        };

        var days = new List<decimal>();
        var choPendCodes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in results)
        {
            var resolved = ChoResolved(r);
            if (resolved)
            {
                row.ChoWouldResolve++;
                if (r.ChoOutcome == ShadowOutcome.Paid) row.ChoWouldPay++;
                else row.ChoWouldDeny++;
            }
            else
            {
                row.ChoWouldPend++;
                var why = r.ChoPendCode ?? r.ChoOutcome;
                choPendCodes[why] = choPendCodes.GetValueOrDefault(why) + 1;
            }

            var start = (r.CorePendedDate ?? r.CoreReceivedDate)?.Date;
            current.TryGetValue(r.ClaimId, out var claim);
            var examiner = ExaminerOutcome(claim);
            switch (examiner)
            {
                case ShadowOutcome.Paid or ShadowOutcome.Denied:
                    row.ExaminerDecided++;
                    var decidedOn = (claim!.AdjudicatedDate ?? claim.PaidDate)?.Date;
                    if (start is { } s && decidedOn is { } d && d >= s)
                        days.Add((decimal)(d - s).TotalDays);
                    if (!resolved) break;
                    row.Compared++;
                    if (r.ChoOutcome != examiner)
                    {
                        if (r.ChoOutcome == ShadowOutcome.Paid) row.ChoPaidExaminerDenied++;
                        else row.ChoDeniedExaminerPaid++;
                    }
                    else if (examiner == ShadowOutcome.Paid
                             && Math.Abs(r.ChoPaid - (claim.AdjudicationResult?.PayerPayment ?? 0m)) > tolerance)
                    {
                        row.PaidAmountDiffers++;
                    }
                    else
                    {
                        row.Agreed++;
                    }
                    break;
                case "Voided":
                    row.Voided++;
                    break;
                default:
                    row.StillPended++;
                    if (start is { } since)
                    {
                        var waited = Math.Max(0, (int)(asOfUtc.Date - since).TotalDays);
                        row.OldestStillPendedDays = Math.Max(row.OldestStillPendedDays ?? 0, waited);
                    }
                    break;
            }
        }

        row.AutoResolveRate = Rate(row.ChoWouldResolve, row.Claims);
        row.AgreementRate = Rate(row.Agreed, row.Compared);
        row.AverageDaysPended = days.Count == 0 ? null : Math.Round(days.Average(), 1);
        row.ChoPendCodes = choPendCodes.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Take(5).Select(p => $"{p.Key} ({p.Value})").ToList();
        row.AutomationCandidate = row.Claims >= CandidateMinClaims
                                  && row.Compared >= CandidateMinCompared
                                  && row.AutoResolveRate >= CandidateMinAutoResolveRate
                                  && row.AgreementRate >= CandidateMinAgreementRate;
        return row;
    }

    /// <summary>Paid or Denied once the examiner decided; "Voided"; null while still pended or not found.</summary>
    private static string? ExaminerOutcome(Claim? claim)
    {
        if (claim is null || claim.ExternalSource?.CorePend is not null) return null;
        if (claim.Status == ClaimStatus.Voided) return "Voided";
        var outcome = ShadowDiscrepancyComparer.CoreOutcome(claim.Status);
        return outcome == ShadowOutcome.NotFinal ? null : outcome;
    }

    private static decimal? Rate(int part, int whole) => whole == 0 ? null : Math.Round((decimal)part / whole, 4);
}

/// <summary>Reads the pended claims' shadow results and their current versions, and builds the analysis.</summary>
public sealed class ShadowPendedAnalysisService
{
    /// <summary>Most pended claims one report reads.</summary>
    public const int MaxClaims = 20_000;

    private readonly IShadowAdjudicationRepository _results;
    private readonly IClaimRepository _claims;
    private readonly ShadowAdjudicationOptions _options;
    private readonly TimeProvider _clock;

    public ShadowPendedAnalysisService(
        IShadowAdjudicationRepository results, IClaimRepository claims, IOptions<ShadowAdjudicationOptions> options,
        TimeProvider? clock = null)
    {
        _results = results;
        _claims = claims;
        _options = options.Value;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<ShadowPendedAnalysis> BuildAsync(string tenantId, int days, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var since = now.Date.AddDays(-Math.Clamp(days, 1, 3660));
        // Several runs may have shadowed the same claim; read enough results
        // to find MaxClaims claims, newest first.
        var results = await _results.ListPendedResultsAsync(tenantId, since, MaxClaims * 2, ct).ConfigureAwait(false);
        var claimIds = results.Select(r => r.ClaimId).Distinct(StringComparer.Ordinal).ToList();
        var truncated = claimIds.Count > MaxClaims || results.Count >= MaxClaims * 2;
        claimIds = claimIds.Take(MaxClaims).ToList();
        var keep = claimIds.ToHashSet(StringComparer.Ordinal);

        var current = new Dictionary<string, Claim>(StringComparer.Ordinal);
        foreach (var id in claimIds)
        {
            ct.ThrowIfCancellationRequested();
            if (await _claims.GetLatestVersionAsync(id, now).ConfigureAwait(false) is { } claim)
                current[id] = claim;
        }

        var analysis = ShadowPendedAnalysisBuilder.Build(
            results.Where(r => keep.Contains(r.ClaimId)), current, _options.Tolerance, now, since);
        analysis.Truncated = truncated;
        return analysis;
    }
}
