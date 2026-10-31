using EnrollmentImportService.Clients;
using EnrollmentImportService.Models;
using EnrollmentImportService.Services;
using EnrollmentImportService.Services.Cpcds;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ImportSvc = EnrollmentImportService.Services.EnrollmentImportService;

namespace EnrollmentImportService.Tests.Services.Cpcds;

public class CpcdsEnrollmentProjectorTests
{
    private static string Sample(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "cloudhealthoffice-main.sln")))
            dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "docs/integration/samples/cpcds", file));
    }

    private static CpcdsEnrollmentProjector.Projection ProjectSample()
        => CpcdsEnrollmentProjector.Project(Sample("members.csv"), Sample("coverages.csv"), "members.csv");

    [Fact]
    public void Every_member_is_its_own_enrollment_under_the_cores_member_id()
    {
        var projection = ProjectSample();

        projection.FileErrors.Should().BeEmpty();
        projection.RowErrors.Should().BeEmpty();
        projection.Enrollment.Enrollments.Select(e => e.ExplicitMemberId)
            .Should().Equal("M100", "M101", "M200", "M300");
        projection.Enrollment.Enrollments.Should().OnlyContain(e => e.MaintenanceType == "001" && e.BenefitStatus == "A");
    }

    [Fact]
    public void A_dependent_keeps_its_own_id_and_names_its_subscriber_and_relationship()
    {
        var child = ProjectSample().Enrollment.Enrollments.Single(e => e.ExplicitMemberId == "M101");

        child.SubscriberId.Should().Be("M100");
        child.Relationship.Should().Be("19");
        child.Demographics!.FirstName.Should().Be("Jordan");
        child.Demographics.DateOfBirth.Should().Be("20090930");
        child.Demographics.Gender.Should().Be("M");
    }

    [Fact]
    public void Coverage_rows_become_coverage_lines_with_plan_code_group_and_dates()
    {
        var projection = ProjectSample();
        var m200 = projection.Enrollment.Enrollments.Single(e => e.ExplicitMemberId == "M200");

        m200.GroupNumber.Should().Be("GRP-CITY");
        m200.Sponsor!.Id.Should().Be("GRP-CITY");
        m200.Sponsor.Name.Should().Be("City of Eugene");
        var line = m200.Coverage.Single();
        line.InsuranceLineCode.Should().Be("HLT");
        line.PlanCoverageDescription.Should().Be("HMO-SILVER");
        line.BenefitBeginDate.Should().Be("20250701");
        line.BenefitEndDate.Should().Be("20261231");
        line.MaintenanceType.Should().Be("001");
        m200.EnrollmentDate.Should().Be("20250701");

        projection.Enrollment.Enrollments.Single(e => e.ExplicitMemberId == "M300").Coverage.Single()
            .InsuranceLineCode.Should().Be("DEN");
    }

    [Theory]
    [InlineData("Lopez, Avery J", "Avery", "J", "Lopez")]
    [InlineData("Avery Lopez", "Avery", null, "Lopez")]
    [InlineData("Avery June Lopez", "Avery", "June", "Lopez")]
    public void A_single_name_column_is_split(string name, string first, string? middle, string last)
    {
        var projection = CpcdsEnrollmentProjector.Project($"Member id,Name\nM1,\"{name}\"\n", null, "m.csv");

        var demographics = projection.Enrollment.Enrollments.Single().Demographics!;
        (demographics.FirstName, demographics.MiddleName, demographics.LastName).Should().Be((first, middle, last));
    }

    [Fact]
    public void A_member_without_a_full_name_is_refused_and_the_rest_load()
    {
        var projection = CpcdsEnrollmentProjector.Project("Member id,Name\nM1,Cher\nM2,Ann Lee\n", null, "m.csv");

        projection.RowErrors.Should().ContainSingle().Which.Should().Contain("M1");
        projection.Enrollment.Enrollments.Should().ContainSingle(e => e.ExplicitMemberId == "M2");
    }

    [Fact]
    public void Cancelled_coverage_is_skipped_and_orphan_coverage_reported()
    {
        var coverages = "Member id,Coverage status,Start date,Group id,Plan identifier\n" +
                        "M1,cancelled,2026-01-01,G,P\nM9,active,2026-01-01,G,P\n";

        var projection = CpcdsEnrollmentProjector.Project("Member id,First name,Last name\nM1,Ann,Lee\n", coverages, "m.csv");

        projection.Enrollment.Enrollments.Single().Coverage.Should().BeEmpty();
        projection.Warnings.Should().ContainSingle().Which.Should().Contain("cancelled");
        projection.RowErrors.Should().ContainSingle().Which.Should().Contain("M9");
    }

    [Fact]
    public void Files_missing_required_columns_are_file_errors()
    {
        var projection = CpcdsEnrollmentProjector.Project("First name,Last name\nAnn,Lee\n", "Member id\nM1\n", "m.csv");

        projection.FileErrors.Should().Contain(e => e.Contains("member file") && e.Contains("\"Member id\""));
        projection.FileErrors.Should().Contain(e => e.Contains("coverage file") && e.Contains("\"Plan identifier\""));
    }

    [Fact]
    public async Task Through_the_834_import_members_are_created_under_the_cores_ids_and_dependents_are_not_subscribers()
    {
        var created = new List<CreateMemberRequestDto>();
        var memberClient = new Mock<IMemberServiceClient>();
        memberClient.Setup(m => m.ExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        memberClient.Setup(m => m.CreateAsync(It.IsAny<string>(), It.IsAny<CreateMemberRequestDto>(), It.IsAny<CancellationToken>()))
            .Callback<string, CreateMemberRequestDto, CancellationToken>((_, dto, _) => created.Add(dto))
            .Returns(Task.CompletedTask);
        var sponsorClient = new Mock<ISponsorServiceClient>();
        sponsorClient.Setup(s => s.ResolveIdentifierAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SponsorIdentifier(SponsorIdentifierKind.Unknown));
        var benefitPlanClient = new Mock<IBenefitPlanServiceClient>();
        benefitPlanClient.Setup(b => b.ResolvePlanIdAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("plan-1");
        var coverageClient = new Mock<ICoverageServiceClient>();
        coverageClient.Setup(c => c.GetMemberCoverageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        coverageClient.Setup(c => c.CreateAsync(It.IsAny<string>(), It.IsAny<CreateCoverageRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("cov-1");
        var txns = new Mock<IEnrollmentTransactionRepository>();
        txns.Setup(t => t.CreateAsync(It.IsAny<EnrollmentTransaction>())).ReturnsAsync((EnrollmentTransaction t) => t);
        var runs = new Mock<IEnrollmentImportRunRepository>();
        runs.Setup(r => r.CreateAsync(It.IsAny<EnrollmentImportRun>())).ReturnsAsync((EnrollmentImportRun r) => r);
        var svc = new ImportSvc(
            memberClient.Object, sponsorClient.Object, benefitPlanClient.Object, coverageClient.Object,
            txns.Object, runs.Object,
            new EnrollmentEventPublisher(new InMemoryEnrollmentEventRepository(), NullLogger<EnrollmentEventPublisher>.Instance),
            new EnrollmentValidator(), NullLogger<ImportSvc>.Instance);

        var result = await svc.ImportEnrollmentAsync(ProjectSample().Enrollment, "t1");

        result.Errors.Should().BeEmpty();
        result.MembersCreated.Should().Be(4);
        created.Select(c => (c.MemberId, c.IsSubscriber)).Should().BeEquivalentTo(new[]
        {
            ("M100", true), ("M101", false), ("M200", true), ("M300", true),
        });
        result.CoverageRecordsCreated.Should().Be(4);
    }
}
