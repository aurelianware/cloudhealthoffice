using EnrollmentImportService.Clients;
using EnrollmentImportService.Models;
using EnrollmentImportService.Services;
using EnrollmentImportService.Services.Edi;
using Microsoft.Extensions.Logging.Abstractions;

using ImportSvc = EnrollmentImportService.Services.EnrollmentImportService;

namespace EnrollmentImportService.Tests.Services;

/// <summary>
/// Dependents are their own members (their own 834 Loop 2000): they get a
/// deterministic id under the subscriber, their own maintenance type, and
/// their INS02 relationship — and nothing about them is ever written onto
/// the subscriber's record.
/// </summary>
public class EnrollmentImportServiceDependentTests
{
    /// <summary>member-service stand-in that remembers what was created, so re-imports see prior state.</summary>
    private sealed class Harness
    {
        public HashSet<string> Existing { get; } = new(StringComparer.Ordinal);
        public List<CreateMemberRequestDto> Created { get; } = new();
        public List<(string Id, UpdateMemberRequestDto Request)> Updated { get; } = new();
        public List<(string Id, TerminateMemberRequestDto Request)> Terminated { get; } = new();
        public List<CreateCoverageRequestDto> Coverage { get; } = new();
        public ImportSvc Service { get; }

        public Harness()
        {
            var memberClient = new Mock<IMemberServiceClient>();
            memberClient.Setup(m => m.ExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, string id, CancellationToken _) => Existing.Contains(id));
            memberClient.Setup(m => m.CreateAsync(It.IsAny<string>(), It.IsAny<CreateMemberRequestDto>(), It.IsAny<CancellationToken>()))
                .Callback((string _, CreateMemberRequestDto r, CancellationToken _) =>
                {
                    Created.Add(r);
                    Existing.Add(r.MemberId);
                })
                .Returns(Task.CompletedTask);
            memberClient.Setup(m => m.UpdateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<UpdateMemberRequestDto>(), It.IsAny<CancellationToken>()))
                .Callback((string _, string id, UpdateMemberRequestDto r, CancellationToken _) => Updated.Add((id, r)))
                .Returns(Task.CompletedTask);
            memberClient.Setup(m => m.TerminateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TerminateMemberRequestDto>(), It.IsAny<CancellationToken>()))
                .Callback((string _, string id, TerminateMemberRequestDto r, CancellationToken _) => Terminated.Add((id, r)))
                .Returns(Task.CompletedTask);

            var coverageClient = new Mock<ICoverageServiceClient>();
            coverageClient.Setup(c => c.CreateAsync(It.IsAny<string>(), It.IsAny<CreateCoverageRequestDto>(), It.IsAny<CancellationToken>()))
                .Callback((string _, CreateCoverageRequestDto r, CancellationToken _) => Coverage.Add(r))
                .Returns(Task.CompletedTask);

            var sponsorClient = new Mock<ISponsorServiceClient>();
            sponsorClient.Setup(s => s.ExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var benefitPlanClient = new Mock<IBenefitPlanServiceClient>();
            benefitPlanClient.Setup(b => b.ResolvePlanIdAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("resolved-plan-id");

            var txns = new Mock<IEnrollmentTransactionRepository>();
            txns.Setup(t => t.CreateAsync(It.IsAny<EnrollmentTransaction>()))
                .ReturnsAsync((EnrollmentTransaction t) => t);
            var importRuns = new Mock<IEnrollmentImportRunRepository>();
            importRuns.Setup(r => r.CreateAsync(It.IsAny<EnrollmentImportRun>()))
                .ReturnsAsync((EnrollmentImportRun r) => r);

            var publisher = new EnrollmentEventPublisher(
                new InMemoryEnrollmentEventRepository(), NullLogger<EnrollmentEventPublisher>.Instance);

            Service = new ImportSvc(
                memberClient.Object, sponsorClient.Object, benefitPlanClient.Object, coverageClient.Object,
                txns.Object, importRuns.Object, publisher, new EnrollmentValidator(),
                NullLogger<ImportSvc>.Instance);
        }

        public Task<ImportResult> ImportAsync(Enrollment834 batch) => Service.ImportEnrollmentAsync(batch, "t1");
    }

    private static MemberEnrollment Subscriber(string id, string maintenanceType, params Dependent[] dependents) => new()
    {
        SubscriberId = id,
        MaintenanceType = maintenanceType,
        BenefitStatus = "A",
        Relationship = "18",
        GroupNumber = "GRP0001",
        EnrollmentDate = "20260201",
        TerminationDate = maintenanceType == "024" ? "20260131" : null,
        Demographics = new Demographics
        {
            FirstName = "JOHN", LastName = "SMITH", DateOfBirth = "19850315",
            Address1 = "123 MAIN STREET", City = "SAN FRANCISCO", State = "CA", Zip = "94102"
        },
        Dependents = dependents.ToList()
    };

    private static Dependent Child(string firstName, string dob, string? maintenanceType = null, string relationship = "19") => new()
    {
        FirstName = firstName,
        LastName = "SMITH",
        DateOfBirth = dob,
        Relationship = relationship,
        MaintenanceType = maintenanceType,
        SubscriberId = "SUB1"
    };

    private static string? DependentId(Dependent d) => ImportSvc.BuildDependentMemberId("SUB1", d);

    [Fact]
    public async Task FamilyAdd_CreatesEachDependentUnderTheSubscriber_WithItsOwnRelationshipCode()
    {
        var h = new Harness();
        var spouse = Child("JANE", "19870520", "021", relationship: "01");
        var child = Child("MICHAEL", "20150610", "021");

        var result = await h.ImportAsync(new Enrollment834
        {
            BatchId = "B1",
            Enrollments = { Subscriber("SUB1", "021", spouse, child) }
        });

        result.MembersCreated.Should().Be(1);
        result.DependentsCreated.Should().Be(2);
        h.Created.Should().HaveCount(3);

        var sub = h.Created.Single(c => c.IsSubscriber);
        sub.MemberId.Should().Be("SUB1");
        sub.FirstName.Should().Be("JOHN");

        var deps = h.Created.Where(c => !c.IsSubscriber).ToList();
        deps.Should().OnlyContain(d => d.SubscriberMemberId == "SUB1" && d.MemberId.StartsWith("SUB1-D"));
        deps.Select(d => d.MemberId).Should().OnlyHaveUniqueItems();
        deps.Single(d => d.FirstName == "JANE").RelationshipCode.Should().Be("01");
        deps.Single(d => d.FirstName == "MICHAEL").RelationshipCode.Should().Be("19");
    }

    [Fact]
    public async Task ReImportingTheSameFamily_DoesNotDuplicateDependents()
    {
        var h = new Harness();
        Enrollment834 Batch() => new()
        {
            BatchId = "B-replay",
            Enrollments = { Subscriber("SUB1", "021", Child("MICHAEL", "20150610", "021")) }
        };

        await h.ImportAsync(Batch());
        var second = await h.ImportAsync(Batch());

        h.Created.Where(c => !c.IsSubscriber).Should().ContainSingle();
        second.DependentsCreated.Should().Be(0);
    }

    [Fact]
    public void DependentKey_IsStableAcrossDateFormats_AndPrefersTheMemberLevelIdentifier()
    {
        var a = Child("Michael", "20150610");
        var b = Child(" MICHAEL ", "2015-06-10");
        DependentId(a).Should().Be(DependentId(b));

        var withRef = Child("MICHAEL", "20150610");
        withRef.MemberIdentifier = "BSCA-03";
        var renamed = Child("MIKE", "20150611");
        renamed.MemberIdentifier = "BSCA-03";
        DependentId(withRef).Should().Be(DependentId(renamed));
        DependentId(withRef).Should().NotBe(DependentId(a));

        DependentId(Child("MICHAEL", "")).Should().BeNull();
    }

    [Fact]
    public async Task DependentChange_UpdatesTheDependent_NeverTheSubscriber()
    {
        var h = new Harness();
        var spouse = Child("JANE", "19870520", "001", relationship: "01");
        spouse.Address1 = "999 NEW ROAD";
        h.Existing.Add("SUB1");
        h.Existing.Add(DependentId(spouse)!);

        var result = await h.ImportAsync(new Enrollment834
        {
            BatchId = "B2",
            Enrollments = { Subscriber("SUB1", "001", spouse) }
        });

        result.DependentsUpdated.Should().Be(1);
        h.Created.Should().BeEmpty();
        h.Updated.Should().HaveCount(2);
        h.Updated.Single(u => u.Id == "SUB1").Request.Address.Should().Be("123 MAIN STREET");
        h.Updated.Single(u => u.Id == DependentId(spouse)).Request.Address.Should().Be("999 NEW ROAD");
    }

    [Fact]
    public async Task Termination_TerminatesTheMatchingDependent_InsteadOfCreatingOne()
    {
        var h = new Harness();
        var child = Child("EMMA", "20120304", "024");
        child.TerminationDate = "20260131";
        h.Existing.Add("SUB1");
        h.Existing.Add(DependentId(child)!);

        var result = await h.ImportAsync(new Enrollment834
        {
            BatchId = "B3",
            Enrollments = { Subscriber("SUB1", "024", child) }
        });

        h.Created.Should().BeEmpty();
        h.Coverage.Should().BeEmpty();
        h.Terminated.Select(t => t.Id).Should().BeEquivalentTo(new[] { "SUB1", DependentId(child) });
        h.Terminated.Should().OnlyContain(t => t.Request.TerminationDate == new DateTime(2026, 1, 31));
        result.MembersTerminated.Should().Be(1);
        result.DependentsTerminated.Should().Be(1);
        result.DependentsCreated.Should().Be(0);
    }

    [Fact]
    public async Task Termination_OfAnUnknownDependent_IsSkipped_NotCreated()
    {
        var h = new Harness();
        h.Existing.Add("SUB1");

        await h.ImportAsync(new Enrollment834
        {
            BatchId = "B4",
            Enrollments = { Subscriber("SUB1", "001", Child("EMMA", "20120304", "024")) }
        });

        h.Created.Should().BeEmpty();
        h.Terminated.Should().BeEmpty();
    }

    [Fact]
    public async Task DependentWithoutOwnMaintenanceType_InheritsTheSubscribers()
    {
        // JSON callers that predate per-dependent INS fields.
        var h = new Harness();
        var child = Child("EMMA", "20120304");
        h.Existing.Add("SUB1");
        h.Existing.Add(DependentId(child)!);

        await h.ImportAsync(new Enrollment834
        {
            BatchId = "B5",
            Enrollments = { Subscriber("SUB1", "024", child) }
        });

        h.Terminated.Select(t => t.Id).Should().Contain(DependentId(child));
        h.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task StandaloneDependent_IsAddedUnderAnExistingSubscriber()
    {
        var h = new Harness();
        h.Existing.Add("SUB1");
        var baby = Child("BABY", "20260115", "021");
        baby.Coverage = [new CoverageDetail { InsuranceLineCode = "HLT", PlanCoverageDescription = "PPO", BenefitBeginDate = "20260115" }];
        baby.GroupNumber = "GRP0001";

        var result = await h.ImportAsync(new Enrollment834 { BatchId = "B6", DependentEnrollments = { baby } });

        result.SuccessCount.Should().Be(1);
        h.Created.Should().ContainSingle(c => c.MemberId == DependentId(baby) && c.SubscriberMemberId == "SUB1" && !c.IsSubscriber);
        h.Coverage.Should().ContainSingle(c => c.MemberId == DependentId(baby) && c.EffectiveDate == new DateTime(2026, 1, 15));
    }

    [Fact]
    public async Task StandaloneDependent_ForAnUnknownSubscriber_FailsWithoutCreatingAnything()
    {
        var h = new Harness();

        var result = await h.ImportAsync(new Enrollment834
        {
            BatchId = "B7",
            DependentEnrollments = { Child("BABY", "20260115", "021") }
        });

        result.FailedCount.Should().Be(1);
        h.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task ParsedSampleFile_EndToEnd_AddsChangesAndTerminatesTheRightMembers()
    {
        var edi = await File.ReadAllTextAsync(FindRepoFile("docs/testing/test-x12-834-enrollment-sample.edi"));
        var batch = new Enrollment834EdiParser().Parse(edi, "sample.edi");
        batch.BatchId = "B-sample";

        var h = new Harness();
        // JOHNSON (001) and WILLIAMS (024) families are already on file.
        h.Existing.UnionWith(new[] { "BSCA987654321", "BSCA555666777" });
        foreach (var e in batch.Enrollments.Skip(1))
        {
            foreach (var d in e.Dependents)
            {
                h.Existing.Add(ImportSvc.BuildDependentMemberId(e.SubscriberId!, d)!);
            }
        }

        var result = await h.ImportAsync(batch);

        result.FailedCount.Should().Be(0, string.Join("; ", result.Errors));
        result.MembersCreated.Should().Be(1);
        result.DependentsCreated.Should().Be(2);
        result.MembersUpdated.Should().Be(1);
        result.DependentsUpdated.Should().Be(1);
        result.MembersTerminated.Should().Be(1);
        result.DependentsTerminated.Should().Be(1);

        h.Created.Select(c => (c.FirstName, c.IsSubscriber, c.RelationshipCode))
            .Should().BeEquivalentTo(new[]
            {
                ("JOHN", true, (string?)null),
                ("JANE", false, (string?)"01"),
                ("MICHAEL", false, (string?)"19")
            });
        h.Updated.Select(u => u.Id).Should().Contain("BSCA987654321");
        h.Updated.Should().HaveCount(2);
        h.Terminated.Should().HaveCount(2);
        h.Terminated.Should().OnlyContain(t => t.Request.TerminationDate == new DateTime(2026, 1, 31));
        // Subscriber + 2 dependents with HLT, plus subscriber DEN/VIS; Johnson x2 HLT.
        h.Coverage.Should().HaveCount(7);
        h.Coverage.Where(c => c.MemberId == "BSCA123456789")
            .Should().OnlyContain(c => c.EffectiveDate == new DateTime(2026, 2, 1));
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException(relative);
    }
}
