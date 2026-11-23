using EncounterSubmissionService.Models;
using MongoDB.Driver;

namespace EncounterSubmissionService.StateRoutes;

/// <summary>
/// How one state route's encounters did for one tenant, for claims adjudicated in a
/// period: were they sent by the deadline, did the state take them, and how long
/// are rejections sitting. The measures a state scores a plan's encounter data on
/// (timeliness, acceptance, correction turnaround), computed from CHO's own records.
///
/// <para>Targets are only shown when the route configures them
/// (<see cref="StateEncounterRouteOptions.TimelinessTarget"/> and friends). No state
/// threshold is defaulted in code: the HHSC and DHCS values come from the plan's
/// contract and the state's encounter manual.</para>
/// </summary>
public sealed class EncounterScorecard
{
    public string StateCode { get; init; } = string.Empty;
    public string Receiver { get; init; } = string.Empty;

    /// <summary>Claims adjudicated from this date (inclusive), UTC.</summary>
    public DateTime From { get; init; }

    /// <summary>Claims adjudicated before this date (exclusive), UTC.</summary>
    public DateTime To { get; init; }
    public DateTime GeneratedAt { get; init; }

    /// <summary>Encounter records for claims adjudicated in the period, closed ones included.</summary>
    public int Total { get; set; }

    /// <summary>Closed without reporting (duplicates, non-reportable services, voids of never-accepted originals).</summary>
    public int ClosedNotReportable { get; set; }

    /// <summary>Total less <see cref="ClosedNotReportable"/>.</summary>
    public int Reportable { get; set; }

    public EncounterTimeliness Timeliness { get; init; } = new();
    public EncounterAccuracy Accuracy { get; init; } = new();
    public EncounterOpenRework OpenRework { get; init; } = new();
    public EncounterScorecardTargets Targets { get; init; } = new();
}

public sealed class EncounterTimeliness
{
    /// <summary>Sent to the state (file marked transmitted) on or before the deadline.</summary>
    public int OnTime { get; set; }

    /// <summary>Sent after the deadline.</summary>
    public int Late { get; set; }

    /// <summary>Not sent, and the deadline has passed.</summary>
    public int Overdue { get; set; }

    /// <summary>Not sent, deadline still ahead.</summary>
    public int NotYetDue { get; set; }

    /// <summary>OnTime / (OnTime + Late + Overdue), as a percentage. Null when nothing is due yet.</summary>
    public decimal? Rate { get; set; }
}

public sealed class EncounterAccuracy
{
    /// <summary>Taken by the state's front end (277CA / 277 accepted).</summary>
    public int Accepted { get; set; }

    /// <summary>Currently rejected by the state (999 or 277).</summary>
    public int Rejected { get; set; }

    /// <summary>Sent, no claim-level answer yet.</summary>
    public int AwaitingResponse { get; set; }

    /// <summary>Held back by CHO's own validation; never sent.</summary>
    public int HeldByValidation { get; set; }

    /// <summary>Accepted / (Accepted + Rejected), as a percentage.</summary>
    public decimal? AcceptanceRate { get; set; }

    /// <summary>
    /// Accepted with no earlier rejection or requeue, as a percentage of encounters the
    /// state has answered (accepted or rejected). The measure of clean data on the first send.
    /// </summary>
    public decimal? FirstPassAcceptanceRate { get; set; }

    /// <summary>Encounters whose latest rejection came from a 999 (syntax).</summary>
    public int SyntaxRejected { get; set; }

    /// <summary>Encounters whose latest rejection came from a 277CA / 277 (business edits).</summary>
    public int StateRejected { get; set; }

    /// <summary>The most frequent state rejection codes in the period (STC composites, IK5 codes), with counts.</summary>
    public List<EncounterCodeCount> TopRejectionCodes { get; set; } = new();

    /// <summary>Warnings the state sent on accepted encounters (TMHP R3), with counts.</summary>
    public List<EncounterCodeCount> TopWarningCodes { get; set; } = new();
}

public sealed record EncounterCodeCount(string Code, int Count);

public sealed class EncounterOpenRework
{
    /// <summary>Rejected or held back, by days since they last changed.</summary>
    public int Days0To15 { get; set; }
    public int Days16To30 { get; set; }
    public int Over30Days { get; set; }

