using AuthorizationService.Services;
using AuthorizationService.Backends;
using AuthorizationService.Controllers;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using AuthorizationService.Services.Cms0057;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuthorizationService.Tests.Services;

public class Cms0057ReportingTests
{
    private static readonly DateTime Jan1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Dec31 = new(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc);

    private static Authorization Auth(
        string number,
        AuthorizationStatus status,
        string levelOfService = "E",
        DateTime? submitted = null,
        double? decidedAfterHours = null,
        string? source = null,
        LineOfBusiness lob = LineOfBusiness.Medicaid)
    {
        var submittedAt = submitted ?? new DateTime(2026, 3, 2, 8, 0, 0, DateTimeKind.Utc);
        var auth = new Authorization
        {
            Id = number,
            AuthorizationNumber = number,
            TenantId = "tenant-1",
            MemberId = "MBR-" + number,
            Status = status,
            LevelOfService = levelOfService,
            LineOfBusiness = lob,
            SubmittedDate = submittedAt,
            ReviewedDate = decidedAfterHours is null ? null : submittedAt.AddHours(decidedAfterHours.Value),
        };
        if (decidedAfterHours is not null)
        {
            auth.StatusHistory.Add(new AuthorizationStatusChange
            {
                Status = status,
                ChangedAt = auth.ReviewedDate!.Value,
                ChangedBy = source is null ? "reviewer-1" : "system",
                Source = source,
                Reason = status == AuthorizationStatus.Denied ? "Not medically necessary" : "Meets criteria",
            });
        }
        return auth;
    }

    [Theory]
    [InlineData(AuthorizationStatus.Expired)]
    [InlineData(AuthorizationStatus.Cancelled)]
    public void ClosingAnApprovalPreservesClinicalMetricsAndDecisionProvenance(AuthorizationStatus closed)
    {
        var authorization = Auth("retained-decision", AuthorizationStatus.Approved, decidedAfterHours: 24, source: "automated:ClinicalRule");
        var before = Cms0057Reporting.ComputeMetrics(new[] { authorization }, Jan1, Dec31, null);
        authorization.Status = closed;
        authorization.StatusHistory.Add(new AuthorizationStatusChange { Status = closed, ChangedAt = Dec31, Source = "authorization-expiry" });
        var after = Cms0057Reporting.ComputeMetrics(new[] { authorization }, Jan1, Dec31, null);
        after.DecidedRequests.Should().Be(before.DecidedRequests);
        after.Standard.Approved.Should().Be(1);
        after.Standard.MedianDecisionHours.Should().Be(24);
        after.AutomatedDecisions.Should().Be(1);
        var decision = Cms0057Reporting.RecentDecisions(new[] { authorization }, Jan1, 10).Should().ContainSingle().Subject;
        decision.Status.Should().Be(AuthorizationStatus.Approved);
        decision.LifecycleStatus.Should().Be(closed);
        decision.DecidedBy.Should().Be("automated:ClinicalRule");
    }

    [Fact]
    public void Metrics_SplitStandardAndExpedited_WithPercentagesOfAllRequests()
    {
        var auths = new[]
        {
            Auth("S1", AuthorizationStatus.Approved, decidedAfterHours: 24),
            Auth("S2", AuthorizationStatus.Modified, decidedAfterHours: 48),
            Auth("S3", AuthorizationStatus.Denied, decidedAfterHours: 200),
            Auth("S4", AuthorizationStatus.Pended),
            Auth("E1", AuthorizationStatus.Approved, "U", decidedAfterHours: 10),
            Auth("E2", AuthorizationStatus.Denied, "U", decidedAfterHours: 80),
        };

        var m = Cms0057Reporting.ComputeMetrics(auths, Jan1, Dec31, null);

        m.TotalRequests.Should().Be(6);
        m.OpenRequests.Should().Be(1);
        m.DecidedRequests.Should().Be(5);

        m.Standard.Requests.Should().Be(4);
        m.Standard.Decided.Should().Be(3);
        m.Standard.Approved.Should().Be(2, "Modified (approved with changes) counts as approved");
        m.Standard.ApprovedPercent.Should().Be(50);
        m.Standard.DeniedPercent.Should().Be(25);
        m.Standard.AverageDecisionHours.Should().Be(90.7);
        m.Standard.MedianDecisionHours.Should().Be(48);
        m.Standard.DecidedWithinTimeframe.Should().Be(2, "200 hours is past the 7-day (168-hour) timeframe");
        m.Standard.TimeframeHours.Should().Be(168);

        m.Expedited.Requests.Should().Be(2);
        m.Expedited.ApprovedPercent.Should().Be(50);
        m.Expedited.MedianDecisionHours.Should().Be(45, "the median of an even count is the mean of the middle two");
        m.Expedited.DecidedWithinTimeframe.Should().Be(1, "80 hours is past the 72-hour expedited timeframe");
        m.Expedited.TimeframeHours.Should().Be(72);
    }

