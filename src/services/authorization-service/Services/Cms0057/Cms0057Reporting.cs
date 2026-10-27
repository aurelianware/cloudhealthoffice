using AuthorizationService.Models;
using AuthorizationService.Services.Fhir;
using AuthorizationService.Services.Retention;

namespace AuthorizationService.Services.Cms0057;

/// <summary>
/// The CMS-0057-F prior-authorization metrics (45 CFR 156.223 and the parallel
/// MA/Medicaid/CHIP provisions) computed from the tenant's authorizations, plus
/// the recent-decision list the portal's CMS-0057-F console shows.
///
/// Request type follows the SLA watchdog: level of service <c>U</c> (urgent,
/// 278 UM04) is an expedited request with a 72-hour decision timeframe;
/// everything else is a standard request with 7 calendar days (168 hours).
/// Elapsed time is measured from submission to decision, as the rule's
/// public-reporting metric defines it (not the RFAI-resumed SLA clock).
/// "Within timeframe" is judged against the request's own deadline, so a
/// lawfully extended request decided inside its extension is not counted late.
/// </summary>
public static class Cms0057Reporting
{
    public const double ExpeditedTimeframeHours = 72;
    public const double StandardTimeframeHours = 168;

    /// <summary>The decided outcomes. Modified (approved with changes) counts as approved.</summary>
    public static bool IsDecided(AuthorizationStatus status) =>
        status is AuthorizationStatus.Approved or AuthorizationStatus.Modified or AuthorizationStatus.Denied;

    public static bool IsApproved(AuthorizationStatus status) =>
        status is AuthorizationStatus.Approved or AuthorizationStatus.Modified;

    /// <summary>Clinical outcome survives expiry/withdrawal; current lifecycle status is separate.</summary>
    public static AuthorizationStatus DecisionStatus(Authorization auth) =>
        Backends.AuthorizationLifecyclePolicy.ClinicalDecisionStatus(auth);

    public static bool IsDecided(Authorization auth) => IsDecided(DecisionStatus(auth));
    public static bool IsApproved(Authorization auth) => IsApproved(DecisionStatus(auth));

    public static bool IsExpedited(Authorization auth) =>
        string.Equals(auth.LevelOfService, "U", StringComparison.OrdinalIgnoreCase);

    public static double TimeframeHours(Authorization auth) =>
        IsExpedited(auth) ? ExpeditedTimeframeHours : StandardTimeframeHours;

    /// <summary>Hours from receipt to this request's deadline, including a lawful extension.</summary>
    public static double DeadlineHours(Authorization auth) =>
        (AuthorizationDeadlinePolicy.Deadline(auth) - AuthorizationDeadlinePolicy.Receipt(auth)).TotalHours;

    /// <summary>True when the decision timeframe was extended, natively or by sourced evidence.</summary>
    public static bool WasExtended(Authorization auth) =>
        auth.DecisionExtension is not null
        || auth.ReportingLedger?.Facts.Any(f => f.Kind == PaReportingFactKind.ReviewExtension) == true;

    /// <summary>
    /// True when the request's latest recorded decision is an approving PA appeal
    /// decision (reporting-ledger evidence; appeals not recorded there are not seen).
    /// </summary>
    public static bool ApprovedAfterAppeal(Authorization auth)
    {
        var latest = (auth.ReportingLedger?.Facts ?? new())
            .Where(f => f.Kind is PaReportingFactKind.Determination or PaReportingFactKind.AppealDecision)
            .OrderBy(f => f.EffectiveAt).ThenBy(f => f.Kind).LastOrDefault();
        return latest is { Kind: PaReportingFactKind.AppealDecision, Outcome: { } outcome } && IsApproved(outcome);
    }

