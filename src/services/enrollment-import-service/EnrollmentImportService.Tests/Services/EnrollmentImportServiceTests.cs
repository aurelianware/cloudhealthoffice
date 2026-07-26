using EnrollmentImportService.Clients;
using EnrollmentImportService.Models;
using EnrollmentImportService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

// Classification is tested via the dedicated EnrollmentEventClassifier helper to avoid
// the name-shadowing issue between the root namespace and the production import-service
// class — see EnrollmentEventClassifier's XML doc for the full rationale.
using ImportSvc = EnrollmentImportService.Services.EnrollmentImportService;

namespace EnrollmentImportService.Tests.Services;

public class EnrollmentImportServiceTests
{
    private static (ImportSvc svc,
        InMemoryEnrollmentEventRepository events,
        Mock<ICoverageServiceClient> coverageClient,
        Mock<IMemberServiceClient> memberClient,
        Mock<ISponsorServiceClient> sponsorClient,
        Mock<IBenefitPlanServiceClient> benefitPlanClient,
        Mock<IEnrollmentImportRunRepository> importRuns)
        Build()
    {
        var coverageClient = new Mock<ICoverageServiceClient>();
        coverageClient.Setup(c => c.CreateAsync(It.IsAny<string>(), It.IsAny<CreateCoverageRequestDto>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var memberClient = new Mock<IMemberServiceClient>();
        // No pre-existing member, so imports take the "create new" path.
        memberClient.Setup(m => m.ExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        memberClient.Setup(m => m.CreateAsync(It.IsAny<string>(), It.IsAny<CreateMemberRequestDto>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        memberClient.Setup(m => m.UpdateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<UpdateMemberRequestDto>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        memberClient.Setup(m => m.TerminateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TerminateMemberRequestDto>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sponsorClient = new Mock<ISponsorServiceClient>();
        sponsorClient.Setup(s => s.ExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        sponsorClient.Setup(s => s.CreateAsync(It.IsAny<string>(), It.IsAny<CreateSponsorRequestDto>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var benefitPlanClient = new Mock<IBenefitPlanServiceClient>();
        // Default: every plan code resolves, so tests that don't care about
        // coverage-mapping behavior see coverage created same as before.
        benefitPlanClient.Setup(b => b.ResolvePlanIdAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("resolved-plan-id");

        var txns = new Mock<IEnrollmentTransactionRepository>();
        txns.Setup(t => t.CreateAsync(It.IsAny<EnrollmentTransaction>()))
            .ReturnsAsync((EnrollmentTransaction t) => t);

        var importRuns = new Mock<IEnrollmentImportRunRepository>();
        importRuns.Setup(r => r.CreateAsync(It.IsAny<EnrollmentImportRun>()))
            .ReturnsAsync((EnrollmentImportRun r) => r);

        var events = new InMemoryEnrollmentEventRepository();
        var publisher = new EnrollmentEventPublisher(events, NullLogger<EnrollmentEventPublisher>.Instance);
        var validator = new EnrollmentValidator();

        var svc = new ImportSvc(
            memberClient.Object, sponsorClient.Object, benefitPlanClient.Object, coverageClient.Object,
            txns.Object, importRuns.Object, publisher, validator,
            NullLogger<ImportSvc>.Instance);
        return (svc, events, coverageClient, memberClient, sponsorClient, benefitPlanClient, importRuns);
    }

    private static MemberEnrollment NewSubscriber(string subscriberId) => new()
    {
        SubscriberId = subscriberId,
        MaintenanceType = "021",
        BenefitStatus = "A",
        Relationship = "18",
        EnrollmentDate = "2026-01-01",
        Demographics = new Demographics { FirstName = "Jane", LastName = "Doe" }
    };

    [Fact]
    public async Task Import_EmitsOneEvent_PerAcceptedTransaction()
    {
        var (svc, events, _, _, _, _, _) = Build();

        var batch = new Enrollment834
        {
            FileName = "test.834",
            BatchId = "B-1",
            Enrollments = new()
            {
                NewSubscriber("M-001"),
                NewSubscriber("M-002")
            }
        };
        var result = await svc.ImportEnrollmentAsync(batch, "t1");

        result.SuccessCount.Should().Be(2);
        events.AllEvents.Should().HaveCount(2);
        events.AllEvents.Select(e => e.MemberId).Should().BeEquivalentTo(new[] { "M-001", "M-002" });
    }

    [Fact]
    public async Task Import_RejectedValidation_DoesNotEmitEvent()
    {
        var (svc, events, _, _, _, _, _) = Build();

        var bad = new MemberEnrollment(); // no required fields
        var batch = new Enrollment834
        {
            FileName = "bad.834",
            BatchId = "B-bad",
            Enrollments = new() { bad }
        };
        var result = await svc.ImportEnrollmentAsync(batch, "t1");

        result.FailedCount.Should().Be(1);
        events.AllEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task Import_ReplayingSameBatch_ProducesNoDuplicateEvents()
    {
        var (svc, events, _, _, _, _, _) = Build();

        var batch = new Enrollment834
        {
            FileName = "test.834",
            BatchId = "B-replay",
            Enrollments = new() { NewSubscriber("M-replay") }
        };

        await svc.ImportEnrollmentAsync(batch, "t1");
        await svc.ImportEnrollmentAsync(batch, "t1");

        events.AllEvents.Should().HaveCount(1);
        events.AllEvents[0].EventId.Should().StartWith("834-B-replay:");
    }

    [Fact]
    public async Task Import_ManualSource_UsesManualPrefix_AndDoesNotAccidentallyDedupe()
    {
        var (svc, events, _, _, _, _, _) = Build();

        // Two manual enrollments for the same subscriber but with distinct EventIds —
        // each should produce a separate event even if the synthesized batch ids
        // happened to collide, because the prefix makes them disjoint and the per-event
        // EventId differs.
        var e1 = NewSubscriber("M-manual");
        e1.EventId = "evt-A";
        var e2 = NewSubscriber("M-manual");
        e2.EventId = "evt-B";

        await svc.ImportEnrollmentAsync(new Enrollment834
        {
            FileName = "manual:user1",
            BatchId = "MANUAL-1",
            ManualSource = true,
            Enrollments = new() { e1 }
        }, "t1");

        // Second submission shouldn't have a colliding M-manual address with an existing
        // member, so set MaintenanceType=001 to take the update path.
        e2.MaintenanceType = "001";
        e2.EnrollmentDate = "2026-02-01";
        await svc.ImportEnrollmentAsync(new Enrollment834
        {
            FileName = "manual:user1",
            BatchId = "MANUAL-1", // intentionally identical
            ManualSource = true,
            Enrollments = new() { e2 }
        }, "t1");

        events.AllEvents.Should().HaveCount(2);
        events.AllEvents.All(e => e.EventId.StartsWith("manual-")).Should().BeTrue();
    }

    [Fact]
    public async Task Import_ManualSource_SameRequestEventId_IsIdempotent()
    {
        var (svc, events, _, _, _, _, _) = Build();

        var e1 = NewSubscriber("M-idem");
        e1.EventId = "stable-key";

        await svc.ImportEnrollmentAsync(new Enrollment834
        {
            FileName = "manual",
            BatchId = "MANUAL-A",
            ManualSource = true,
            Enrollments = new() { e1 }
        }, "t1");

        await svc.ImportEnrollmentAsync(new Enrollment834
        {
            FileName = "manual",
            BatchId = "MANUAL-B", // different batch id
            ManualSource = true,
            Enrollments = new() { e1 }
        }, "t1");

        events.AllEvents.Should().HaveCount(1);
    }

    [Fact]
    public async Task Import_ParsesX12D8Dates_NotJustTheDashedFormatTestFixturesHappenToUse()
    {
        // NewSubscriber's EnrollmentDate above is "2026-01-01" (dashed, ISO-ish) —
        // DateTime.TryParse already handles that, so it never would have caught
        // the real bug. Actual 834 dates are X12's D8 format (CCYYMMDD, no
        // separators, e.g. "19780922"), which DateTime.TryParse silently fails
        // to recognize as a date at all (confirmed empirically: it returns
        // false, not an exception) rather than a differently-formatted date.
        var (svc, _, _, memberClient, _, _, _) = Build();

        CreateMemberRequestDto? created = null;
        memberClient.Setup(m => m.CreateAsync(It.IsAny<string>(), It.IsAny<CreateMemberRequestDto>(), It.IsAny<CancellationToken>()))
            .Callback<string, CreateMemberRequestDto, CancellationToken>((_, req, _) => created = req)
            .Returns(Task.CompletedTask);

        var enrollment = NewSubscriber("M-dob");
        enrollment.EnrollmentDate = "20260101";
        enrollment.Demographics!.DateOfBirth = "19780922";

        var batch = new Enrollment834
        {
            FileName = "test.834",
            BatchId = "B-dob",
            Enrollments = new() { enrollment }
        };
        await svc.ImportEnrollmentAsync(batch, "t1");

        created.Should().NotBeNull();
        created!.DateOfBirth.Should().Be(new DateTime(1978, 9, 22));
    }

    [Fact]
    public async Task Import_DelegatesMemberCreation_ToMemberService_NotMongo()
    {
        // Regression guard for the architectural fix itself: member creation
        // must go through IMemberServiceClient rather than writing directly
        // to Mongo — see IMemberServiceClient's doc comment for why
        // Member/Sponsor/Coverage all moved to their owning services.
        var (svc, _, _, memberClient, _, _, _) = Build();

        var batch = new Enrollment834
        {
            FileName = "test.834",
            BatchId = "B-delegate",
            Enrollments = new() { NewSubscriber("M-delegate") }
        };
        await svc.ImportEnrollmentAsync(batch, "t1");

        memberClient.Verify(m => m.CreateAsync(
            "t1",
            It.Is<CreateMemberRequestDto>(r => r.MemberId == "M-delegate" && r.IsSubscriber),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Import_Termination_CallsMemberServiceTerminate()
    {
        var (svc, _, _, memberClient, _, _, _) = Build();

        // First: create the member (existence check defaults to false via Build()).
        var create = NewSubscriber("M-term");
        await svc.ImportEnrollmentAsync(new Enrollment834
        {
            FileName = "test.834",
            BatchId = "B-term-1",
            Enrollments = new() { create }
        }, "t1");

        // Now simulate the member existing for the termination pass.
        memberClient.Setup(m => m.ExistsAsync("t1", "M-term", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var terminate = NewSubscriber("M-term");
        terminate.MaintenanceType = "024";
        terminate.TerminationDate = "20260601";
        await svc.ImportEnrollmentAsync(new Enrollment834
        {
            FileName = "test.834",
            BatchId = "B-term-2",
            Enrollments = new() { terminate }
        }, "t1");

        memberClient.Verify(m => m.TerminateAsync(
            "t1", "M-term",
            It.Is<TerminateMemberRequestDto>(r => r.TerminationDate == new DateTime(2026, 6, 1)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Import_Reinstatement_OfKnownMember_UpdatesRatherThanSkips()
    {
        // Regression guard: maintenance type "025" (Reinstatement) used to fall
        // into the switch statement's default branch ("Unknown maintenance
        // type") and get silently skipped — real 834 fixtures use "025" for
        // subscribers being brought back after a prior termination, and they
        // were never actually landing in member-service.
        var (svc, _, _, memberClient, _, _, _) = Build();

        memberClient.Setup(m => m.ExistsAsync("t1", "M-reinstate", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        UpdateMemberRequestDto? updated = null;
        memberClient.Setup(m => m.UpdateAsync("t1", "M-reinstate", It.IsAny<UpdateMemberRequestDto>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, UpdateMemberRequestDto, CancellationToken>((_, _, req, _) => updated = req)
            .Returns(Task.CompletedTask);

        var reinstate = NewSubscriber("M-reinstate");
        reinstate.MaintenanceType = "025";
        reinstate.BenefitStatus = "A";

        var result = await svc.ImportEnrollmentAsync(new Enrollment834
        {
            FileName = "test.834",
            BatchId = "B-reinstate",
            Enrollments = new() { reinstate }
        }, "t1");

        result.SkippedCount.Should().Be(0);
        result.MembersUpdated.Should().Be(1);
        updated.Should().NotBeNull();
        updated!.Status.Should().Be("Active");
        memberClient.Verify(m => m.CreateAsync(It.IsAny<string>(), It.IsAny<CreateMemberRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Import_Reinstatement_OfUnknownMember_CreatesRatherThanSkips()
    {
        var (svc, _, _, memberClient, _, _, _) = Build();
        // Build() defaults ExistsAsync to false — member is unknown to member-service.

        var reinstate = NewSubscriber("M-reinstate-new");
        reinstate.MaintenanceType = "025";

        var result = await svc.ImportEnrollmentAsync(new Enrollment834
        {
            FileName = "test.834",
            BatchId = "B-reinstate-new",
            Enrollments = new() { reinstate }
        }, "t1");

        result.SkippedCount.Should().Be(0);
        result.MembersCreated.Should().Be(1);
        memberClient.Verify(m => m.CreateAsync(
            "t1",
            It.Is<CreateMemberRequestDto>(r => r.MemberId == "M-reinstate-new"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void ClassifyEvent_TerminationWithCobra_IsCobraTerminated()
    {
        var e = NewSubscriber("M-1");
        e.MaintenanceType = "024";
        e.BenefitStatus = "C";
        e.TerminationDate = "2026-04-01";
        EnrollmentEventClassifier.Classify(e)
            .Should().Be(EnrollmentEventType.CobraTerminated);
    }

    [Fact]
    public void ClassifyEvent_AdditionWithCobra_IsCobraElected()
    {
        var e = NewSubscriber("M-1");
        e.MaintenanceType = "021";
        e.BenefitStatus = "C";
        EnrollmentEventClassifier.Classify(e)
            .Should().Be(EnrollmentEventType.CobraElected);
    }

    [Fact]
    public void ClassifyEvent_Reinstatement_IsReinstatementApproved()
    {
        var e = NewSubscriber("M-1");
        e.MaintenanceType = "025";
        EnrollmentEventClassifier.Classify(e)
            .Should().Be(EnrollmentEventType.ReinstatementApproved);
    }

    [Fact]
    public async Task Import_CoverageWithMappedPlanCode_CreatesCoverageUsingResolvedPlanId()
    {
        var (svc, _, coverageClient, _, _, benefitPlanClient, _) = Build();
        benefitPlanClient.Setup(b => b.ResolvePlanIdAsync(
                "t1", "GRP0001", "HLT", "PPO2026", It.IsAny<CancellationToken>()))
            .ReturnsAsync("benefit-plan-guid-123");

        var enrollment = NewSubscriber("M-cov");
        enrollment.GroupNumber = "GRP0001";
        enrollment.Coverage.Add(new CoverageDetail { InsuranceLineCode = "HLT", PlanCoverageDescription = "PPO2026" });

        var result = await svc.ImportEnrollmentAsync(new Enrollment834
        {
            FileName = "test.834",
            BatchId = "B-cov",
            Enrollments = new() { enrollment }
        }, "t1");

        result.CoverageRecordsCreated.Should().Be(1);
        result.CoverageMappingsUnresolved.Should().Be(0);
        coverageClient.Verify(c => c.CreateAsync(
            "t1",
            It.Is<CreateCoverageRequestDto>(r => r.PlanId == "benefit-plan-guid-123" && r.MemberId == "M-cov"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Import_CoverageWithUnmappedPlanCode_SkipsCoverageInsteadOfDefaulting()
    {
        // This is the regression guard for the original bug: an unresolved
        // 834 plan code must NOT fall back to a literal "DEFAULT" PlanId —
        // it should be skipped and counted so the gap is visible in ImportResult.
        var (svc, _, coverageClient, _, _, benefitPlanClient, _) = Build();
        benefitPlanClient.Setup(b => b.ResolvePlanIdAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var enrollment = NewSubscriber("M-nocov");
        enrollment.GroupNumber = "GRP0001";
        enrollment.Coverage.Add(new CoverageDetail { InsuranceLineCode = "HLT", PlanCoverageDescription = "UNKNOWN-CODE" });

        var result = await svc.ImportEnrollmentAsync(new Enrollment834
        {
            FileName = "test.834",
            BatchId = "B-nocov",
            Enrollments = new() { enrollment }
        }, "t1");

        result.CoverageRecordsCreated.Should().Be(0);
        result.CoverageMappingsUnresolved.Should().Be(1);
        coverageClient.Verify(c => c.CreateAsync(
            It.IsAny<string>(), It.IsAny<CreateCoverageRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Import_CoverageMissingGroupNumber_SkipsResolution_WithoutCallingBenefitPlanClient()
    {
        var (svc, _, coverageClient, _, _, benefitPlanClient, _) = Build();

        var enrollment = NewSubscriber("M-nogroup");
        enrollment.GroupNumber = null;
        enrollment.Coverage.Add(new CoverageDetail { InsuranceLineCode = "HLT", PlanCoverageDescription = "PPO2026" });

        var result = await svc.ImportEnrollmentAsync(new Enrollment834
        {
            FileName = "test.834",
            BatchId = "B-nogroup",
            Enrollments = new() { enrollment }
        }, "t1");

        result.CoverageMappingsUnresolved.Should().Be(1);
        coverageClient.Verify(c => c.CreateAsync(
            It.IsAny<string>(), It.IsAny<CreateCoverageRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
        benefitPlanClient.Verify(b => b.ResolvePlanIdAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Import_PersistsRunSummary_MatchingTheReturnedImportResult()
    {
        var (svc, _, _, _, _, _, importRuns) = Build();

        var batch = new Enrollment834
        {
            FileName = "test.834",
            BatchId = "B-run-summary",
            Enrollments = new()
            {
                NewSubscriber("M-run-1"),
                NewSubscriber("M-run-2")
            }
        };
        var result = await svc.ImportEnrollmentAsync(batch, "t1");

        importRuns.Verify(r => r.CreateAsync(It.Is<EnrollmentImportRun>(run =>
            run.TenantId == "t1"
            && run.BatchId == result.BatchId
            && run.FileName == result.FileName
            && run.SuccessCount == result.SuccessCount
            && run.FailedCount == result.FailedCount
            && run.StartedAt == result.StartedAt
            && run.CompletedAt == result.CompletedAt)), Times.Once);
    }

    [Fact]
    public async Task Import_RunPersistenceFailure_DoesNotFailTheImport()
    {
        var (svc, events, _, _, _, _, importRuns) = Build();
        importRuns.Setup(r => r.CreateAsync(It.IsAny<EnrollmentImportRun>()))
            .ThrowsAsync(new InvalidOperationException("Mongo down"));

        var batch = new Enrollment834
        {
            FileName = "test.834",
            BatchId = "B-run-failure",
            Enrollments = new() { NewSubscriber("M-run-failure") }
        };
        var result = await svc.ImportEnrollmentAsync(batch, "t1");

        result.SuccessCount.Should().Be(1);
        events.AllEvents.Should().HaveCount(1);
    }
}
