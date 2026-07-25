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
        Mock<IEnrollmentRepository> repo)
        Build()
    {
        var repo = new Mock<IEnrollmentRepository>();
        // Subscriber-id queries return null so the import takes the "create new" path.
        repo.Setup(r => r.GetMemberBySubscriberIdAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((Member?)null);
        repo.Setup(r => r.GetMemberByIdAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((Member?)null);
        repo.Setup(r => r.CreateMemberAsync(It.IsAny<Member>())).ReturnsAsync((Member m) => m);
        repo.Setup(r => r.UpdateMemberAsync(It.IsAny<Member>())).ReturnsAsync((Member m) => m);
        repo.Setup(r => r.CreateCoverageAsync(It.IsAny<Coverage>())).ReturnsAsync((Coverage c) => c);

        var txns = new Mock<IEnrollmentTransactionRepository>();
        txns.Setup(t => t.CreateAsync(It.IsAny<EnrollmentTransaction>()))
            .ReturnsAsync((EnrollmentTransaction t) => t);

        var events = new InMemoryEnrollmentEventRepository();
        var publisher = new EnrollmentEventPublisher(events, NullLogger<EnrollmentEventPublisher>.Instance);
        var validator = new EnrollmentValidator();

        var svc = new ImportSvc(
            repo.Object, txns.Object, publisher, validator,
            NullLogger<ImportSvc>.Instance);
        return (svc, events, repo);
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
        var (svc, events, _) = Build();

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
        var (svc, events, _) = Build();

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
        var (svc, events, _) = Build();

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
        var (svc, events, _) = Build();

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
        var (svc, events, _) = Build();

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
        var (svc, _, repo) = Build();

        Member? created = null;
        repo.Setup(r => r.CreateMemberAsync(It.IsAny<Member>()))
            .Callback<Member>(m => created = m)
            .ReturnsAsync((Member m) => m);

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
        created.EnrollmentDate.Should().Be(new DateTime(2026, 1, 1));
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
}