    /// <summary>Open rework older than the route's correction window, when one is configured.</summary>
    public int? PastCorrectionWindow { get; set; }
}

public sealed class EncounterScorecardTargets
{
    public decimal? Timeliness { get; init; }
    public decimal? Acceptance { get; init; }
    public int? CorrectionDays { get; init; }

    /// <summary>Null when no target is configured or nothing is measurable yet.</summary>
    public bool? MeetsTimeliness { get; set; }
    public bool? MeetsAcceptance { get; set; }

    /// <summary>Where the targets come from, so nobody mistakes a placeholder for the contract.</summary>
    public string? Source { get; init; }
}

public interface IEncounterScorecardService
{
    Task<EncounterScorecard> GetAsync(StateEncounterRoute route, string tenantId, DateTime fromUtc, DateTime toUtc,
        CancellationToken ct = default);
}

public sealed class EncounterScorecardService : IEncounterScorecardService
{
    private static readonly EncounterSubmissionStatus[] Answered =
    {
        EncounterSubmissionStatus.StateFrontEndAccepted,
        EncounterSubmissionStatus.Rejected
    };

    private readonly IMongoCollection<EncounterSubmission> _submissions;
    private readonly IStateEncounterRouteRegistry _registry;
    private readonly TimeProvider _clock;

    public EncounterScorecardService(IMongoDatabase database, IStateEncounterRouteRegistry registry,
        TimeProvider? clock = null)
    {
        _submissions = database.GetCollection<EncounterSubmission>("encounter_submissions");
        _registry = registry;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<EncounterScorecard> GetAsync(StateEncounterRoute route, string tenantId,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        if (route.Format != EncounterRouteFormat.X12_837)
            throw new ArgumentException($"{route.StateCode} is not an X12 route.", nameof(route));
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("A tenant is required.", nameof(tenantId));
        if (toUtc <= fromUtc)
            throw new ArgumentException("The period must end after it starts.");
        if (toUtc - fromUtc > TimeSpan.FromDays(366))
            throw new ArgumentException("The period can be at most one year.");

        var now = _clock.GetUtcNow().UtcDateTime;
        var options = _registry.GetOptions(route.StateCode);
        var card = new EncounterScorecard
        {
            StateCode = route.StateCode,
            Receiver = route.Receiver,
            From = fromUtc,
            To = toUtc,
            GeneratedAt = now,
            Targets = new EncounterScorecardTargets
            {
                Timeliness = options?.TimelinessTarget,
                Acceptance = options?.AcceptanceTarget,
                CorrectionDays = options?.CorrectionDays,
                Source = options?.ScorecardTargetSource
            }
        };
        if (card.Targets.CorrectionDays is > 0) card.OpenRework.PastCorrectionWindow = 0;

        var filter = Builders<EncounterSubmission>.Filter.And(
            Builders<EncounterSubmission>.Filter.Eq(s => s.TenantId, tenantId),
            Builders<EncounterSubmission>.Filter.Eq(s => s.StateCode, route.StateCode),
            Builders<EncounterSubmission>.Filter.Gte(s => s.ClaimAdjudicatedAt, fromUtc),
            Builders<EncounterSubmission>.Filter.Lt(s => s.ClaimAdjudicatedAt, toUtc));
        var projection = Builders<EncounterSubmission>.Projection
            .Include(s => s.Status)
            .Include(s => s.SubmissionDeadline)
            .Include(s => s.SubmittedAt)
            .Include(s => s.UpdatedAt)
            .Include(s => s.ResponseHistory)
            .Include(s => s.ReworkHistory);

        var firstPass = 0;
        var rejectionCodes = new Dictionary<string, int>(StringComparer.Ordinal);
        var warningCodes = new Dictionary<string, int>(StringComparer.Ordinal);
        var warningCategories = route.WarningStatusCategories.ToHashSet(StringComparer.Ordinal);

        using var cursor = await _submissions.Find(filter)
            .Project<EncounterSubmission>(projection)
            .ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var s in cursor.Current)
            {
                card.Total++;
                if (s.Status == EncounterSubmissionStatus.ClosedNotReportable)
                {
                    card.ClosedNotReportable++;
                    continue;
                }

                // ── Timeliness: latest send against the deadline ─────
                if (s.SubmittedAt is { } sent)
                {
                    if (sent <= s.SubmissionDeadline) card.Timeliness.OnTime++;
                    else card.Timeliness.Late++;
                }
                else if (s.SubmissionDeadline < now) card.Timeliness.Overdue++;
                else card.Timeliness.NotYetDue++;

                // ── Accuracy ─────────────────────────────────────────
                var everRejected = s.ResponseHistory.Any(r => r.ResultingStatus == EncounterSubmissionStatus.Rejected)
                                   || s.ReworkHistory.Any(r => r.Action == "Requeued");
                switch (s.Status)
                {
                    case EncounterSubmissionStatus.StateFrontEndAccepted:
                        card.Accuracy.Accepted++;
                        if (!everRejected) firstPass++;
                        break;
                    case EncounterSubmissionStatus.Rejected:
                        card.Accuracy.Rejected++;
                        var last = s.ResponseHistory.LastOrDefault(r => r.ResultingStatus == EncounterSubmissionStatus.Rejected);
                        if (last?.Kind == "999") card.Accuracy.SyntaxRejected++;
                        else card.Accuracy.StateRejected++;
                        break;
                    case EncounterSubmissionStatus.ValidationFailed:
                        card.Accuracy.HeldByValidation++;
                        break;
                    case EncounterSubmissionStatus.Submitted:
                    case EncounterSubmissionStatus.SyntaxAccepted:
                    case EncounterSubmissionStatus.SyntaxAcceptedWithErrors:
                        card.Accuracy.AwaitingResponse++;
                        break;
                }

                foreach (var response in s.ResponseHistory)
                {
                    if (response.ResultingStatus == EncounterSubmissionStatus.Rejected)
                        foreach (var code in response.StatusCodes.Distinct(StringComparer.Ordinal))
                            rejectionCodes[code] = rejectionCodes.GetValueOrDefault(code) + 1;
                    foreach (var code in response.StatusCodes.Where(c => warningCategories.Contains(c.Split(':')[0])))
                        warningCodes[code] = warningCodes.GetValueOrDefault(code) + 1;
                }

                // ── Open rework aging ────────────────────────────────
                if (s.Status is EncounterSubmissionStatus.Rejected or EncounterSubmissionStatus.ValidationFailed)
                {
                    var age = (now - s.UpdatedAt).TotalDays;
                    if (age <= 15) card.OpenRework.Days0To15++;
                    else if (age <= 30) card.OpenRework.Days16To30++;
                    else card.OpenRework.Over30Days++;
                    if (card.Targets.CorrectionDays is > 0 and var days && age > days)
                        card.OpenRework.PastCorrectionWindow++;
                }
            }
        }