    /// <summary>
    /// Every specific reason given for an adverse outcome: one per denied or
    /// reduced service line, or the request-level reason when the denial has no
    /// line detail. Empty for a request that was not denied or modified.
    /// </summary>
    public static IReadOnlyList<Cms0057AdverseReason> AdverseReasons(Authorization auth)
    {
        var decision = DecisionStatus(auth);
        if (decision is not (AuthorizationStatus.Denied or AuthorizationStatus.Modified)) return [];
        var services = auth.RequestedServices ?? new();
        var lines = services
            .Select((s, i) => (Service: s, Number: s.LineNumber ?? i + 1))
            .Where(l => l.Service.ServiceStatus is "A2" or "A3")
            .Select(l => new Cms0057AdverseReason(l.Number, l.Service.ServiceStatus!,
                Blank(l.Service.DenialReasonCode), Blank(l.Service.DenialReason)))
            .ToList();
        if (lines.Count > 0) return lines;
        return [new Cms0057AdverseReason(null, decision == AuthorizationStatus.Denied ? "A3" : "A2",
            Blank(auth.DenialReasonCode), Blank(auth.DenialReason))];
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static double? DecisionHours(Authorization auth)
    {
        var receipt = AuthorizationDeadlinePolicy.Receipt(auth);
        // Missing/corrupt legacy timestamps are not instantaneous decisions.
        // Keep their outcomes in the counts, but expose unavailable timing.
        return IsDecided(auth) && receipt != default && auth.ReviewedDate is { } reviewed
            && reviewed >= receipt
                ? (reviewed - receipt).TotalHours
                : null;
    }

    /// <summary>
    /// The history entry that recorded the decision (the latest change to the
    /// current decided status), or null when the record carries no history.
    /// </summary>
    public static AuthorizationStatusChange? DecisionEntry(Authorization auth)
    {
        var decision = DecisionStatus(auth);
        return (auth.StatusHistory ?? new()).Where(h => h.Status == decision).MaxBy(h => h.ChangedAt);
    }

    /// <summary>True when an automated rule, not a reviewer, made the decision.</summary>
    public static bool IsAutomated(Authorization auth) =>
        DecisionEntry(auth)?.Source?.StartsWith("automated", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Non-drug requests received in [from, to], in the line of business.</summary>
    public static IEnumerable<Authorization> InPeriod(
        IEnumerable<Authorization> authorizations, DateTime from, DateTime to, LineOfBusiness? lineOfBusiness) =>
        authorizations
            .Where(PriorAuthorizationApiScope.IsInScope)
            .Where(a => AuthorizationDeadlinePolicy.Receipt(a) >= from
                && AuthorizationDeadlinePolicy.Receipt(a) <= to)
            .Where(a => lineOfBusiness is null || a.LineOfBusiness == lineOfBusiness);

    /// <summary>Decided by its deadline (72h expedited, 168h standard, or the lawful extension).</summary>
    public static bool DecidedWithinTimeframe(Authorization auth) =>
        DecisionHours(auth) is { } hours && hours <= DeadlineHours(auth);

    public static Cms0057Metrics ComputeMetrics(
        IEnumerable<Authorization> authorizations, DateTime from, DateTime to, LineOfBusiness? lineOfBusiness)
    {
        var inPeriod = InPeriod(authorizations, from, to, lineOfBusiness).ToList();

        var decided = inPeriod.Where(a => IsDecided(a)).ToList();
        return new Cms0057Metrics
        {
            From = from,
            To = to,
            LineOfBusiness = lineOfBusiness,
            TotalRequests = inPeriod.Count,
            OpenRequests = inPeriod.Count(a => a.Status.IsOpen()),
            Standard = Group(inPeriod.Where(a => !IsExpedited(a)).ToList(), StandardTimeframeHours),
            Expedited = Group(inPeriod.Where(IsExpedited).ToList(), ExpeditedTimeframeHours),
            AutomatedDecisions = decided.Count(IsAutomated),
            DecidedRequests = decided.Count,
            ExtendedThenApprovedPercent = Percent(inPeriod.Count(a => WasExtended(a) && IsApproved(a)), inPeriod.Count),
            NotYetTracked =
            [
                new("Approved after appeal",
                    "Counts only PA appeal decisions recorded as reporting evidence; appeals-service outcomes are not linked automatically yet."),
            ],
        };
    }

    private static Cms0057RequestTypeMetrics Group(IReadOnlyList<Authorization> requests, double timeframeHours)
    {
        var decided = requests.Where(a => IsDecided(a)).ToList();
        var hours = decided.Select(DecisionHours).OfType<double>().OrderBy(h => h).ToList();
        var withinDeadline = decided.Count(DecidedWithinTimeframe);
        return new Cms0057RequestTypeMetrics
        {
            TimeframeHours = timeframeHours,
            Requests = requests.Count,
            Decided = decided.Count,
            DecisionTimingUnavailable = decided.Count - hours.Count,
            Approved = decided.Count(a => IsApproved(a)),
            Denied = decided.Count(a => DecisionStatus(a) == AuthorizationStatus.Denied),
            // CMS's reporting template uses total requests, including requests
            // not yet decided. Do not silently inflate rates by dropping pends.
            ApprovedPercent = Percent(decided.Count(a => IsApproved(a)), requests.Count),
            DeniedPercent = Percent(decided.Count(a => DecisionStatus(a) == AuthorizationStatus.Denied), requests.Count),
            AverageDecisionHours = hours.Count == 0 ? null : Math.Round(hours.Average(), 1),
            MedianDecisionHours = hours.Count == 0 ? null : Math.Round(Median(hours), 1),
            DecidedWithinTimeframe = withinDeadline,
            DecidedWithinTimeframePercent = Percent(withinDeadline, hours.Count),
            Extended = requests.Count(WasExtended),
            ExtendedThenApproved = requests.Count(a => WasExtended(a) && IsApproved(a)),
            ApprovedAfterAppeal = requests.Count(ApprovedAfterAppeal),
            ApprovedAfterAppealPercent = Percent(requests.Count(ApprovedAfterAppeal), requests.Count),
        };
    }

    public static IReadOnlyList<Cms0057Decision> RecentDecisions(
        IEnumerable<Authorization> authorizations, DateTime since, int limit)
    {
        return authorizations
            .Where(PriorAuthorizationApiScope.IsInScope)
            .Where(a => IsDecided(a) && a.ReviewedDate >= since)
            .OrderByDescending(a => a.ReviewedDate)
            .Take(limit)
            .Select(a =>
            {
                var entry = DecisionEntry(a);
                var hours = DecisionHours(a);
                var timeframe = DeadlineHours(a);
                return new Cms0057Decision
                {
                    Id = a.Id,
                    AuthorizationNumber = a.AuthorizationNumber,
                    MemberId = a.MemberId,
                    RequestingProviderName = a.RequestingProviderName,
                    RequestType = IsExpedited(a) ? "Expedited" : "Standard",
                    Status = DecisionStatus(a),
                    LifecycleStatus = a.Status,
                    SubmittedDate = AuthorizationDeadlinePolicy.Receipt(a),
                    DecidedDate = a.ReviewedDate!.Value,
                    DecisionHours = hours is null ? null : Math.Round(hours.Value, 1),
                    TimeframeHours = timeframe,
                    WithinTimeframe = hours is not null && hours.Value <= timeframe,
                    Extended = WasExtended(a),
                    Automated = IsAutomated(a),
                    DecidedBy = IsAutomated(a)
                        ? entry?.Source
                        : a.ReviewerName ?? entry?.ChangedBy,
                    Reason = DecisionStatus(a) == AuthorizationStatus.Denied
                        ? a.DenialReason ?? entry?.Reason
                        : entry?.Reason ?? a.ReviewDecision,
                    DenialReasonCode = a.DenialReasonCode,
                    AdverseReasons = AdverseReasons(a).ToList(),
                };
            })
            .ToList();
    }

    private static double? Percent(int part, int whole) =>
        whole == 0 ? null : Math.Round(100.0 * part / whole, 1);

    private static double Median(IReadOnlyList<double> sorted) =>
        sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
}

public sealed class Cms0057Metrics
{
    /// <summary>This is a receipt-cohort operational report, not a complete annual public report.</summary>
    public string ReportingBasis { get; set; } = "non-drug-requests-received-in-period";
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public LineOfBusiness? LineOfBusiness { get; set; }
    public int TotalRequests { get; set; }
    public int OpenRequests { get; set; }
    public int DecidedRequests { get; set; }
    public int AutomatedDecisions { get; set; }
    public Cms0057RequestTypeMetrics Standard { get; set; } = new();
    public Cms0057RequestTypeMetrics Expedited { get; set; } = new();
    /// <summary>Of all requests: timeframe extended, then approved (CMS-0057-F's combined metric).</summary>
    public double? ExtendedThenApprovedPercent { get; set; }
    /// <summary>Required metrics the data can't support yet, and why.</summary>
    public List<Cms0057MetricGap> NotYetTracked { get; set; } = new();
}

public sealed class Cms0057RequestTypeMetrics
{
    public double TimeframeHours { get; set; }
    public int Requests { get; set; }
    public int Decided { get; set; }
    public int Approved { get; set; }
    public int Denied { get; set; }
    /// <summary>Decided requests with missing or inconsistent receipt/decision timestamps.</summary>
    public int DecisionTimingUnavailable { get; set; }
    /// <summary>Of all requests of this type; null when there were no requests.</summary>
    public double? ApprovedPercent { get; set; }
    public double? DeniedPercent { get; set; }
    public double? AverageDecisionHours { get; set; }
    public double? MedianDecisionHours { get; set; }
    public int DecidedWithinTimeframe { get; set; }
    public double? DecidedWithinTimeframePercent { get; set; }
    /// <summary>Requests whose decision timeframe was extended.</summary>
    public int Extended { get; set; }
    public int ExtendedThenApproved { get; set; }
    public int ApprovedAfterAppeal { get; set; }
    public double? ApprovedAfterAppealPercent { get; set; }
}

public sealed record Cms0057MetricGap(string Metric, string Reason);

/// <summary>One specific reason for a denied or reduced request or service line. LineNumber is null for a request-level reason.</summary>
public sealed record Cms0057AdverseReason(int? LineNumber, string ReviewDecision, string? Code, string? Reason);

public sealed class Cms0057Decision
{
    public AuthorizationStatus LifecycleStatus { get; set; }
    public string Id { get; set; } = string.Empty;
    public string AuthorizationNumber { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string? RequestingProviderName { get; set; }
    public string RequestType { get; set; } = "Standard";
    public AuthorizationStatus Status { get; set; }
    public DateTime SubmittedDate { get; set; }
    public DateTime DecidedDate { get; set; }
    public double? DecisionHours { get; set; }
    public double TimeframeHours { get; set; }
    public bool WithinTimeframe { get; set; }
    public bool Automated { get; set; }
    public string? DecidedBy { get; set; }
    public string? Reason { get; set; }
    public string? DenialReasonCode { get; set; }
    /// <summary>The deadline was lawfully extended; <see cref="TimeframeHours"/> is the extended window.</summary>
    public bool Extended { get; set; }
    /// <summary>Every denial or reduction reason, per service line, not only the first.</summary>
    public List<Cms0057AdverseReason> AdverseReasons { get; set; } = new();
}