    [Fact]
    public void Metrics_UsePayerReceiptRatherThanSenderSubmission_ForPeriodAndElapsedTime()
    {
        var authorization = Auth("receipt", AuthorizationStatus.Approved,
            submitted: Jan1.AddHours(-2), decidedAfterHours: 6);
        authorization.DecisionReceivedAt = Jan1;

        var metrics = Cms0057Reporting.ComputeMetrics(new[] { authorization }, Jan1, Dec31, null);

        metrics.TotalRequests.Should().Be(1);
        metrics.Standard.AverageDecisionHours.Should().Be(4);
    }

    [Fact]
    public void Metrics_ExcludeDrugRequests_UsingTheExistingPaScopePolicy()
    {
        var drug = Auth("drug", AuthorizationStatus.Denied, decidedAfterHours: 24);
        drug.ServiceTypeCode = "88";
        var medical = Auth("medical", AuthorizationStatus.Approved, decidedAfterHours: 12);

        var metrics = Cms0057Reporting.ComputeMetrics(new[] { drug, medical }, Jan1, Dec31, null);

        metrics.TotalRequests.Should().Be(1);
        metrics.Standard.ApprovedPercent.Should().Be(100);
        Cms0057Reporting.RecentDecisions(new[] { drug, medical }, Jan1, 10)
            .Should().ContainSingle(d => d.AuthorizationNumber == "medical");
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(null)]
    public void InvalidOrMissingDecisionTimes_AreUnknown_NotInstantOrTimely(double? hours)
    {
        var authorization = Auth("invalid", AuthorizationStatus.Approved, decidedAfterHours: hours);

        Cms0057Reporting.DecisionHours(authorization).Should().BeNull();
        Cms0057Reporting.DecidedWithinTimeframe(authorization).Should().BeFalse();
        var metrics = Cms0057Reporting.ComputeMetrics(new[] { authorization }, Jan1, Dec31, null);
        metrics.Standard.Decided.Should().Be(1);
        metrics.Standard.AverageDecisionHours.Should().BeNull();
        metrics.Standard.DecidedWithinTimeframe.Should().Be(0);
        metrics.Standard.DecisionTimingUnavailable.Should().Be(1);
    }

    [Fact]
    public void Metrics_CountOnlyRequestsSubmittedInThePeriod_AndTheLineOfBusiness()
    {
        var auths = new[]
        {
            Auth("IN", AuthorizationStatus.Approved, decidedAfterHours: 5),
            Auth("BEFORE", AuthorizationStatus.Approved, submitted: Jan1.AddDays(-1), decidedAfterHours: 48),
            Auth("OTHER-LOB", AuthorizationStatus.Approved, decidedAfterHours: 5, lob: LineOfBusiness.Commercial),
        };

        var m = Cms0057Reporting.ComputeMetrics(auths, Jan1, Dec31, LineOfBusiness.Medicaid);

        m.TotalRequests.Should().Be(1);
        m.Standard.Approved.Should().Be(1);
    }

