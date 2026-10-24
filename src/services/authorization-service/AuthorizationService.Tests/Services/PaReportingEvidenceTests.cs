using System.Text.Json;
using AuthorizationService.Models;
using AuthorizationService.Services.Cms0057;
using CloudHealthOffice.Infrastructure.Data;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AuthorizationService.Tests.Services;

public class PaReportingEvidenceTests
{
    private static readonly DateTime Receipt = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = Receipt.AddDays(20);
    internal static Authorization NewAuthorization(AuthorizationStatus status = AuthorizationStatus.Submitted)
    {
        var auth = new Authorization
        {
            Id = "synthetic-pa", SubmittedDate = Receipt, LineOfBusiness = LineOfBusiness.Medicaid,
            Status = status, ReviewedDate = status == AuthorizationStatus.Submitted ? null : Receipt.AddHours(24),
            RequestedServices = new() { new() { ProcedureCode = "99213" } }, CreatedBy = "intake",
        };
        PaReportingEvidence.Initialize(auth, Receipt.AddDays(2));
        return auth;
    }

    internal static RecordPaReportingFact Appeal(string id = "appeal-1") => new()
    {
        EventId = id, Kind = PaReportingFactKind.AppealDecision, EffectiveAt = Receipt.AddDays(5),
        Outcome = AuthorizationStatus.Approved, SourceSystem = "pa-review", EvidenceReference = "case:123/decision:1",
    };

    [Fact]
    public void FullModelJson_CannotInjectOrReadLedger()
    {
        var auth = JsonSerializer.Deserialize<Authorization>("{\"ReportingLedger\":{\"Version\":999}}")!;
        auth.ReportingLedger.Should().BeNull();
        JsonSerializer.Serialize(NewAuthorization()).Should().NotContain("ReportingLedger");
    }

    [Fact]
    public void Create_ReplacesSuppliedEvidence_AndCapturesReceiptScope()
    {
        var auth = NewAuthorization();
        auth.ReportingLedger!.Version = 999;
        PaReportingEvidence.Initialize(auth, Receipt);
        auth.ReportingLedger!.Version.Should().Be(1);
        auth.ReportingLedger.ReceivedAt.Should().Be(Receipt);
        auth.ReportingLedger.LineOfBusiness.Should().Be(LineOfBusiness.Medicaid);
        auth.ReportingLedger.Facts.Should().BeEmpty();
    }

    [Fact]
    public void OrdinaryWrite_PreservesReceiptAndFacts_AndCapturesDecisionOnce()
    {
        var previous = NewAuthorization();
        var next = NewAuthorization(AuthorizationStatus.Denied);
        next.SubmittedDate = Receipt.AddYears(1); next.LineOfBusiness = LineOfBusiness.Commercial;
        next.Revision = 1;
        PaReportingEvidence.PreserveAndCapture(next, previous, Now);
        next.ReportingLedger!.ReceivedAt.Should().Be(Receipt);
        next.ReportingLedger.LineOfBusiness.Should().Be(LineOfBusiness.Medicaid);
        next.ReportingLedger.Facts.Should().ContainSingle().Which.Outcome.Should().Be(AuthorizationStatus.Denied);
        var same = NewAuthorization(AuthorizationStatus.Denied);
        PaReportingEvidence.PreserveAndCapture(same, next, Now);
        same.ReportingLedger!.Facts.Should().ContainSingle();
    }

    [Fact]
    public void LegacyWrite_DoesNotInventReportingHistory()
    {
        var legacy = new Authorization { ReportingLedger = null };
        var next = NewAuthorization(AuthorizationStatus.Approved);
        PaReportingEvidence.PreserveAndCapture(next, legacy, Now);
        next.ReportingLedger.Should().BeNull();
        var record = () => PaReportingEvidence.Prepare(next, Appeal(), "reviewer", Now);
        record.Should().Throw<ConcurrencyConflictException>();
    }

    [Fact]
    public void InvalidNativeTiming_IsMissing_NotZero()
    {
        var auth = NewAuthorization(AuthorizationStatus.Denied);
        auth.ReviewedDate = Receipt.AddHours(-1);
        PaReportingEvidence.Initialize(auth, Receipt.AddDays(1));
        auth.ReportingLedger!.Facts.Single().DecisionAt.Should().BeNull();
    }

