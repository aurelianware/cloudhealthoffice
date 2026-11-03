using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Shadow;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.Infrastructure.Logging;
using Microsoft.Extensions.Options;

namespace ClaimsService.Services.RetroRate;

/// <summary>
/// Dry-run impact of a fee schedule change on claims already paid: finds the
/// paid claims the change touches, re-prices each through the shadow pipeline
/// (nothing persisted, no event, no accumulator write) and records what it
/// would owe or recover, per claim and per provider. Step 2 of retro rate
/// reprocessing; see docs/integration/RETRO-RATE-REPROCESSING.md.
/// </summary>
public interface IRetroRateImpactService
{
    Task<RetroRateImpactRun> RunAsync(string tenantId, RetroRateImpactRunRequest request, string? actorId, CancellationToken ct);
}

/// <summary>benefit-plan-service has no change for the schedule (or could not be reached).</summary>
public sealed class FeeScheduleChangeNotFoundException(string message) : Exception(message);

public sealed class RetroRateImpactService : IRetroRateImpactService
{
    internal const int TopProviderCount = 25;

    private readonly IClaimRepository _claims;
    private readonly IFeeScheduleChangeClient _changes;
    private readonly IShadowAdjudicationPipeline _pipeline;
    private readonly IRetroRateImpactRepository _store;
    private readonly RetroRateOptions _options;
    private readonly ILogger<RetroRateImpactService> _logger;

