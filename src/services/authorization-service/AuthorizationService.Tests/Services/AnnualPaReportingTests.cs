using AuthorizationService.Models;
using AuthorizationService.Services.Cms0057;

namespace AuthorizationService.Tests.Services;

public class AnnualPaReportingTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = Start.AddYears(1);
    private static AnnualPaDraft Report(IEnumerable<Authorization> auths, DateTime? outcome = null, DateTime? knowledge = null)
    {
        var report = new AnnualPaReporting(2026, "UTC", LineOfBusiness.Medicaid, outcome ?? End, knowledge ?? End);
        foreach (var auth in auths) report.Add(auth);
        return report.Finish();
    }

    [Fact]
    public void AppealOverturn_CountsRequestOnce_AndPreservesInitialClock()
    {
        var auth = PaReportingEvidenceTests.NewAuthorization(AuthorizationStatus.Denied);
        var command = PaReportingEvidenceTests.Appeal();
        auth.ReportingLedger!.Facts.Add(PaReportingEvidence.Prepare(auth, command, "reviewer", Start.AddDays(10)));
        auth.Status = AuthorizationStatus.Expired; // today's state does not rewrite the report
        var report = Report(new[] { auth });
        report.Standard.Requests.Should().Be(1);
        report.Standard.Approved.Should().Be(1);
        report.Standard.Denied.Should().Be(0);
        report.Standard.ApprovedAfterAppealPercent.Should().Be(100);
        report.Standard.AverageInitialDecisionHours.Should().Be(24);
        report.PublicationReady.Should().BeFalse();
    }

    [Fact]
    public void LateArrival_RespectsBothOutcomeAndKnowledgeCutoffs()
    {
        var auth = PaReportingEvidenceTests.NewAuthorization(AuthorizationStatus.Denied);
        var command = PaReportingEvidenceTests.Appeal();
        auth.ReportingLedger!.Facts.Add(PaReportingEvidence.Prepare(auth, command, "reviewer", Start.AddDays(20)));
        Report(new[] { auth }, Start.AddDays(10), Start.AddDays(10)).Standard.Denied.Should().Be(1);
        Report(new[] { auth }, Start.AddDays(3), Start.AddDays(30)).Standard.Denied.Should().Be(1);
        Report(new[] { auth }, Start.AddDays(10), Start.AddDays(30)).Standard.Approved.Should().Be(1);
    }

    [Fact]
    public void ReorderedAppealLevels_UseEffectiveTime_NotDeliveryOrder()
    {
        var auth = PaReportingEvidenceTests.NewAuthorization(AuthorizationStatus.Denied);
        var approved = PaReportingEvidenceTests.Appeal("level-2"); approved.EffectiveAt = Start.AddDays(10);
        var denied = PaReportingEvidenceTests.Appeal("level-1"); denied.Outcome = AuthorizationStatus.Denied;
        auth.ReportingLedger!.Facts.Add(PaReportingEvidence.Prepare(auth, approved, "reviewer", Start.AddDays(20)));
        auth.ReportingLedger.Facts.Add(PaReportingEvidence.Prepare(auth, denied, "reviewer", Start.AddDays(21)));
        Report(new[] { auth }).Standard.Approved.Should().Be(1);
    }

    [Fact]
    public void ScopeIsImmutable_AndOpenRequestsRemainInDenominator()
    {
        var approved = PaReportingEvidenceTests.NewAuthorization(AuthorizationStatus.Approved);
        approved.LineOfBusiness = LineOfBusiness.Commercial; approved.SubmittedDate = End;
        var open = PaReportingEvidenceTests.NewAuthorization();
        var drug = PaReportingEvidenceTests.NewAuthorization(); drug.ReportingLedger!.DrugExcluded = true;
        var otherLob = PaReportingEvidenceTests.NewAuthorization(); otherLob.ReportingLedger!.LineOfBusiness = LineOfBusiness.Medicare;
        var report = Report(new[] { approved, open, drug, otherLob });
        report.Standard.Requests.Should().Be(2);
        report.Standard.ApprovedPercent.Should().Be(50);
        report.Standard.Undecided.Should().Be(1);
    }

    [Fact]
    public void LegacyAndUnknownVersion_AreDiagnostics_NotFabricatedOutcomes()
    {
        var legacy = new Authorization { Status = AuthorizationStatus.Approved };
        var unknown = PaReportingEvidenceTests.NewAuthorization(); unknown.ReportingLedger!.Version = 2;
        var report = Report(new[] { legacy, unknown });
        report.LegacyRecordsWithoutEvidence.Should().Be(1);
        report.UnsupportedLedgerRecords.Should().Be(1);
        report.Standard.Requests.Should().Be(0);
        report.Standard.ApprovedPercent.Should().BeNull();
    }

    [Fact]
    public void CaliforniaCalendarYear_UsesLocalMidnightAndExclusiveEnd()
    {
        var report = new AnnualPaReporting(2026, "America/Los_Angeles", LineOfBusiness.Medicaid, End.AddDays(2), End.AddDays(2));
        report.FromUtc.Should().Be(Start.AddHours(8));
        report.ToExclusiveUtc.Should().Be(End.AddHours(8));
        var inside = PaReportingEvidenceTests.NewAuthorization();
        inside.ReportingLedger!.ReceivedAt = End.AddHours(7); inside.ReportingLedger.RecordedAt = End.AddHours(7);
        var outside = PaReportingEvidenceTests.NewAuthorization();
        outside.ReportingLedger!.ReceivedAt = End.AddHours(8); outside.ReportingLedger.RecordedAt = End.AddHours(8);
        report.Add(inside); report.Add(outside);
        report.Finish().Standard.Requests.Should().Be(1);
    }

    [Fact]
    public void ExtendedApproval_CountsOnceAcrossRequestTypes()
    {
        var auth = PaReportingEvidenceTests.NewAuthorization(AuthorizationStatus.Approved);
        auth.ReportingLedger!.Facts.Add(new PaReportingFact
        {
            EventId = "extension", Kind = PaReportingFactKind.ReviewExtension,
            EffectiveAt = Start.AddHours(20), RecordedAt = Start.AddHours(20), SourceSystem = "pa-review",
        });
        var report = Report(new[] { auth, PaReportingEvidenceTests.NewAuthorization() });
        report.Standard.ExtendedThenApproved.Should().Be(1);
        report.ExtendedThenApprovedPercent.Should().Be(50);
    }

    [Fact]
    public void EqualTimeConflictingAppeals_AreUnresolved_NotAnArbitraryFinalOutcome()
    {
        var auth = PaReportingEvidenceTests.NewAuthorization(AuthorizationStatus.Denied);
        var approved = PaReportingEvidenceTests.Appeal("a");
        var denied = PaReportingEvidenceTests.Appeal("b"); denied.Outcome = AuthorizationStatus.Denied;
        auth.ReportingLedger!.Facts.Add(PaReportingEvidence.Prepare(auth, approved, "reviewer", Start.AddDays(20)));
        auth.ReportingLedger.Facts.Add(PaReportingEvidence.Prepare(auth, denied, "reviewer", Start.AddDays(20)));
        var report = Report(new[] { auth });
        report.Standard.ConflictingFinalOutcomes.Should().Be(1);
        report.Standard.Approved.Should().Be(0);
        report.Standard.Denied.Should().Be(0);
        report.Standard.Undecided.Should().Be(1);
        report.Standard.AverageInitialDecisionHours.Should().Be(24);
    }

    [Fact]
    public void EvenSampleMedian_AndMissingTiming_AreExplicit()
    {
        var first = PaReportingEvidenceTests.NewAuthorization(AuthorizationStatus.Approved);
        var second = PaReportingEvidenceTests.NewAuthorization(AuthorizationStatus.Denied);
        second.ReportingLedger!.Facts.Single().DecisionAt = second.ReportingLedger.ReceivedAt.AddHours(48);
        var missing = PaReportingEvidenceTests.NewAuthorization(AuthorizationStatus.Denied);
        missing.ReportingLedger!.Facts.Single().DecisionAt = null;
        var report = Report(new[] { first, second, missing });
        report.Standard.MedianInitialDecisionHours.Should().Be(36);
        report.Standard.AverageInitialDecisionHours.Should().Be(36);
        report.Standard.InitialDecisionTimingUnavailable.Should().Be(1);
    }
}
