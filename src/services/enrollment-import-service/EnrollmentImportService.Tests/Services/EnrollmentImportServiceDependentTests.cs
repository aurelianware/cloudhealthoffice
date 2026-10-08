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
        /// <summary>coverage-service stand-in: what's on file, by member.</summary>
        public List<CoverageRecordDto> StoredCoverage { get; } = new();
        public List<(string Id, UpdateCoverageRequestDto Request)> CoverageUpdates { get; } = new();
        public List<(string Id, DateTime Date)> CoverageTerminations { get; } = new();
        public List<string> CoverageReinstatements { get; } = new();
        public List<EnrollmentTransaction> Transactions { get; } = new();
        public InMemoryEnrollmentEventRepository Events { get; } = new();
        public string? ResolvedPlanId { get; set; } = "resolved-plan-id";
        public ImportSvc Service { get; }

        public CoverageRecordDto SeedCoverage(string memberId, string line, string level, string effective, string? termination = null)
        {
            var record = new CoverageRecordDto
            {
                Id = $"cov-{StoredCoverage.Count + 1}",
                MemberId = memberId,
                GroupNumber = "GRP0001",
                PlanId = "resolved-plan-id",
                InsuranceLineCode = line,
                CoverageLevel = level,
                EffectiveDate = DateTime.ParseExact(effective, "yyyyMMdd", null),
                TerminationDate = termination is null ? null : DateTime.ParseExact(termination, "yyyyMMdd", null)
            };
            StoredCoverage.Add(record);
            return record;
        }

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
                .Callback((string _, CreateCoverageRequestDto r, CancellationToken _) =>
                {
                    Coverage.Add(r);
                    StoredCoverage.Add(new CoverageRecordDto
                    {
                        Id = $"cov-{StoredCoverage.Count + 1}",
                        MemberId = r.MemberId,
                        GroupNumber = r.GroupNumber,
                        PlanId = r.PlanId,
                        InsuranceLineCode = r.InsuranceLineCode,
                        CoverageLevel = r.CoverageLevel,
                        EffectiveDate = r.EffectiveDate,
                        TerminationDate = r.TerminationDate
                    });
                })
                .Returns(Task.CompletedTask);
            coverageClient.Setup(c => c.GetMemberCoverageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, string memberId, CancellationToken _) =>
                    StoredCoverage.Where(s => s.MemberId == memberId)
                        .Select(s => new CoverageRecordDto
                        {
                            Id = s.Id, MemberId = s.MemberId, GroupNumber = s.GroupNumber, PlanId = s.PlanId,
                            InsuranceLineCode = s.InsuranceLineCode, CoverageLevel = s.CoverageLevel,
                            EffectiveDate = s.EffectiveDate, TerminationDate = s.TerminationDate
                        })
                        .ToList());
            coverageClient.Setup(c => c.UpdateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<UpdateCoverageRequestDto>(), It.IsAny<CancellationToken>()))
                .Callback((string _, string id, UpdateCoverageRequestDto r, CancellationToken _) =>
                {
                    CoverageUpdates.Add((id, r));
                    var s = StoredCoverage.Single(x => x.Id == id);
                    s.PlanId = r.PlanId ?? s.PlanId;
                    s.CoverageLevel = r.CoverageLevel ?? s.CoverageLevel;
                })
                .Returns(Task.CompletedTask);
            coverageClient.Setup(c => c.TerminateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback((string _, string id, DateTime date, string? _, CancellationToken _) =>
                {
                    CoverageTerminations.Add((id, date));
                    StoredCoverage.Single(x => x.Id == id).TerminationDate = date;
                })
                .Returns(Task.CompletedTask);

            coverageClient.Setup(c => c.ReinstateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback((string _, string id, string? _, CancellationToken _) =>
                {
                    CoverageReinstatements.Add(id);
                    StoredCoverage.Single(x => x.Id == id).TerminationDate = null;
                })
                .Returns(Task.CompletedTask);

            var sponsorClient = new Mock<ISponsorServiceClient>();
            sponsorClient.Setup(s => s.ExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var benefitPlanClient = new Mock<IBenefitPlanServiceClient>();
            benefitPlanClient.Setup(b => b.ResolvePlanIdAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => ResolvedPlanId);

            var txns = new Mock<IEnrollmentTransactionRepository>();
            txns.Setup(t => t.CreateAsync(It.IsAny<EnrollmentTransaction>()))
                .Callback((EnrollmentTransaction t) => Transactions.Add(t))
                .ReturnsAsync((EnrollmentTransaction t) => t);
            var importRuns = new Mock<IEnrollmentImportRunRepository>();
            importRuns.Setup(r => r.CreateAsync(It.IsAny<EnrollmentImportRun>()))
                .ReturnsAsync((EnrollmentImportRun r) => r);

            var publisher = new EnrollmentEventPublisher(Events, NullLogger<EnrollmentEventPublisher>.Instance);

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
        // JOHNSON (001) and WILLIAMS (024) families — and their coverage —
        // are already on file.
        h.Existing.UnionWith(new[] { "BSCA987654321", "BSCA555666777" });
        h.SeedCoverage("BSCA987654321", "HLT", "ESP", "20250101");
        h.SeedCoverage("BSCA555666777", "HLT", "ECH", "20250115");
        var johnsonSpouse = ImportSvc.BuildDependentMemberId("BSCA987654321", batch.Enrollments[1].Dependents[0])!;
        var williamsChild = ImportSvc.BuildDependentMemberId("BSCA555666777", batch.Enrollments[2].Dependents[0])!;
        h.Existing.UnionWith(new[] { johnsonSpouse, williamsChild });
        h.SeedCoverage(johnsonSpouse, "HLT", "ESP", "20250101");
        var emmaCoverage = h.SeedCoverage(williamsChild, "HLT", "ECH", "20250115");

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

        // Smith family's 5 lines are created; Johnson's 001 matches what's on
        // file (no create, nothing to update); Williams' 024 ends the existing
        // coverage on DTP*349 instead of creating any.
        h.Coverage.Should().HaveCount(5);
        h.Coverage.Should().OnlyContain(c => c.MemberId.StartsWith("BSCA123456789"));
        h.Coverage.Where(c => c.MemberId == "BSCA123456789")
            .Should().OnlyContain(c => c.EffectiveDate == new DateTime(2026, 2, 1));
        h.CoverageUpdates.Should().BeEmpty();
        h.CoverageTerminations.Should().HaveCount(2);
        h.CoverageTerminations.Should().OnlyContain(t => t.Date == new DateTime(2026, 1, 31));
        emmaCoverage.TerminationDate.Should().Be(new DateTime(2026, 1, 31));
        result.CoverageRecordsTerminated.Should().Be(2);

        // Audit trail covers every member, dependents included.
        h.Transactions.Select(t => t.MemberId).Should().HaveCount(7)
            .And.Contain(new[] { johnsonSpouse, williamsChild });
        h.Events.AllEvents.Select(e => e.MemberId).Should().HaveCount(7)
            .And.OnlyHaveUniqueItems()
            .And.Contain(new[] { johnsonSpouse, williamsChild });

        // Re-importing the identical file changes nothing.
        var createdBefore = h.Coverage.Count;
        var terminationsBefore = h.CoverageTerminations.Count;
        var membersBefore = h.Created.Count;
        await h.ImportAsync(batch);
        h.Coverage.Should().HaveCount(createdBefore);
        h.CoverageTerminations.Should().HaveCount(terminationsBefore);
        h.Created.Should().HaveCount(membersBefore);
        h.Events.AllEvents.Should().HaveCount(7);
    }

    [Fact]
    public async Task HdTermination_EndsTheMatchingCoverage_WithoutCreatingOne()
    {
        var h = new Harness();
        h.Existing.Add("SUB1");
        var dental = h.SeedCoverage("SUB1", "DEN", "EMP", "20250101");
        var health = h.SeedCoverage("SUB1", "HLT", "EMP", "20250101");
        var sub = Subscriber("SUB1", "001");
        sub.Coverage =
        [
            new CoverageDetail
            {
                MaintenanceType = "024", InsuranceLineCode = "DEN", PlanCoverageDescription = "Dental Basic",
                CoverageLevel = "EMP", BenefitBeginDate = "20250101", BenefitEndDate = "20260331"
            }
        ];

        var result = await h.ImportAsync(new Enrollment834 { BatchId = "C1", Enrollments = { sub } });

        h.Coverage.Should().BeEmpty();
        h.CoverageTerminations.Should().ContainSingle().Which.Should().Be((dental.Id, new DateTime(2026, 3, 31)));
        health.TerminationDate.Should().BeNull();
        result.CoverageRecordsTerminated.Should().Be(1);
    }

    [Fact]
    public async Task HdChange_UpdatesTheMatchingCoverage_InsteadOfAddingASecond()
    {
        var h = new Harness();
        h.Existing.Add("SUB1");
        var health = h.SeedCoverage("SUB1", "HLT", "EMP", "20250101");
        var sub = Subscriber("SUB1", "001");
        sub.Coverage =
        [
            new CoverageDetail
            {
                MaintenanceType = "001", InsuranceLineCode = "HLT", PlanCoverageDescription = "PPO",
                CoverageLevel = "FAM", BenefitBeginDate = "20250101"
            }
        ];

        var result = await h.ImportAsync(new Enrollment834 { BatchId = "C2", Enrollments = { sub } });

        h.Coverage.Should().BeEmpty();
        h.CoverageUpdates.Should().ContainSingle(u => u.Id == health.Id && u.Request.CoverageLevel == "FAM");
        result.CoverageRecordsUpdated.Should().Be(1);
    }

    private static MemberEnrollment Reinstatement(string? begin, string? end = null)
    {
        var sub = Subscriber("SUB1", "025");
        sub.EnrollmentDate = null;
        sub.MaintenanceReason = "41";
        sub.Coverage =
        [
            new CoverageDetail
            {
                MaintenanceType = "025", InsuranceLineCode = "HLT", PlanCoverageDescription = "PPO",
                CoverageLevel = "EMP", BenefitBeginDate = begin, BenefitEndDate = end
            }
        ];
        return sub;
    }

    [Fact]
    public async Task Reinstatement_WithoutBenefitBegin_ReinstatesTheTerminatedCoverage()
    {
        // Before: the 025 matched the terminated coverage and only (maybe)
        // updated plan/level, leaving the member terminated.
        var h = new Harness();
        h.Existing.Add("SUB1");
        var health = h.SeedCoverage("SUB1", "HLT", "EMP", "20250101", "20251231");

        var result = await h.ImportAsync(new Enrollment834 { BatchId = "R1", Enrollments = { Reinstatement(begin: null) } });

        h.CoverageReinstatements.Should().Equal(health.Id);
        health.TerminationDate.Should().BeNull();
        health.EffectiveDate.Should().Be(new DateTime(2025, 1, 1));
        h.Coverage.Should().BeEmpty();
        h.CoverageTerminations.Should().BeEmpty();
        result.CoverageRecordsReinstated.Should().Be(1);

        // Replaying the file changes nothing.
        await h.ImportAsync(new Enrollment834 { BatchId = "R1", Enrollments = { Reinstatement(begin: null) } });
        h.CoverageReinstatements.Should().HaveCount(1);
        h.Coverage.Should().BeEmpty();
    }

    [Theory]
    [InlineData("20251001")] // within the old span
    [InlineData("20260101")] // the day after the termination date: no gap
    public async Task Reinstatement_ContinuingTheTerminatedSpan_ReinstatesTheSameRecord(string begin)
    {
        var h = new Harness();
        h.Existing.Add("SUB1");
        var health = h.SeedCoverage("SUB1", "HLT", "EMP", "20250101", "20251231");

        await h.ImportAsync(new Enrollment834 { BatchId = "R2", Enrollments = { Reinstatement(begin) } });

        h.CoverageReinstatements.Should().Equal(health.Id);
        h.Coverage.Should().BeEmpty();
    }

    [Fact]
    public async Task Reinstatement_AfterAGap_CreatesANewSpan_LeavingTheGapUncovered()
    {
        var h = new Harness();
        h.Existing.Add("SUB1");
        var old = h.SeedCoverage("SUB1", "HLT", "EMP", "20250101", "20251231");

        var result = await h.ImportAsync(new Enrollment834 { BatchId = "R3", Enrollments = { Reinstatement("20260301") } });

        h.CoverageReinstatements.Should().BeEmpty();
        old.TerminationDate.Should().Be(new DateTime(2025, 12, 31));
        var created = h.Coverage.Should().ContainSingle().Subject;
        created.EffectiveDate.Should().Be(new DateTime(2026, 3, 1));
        created.TerminationDate.Should().BeNull();
        created.MaintenanceTypeCode.Should().Be("025");
        result.CoverageRecordsCreated.Should().Be(1);

        // Replay: the new span is found by the overlap match.
        await h.ImportAsync(new Enrollment834 { BatchId = "R3", Enrollments = { Reinstatement("20260301") } });
        h.Coverage.Should().HaveCount(1);
        h.CoverageReinstatements.Should().BeEmpty();
    }

    [Fact]
    public async Task Reinstatement_WithBenefitEnd_MovesTheTerminationDate()
    {
        var h = new Harness();
        h.Existing.Add("SUB1");
        var health = h.SeedCoverage("SUB1", "HLT", "EMP", "20250101", "20251231");

        await h.ImportAsync(new Enrollment834 { BatchId = "R4", Enrollments = { Reinstatement(null, "20260630") } });

        h.CoverageTerminations.Should().Equal((health.Id, new DateTime(2026, 6, 30)));
        h.CoverageReinstatements.Should().BeEmpty();
        h.Coverage.Should().BeEmpty();
    }

    [Fact]
    public async Task Reinstatement_OfAFutureDatedTermination_ClearsIt()
    {
        var h = new Harness();
        h.Existing.Add("SUB1");
        var health = h.SeedCoverage("SUB1", "HLT", "EMP", "20250101", "20991231");

        await h.ImportAsync(new Enrollment834 { BatchId = "R5", Enrollments = { Reinstatement(null) } });

        h.CoverageReinstatements.Should().Equal(health.Id);
    }

    [Fact]
    public async Task Reinstatement_OfOpenCoverage_ChangesNothing()
    {
        var h = new Harness();
        h.Existing.Add("SUB1");
        h.SeedCoverage("SUB1", "HLT", "EMP", "20250101");

        await h.ImportAsync(new Enrollment834 { BatchId = "R6", Enrollments = { Reinstatement(null) } });

        h.CoverageReinstatements.Should().BeEmpty();
        h.CoverageTerminations.Should().BeEmpty();
        h.CoverageUpdates.Should().BeEmpty();
        h.Coverage.Should().BeEmpty();
    }

    [Fact]
    public async Task Reinstatement_WithNothingOnFile_CreatesTheCoverage()
    {
        var h = new Harness();
        h.Existing.Add("SUB1");

        await h.ImportAsync(new Enrollment834 { BatchId = "R7", Enrollments = { Reinstatement("20260101") } });

        h.Coverage.Should().ContainSingle().Which.EffectiveDate.Should().Be(new DateTime(2026, 1, 1));
    }

    [Fact]
    public async Task HdAddition_CreatesOnlyWhenNoMatchingCoverageIsOnFile()
    {
        var h = new Harness();
        h.Existing.Add("SUB1");
        h.SeedCoverage("SUB1", "HLT", "EMP", "20250101");
        var sub = Subscriber("SUB1", "001");
        sub.Coverage =
        [
            new CoverageDetail { MaintenanceType = "021", InsuranceLineCode = "HLT", PlanCoverageDescription = "PPO", BenefitBeginDate = "20260101" },
            new CoverageDetail { MaintenanceType = "021", InsuranceLineCode = "VIS", PlanCoverageDescription = "Vision", BenefitBeginDate = "20260101" }
        ];

        await h.ImportAsync(new Enrollment834 { BatchId = "C3", Enrollments = { sub } });

        h.Coverage.Should().ContainSingle(c => c.InsuranceLineCode == "VIS");
    }

    [Fact]
    public async Task Termination_WithUnmappedPlanCode_StillEndsTheOpenCoverageOnThatLine()
    {
        var h = new Harness { ResolvedPlanId = null };
        h.Existing.Add("SUB1");
        var health = h.SeedCoverage("SUB1", "HLT", "EMP", "20250101");
        var sub = Subscriber("SUB1", "024");
        sub.Coverage =
        [
            new CoverageDetail { MaintenanceType = "024", InsuranceLineCode = "HLT", PlanCoverageDescription = "Unmapped", BenefitEndDate = "20260131" }
        ];

        var result = await h.ImportAsync(new Enrollment834 { BatchId = "C4", Enrollments = { sub } });

        result.CoverageMappingsUnresolved.Should().Be(1);
        health.TerminationDate.Should().Be(new DateTime(2026, 1, 31));
    }

    [Fact]
    public async Task MemberTermination_WithoutHdLines_EndsEveryOpenCoverage()
    {
        var h = new Harness();
        h.Existing.Add("SUB1");
        var health = h.SeedCoverage("SUB1", "HLT", "EMP", "20250101");
        var old = h.SeedCoverage("SUB1", "DEN", "EMP", "20230101", termination: "20231231");

        await h.ImportAsync(new Enrollment834 { BatchId = "C5", Enrollments = { Subscriber("SUB1", "024") } });

        health.TerminationDate.Should().Be(new DateTime(2026, 1, 31));
        old.TerminationDate.Should().Be(new DateTime(2023, 12, 31));
        h.Coverage.Should().BeEmpty();
    }

    [Theory]
    [InlineData("T", "Active")]   // TEFRA — not "terminated"
    [InlineData("S", "Active")]   // surviving insured
    [InlineData("A", "Active")]
    [InlineData("C", "COBRA")]
    public async Task BenefitStatus_IsMappedPerX12_TefraIsNotTerminated(string ins05, string expected)
    {
        var h = new Harness();
        h.Existing.Add("SUB1");
        var sub = Subscriber("SUB1", "001");
        sub.BenefitStatus = ins05;

        var result = await h.ImportAsync(new Enrollment834 { BatchId = "S-" + ins05, Enrollments = { sub } });

        result.FailedCount.Should().Be(0);
        h.Updated.Single(u => u.Id == "SUB1").Request.Status.Should().Be(expected);
        h.Terminated.Should().BeEmpty();
    }

    [Fact]
    public async Task EachDependent_GetsItsOwnTransactionRowAndEvent()
    {
        var h = new Harness();
        var spouse = Child("JANE", "19870520", "021", relationship: "01");

        await h.ImportAsync(new Enrollment834
        {
            BatchId = "E1",
            Enrollments = { Subscriber("SUB1", "021", spouse) }
        });

        var spouseId = DependentId(spouse)!;
        h.Transactions.Should().ContainSingle(t => t.MemberId == spouseId && t.SubscriberId == "SUB1" && t.Status == "Accepted");
        var evt = h.Events.AllEvents.Single(e => e.MemberId == spouseId);
        evt.EventType.Should().Be(EnrollmentEventType.Enrolled);
        evt.Payload!["relationship"]!.GetValue<string>().Should().Be("01");
        evt.Payload["subscriberId"]!.GetValue<string>().Should().Be("SUB1");
        h.Events.AllEvents.Should().HaveCount(2);
    }

    [Fact]
    public async Task LegacyLsLeDependentBlock_IsReportedOnTheRunErrors_NotSilentlyDropped()
    {
        const string legacy = """
            ISA*00*          *00*          *ZZ*SPONSOR123     *ZZ*PAYER456       *260206*1200*^*00501*000000001*0*P*:~
            ST*834*0001*005010X220A1~
            INS*Y*18*021*28*A~
            REF*0F*SUB1~
            DTP*303*D8*20260201~
            NM1*IL*1*SMITH*JOHN~
            HD*021**HLT*PPO*FAM~
            LS*2700~
            NM1*70*1*SMITH*JANE~
            DMG*D8*19870520*F~
            HD*021**HLT*PPO~
            LE*2700~
            SE*12*0001~
            """;
        var batch = new Enrollment834EdiParser().Parse(legacy, "legacy.edi");
        batch.BatchId = "L1";

        batch.ParseWarnings.Should().ContainSingle().Which.Should().Contain("LS...LE");
        batch.Enrollments.Single().Coverage.Should().ContainSingle(); // the LS-block HD is not the subscriber's

        var result = await new Harness().ImportAsync(batch);

        result.Errors.Should().Contain(e => e.Contains("NOT imported"));
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