    public RetroRateImpactService(
        IClaimRepository claims,
        IFeeScheduleChangeClient changes,
        IShadowAdjudicationPipeline pipeline,
        IRetroRateImpactRepository store,
        IOptions<RetroRateOptions> options,
        ILogger<RetroRateImpactService> logger)
    {
        _claims = claims;
        _changes = changes;
        _pipeline = pipeline;
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<RetroRateImpactRun> RunAsync(
        string tenantId, RetroRateImpactRunRequest request, string? actorId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentNullException.ThrowIfNull(request);

        var scheduleId = request.FeeScheduleId.Trim();
        var change = await _changes.GetChangeAsync(tenantId, scheduleId, request.Revision, ct).ConfigureAwait(false)
            ?? throw new FeeScheduleChangeNotFoundException(request.Revision is { } r
                ? $"No change to fee schedule '{scheduleId}' stored revision {r}."
                : $"No recorded change to fee schedule '{scheduleId}' (changes are recorded from the release that added fee schedule history).");

        var max = Math.Max(1, _options.MaxClaimsPerRun);
        var limit = Math.Clamp(request.Limit ?? max, 1, max);
        var run = new RetroRateImpactRun
        {
            TenantId = tenantId,
            RequestedBy = actorId,
            FeeScheduleId = change.FeeScheduleId,
            FeeScheduleName = change.FeeScheduleName,
            ChangeId = change.Id,
            ChangeKind = change.Kind.ToString(),
            NewRevision = change.NewRevision,
            EffectiveDate = change.EffectiveDate,
            TermDate = change.TermDate,
            AllCodes = change.AllCodes,
            ChangedCodeCount = change.ChangedCodes?.Count ?? 0,
            Tolerance = _options.Tolerance,
            StartedAtUtc = DateTime.UtcNow,
        };

        var (from, before) = RetroRateMatcher.ServiceWindow(change);
        var query = new RetroRateCandidateQuery
        {
            FeeScheduleId = change.FeeScheduleId,
            PlanKeys = RetroRateMatcher.PlanKeys(change),
            ServiceFrom = from,
            ServiceBefore = before,
        };

        // One extra row tells a truncated read from an exact fit.
        var candidates = await _claims.FindRetroRateCandidatesAsync(query, limit + 1, ct).ConfigureAwait(false);
        run.Truncated = candidates.Count > limit;
        candidates = candidates.Take(limit).ToList();
        run.CandidatesRead = candidates.Count;

        var results = new List<RetroRateImpactResult>();
        foreach (var claim in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var match = RetroRateMatcher.Match(claim, change);
            if (match is null) continue;

            try
            {
                var context = await _pipeline
                    .EvaluateShadowAsync(tenantId, ShadowAdjudicationService.DetachedCopy(claim), actorId, $"retro-rate:{run.Id}", ct)
                    .ConfigureAwait(false);
                var result = Build(claim, context, match, _options.Tolerance);
                result.RunId = run.Id;
                result.TenantId = tenantId;
                results.Add(result);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var id = ClaimKey(claim);
                _logger.LogError(ex, "Retro rate re-price failed for claim {ClaimId}", LogSanitizer.Sanitize(id));
                run.Failed.Add($"{id}: {ex.GetType().Name}");
            }
        }

        Summarize(run, results);
        AddNotes(run, change);
        run.CompletedAtUtc = DateTime.UtcNow;
        await _store.SaveRunAsync(run, results, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Retro rate impact run {RunId} for {ScheduleId}@{Revision}: {Affected} affected of {Read} read, net {Net}",
            run.Id, LogSanitizer.Sanitize(change.FeeScheduleId, 256), change.NewRevision,
            run.ClaimsAffected, run.CandidatesRead, run.NetPaidDelta);
        return run;
    }

    private static string ClaimKey(Claim claim) =>
        string.IsNullOrEmpty(claim.ClaimVersionId) ? claim.Id : claim.ClaimVersionId;

    /// <summary>
    /// The paid claim against its shadow re-price. Reuses the shadow
    /// comparer with the paid claim as the "core" answer.
    /// </summary>
    internal static RetroRateImpactResult Build(
        Claim paid, ClaimAdjudicationContext repriced, RetroRateMatcher.MatchResult match, decimal tolerance)
    {
        var comparison = ShadowDiscrepancyComparer.Compare(paid, repriced, tolerance);
        var decided = comparison.ChoOutcome is ShadowOutcome.Paid
                      && comparison.Kind is not (ShadowDiscrepancyKind.ChoUndecided or ShadowDiscrepancyKind.NotComparable);
        var net = decided ? comparison.ChoPaid - comparison.CorePaid : 0m;
        var memberDelta = decided ? comparison.ChoMemberLiability - comparison.CoreMemberLiability : 0m;

        return new RetroRateImpactResult
        {
            ClaimId = ClaimKey(paid),
            ClaimDocumentId = paid.Id,
            ClaimNumber = paid.ClaimNumber,
            ClaimStatus = paid.Status.ToString(),
            ServiceDateFrom = paid.ServiceDateFrom,
            BillingProviderNpi = paid.BillingProviderNPI,
            BenefitPlanId = paid.BenefitPlanId,
            Match = match.Match,
            AffectedLines = match.AffectedLines.ToList(),
            PricedOnRevision = match.PricedOnRevision,
            PaidAllowed = comparison.CoreAllowed,
            RepricedAllowed = decided ? comparison.ChoAllowed : 0m,
            Paid = comparison.CorePaid,
            Repriced = decided ? comparison.ChoPaid : 0m,
            PaidMemberLiability = comparison.CoreMemberLiability,
            RepricedMemberLiability = decided ? comparison.ChoMemberLiability : 0m,
            NetPaidDelta = net,
            AbsoluteNetPaidDelta = Math.Abs(net),
            MemberLiabilityDelta = memberDelta,
            Direction = decided
                ? RetroRateMatcher.Direction(true, net, tolerance)
                : RetroRateDirection.Undecided,
            PendCode = decided ? null : comparison.ChoPendCode ?? comparison.ChoOutcome,
            PendReason = decided ? null : comparison.ChoPendReason,
            Lines = comparison.Lines,
        };
    }

    internal static void Summarize(RetroRateImpactRun run, IReadOnlyList<RetroRateImpactResult> results)
    {
        run.ClaimsAffected = results.Count;
        run.CountsByDirection = RetroRateDirection.All
            .ToDictionary(d => d, d => results.Count(r => r.Direction == d), StringComparer.Ordinal);
        run.CountsByMatch = results
            .GroupBy(r => r.Match, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        run.TotalUnderpaid = results.Where(r => r.Direction == RetroRateDirection.Underpaid).Sum(r => r.NetPaidDelta);
        run.TotalOverpaid = -results.Where(r => r.Direction == RetroRateDirection.Overpaid).Sum(r => r.NetPaidDelta);
        run.NetPaidDelta = run.TotalUnderpaid - run.TotalOverpaid;
        run.NetMemberLiabilityDelta = results
            .Where(r => r.Direction is RetroRateDirection.Underpaid or RetroRateDirection.Overpaid)
            .Sum(r => r.MemberLiabilityDelta);
        run.TopProviders = results
            .Where(r => r.Direction is RetroRateDirection.Underpaid or RetroRateDirection.Overpaid)
            .GroupBy(r => r.BillingProviderNpi ?? string.Empty, StringComparer.Ordinal)
            .Select(g => new RetroRateProviderTotal
            {
                BillingProviderNpi = g.Key,
                Claims = g.Count(),
                Underpaid = g.Where(r => r.NetPaidDelta > 0).Sum(r => r.NetPaidDelta),
                Overpaid = -g.Where(r => r.NetPaidDelta < 0).Sum(r => r.NetPaidDelta),
                Net = g.Sum(r => r.NetPaidDelta),
            })
            .OrderByDescending(p => Math.Abs(p.Net))
            .ThenBy(p => p.BillingProviderNpi, StringComparer.Ordinal)
            .Take(TopProviderCount)
            .ToList();
    }

    private static void AddNotes(RetroRateImpactRun run, FeeScheduleChange change)
    {
        if (run.Truncated)
            run.Notes.Add($"More paid claims are in scope than this run read ({run.CandidatesRead}); run again with a higher limit or raise {RetroRateOptions.SectionName}:MaxClaimsPerRun.");
        if (run.CountsByMatch.TryGetValue(RetroRateMatch.PlanFallback, out var fallback) && fallback > 0)
            run.Notes.Add($"{fallback} claim(s) were priced before CHO recorded which schedule priced each line, so they were matched by plan, date and code. Some may have been priced on a provider contract rather than this schedule; their re-price shows the difference either way.");
        if (change.Kind == FeeScheduleChangeKind.Created)
            run.Notes.Add("This is a new schedule. Claims on its plan with dates of service it now covers were re-priced; where it does not apply to them (a provider contract wins), they come back Unchanged.");
        if (run.CountsByDirection.GetValueOrDefault(RetroRateDirection.Undecided) > 0)
            run.Notes.Add("Undecided claims pended, denied or rejected when re-priced (see each claim's pend code); they need a person before any adjustment.");
        run.Notes.Add("Member cost share is recalculated against the member's accumulators as they stand today, which already include the paid claim; treat member liability differences on deductible or out-of-pocket claims as estimates.");
        run.Notes.Add("Nothing was changed. Adjustments are made by a reprocessing run (next step); underpayments are paid by default and recoupments only where the plan or contract opts in.");
    }
}