    [Fact]
    public void Metrics_WithReceivedRequestsButNoDecisions_ReportZeroApprovalsAndUnknownTiming()
    {
        var m = Cms0057Reporting.ComputeMetrics(new[] { Auth("P", AuthorizationStatus.Submitted) }, Jan1, Dec31, null);

        m.Standard.ApprovedPercent.Should().Be(0, "there is one received request and no approval");
        m.Standard.AverageDecisionHours.Should().BeNull();
        m.Expedited.Requests.Should().Be(0);
    }

    [Fact]
    public void Metrics_NameTheRequiredMetricsTheDataCannotSupportYet()
    {
        var m = Cms0057Reporting.ComputeMetrics(Array.Empty<Authorization>(), Jan1, Dec31, null);

        m.NotYetTracked.Select(g => g.Metric).Should().BeEquivalentTo("Approved after appeal");
    }

    private static Authorization Extended(Authorization auth, double extendedDays = 14)
    {
        AuthorizationDeadlinePolicy.Initialize(auth);
        var original = auth.DecisionDueAt!.Value;
        auth.DecisionExtension = new AuthorizationDecisionExtension
        {
            GrantedAt = auth.SubmittedDate.AddDays(1), GrantedBy = "reviewer-1", OriginalDueAt = original,
            ExtendedDueAt = original.AddDays(extendedDays), Basis = DecisionExtensionBasis.EnrolleeRequested, Reason = "Member asked",
        };
        auth.DecisionDueAt = auth.DecisionExtension.ExtendedDueAt;
        return auth;
    }

    [Fact]
    public void Metrics_JudgeAnExtendedRequestAgainstItsExtendedDeadline()
    {
        var auths = new[]
        {
            Extended(Auth("EXT-ON-TIME", AuthorizationStatus.Approved, decidedAfterHours: 240)),
            Auth("LATE", AuthorizationStatus.Approved, decidedAfterHours: 240),
            Extended(Auth("EXT-DENIED", AuthorizationStatus.Denied, decidedAfterHours: 200)),
            Auth("OPEN", AuthorizationStatus.Submitted),
        };

        var m = Cms0057Reporting.ComputeMetrics(auths, Jan1, Dec31, null);

        m.Standard.DecidedWithinTimeframe.Should().Be(2, "both extended requests were decided inside their extension");
        m.Standard.Extended.Should().Be(2);
        m.Standard.ExtendedThenApproved.Should().Be(1);
        m.ExtendedThenApprovedPercent.Should().Be(25, "CMS's denominator is all requests");
        Cms0057Reporting.DecidedWithinTimeframe(auths[1]).Should().BeFalse("an unextended request is still late after 7 days");

        var decision = Cms0057Reporting.RecentDecisions(auths, Jan1, 10).Single(d => d.Id == "EXT-ON-TIME");
        decision.Extended.Should().BeTrue();
        decision.TimeframeHours.Should().Be(168 + 14 * 24);
        decision.WithinTimeframe.Should().BeTrue();
    }

    [Fact]
    public void Metrics_CountSourcedExtensionEvidence_AsExtended()
    {
        var auth = Auth("SOURCED", AuthorizationStatus.Approved, decidedAfterHours: 100);
        PaReportingEvidence.Initialize(auth, auth.SubmittedDate);
        auth.ReportingLedger!.Facts.Add(new PaReportingFact
        {
            EventId = "ext", Kind = PaReportingFactKind.ReviewExtension, EffectiveAt = auth.SubmittedDate.AddDays(1),
            SourceSystem = "um-core", EvidenceReference = "ext:1",
        });
        Cms0057Reporting.ComputeMetrics(new[] { auth }, Jan1, Dec31, null).Standard.ExtendedThenApproved.Should().Be(1);
    }