        card.Reportable = card.Total - card.ClosedNotReportable;

        var due = card.Timeliness.OnTime + card.Timeliness.Late + card.Timeliness.Overdue;
        card.Timeliness.Rate = Percent(card.Timeliness.OnTime, due);
        var answered = card.Accuracy.Accepted + card.Accuracy.Rejected;
        card.Accuracy.AcceptanceRate = Percent(card.Accuracy.Accepted, answered);
        card.Accuracy.FirstPassAcceptanceRate = Percent(firstPass, answered);
        card.Accuracy.TopRejectionCodes = Top(rejectionCodes);
        card.Accuracy.TopWarningCodes = Top(warningCodes);

        if (card.Targets.Timeliness is { } tt && card.Timeliness.Rate is { } tr)
            card.Targets.MeetsTimeliness = tr >= tt;
        if (card.Targets.Acceptance is { } at && card.Accuracy.AcceptanceRate is { } ar)
            card.Targets.MeetsAcceptance = ar >= at;

        return card;
    }

    private static decimal? Percent(int part, int whole) =>
        whole == 0 ? null : decimal.Round(100m * part / whole, 2, MidpointRounding.AwayFromZero);

    private static List<EncounterCodeCount> Top(Dictionary<string, int> counts) =>
        counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(10).Select(kv => new EncounterCodeCount(kv.Key, kv.Value)).ToList();
}
