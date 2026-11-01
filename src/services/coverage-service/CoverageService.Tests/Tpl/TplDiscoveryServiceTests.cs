using CoverageService.Models.Tpl;
using CoverageService.Repositories;
using CoverageService.Services.Tpl;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoverageService.Tests.Tpl;

public class TplDiscoveryServiceTests
{
    private const string Tenant = "t1";
    private readonly InMemoryTplRepository _repo = new();
    private readonly Mock<ICoverageRepository> _coverage = new();

    public TplDiscoveryServiceTests()
    {
        _coverage.Setup(c => c.GetKnownMemberIdsAsync(Tenant, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IReadOnlyCollection<string> ids, CancellationToken _) =>
                (IReadOnlySet<string>)ids.Where(i => i == "M1").ToHashSet());
    }

    private TplDiscoveryService Service() => new(_repo, _coverage.Object, Options.Create(new TplReportingOptions()));

    private static TplDiscoveryRequest Request(string state = "CA", string coverage = "K", DateTime? discovered = null) => new(
        state, "Kaiser", "K001", "POL9", null, "Pat Holder", coverage,
        new DateTime(2026, 9, 1), null, discovered ?? new DateTime(2026, 10, 20), "Provider EOB");

    [Fact]
    public async Task California_Discovery_IsQueuedWithATenDayClock()
    {
        var p = await Service().RecordDiscoveryAsync(Tenant, "M1", Request(), "ops");

        p.Source.Should().Be(TplImportService.PlanDiscoveredSource);
        p.Scope.Should().Be(TplScope.Comprehensive);
        p.StateCoverageCode.Should().Be("K");
        p.PendingStateAction.Should().Be(TplStateAction.Add);
        p.StateReportDueBy.Should().Be(new DateTime(2026, 10, 30));
        p.IsActiveOn(new DateTime(2026, 10, 1)).Should().BeTrue("it counts for cost avoidance at once");
    }

    [Fact]
    public async Task Texas_Discovery_HasNoClockButIsListed()
    {
        var p = await Service().RecordDiscoveryAsync(Tenant, "M1", Request(state: "TX", coverage: "MAJOR MEDICAL"), "ops");

        p.StateReportDueBy.Should().BeNull();
        p.Scope.Should().Be(TplScope.Medical);
        (await Service().BuildStateReportCsvAsync(Tenant, "TX", false, DateTime.UtcNow)).Should().Contain("POL9");
    }

    [Fact]
    public async Task ScopeNames_AreAcceptedWhenNoStateCode()
    {
        var p = await Service().RecordDiscoveryAsync(Tenant, "M1", Request(coverage: "Dental; Vision"), "ops");

        p.Scope.Should().Be(TplScope.Dental | TplScope.Vision);
        p.StateCoverageCode.Should().BeNull();
    }

    [Theory]
    [InlineData("N")]
    [InlineData("Hospitl")]
    public async Task BadCoverage_IsRefused(string coverage)
    {
        var act = () => Service().RecordDiscoveryAsync(Tenant, "M1", Request(coverage: coverage), "ops");
        await act.Should().ThrowAsync<TplDiscoveryException>();
    }

    [Fact]
    public async Task UnknownMember_AndDuplicates_AreRefused()
    {
        await FluentActions.Awaiting(() => Service().RecordDiscoveryAsync(Tenant, "NOPE", Request(), "ops"))
            .Should().ThrowAsync<TplDiscoveryException>().WithMessage("*not enrolled*");

        await Service().RecordDiscoveryAsync(Tenant, "M1", Request(), "ops");
        await FluentActions.Awaiting(() => Service().RecordDiscoveryAsync(Tenant, "M1", Request(), "ops"))
            .Should().ThrowAsync<TplDiscoveryException>().WithMessage("*already on file*");
    }

    [Fact]
    public async Task Report_FlagsOverdue_AndMarkReportedClearsIt()
    {
        var p = await Service().RecordDiscoveryAsync(Tenant, "M1", Request(), "ops");

        var csv = await Service().BuildStateReportCsvAsync(Tenant, "CA", false, new DateTime(2026, 11, 2));
        csv.Should().Contain("Add,M1,K001,Kaiser,POL9").And.Contain(",Y,");

        (await Service().MarkReportedAsync(Tenant, [p.Id], new DateTime(2026, 10, 25), "HI36-0001", "ops")).Should().Be(1);
        var after = await _repo.GetByIdAsync(Tenant, p.Id);
        after!.PendingStateAction.Should().BeNull();
        after.StateReportReference.Should().Be("HI36-0001");
        (await Service().BuildStateReportCsvAsync(Tenant, "CA", false, DateTime.UtcNow)).Trim().Split('\n').Should().HaveCount(1);
    }

    [Fact]
    public async Task RemovalRequest_KeepsAStateRecordInForce_ButWithdrawsAnUnreportedPlanRow()
    {
        var stateRow = new TplPolicy
        {
            TenantId = Tenant, MemberId = "M1", State = "CA", Source = TplLayouts.CaliforniaOhc, CarrierName = "Old carrier",
            Scope = TplScope.Comprehensive, EffectiveDate = new DateTime(2025, 1, 1), NaturalKey = "s1",
        };
        _repo.Seed(stateRow);

        var requested = await Service().RequestRemovalAsync(Tenant, stateRow.Id, "Member says policy ended 2025", new DateTime(2026, 10, 1), "ops");
        requested!.Status.Should().Be(TplPolicyStatus.Active);
        requested.PendingStateAction.Should().Be(TplStateAction.Remove);
        requested.StateReportDueBy.Should().Be(new DateTime(2026, 10, 11));

        var planRow = await Service().RecordDiscoveryAsync(Tenant, "M1", Request(), "ops");
        var withdrawn = await Service().RequestRemovalAsync(Tenant, planRow.Id, "Entered in error", null, "ops");
        withdrawn!.Status.Should().Be(TplPolicyStatus.Removed);
        withdrawn.PendingStateAction.Should().BeNull();
    }

    [Fact]
    public async Task StateFileCarryingThePolicy_AdoptsThePlanRow()
    {
        var planRow = await Service().RecordDiscoveryAsync(Tenant, "M1", Request(), "ops");
        var importer = new TplImportService(_repo, _coverage.Object, NullLogger<TplImportService>.Instance);

        var report = await importer.ImportAsync(new TplImportRequest(Tenant,
            "CIN,OHC Code,Carrier Code,Carrier Name,Policy Number,OHC Start Date\nM1,K,K001,Kaiser,POL9,20260901\n",
            TplLayouts.Find(TplLayouts.CaliforniaOhc)!, "CA", "ohc.csv", new DateTime(2026, 11, 1),
            TplImportMode.Incremental, false, false, "loader"));

        report.ConfirmedPlanDiscovered.Should().Be(1);
        var row = _repo.Rows.Single();
        row.Id.Should().Be(planRow.Id);
        row.Source.Should().Be(TplLayouts.CaliforniaOhc);
        row.PendingStateAction.Should().BeNull();
        row.StateConfirmedAt.Should().NotBeNull();
    }
}