    [Fact]
    public void Metrics_CountApprovalsAfterAppeal_FromReportingEvidence()
    {
        var overturned = Auth("OVERTURNED", AuthorizationStatus.Denied, decidedAfterHours: 24);
        PaReportingEvidence.Initialize(overturned, overturned.SubmittedDate);
        overturned.ReportingLedger!.Facts.Add(new PaReportingFact
        {
            EventId = "appeal-1", Kind = PaReportingFactKind.AppealDecision, EffectiveAt = overturned.SubmittedDate.AddDays(20),
            Outcome = AuthorizationStatus.Approved, SourceSystem = "appeals", EvidenceReference = "appeal:1",
        });
        var upheld = Auth("UPHELD", AuthorizationStatus.Denied, decidedAfterHours: 24);
        PaReportingEvidence.Initialize(upheld, upheld.SubmittedDate);
        upheld.ReportingLedger!.Facts.Add(new PaReportingFact
        {
            EventId = "appeal-2", Kind = PaReportingFactKind.AppealDecision, EffectiveAt = upheld.SubmittedDate.AddDays(20),
            Outcome = AuthorizationStatus.Denied, SourceSystem = "appeals", EvidenceReference = "appeal:2",
        });

        var m = Cms0057Reporting.ComputeMetrics(new[] { overturned, upheld }, Jan1, Dec31, null);
        m.Standard.ApprovedAfterAppeal.Should().Be(1);
        m.Standard.ApprovedAfterAppealPercent.Should().Be(50);
    }

    private static Authorization MultiLineModified()
    {
        var auth = Auth("MOD", AuthorizationStatus.Modified, decidedAfterHours: 30);
        auth.DenialReasonCode = "NOTMEDNEC"; auth.DenialReason = "Not medically necessary";
        auth.RequestedServices = new()
        {
            new() { LineNumber = 1, ProcedureCode = "97110", ServiceStatus = "A1" },
            new() { LineNumber = 2, ProcedureCode = "97112", ServiceStatus = "A2", DenialReasonCode = "NOTMEDNEC", DenialReason = "Not medically necessary" },
            new() { LineNumber = 3, ProcedureCode = "E0601", ServiceStatus = "A3", DenialReasonCode = "BENEFIT_EXCLUDED", DenialReason = "Not a covered benefit" },
        };
        return auth;
    }

    [Fact]
    public void AdverseReasons_KeepEveryDeniedOrReducedLine_NotOnlyTheFirst()
    {
        var reasons = Cms0057Reporting.AdverseReasons(MultiLineModified());

        reasons.Should().BeEquivalentTo(new[]
        {
            new Cms0057AdverseReason(2, "A2", "NOTMEDNEC", "Not medically necessary"),
            new Cms0057AdverseReason(3, "A3", "BENEFIT_EXCLUDED", "Not a covered benefit"),
        });
        Cms0057Reporting.RecentDecisions(new[] { MultiLineModified() }, Jan1, 10).Single()
            .AdverseReasons.Should().HaveCount(2);
    }

    [Fact]
    public void AdverseReasons_FallBackToTheRequestLevelReason_AndAreEmptyForApprovals()
    {
        var denied = Auth("D", AuthorizationStatus.Denied, decidedAfterHours: 2);
        denied.DenialReasonCode = "PROVIDER_EXCLUDED"; denied.DenialReason = "Provider is excluded";
        Cms0057Reporting.AdverseReasons(denied).Should().ContainSingle()
            .Which.Should().Be(new Cms0057AdverseReason(null, "A3", "PROVIDER_EXCLUDED", "Provider is excluded"));
        Cms0057Reporting.AdverseReasons(Auth("A", AuthorizationStatus.Approved, decidedAfterHours: 2)).Should().BeEmpty();
    }

    [Fact]
    public void Insights_DenialReasons_IncludeReductionsAndEveryLineCode_AndTheDrillDownAgrees()
    {
        var denied = Auth("D", AuthorizationStatus.Denied, decidedAfterHours: 2);
        denied.DenialReasonCode = "NOTMEDNEC"; denied.DenialReason = "Not medically necessary";
        var activity = new[] { MultiLineModified(), denied };

        var insights = AuthorizationInsightSets.Build(activity, Array.Empty<Authorization>(), Jan1, Dec31, null, Dec31);

        insights.DenialReasons.Select(r => (r.Code, r.Count)).Should().BeEquivalentTo(new[]
        {
            ("NOTMEDNEC", 2), ("BENEFIT_EXCLUDED", 1),
        });
        foreach (var reason in insights.DenialReasons)
            AuthorizationInsightSets.List(new AuthorizationInsightCriteria { DenialReasonCode = reason.Code },
                    activity, Array.Empty<Authorization>(), Jan1, Dec31, null, 100)
                .Should().HaveCount(reason.Count, $"the {reason.Code} figure and its drill-down use one predicate");
    }

