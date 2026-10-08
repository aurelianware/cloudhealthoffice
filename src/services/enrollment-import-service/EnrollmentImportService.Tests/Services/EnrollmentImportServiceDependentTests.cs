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
        public bool ReturnCreatedCoverageId { get; set; } = true;
        public string? ThrowOnExistsFor { get; set; }
        /// <summary>coverage-service fails the reinstate call for this coverage id.</summary>
        public string? FailReinstateFor { get; set; }
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
                .ReturnsAsync((string _, string id, CancellationToken _) =>
                    id == ThrowOnExistsFor
                        ? throw new HttpRequestException("member-service unavailable")
                        : Existing.Contains(id));
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
                        TerminationDate = r.TerminationDate,
                        MaintenanceTypeCode = r.MaintenanceTypeCode
                    });
                })
                .ReturnsAsync(() => ReturnCreatedCoverageId ? StoredCoverage[^1].Id : null);
            coverageClient.Setup(c => c.GetMemberCoverageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, string memberId, CancellationToken _) =>
                    StoredCoverage.Where(s => s.MemberId == memberId)
                        .Select(s => new CoverageRecordDto
                        {
                            Id = s.Id, MemberId = s.MemberId, GroupNumber = s.GroupNumber, PlanId = s.PlanId,
                            InsuranceLineCode = s.InsuranceLineCode, CoverageLevel = s.CoverageLevel,
                            EffectiveDate = s.EffectiveDate, TerminationDate = s.TerminationDate,
                            MaintenanceTypeCode = s.MaintenanceTypeCode,
                            ReinstatedTerminationDate = s.ReinstatedTerminationDate
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
                .Returns((string _, string id, string? _, CancellationToken _) =>
                {
                    if (id == FailReinstateFor)
                    {
                        throw new HttpRequestException("coverage-service unavailable");
                    }
                    CoverageReinstatements.Add(id);
                    var s = StoredCoverage.Single(x => x.Id == id);
                    if (s.TerminationDate is not null) s.ReinstatedTerminationDate = s.TerminationDate;
                    s.TerminationDate = null;
                    s.MaintenanceTypeCode = "025";
                    return Task.CompletedTask;
                });

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
    public async Task Reinstatement_WithALevelChangeFromItsBeginDate_OpensANewSpan_LeavingTheOldOnItsLevel()
    {
        // Same rule as an effective-dated 001 change: earlier dates of service
        // stay on the earlier coverage level.
        var h = new Harness();
        h.Existing.Add("SUB1");
        var old = h.SeedCoverage("SUB1", "HLT", "EMP", "20250101", "20251231");
        var sub = Reinstatement("20260101");
        sub.Coverage[0].CoverageLevel = "FAM";

        await h.ImportAsync(new Enrollment834 { BatchId = "R8", Enrollments = { sub } });

        h.CoverageReinstatements.Should().BeEmpty();
        h.CoverageUpdates.Should().BeEmpty();
        h.CoverageTerminations.Should().BeEmpty("the old span already ends the day before");
        old.TerminationDate.Should().Be(new DateTime(2025, 12, 31));
        var created = h.Coverage.Should().ContainSingle().Subject;
        created.EffectiveDate.Should().Be(new DateTime(2026, 1, 1));
        created.CoverageLevel.Should().Be("FAM");

        await h.ImportAsync(new Enrollment834 { BatchId = "R8", Enrollments = { sub } });
        h.Coverage.Should().HaveCount(1);
    }

    [Fact]
    public async Task Reinstatement_WithNothingOnFile_CreatesTheCoverage()
    {
        var h = new Harness();
        h.Existing.Add("SUB1");

        await h.ImportAsync(new Enrollment834 { BatchId = "R7", Enrollments = { Reinstatement("20260101") } });

        h.Coverage.Should().ContainSingle().Which.EffectiveDate.Should().Be(new DateTime(2026, 1, 1));
    }

    private static MemberEnrollment MemberReinstatement(string? begin)
    {
        var sub = Subscriber("SUB1", "025");
        sub.EnrollmentDate = begin;
        sub.MaintenanceReason = "41";
        sub.Coverage = [];
        return sub;
    }

    [Fact]
    public async Task MemberReinstatement_WithoutHd_ReinstatesTheCoveragesEndedByTheLastTermination()
    {
        var h = new Harness();
        h.Existing.Add("SUB1");
        // A member-level 024 ended health and dental together; vision had
        // ended earlier on its own and must stay ended.
        var health = h.SeedCoverage("SUB1", "HLT", "EMP", "20250101", "20251231");
        var dental = h.SeedCoverage("SUB1", "DEN", "EMP", "20250101", "20251231");
        var vision = h.SeedCoverage("SUB1", "VIS", "EMP", "20240101", "20240630");
        var olderHealth = h.SeedCoverage("SUB1", "HLT", "EMP", "20230101", "20231231");

        var result = await h.ImportAsync(new Enrollment834 { BatchId = "M1", Enrollments = { MemberReinstatement(null) } });

        h.CoverageReinstatements.Should().BeEquivalentTo(health.Id, dental.Id);
        vision.TerminationDate.Should().Be(new DateTime(2024, 6, 30));
        olderHealth.TerminationDate.Should().Be(new DateTime(2023, 12, 31));
        h.Coverage.Should().BeEmpty();
        result.CoverageRecordsReinstated.Should().Be(2);

        // Replay: each line's latest coverage is open now; the older
        // terminations are not picked up as "the last one".
        await h.ImportAsync(new Enrollment834 { BatchId = "M1", Enrollments = { MemberReinstatement(null) } });
        h.CoverageReinstatements.Should().HaveCount(2);
        h.Coverage.Should().BeEmpty();
    }

    [Fact]
    public async Task MemberReinstatement_WithoutHd_ContinuingTheSpan_ReinstatesTheSameRecord()
    {
        var h = new Harness();
        h.Existing.Add("SUB1");
        var health = h.SeedCoverage("SUB1", "HLT", "EMP", "20250101", "20251231");

        await h.ImportAsync(new Enrollment834 { BatchId = "M2", Enrollments = { MemberReinstatement("20260101") } });

        h.CoverageReinstatements.Should().Equal(health.Id);
        h.Coverage.Should().BeEmpty();
    }

    [Fact]
    public async Task MemberReinstatement_WithoutHd_AfterAGap_CreatesNewSpans()
    {
        var h = new Harness();
        h.Existing.Add("SUB1");
        var health = h.SeedCoverage("SUB1", "HLT", "FAM", "20250101", "20251231");
        h.SeedCoverage("SUB1", "DEN", "EMP", "20250101", "20251231");

        var result = await h.ImportAsync(new Enrollment834 { BatchId = "M3", Enrollments = { MemberReinstatement("20260301") } });

        h.CoverageReinstatements.Should().BeEmpty();
        health.TerminationDate.Should().Be(new DateTime(2025, 12, 31));
        h.Coverage.Should().HaveCount(2).And.OnlyContain(c =>
            c.EffectiveDate == new DateTime(2026, 3, 1) && c.TerminationDate == null
            && c.MaintenanceTypeCode == "025" && c.PlanId == "resolved-plan-id" && c.GroupNumber == "GRP0001");
        h.Coverage.Single(c => c.InsuranceLineCode == "HLT").CoverageLevel.Should().Be("FAM");
        result.CoverageRecordsCreated.Should().Be(2);

        // Replay: the new spans are each line's latest coverage, and open.
        await h.ImportAsync(new Enrollment834 { BatchId = "M3", Enrollments = { MemberReinstatement("20260301") } });
        h.Coverage.Should().HaveCount(2);
    }

    [Fact]
    public async Task MemberReinstatement_WithoutHd_NothingTerminated_ChangesNothing()
    {
        var h = new Harness();
        h.Existing.Add("SUB1");
        h.SeedCoverage("SUB1", "HLT", "EMP", "20250101");

        await h.ImportAsync(new Enrollment834 { BatchId = "M4", Enrollments = { MemberReinstatement(null) } });

        h.CoverageReinstatements.Should().BeEmpty();
        h.Coverage.Should().BeEmpty();
    }

    [Fact]
    public async Task MemberReinstatement_StoppedPartWay_IsFinishedByReplayingTheFile()
    {
        var h = new Harness();
        h.Existing.Add("SUB1");
        // A member-level 024 ended health and dental together; vision ended earlier on its own.
        var health = h.SeedCoverage("SUB1", "HLT", "EMP", "20250101", "20251231");
        var dental = h.SeedCoverage("SUB1", "DEN", "EMP", "20250101", "20251231");
        var vision = h.SeedCoverage("SUB1", "VIS", "EMP", "20240101", "20240630");

        // First run: health is reinstated, then the dental call fails.
        h.FailReinstateFor = dental.Id;
        var first = await h.ImportAsync(new Enrollment834 { BatchId = "M5", Enrollments = { MemberReinstatement(null) } });
        first.FailedCount.Should().Be(1);
        health.TerminationDate.Should().BeNull();
        dental.TerminationDate.Should().Be(new DateTime(2025, 12, 31));

        // Replay: health is open, but it says it reversed the 2025-12-31
        // termination, so dental (ended that same day) is finished; vision is not.
        h.FailReinstateFor = null;
        var replay = await h.ImportAsync(new Enrollment834 { BatchId = "M5", Enrollments = { MemberReinstatement(null) } });
        replay.FailedCount.Should().Be(0);
        h.CoverageReinstatements.Should().Equal(health.Id, dental.Id);
        dental.TerminationDate.Should().BeNull();
        vision.TerminationDate.Should().Be(new DateTime(2024, 6, 30));

        // A further replay changes nothing: vision's older termination is not the one reversed.
        await h.ImportAsync(new Enrollment834 { BatchId = "M5", Enrollments = { MemberReinstatement(null) } });
        h.CoverageReinstatements.Should().HaveCount(2);
        vision.TerminationDate.Should().Be(new DateTime(2024, 6, 30));
        h.Coverage.Should().BeEmpty();
    }

    [Fact]
    public async Task MemberReinstatement_AfterAGap_StoppedPartWay_IsFinishedByReplay()
    {
        var h = new Harness();
        h.Existing.Add("SUB1");
        // State a run leaves when it stopped after health's new span was
        // created and before dental's: both old spans end 2025-12-31.
        h.SeedCoverage("SUB1", "HLT", "FAM", "20250101", "20251231");
        h.SeedCoverage("SUB1", "DEN", "EMP", "20250101", "20251231");
        h.SeedCoverage("SUB1", "VIS", "EMP", "20240101", "20240630");
        h.SeedCoverage("SUB1", "HLT", "FAM", "20260301").MaintenanceTypeCode = "025";

        await h.ImportAsync(new Enrollment834 { BatchId = "M6", Enrollments = { MemberReinstatement("20260301") } });

        var created = h.Coverage.Should().ContainSingle().Subject;
        created.InsuranceLineCode.Should().Be("DEN");
        created.EffectiveDate.Should().Be(new DateTime(2026, 3, 1));
        h.CoverageReinstatements.Should().BeEmpty();
        h.StoredCoverage.Single(c => c.InsuranceLineCode == "VIS").TerminationDate.Should().Be(new DateTime(2024, 6, 30));

        await h.ImportAsync(new Enrollment834 { BatchId = "M6", Enrollments = { MemberReinstatement("20260301") } });
        h.Coverage.Should().HaveCount(1);
    }

    [Fact]
    public async Task Reinstatement_OpenEnded_WithALaterSpanOnFile_ReinstatesThePredecessor_BoundedByTheLaterSpan()
    {
        var h = new Harness();
        h.Existing.Add("SUB1");
        var first = h.SeedCoverage("SUB1", "HLT", "EMP", "20250101", "20250630");
        var later = h.SeedCoverage("SUB1", "HLT", "EMP", "20251001", "20251231");

        await h.ImportAsync(new Enrollment834 { BatchId = "R9", Enrollments = { Reinstatement("20250701") } });

        // The later span is untouched; the predecessor continues up to it.
        later.EffectiveDate.Should().Be(new DateTime(2025, 10, 1));
        later.TerminationDate.Should().Be(new DateTime(2025, 12, 31));
        first.TerminationDate.Should().Be(new DateTime(2025, 9, 30));
        h.CoverageTerminations.Should().Equal((first.Id, new DateTime(2025, 9, 30)));
        h.CoverageReinstatements.Should().BeEmpty();
        h.Coverage.Should().BeEmpty();

        await h.ImportAsync(new Enrollment834 { BatchId = "R9", Enrollments = { Reinstatement("20250701") } });
        h.CoverageTerminations.Should().HaveCount(1);
        h.Coverage.Should().BeEmpty();
    }

    [Fact]
    public async Task Reinstatement_Bounded_WithALaterSpanOnFile_ExtendsThePredecessor_NotTheLaterSpan()
    {
        var h = new Harness();
        h.Existing.Add("SUB1");
        var first = h.SeedCoverage("SUB1", "HLT", "EMP", "20250101", "20250630");
        var later = h.SeedCoverage("SUB1", "HLT", "EMP", "20251001", "20251231");

        await h.ImportAsync(new Enrollment834 { BatchId = "R10", Enrollments = { Reinstatement("20250701", "20250731") } });

        first.TerminationDate.Should().Be(new DateTime(2025, 7, 31));
        later.TerminationDate.Should().Be(new DateTime(2025, 12, 31));
        later.EffectiveDate.Should().Be(new DateTime(2025, 10, 1));
        h.CoverageTerminations.Should().Equal((first.Id, new DateTime(2025, 7, 31)));
        h.Coverage.Should().BeEmpty();

        await h.ImportAsync(new Enrollment834 { BatchId = "R10", Enrollments = { Reinstatement("20250701", "20250731") } });
        h.CoverageTerminations.Should().HaveCount(1);
    }

    [Fact]
    public async Task Reinstatement_AfterAGap_WithoutHd05_KeepsThePredecessorsCoverageLevel()
    {
        var h = new Harness();
        h.Existing.Add("SUB1");
        h.SeedCoverage("SUB1", "HLT", "FAM", "20250101", "20251231");
        var sub = Reinstatement("20260301");
        sub.Coverage[0].CoverageLevel = null;

        await h.ImportAsync(new Enrollment834 { BatchId = "R11", Enrollments = { sub } });

        var created = h.Coverage.Should().ContainSingle().Subject;
        created.EffectiveDate.Should().Be(new DateTime(2026, 3, 1));
        created.CoverageLevel.Should().Be("FAM");
    }

    [Theory]
    [InlineData(null)]          // predecessor open-ended
    [InlineData("20261231")]    // predecessor future-terminated
    public async Task Reinstatement_ChangingPlan_ClosesThePredecessorOnTheSameLine(string? predecessorEnd)
    {
        var h = new Harness();
        h.Existing.Add("SUB1");
        var planA = h.SeedCoverage("SUB1", "HLT", "EMP", "20250101", predecessorEnd);
        planA.PlanId = "plan-A";

        await h.ImportAsync(new Enrollment834 { BatchId = "R12", Enrollments = { Reinstatement("20260301") } });

        planA.PlanId.Should().Be("plan-A");
        planA.TerminationDate.Should().Be(new DateTime(2026, 2, 28));
        var created = h.Coverage.Should().ContainSingle().Subject;
        created.PlanId.Should().Be("resolved-plan-id");
        created.EffectiveDate.Should().Be(new DateTime(2026, 3, 1));
        created.TerminationDate.Should().BeNull();
        h.CoverageUpdates.Should().BeEmpty();

        // Replay: plan B's span is found; plan A is no longer open on the begin date.
        await h.ImportAsync(new Enrollment834 { BatchId = "R12", Enrollments = { Reinstatement("20260301") } });
        h.Coverage.Should().HaveCount(1);
        h.CoverageTerminations.Should().HaveCount(1);
        planA.TerminationDate.Should().Be(new DateTime(2026, 2, 28));
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

    // ── PR #1239 review follow-ups ──────────────────────────────────────

    private static CoverageDetail Hd(string maint, string line, string? begin = null, string? end = null, string? level = null) => new()
    {
        MaintenanceType = maint,
        InsuranceLineCode = line,
        PlanCoverageDescription = "PLAN",
        CoverageLevel = level,
        BenefitBeginDate = begin,
        BenefitEndDate = end
    };

    [Fact]
    public async Task DependentWithoutRef0F_IsNotAttachedToThePrecedingSubscriber_AndIsRejected()
    {
        const string edi = """
            ISA*00*          *00*          *ZZ*SPONSOR123     *ZZ*PAYER456       *260206*1200*^*00501*000000001*0*P*:~
            ST*834*0001*005010X220A1~
            INS*Y*18*001*AI*A~
            REF*0F*SUB1~
            NM1*IL*1*SMITH*JOHN~
            INS*N*19*021*28*A~
            NM1*IL*1*STRANGER*KID~
            DMG*D8*20150610*M~
            SE*8*0001~
            """;
        var batch = new Enrollment834EdiParser().Parse(edi, "x.edi");
        batch.BatchId = "R1";

        batch.Enrollments.Single().Dependents.Should().BeEmpty();
        batch.DependentEnrollments.Should().ContainSingle(d => d.SubscriberId == null);

        var h = new Harness();
        h.Existing.Add("SUB1");
        var result = await h.ImportAsync(batch);

        result.FailedCount.Should().Be(1);
        h.Created.Should().BeEmpty();
        h.Transactions.Should().Contain(t => t.Status == "Rejected" && t.MemberName.Contains("STRANGER"));
    }

    [Fact]
    public async Task UnmappedHd024_OnAMemberThatStays_IsAmbiguousWhenTwoPoliciesAreOpen()
    {
        var h = new Harness { ResolvedPlanId = null };
        h.Existing.Add("SUB1");
        var a = h.SeedCoverage("SUB1", "HLT", "EMP", "20250101");
        var b = h.SeedCoverage("SUB1", "HLT", "EMP", "20250601");
        var sub = Subscriber("SUB1", "001");
        sub.Coverage = [Hd("024", "HLT", end: "20260131")];

        var result = await h.ImportAsync(new Enrollment834 { BatchId = "R2", Enrollments = { sub } });

        a.TerminationDate.Should().BeNull();
        b.TerminationDate.Should().BeNull();
        result.Errors.Should().Contain(e => e.Contains("ambiguous"));
    }

    [Fact]
    public async Task UnmappedHd024_OnAMemberThatStays_EndsTheSingleOpenPolicy()
    {
        var h = new Harness { ResolvedPlanId = null };
        h.Existing.Add("SUB1");
        var a = h.SeedCoverage("SUB1", "HLT", "EMP", "20250101");
        var sub = Subscriber("SUB1", "001");
        sub.Coverage = [Hd("024", "HLT", end: "20260131")];

        await h.ImportAsync(new Enrollment834 { BatchId = "R3", Enrollments = { sub } });

        a.TerminationDate.Should().Be(new DateTime(2026, 1, 31));
    }

    [Fact]
    public async Task Termination_WithoutDtp349_IsBoundedByTheTerminationDate_NotTheMaintenanceDate()
    {
        // January span already ends Jan 31; a later February span of the same
        // plan must not be picked (the member's DTP*303 is Feb 1).
        var h = new Harness();
        h.Existing.Add("SUB1");
        var jan = h.SeedCoverage("SUB1", "HLT", "EMP", "20250101", termination: "20260131");
        var feb = h.SeedCoverage("SUB1", "HLT", "EMP", "20260201");
        var sub = Subscriber("SUB1", "024");          // TerminationDate 20260131, EnrollmentDate 20260201
        sub.Coverage = [Hd("024", "HLT")];

        await h.ImportAsync(new Enrollment834 { BatchId = "R4", Enrollments = { sub } });

        feb.TerminationDate.Should().BeNull();
        jan.TerminationDate.Should().Be(new DateTime(2026, 1, 31));
        h.CoverageTerminations.Should().BeEmpty(); // Jan span already ends then — replay-safe no-op
    }

    [Fact]
    public async Task Hd024_WithNoTerminationDateAnywhere_IsReportedNotDatedToday()
    {
        var h = new Harness();
        h.Existing.Add("SUB1");
        var a = h.SeedCoverage("SUB1", "HLT", "EMP", "20250101");
        var sub = Subscriber("SUB1", "001");
        sub.Coverage = [Hd("024", "HLT")];

        var result = await h.ImportAsync(new Enrollment834 { BatchId = "R5", Enrollments = { sub } });

        a.TerminationDate.Should().BeNull();
        result.Errors.Should().Contain(e => e.Contains("DTP*349"));
    }

    [Fact]
    public async Task PlanChange_EffectiveLater_KeepsTheEarlierSpanOnTheOldPlan()
    {
        var h = new Harness { ResolvedPlanId = "plan-B" };
        h.Existing.Add("SUB1");
        var planA = h.SeedCoverage("SUB1", "HLT", "EMP", "20260101");
        var sub = Subscriber("SUB1", "001");
        sub.Coverage = [Hd("001", "HLT", begin: "20260301")];

        await h.ImportAsync(new Enrollment834 { BatchId = "R6", Enrollments = { sub } });

        h.CoverageUpdates.Should().BeEmpty();
        planA.PlanId.Should().Be("resolved-plan-id");
        planA.TerminationDate.Should().Be(new DateTime(2026, 2, 28));
        h.Coverage.Should().ContainSingle(c => c.PlanId == "plan-B" && c.EffectiveDate == new DateTime(2026, 3, 1));
    }

    [Fact]
    public async Task RetroPlanChange_MatchesTheCoverageOpenOnTheChangeDate_NotOnTheImportDate()
    {
        var h = new Harness { ResolvedPlanId = "plan-B" };
        h.Existing.Add("SUB1");
        var planA = h.SeedCoverage("SUB1", "HLT", "EMP", "20250101", termination: "20250331");
        var sub = Subscriber("SUB1", "001");
        sub.Coverage = [Hd("001", "HLT", begin: "20250301")];

        await h.ImportAsync(new Enrollment834 { BatchId = "R7", Enrollments = { sub } });

        planA.TerminationDate.Should().Be(new DateTime(2025, 2, 28));
        h.Coverage.Should().ContainSingle();
        h.Coverage[0].EffectiveDate.Should().Be(new DateTime(2025, 3, 1));
        h.Coverage[0].TerminationDate.Should().Be(new DateTime(2025, 3, 31));
    }

    [Fact]
    public async Task AddThenTerminate_InOneMemberLoop_ActsOnTheJustCreatedCoverage()
    {
        var h = new Harness();
        h.Existing.Add("SUB1");
        var sub = Subscriber("SUB1", "001");
        sub.Coverage = [Hd("021", "HLT", begin: "20260101"), Hd("024", "HLT", end: "20260331")];

        await h.ImportAsync(new Enrollment834 { BatchId = "R8", Enrollments = { sub } });

        h.Coverage.Should().ContainSingle();
        h.StoredCoverage.Single().TerminationDate.Should().Be(new DateTime(2026, 3, 31));
    }

    [Fact]
    public async Task AddThenChange_InOneMemberLoop_ActsOnTheJustCreatedCoverage()
    {
        var h = new Harness();
        h.Existing.Add("SUB1");
        var sub = Subscriber("SUB1", "001");
        sub.Coverage = [Hd("021", "HLT", begin: "20260101", level: "EMP"), Hd("001", "HLT", begin: "20260101", level: "FAM")];

        await h.ImportAsync(new Enrollment834 { BatchId = "R9", Enrollments = { sub } });

        h.Coverage.Should().ContainSingle();
        h.StoredCoverage.Single().CoverageLevel.Should().Be("FAM");
    }

    [Fact]
    public async Task AddThenTerminate_WhenCoverageServiceReturnsNoId_IsReportedNotDropped()
    {
        var h = new Harness { ReturnCreatedCoverageId = false };
        h.Existing.Add("SUB1");
        var sub = Subscriber("SUB1", "001");
        sub.Coverage = [Hd("021", "HLT", begin: "20260101"), Hd("024", "HLT", end: "20260331")];

        var result = await h.ImportAsync(new Enrollment834 { BatchId = "R10", Enrollments = { sub } });

        result.Errors.Should().Contain(e => e.Contains("no id from coverage-service"));
    }

    [Fact]
    public async Task DependentTermination_WithoutAnyTerminationDate_IsRejectedBeforeWriting()
    {
        var h = new Harness();
        var child = Child("EMMA", "20120304", "024");
        h.Existing.Add("SUB1");
        h.Existing.Add(DependentId(child)!);
        var cov = h.SeedCoverage(DependentId(child)!, "HLT", "ECH", "20250101");

        var result = await h.ImportAsync(new Enrollment834
        {
            BatchId = "R11",
            Enrollments = { Subscriber("SUB1", "001", child) }
        });

        h.Terminated.Should().BeEmpty();
        cov.TerminationDate.Should().BeNull();
        result.DependentsFailed.Should().Be(1);
        h.Transactions.Should().Contain(t => t.MemberId == DependentId(child) && t.Status == "Rejected");
        h.Events.AllEvents.Should().NotContain(e => e.MemberId == DependentId(child));
    }

    [Fact]
    public async Task DependentWithUnsupportedMaintenanceType_IsRejected_NoEvent()
    {
        var h = new Harness();
        var child = Child("EMMA", "20120304", "999");
        h.Existing.Add("SUB1");

        var result = await h.ImportAsync(new Enrollment834
        {
            BatchId = "R12",
            Enrollments = { Subscriber("SUB1", "001", child) }
        });

        result.DependentsFailed.Should().Be(1);
        h.Transactions.Should().Contain(t => t.MemberId == DependentId(child) && t.Status == "Rejected");
        h.Events.AllEvents.Should().NotContain(e => e.MemberId == DependentId(child));
        result.Errors.Should().Contain(e => e.Contains("999"));
    }

    [Fact]
    public async Task StandaloneDependent_SubscriberLookupFailure_RecordsARejectedRowForTheDependent()
    {
        var h = new Harness { ThrowOnExistsFor = "SUB1" };
        var baby = Child("BABY", "20260115", "021");

        var result = await h.ImportAsync(new Enrollment834 { BatchId = "R13", DependentEnrollments = { baby } });

        result.FailedCount.Should().Be(1);
        h.Transactions.Should().ContainSingle(t => t.MemberId == DependentId(baby) && t.Status == "Rejected");
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