    [Fact]
    public void BsonRoundTrip_PreservesPrivateEvidence_AndSubMillisecondRetryIsExact()
    {
        var auth = NewAuthorization(AuthorizationStatus.Denied);
        var command = Appeal(); command.EffectiveAt = command.EffectiveAt.AddTicks(4567);
        var original = PaReportingEvidence.Prepare(auth, command, "reviewer", Now.AddTicks(7890));
        auth.ReportingLedger!.Facts.Add(original);
        var reloaded = BsonSerializer.Deserialize<Authorization>(auth.ToBson());
        reloaded.ReportingLedger!.Facts.Should().HaveCount(2);
        var retry = PaReportingEvidence.Prepare(reloaded, command, "another-reviewer", Now.AddDays(1));
        retry.RecordedAt.Should().Be(original.RecordedAt);
        retry.RecordedBy.Should().Be("reviewer");
    }

    [Fact]
    public void ExactRetry_PreservesRecordedTimeAndActor_ConflictingKeyIsRejected()
    {
        var auth = NewAuthorization(AuthorizationStatus.Denied);
        var command = Appeal();
        var original = PaReportingEvidence.Prepare(auth, command, "reviewer-1", Now);
        auth.ReportingLedger!.Facts.Add(original);
        PaReportingEvidence.Prepare(auth, command, "reviewer-2", Now.AddDays(1)).Should().BeSameAs(original);
        command.Outcome = AuthorizationStatus.Denied;
        var act = () => PaReportingEvidence.Prepare(auth, command, "reviewer-2", Now);
        act.Should().Throw<ConcurrencyConflictException>();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(999)]
    public void ExternalSource_CannotInjectNativeDeterminationsOrUnknownKinds(int kind)
    {
        var command = Appeal(); command.Kind = (PaReportingFactKind)kind;
        var act = () => PaReportingEvidence.Prepare(NewAuthorization(AuthorizationStatus.Denied), command, "reviewer", Now);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EvidenceReference_CannotContainASignedUrlQuery()
    {
        var command = Appeal(); command.EvidenceReference = "https://source/decision?secret=value";
        var act = () => PaReportingEvidence.Prepare(NewAuthorization(AuthorizationStatus.Denied), command, "reviewer", Now);
        act.Should().Throw<System.ComponentModel.DataAnnotations.ValidationException>();
    }

    [Theory]
    [InlineData(AuthorizationStatus.Submitted)]
    [InlineData(AuthorizationStatus.Approved)]
    public void AppealWithoutAdverseDetermination_IsRefused(AuthorizationStatus status)
    {
        var act = () => PaReportingEvidence.Prepare(NewAuthorization(status), Appeal(), "reviewer", Now);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(21.0)]
    public void FactBeforeReceiptOrInFuture_IsRefused(double days)
    {
        var command = Appeal(); command.EffectiveAt = Receipt.AddDays(days);
        var act = () => PaReportingEvidence.Prepare(NewAuthorization(AuthorizationStatus.Denied), command, "reviewer", Now);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ExtensionRecordsEvidence_WithoutGrantingANewClinicalDeadline()
    {
        var auth = NewAuthorization();
        var originalDeadline = auth.DecisionDueAt;
        var command = new RecordPaReportingFact
        {
            EventId = "ext-1", Kind = PaReportingFactKind.ReviewExtension, EffectiveAt = Receipt.AddHours(1),
            OriginalDueAt = Receipt.AddDays(7), ExtendedDueAt = Receipt.AddDays(14),
            SourceSystem = "pa-review", EvidenceReference = "extension:1",
        };
        var fact = PaReportingEvidence.Prepare(auth, command, "reviewer", Now);
        fact.ExtendedDueAt.Should().Be(Receipt.AddDays(14));
        auth.DecisionDueAt.Should().Be(originalDeadline);
        command.ExtendedDueAt = command.OriginalDueAt;
        var act = () => PaReportingEvidence.Prepare(auth, command, "reviewer", Now);
        act.Should().Throw<ArgumentException>();
    }
}
