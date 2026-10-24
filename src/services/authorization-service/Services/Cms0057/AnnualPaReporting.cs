using AuthorizationService.Models;

namespace AuthorizationService.Services.Cms0057;

/// <summary>Versioned, bitemporal receipt-cohort draft. Never reads today's mutable clinical outcome.</summary>
public sealed class AnnualPaReporting
{
    public const string Version = "cho-pa-annual-draft-v1";
    private readonly AnnualPaDraft _result;
    private readonly List<double> _standardHours = new(), _expeditedHours = new();

    public AnnualPaReporting(int year, string timeZone, LineOfBusiness lineOfBusiness,
        DateTime outcomeCutoffUtc, DateTime knowledgeCutoffUtc)
    {
        if (year is < 2000 or > 9998 || !Enum.IsDefined(lineOfBusiness) || lineOfBusiness == LineOfBusiness.Unknown)
            throw new ArgumentException("A supported calendar year and an explicit line of business are required.");
        if (outcomeCutoffUtc.Kind != DateTimeKind.Utc || knowledgeCutoffUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Cutoffs must be UTC.");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        _result = new AnnualPaDraft
        {
            Year = year, TimeZone = zone.Id, LineOfBusiness = lineOfBusiness,
            FromUtc = TimeZoneInfo.ConvertTimeToUtc(new DateTime(year, 1, 1), zone),
            ToExclusiveUtc = TimeZoneInfo.ConvertTimeToUtc(new DateTime(year + 1, 1, 1), zone),
            OutcomeCutoffUtc = outcomeCutoffUtc, KnowledgeCutoffUtc = knowledgeCutoffUtc,
        };
    }

    public DateTime FromUtc => _result.FromUtc;
    public DateTime ToExclusiveUtc => _result.ToExclusiveUtc;

    public void Add(Authorization authorization)
    {
        var ledger = authorization.ReportingLedger;
        if (ledger is null) { _result.LegacyRecordsWithoutEvidence++; return; }
        if (ledger.Version != 1) { _result.UnsupportedLedgerRecords++; return; }
        if (ledger.LineOfBusiness != _result.LineOfBusiness || ledger.DrugExcluded
            || ledger.ReceivedAt < FromUtc || ledger.ReceivedAt >= ToExclusiveUtc
            || ledger.ReceivedAt > _result.OutcomeCutoffUtc || ledger.RecordedAt > _result.KnowledgeCutoffUtc) return;
        if (ledger.ReceivedAt == default || ledger.RecordedAt < ledger.ReceivedAt)
        { _result.InvalidReceiptRecords++; return; }
        var group = ledger.Expedited ? _result.Expedited : _result.Standard;
        group.Requests++;
        var facts = ledger.Facts.Where(f => f.RecordedAt <= _result.KnowledgeCutoffUtc
            && f.EffectiveAt <= _result.OutcomeCutoffUtc && f.EffectiveAt >= ledger.ReceivedAt).ToList();
        // Stable event keys break equal-time ties, independently of delivery order.
        var decisions = facts.Where(f => f.Kind is PaReportingFactKind.Determination or PaReportingFactKind.AppealDecision
            && f.Outcome is { } s && Cms0057Reporting.IsDecided(s))
            .OrderBy(f => f.EffectiveAt).ThenBy(f => f.Kind).ThenBy(f => f.SourceSystem, StringComparer.Ordinal)
            .ThenBy(f => f.EventId, StringComparer.Ordinal).ToList();
        var final = decisions.LastOrDefault();
        var first = decisions.FirstOrDefault(f => f.Kind == PaReportingFactKind.Determination);
        if (final is not null && decisions.Where(f => f.EffectiveAt == final.EffectiveAt && f.Kind == final.Kind)
            .Select(f => f.Outcome).Distinct().Count() > 1)
        {
            group.ConflictingFinalOutcomes++;
            final = null; // equal-time contradictory evidence needs reconciliation, not a guessed winner
        }
        if (final is null) group.Undecided++;
        else if (Cms0057Reporting.IsApproved(final.Outcome!.Value))
        {
            group.Approved++;
            if (final.Kind == PaReportingFactKind.AppealDecision) group.ApprovedAfterAppeal++;
            if (first is not null && facts.Any(f => f.Kind == PaReportingFactKind.ReviewExtension
                && f.EffectiveAt <= first.EffectiveAt)) group.ExtendedThenApproved++;
        }
        else group.Denied++;
        // Initial response time stays separate from the appeal's final disposition.
        // A corrected/overturned final outcome must not rewrite the initial clock.
        if (first?.DecisionAt is { } decisionAt && decisionAt >= ledger.ReceivedAt)
            (ledger.Expedited ? _expeditedHours : _standardHours).Add((decisionAt - ledger.ReceivedAt).TotalHours);
        else if (first is not null || final is not null) group.InitialDecisionTimingUnavailable++;
    }

    public AnnualPaDraft Finish()
    {
        Complete(_result.Standard, _standardHours);
        Complete(_result.Expedited, _expeditedHours);
        _result.ExtendedThenApprovedPercent = Percent(_result.Standard.ExtendedThenApproved + _result.Expedited.ExtendedThenApproved,
            _result.Standard.Requests + _result.Expedited.Requests);
        return _result;
    }

    private static void Complete(AnnualPaGroup group, List<double> hours)
    {
        group.ApprovedPercent = Percent(group.Approved, group.Requests);
        group.DeniedPercent = Percent(group.Denied, group.Requests);
        group.ApprovedAfterAppealPercent = Percent(group.ApprovedAfterAppeal, group.Requests);
        if (hours.Count == 0) return;
        hours.Sort();
        group.AverageInitialDecisionHours = Math.Round(hours.Average(), 4);
        group.MedianInitialDecisionHours = Math.Round(hours.Count % 2 == 1 ? hours[hours.Count / 2]
            : (hours[hours.Count / 2 - 1] + hours[hours.Count / 2]) / 2, 4);
    }

    private static double? Percent(int part, int whole) => whole == 0 ? null : Math.Round(100.0 * part / whole, 4);
}

public sealed class AnnualPaDraft
{
    public string CalculationVersion { get; set; } = AnnualPaReporting.Version;
    public bool PublicationReady => false;
    public string Scope => "tenant-and-line-of-business; plan mapping requires attestation";
    public int Year { get; set; }
    public string TimeZone { get; set; } = string.Empty;
    public LineOfBusiness LineOfBusiness { get; set; }
    public DateTime FromUtc { get; set; }
    public DateTime ToExclusiveUtc { get; set; }
    public DateTime OutcomeCutoffUtc { get; set; }
    public DateTime KnowledgeCutoffUtc { get; set; }
    public int LegacyRecordsWithoutEvidence { get; set; }
    public int UnsupportedLedgerRecords { get; set; }
    public int InvalidReceiptRecords { get; set; }
    public AnnualPaGroup Standard { get; set; } = new();
    public AnnualPaGroup Expedited { get; set; } = new();
    public double? ExtendedThenApprovedPercent { get; set; }
    public string TimingBasis => "receipt-to-initial-determination; final appeal outcomes counted separately";
    public string[] AcceptanceGaps => ["All-channel completeness and legacy reconciliation",
        "Authoritative PA appeal and lawful-extension source coverage at all levels",
        "Plan/LOB mapping and required-items/services list",
        "Compliance approval of cohort, timing and publication semantics; immutable approved artifact"];
}

public sealed class AnnualPaGroup
{
    public int Requests { get; set; }
    public int Approved { get; set; }
    public int Denied { get; set; }
    public int Undecided { get; set; }
    public int ApprovedAfterAppeal { get; set; }
    public int ExtendedThenApproved { get; set; }
    public int InitialDecisionTimingUnavailable { get; set; }
    public int ConflictingFinalOutcomes { get; set; }
    public double? ApprovedPercent { get; set; }
    public double? DeniedPercent { get; set; }
    public double? ApprovedAfterAppealPercent { get; set; }
    public double? AverageInitialDecisionHours { get; set; }
    public double? MedianInitialDecisionHours { get; set; }
}