    [Fact]
    public void Metrics_CountAutomatedDecisions_FromTheDecisionHistorySource()
    {
        var auths = new[]
        {
            Auth("A", AuthorizationStatus.Approved, decidedAfterHours: 0.1, source: "automated:AutoApproveList;lob=Medicaid;state=TX"),
            Auth("R", AuthorizationStatus.Approved, decidedAfterHours: 20),
            Auth("X", AuthorizationStatus.Denied, decidedAfterHours: 1, source: "benefit-exclusion"),
        };

        Cms0057Reporting.ComputeMetrics(auths, Jan1, Dec31, null).AutomatedDecisions.Should().Be(1);
    }

    [Fact]
    public void RecentDecisions_NewestFirst_WithTimeframeDeciderAndReason()
    {
        var auths = new[]
        {
            Auth("OLD", AuthorizationStatus.Approved, submitted: new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), decidedAfterHours: 1),
            Auth("AUTO", AuthorizationStatus.Approved, "U", new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc), 0.2, "automated:AutoApproveList"),
            Auth("LATE", AuthorizationStatus.Denied, "U", new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc), 90),
            Auth("OPEN", AuthorizationStatus.InReview),
        };
        auths[2].DenialReason = "Clinical criteria not met";
        auths[2].ReviewerName = "Dr. Reviewer";

        var decisions = Cms0057Reporting.RecentDecisions(auths, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), 10);

        decisions.Select(d => d.AuthorizationNumber).Should().Equal(
            new[] { "LATE", "AUTO", "OLD" }, "ordered by decision time: LATE was decided on Mar 6, AUTO on Mar 5");
        var auto = decisions[1];
        auto.Automated.Should().BeTrue();
        auto.DecidedBy.Should().Be("automated:AutoApproveList");
        auto.RequestType.Should().Be("Expedited");
        auto.WithinTimeframe.Should().BeTrue();
        var late = decisions[0];
        late.WithinTimeframe.Should().BeFalse();
        late.TimeframeHours.Should().Be(72);
        late.DecidedBy.Should().Be("Dr. Reviewer");
        late.Reason.Should().Be("Clinical criteria not met");
    }

    [Fact]
    public async Task Endpoints_UseTheTenantScopedReportingQuery()
    {
        var repo = new Mock<IAuthorizationRepository>();
        repo.Setup(r => r.ListActivityBetweenAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new[] { Auth("S1", AuthorizationStatus.Approved, submitted: DateTime.UtcNow.AddHours(-30), decidedAfterHours: 24) });
        var env = new Mock<IWebHostEnvironment>();
        var actor = new Mock<ICurrentActor>();
        actor.SetupGet(a => a.TenantId).Returns("tenant-1");
        var selector = new AuthorizationBackendSelector(
            new IAuthorizationBackend[] { new ChoAuthorizationBackend(repo.Object) },
            Options.Create(new AuthorizationBackendOptions()));
        var controller = new AuthorizationsController(
            repo.Object, selector, env.Object, Mock.Of<ILogger<AuthorizationsController>>(), actor.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var metrics = (await controller.GetCms0057Metrics()).Result.Should().BeOfType<OkObjectResult>()
            .Subject.Value.Should().BeOfType<Cms0057Metrics>().Subject;
        metrics.Standard.Approved.Should().Be(1);

        var decisions = (await controller.GetCms0057Decisions(days: 7)).Result.Should().BeOfType<OkObjectResult>()
            .Subject.Value.Should().BeAssignableTo<IEnumerable<Cms0057Decision>>().Subject;
        decisions.Should().ContainSingle(d => d.AuthorizationNumber == "S1");

        (await controller.GetCms0057Metrics(from: Dec31, to: Jan1)).Result.Should().BeOfType<BadRequestObjectResult>();
    }
}
