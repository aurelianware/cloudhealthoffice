using AuthorizationService.Models;
using AuthorizationService.Services;
using AuthorizationService.Services.Cms0057;

namespace AuthorizationService.Tests.Services;

/// <summary>The one lawful extension of a decision timeframe (CMS-0057-F).</summary>
public class DecisionExtensionTests
{
    private static readonly DateTime Receipt = new(2026, 3, 2, 8, 0, 0, DateTimeKind.Utc);

    private static Authorization Open(LineOfBusiness lob = LineOfBusiness.Medicaid, string levelOfService = "E",
        AuthorizationStatus status = AuthorizationStatus.InReview)
    {
        var auth = new Authorization
        {
            Id = "ext", AuthorizationNumber = "EXT-1", TenantId = "tenant-1", MemberId = "M-1",
            SubmittedDate = Receipt, LevelOfService = levelOfService, LineOfBusiness = lob, Status = status,
            RequestedServices = new() { new() { ProcedureCode = "97110" } },
        };
        AuthorizationDeadlinePolicy.Initialize(auth);
        PaReportingEvidence.Initialize(auth, Receipt);
        return auth;
    }

    private static ExtendDecisionDeadline Command(DateTimeOffset? to = null) => new()
    {
        ExtendedDueAt = to, Basis = DecisionExtensionBasis.PlanNeedsAdditionalInformation,
        Reason = "Therapy notes from the last 30 days are needed to assess progress.",
    };

    [Theory]
    [InlineData(LineOfBusiness.Medicaid, "E", 168)]
    [InlineData(LineOfBusiness.Medicare, "E", 168)]
    [InlineData(LineOfBusiness.Medicare, "U", 72)]
    public void Extension_DefaultsToFourteenDaysAfterTheCurrentDeadline(LineOfBusiness lob, string los, int hours)
    {
        var auth = Open(lob, los);
        var extension = AuthorizationDeadlinePolicy.Extend(auth, Command(), "reviewer-1", Receipt.AddHours(20));

        extension.OriginalDueAt.Should().Be(Receipt.AddHours(hours));
        extension.ExtendedDueAt.Should().Be(Receipt.AddHours(hours).AddDays(14));
        extension.GrantedAt.Should().Be(Receipt.AddHours(20));
        extension.GrantedBy.Should().Be("reviewer-1");
        auth.DecisionDueAt.Should().Be(Receipt.AddHours(hours), "the policy validates; only the store writes");
    }

    [Fact]
    public void Extension_CanBeShorterThanTheMaximum()
    {
        var auth = Open();
        var to = new DateTimeOffset(Receipt.AddDays(10));
        AuthorizationDeadlinePolicy.Extend(auth, Command(to), "reviewer-1", Receipt.AddDays(1))
            .ExtendedDueAt.Should().Be(Receipt.AddDays(10));
    }

    [Theory]
    [InlineData(7)]       // not later than the current deadline
    [InlineData(21.001)]  // more than 14 days after it
    public void Extension_OutsideTheLawfulWindow_IsRefused(double daysAfterReceipt)
    {
        var act = () => AuthorizationDeadlinePolicy.Extend(
            Open(), Command(new DateTimeOffset(Receipt.AddDays(daysAfterReceipt))), "reviewer-1", Receipt.AddDays(1));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Extension_AfterTheDeadline_CannotCureABreach()
    {
        var act = () => AuthorizationDeadlinePolicy.Extend(Open(), Command(), "reviewer-1", Receipt.AddDays(7).AddMinutes(1));
        act.Should().Throw<InvalidOperationException>().WithMessage("*before the decision deadline*");
    }

    [Theory]
    [InlineData(LineOfBusiness.Commercial)]
    [InlineData(LineOfBusiness.Exchange)]
    [InlineData(LineOfBusiness.Unknown)]
    public void Extension_OnALineOfBusinessWithoutAnExtensionProvision_IsRefused(LineOfBusiness lob)
    {
        var act = () => AuthorizationDeadlinePolicy.Extend(Open(lob), Command(), "reviewer-1", Receipt.AddDays(1));
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(AuthorizationStatus.Approved)]
    [InlineData(AuthorizationStatus.Denied)]
    [InlineData(AuthorizationStatus.Cancelled)]
    public void Extension_OfADecidedOrClosedRequest_IsRefused(AuthorizationStatus status)
    {
        var act = () => AuthorizationDeadlinePolicy.Extend(Open(status: status), Command(), "reviewer-1", Receipt.AddDays(1));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Extension_IsAllowedOnce()
    {
        var auth = Open();
        auth.DecisionExtension = AuthorizationDeadlinePolicy.Extend(auth, Command(), "reviewer-1", Receipt.AddDays(1));
        auth.DecisionDueAt = auth.DecisionExtension.ExtendedDueAt;
        var act = () => AuthorizationDeadlinePolicy.Extend(auth, Command(), "reviewer-1", Receipt.AddDays(2));
        act.Should().Throw<InvalidOperationException>().WithMessage("*already been extended*");
    }

    [Fact]
    public void Extension_NeedsAGroundAndAWrittenReason()
    {
        var noBasis = Command(); noBasis.Basis = DecisionExtensionBasis.Unspecified;
        var noReason = Command(); noReason.Reason = "  ";
        foreach (var command in new[] { noBasis, noReason })
        {
            var act = () => AuthorizationDeadlinePolicy.Extend(Open(), command, "reviewer-1", Receipt.AddDays(1));
            act.Should().Throw<ArgumentException>();
        }
    }

    [Fact]
    public void Create_DiscardsAnExtensionClaimedByTheRequestBody()
    {
        var auth = new Authorization
        {
            SubmittedDate = Receipt, LineOfBusiness = LineOfBusiness.Medicaid,
            DecisionExtension = new AuthorizationDecisionExtension { ExtendedDueAt = Receipt.AddDays(60) },
            DecisionDueAt = Receipt.AddDays(60),
        };
        AuthorizationDeadlinePolicy.Initialize(auth);
        auth.DecisionExtension.Should().BeNull();
        auth.DecisionDueAt.Should().Be(Receipt.AddDays(7));
    }

    [Fact]
    public void ExtendedRequest_IsNotABreachBeforeItsExtendedDeadline()
    {
        var auth = Open();
        auth.DecisionExtension = AuthorizationDeadlinePolicy.Extend(auth, Command(), "reviewer-1", Receipt.AddDays(6));
        auth.DecisionDueAt = auth.DecisionExtension.ExtendedDueAt;

        var status = SlaWatchdogService.ComputeSlaStatus(auth, Receipt.AddDays(10));
        status.SlaDeadline.Should().Be(Receipt.AddDays(21));
        status.EscalationLevel.Should().Be(SlaEscalationLevel.None);
    }

    [Fact]
    public void NativeExtension_IsRecordedAsReportingEvidence()
    {
        var auth = Open();
        var extension = AuthorizationDeadlinePolicy.Extend(auth, Command(), "reviewer-1", Receipt.AddDays(1));
        var fact = PaReportingEvidence.NativeExtension(auth, extension, revision: 3)!;

        fact.Kind.Should().Be(PaReportingFactKind.ReviewExtension);
        fact.SourceSystem.Should().Be("cho-native");
        fact.EventId.Should().Be("native-extension:3");
        fact.OriginalDueAt.Should().Be(Receipt.AddDays(7));
        fact.ExtendedDueAt.Should().Be(Receipt.AddDays(21));
        fact.RecordedBy.Should().Be("reviewer-1");

        auth.ReportingLedger = null;
        PaReportingEvidence.NativeExtension(auth, extension, 3).Should().BeNull("a legacy record gets no invented history");
    }

    [Fact]
    public void ExtendedThenApproved_FeedsTheAnnualDraft()
    {
        var auth = Open();
        var extension = AuthorizationDeadlinePolicy.Extend(auth, Command(), "reviewer-1", Receipt.AddDays(1));
        auth.ReportingLedger!.Facts.Add(PaReportingEvidence.NativeExtension(auth, extension, 1)!);
        auth.ReportingLedger.Facts.Add(new PaReportingFact
        {
            EventId = "native-decision:2", Kind = PaReportingFactKind.Determination, EffectiveAt = Receipt.AddDays(12),
            RecordedAt = Receipt.AddDays(12), DecisionAt = Receipt.AddDays(12), Outcome = AuthorizationStatus.Approved,
            SourceSystem = "cho-native", EvidenceReference = "revision:2", RecordedBy = "reviewer-1",
        });

        var report = new AnnualPaReporting(2026, "UTC", LineOfBusiness.Medicaid,
            new DateTime(2027, 1, 31, 0, 0, 0, DateTimeKind.Utc), new DateTime(2027, 1, 31, 0, 0, 0, DateTimeKind.Utc));
        report.Add(auth);
        var draft = report.Finish();
        draft.Standard.ExtendedThenApproved.Should().Be(1);
        draft.ExtendedThenApprovedPercent.Should().Be(100);
    }
}
