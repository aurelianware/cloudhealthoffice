using CoverageService.Models.Tpl;
using CoverageService.Repositories;
using CoverageService.Services.Tpl;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoverageService.Tests.Tpl;

public class TplImportServiceTests
{
    private const string Tenant = "t1";
    private static readonly DateTime FileDate = new(2026, 11, 1);

    private readonly InMemoryTplRepository _repo = new();
    private readonly Mock<ICoverageRepository> _coverage = new();

    public TplImportServiceTests()
    {
        // M1..M3 are enrolled; anyone else is not.
        _coverage.Setup(c => c.GetKnownMemberIdsAsync(Tenant, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IReadOnlyCollection<string> ids, CancellationToken _) =>
                (IReadOnlySet<string>)ids.Where(i => i is "M1" or "M2" or "M3").ToHashSet());
    }

    private TplImportService Service() => new(_repo, _coverage.Object, NullLogger<TplImportService>.Instance);

    private Task<TplImportReport> Run(string content, string layout = TplLayouts.CaliforniaOhc,
        TplImportMode mode = TplImportMode.Incremental, bool dryRun = false, bool allowLargeDrop = false) =>
        Service().ImportAsync(new TplImportRequest(
            Tenant, content, TplLayouts.Find(layout)!, "CA", "ohc.csv", FileDate, mode, dryRun, allowLargeDrop, "tester"));

    private const string Header = "CIN,OHC Code,Carrier Name,Policy Number,OHC Start Date,OHC End Date\n";

    [Fact]
    public async Task DryRun_ReportsButWritesNothing()
    {
        var report = await Run(Header + "M1,K,Kaiser,P1,20260101,\n", dryRun: true);

        report.Inserted.Should().Be(1);
        report.LayoutVerified.Should().BeFalse();
        report.Warnings.Should().Contain(w => w.Contains("not been checked"));
        _repo.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Load_InsertsThenRecognisesUnchanged()
    {
        await Run(Header + "M1,K,Kaiser,P1,20260101,\n");
        var second = await Run(Header + "M1,K,Kaiser,P1,20260101,\n");

        _repo.Rows.Should().ContainSingle();
        var row = _repo.Rows.Single();
        row.Scope.Should().Be(TplScope.Comprehensive);
        row.Source.Should().Be(TplLayouts.CaliforniaOhc);
        row.State.Should().Be("CA");
        second.Inserted.Should().Be(0);
        second.Unchanged.Should().Be(1);
    }

    [Fact]
    public async Task UnknownMember_IsRejected()
    {
        var report = await Run(Header + "NOT-OURS,K,Kaiser,P1,20260101,\n");

        report.UnknownMembers.Should().Be(1);
        report.Rejected.Should().Be(1);
        _repo.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ChangedEndDate_UpdatesAndEnds()
    {
        await Run(Header + "M1,K,Kaiser,P1,20260101,\n");
        var report = await Run(Header + "M1,K,Kaiser,P1,20260101,20260930\n");

        report.Updated.Should().Be(1);
        var row = _repo.Rows.Single();
        row.EndDate.Should().Be(new DateTime(2026, 9, 30));
        row.Status.Should().Be(TplPolicyStatus.Ended);
        row.IsActiveOn(new DateTime(2026, 9, 15)).Should().BeTrue("claims in the span still see it");
        row.IsActiveOn(new DateTime(2026, 10, 15)).Should().BeFalse();
    }

    [Fact]
    public async Task OhcCodeN_EndsWhatTheSourceHolds_ButNotPlanDiscovered()
    {
        await Run(Header + "M1,K,Kaiser,P1,20260101,\nM1,L,Delta,P2,20260101,\n");
        _repo.Seed(new TplPolicy
        {
            TenantId = Tenant, MemberId = "M1", State = "CA", Source = TplImportService.PlanDiscoveredSource,
            CarrierName = "Found by plan", Scope = TplScope.Vision, EffectiveDate = new DateTime(2026, 1, 1),
            NaturalKey = "plan|1",
        });

        var report = await Run(Header + "M1,N,,,20260601,\n");

        report.Ended.Should().Be(2);
        _repo.Rows.Where(r => r.Source == TplLayouts.CaliforniaOhc)
            .Should().OnlyContain(r => r.Status == TplPolicyStatus.Ended && r.EndDate == new DateTime(2026, 5, 31));
        _repo.Rows.Single(r => r.Source == TplImportService.PlanDiscoveredSource).Status.Should().Be(TplPolicyStatus.Active);
    }

    [Fact]
    public async Task FullFile_EndsRowsItNoLongerCarries()
    {
        await Run(Header + "M1,K,Kaiser,P1,20260101,\nM2,L,Delta,P2,20260101,\n");

        var report = await Run(Header + "M1,K,Kaiser,P1,20260101,\n", mode: TplImportMode.Full);

        report.EndedNotOnFile.Should().Be(1);
        var m2 = _repo.Rows.Single(r => r.MemberId == "M2");
        m2.Status.Should().Be(TplPolicyStatus.Ended);
        m2.EndDate.Should().Be(FileDate.AddDays(-1));
        _repo.Rows.Single(r => r.MemberId == "M1").Status.Should().Be(TplPolicyStatus.Active);
    }

    [Fact]
    public async Task FullFile_RefusesALargeDrop()
    {
        var seed = Header + string.Join("", Enumerable.Range(0, 120).Select(i => $"M1,K,Kaiser,P{i},20260101,\n"));
        await Run(seed);

        var report = await Run(Header + "M1,K,Kaiser,P0,20260101,\n", mode: TplImportMode.Full);

        report.Error.Should().Contain("truncated");
        _repo.Rows.Should().OnlyContain(r => r.Status == TplPolicyStatus.Active);

        var forced = await Run(Header + "M1,K,Kaiser,P0,20260101,\n", mode: TplImportMode.Full, allowLargeDrop: true);
        forced.Error.Should().BeNull();
        forced.EndedNotOnFile.Should().Be(119);
    }

    [Fact]
    public async Task RemoveAction_MarksRemoved()
    {
        await Run("member_id,carrier_name,policy_number,scope,effective_date\nM1,Aetna,P1,Medical,2026-01-01\n",
            layout: TplLayouts.ChoCsv);

        var report = await Run(
            "member_id,carrier_name,policy_number,scope,effective_date,action\nM1,Aetna,P1,Medical,2026-01-01,R\n",
            layout: TplLayouts.ChoCsv);

        report.Removed.Should().Be(1);
        var row = _repo.Rows.Single();
        row.Status.Should().Be(TplPolicyStatus.Removed);
        row.IsActiveOn(new DateTime(2026, 3, 1)).Should().BeFalse("a removed record never applied");
    }

    private sealed class InMemoryTplRepository : ITplPolicyRepository
    {
        private readonly Dictionary<string, TplPolicy> _rows = new();
        public IEnumerable<TplPolicy> Rows => _rows.Values;

        public void Seed(TplPolicy p) => _rows[p.Id] = p;

        public Task<IReadOnlyList<TplPolicy>> GetByMemberAsync(string tenantId, string memberId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TplPolicy>>(_rows.Values.Where(r => r.TenantId == tenantId && r.MemberId == memberId).ToList());

        public Task<IReadOnlyList<TplPolicy>> GetByMembersAsync(string tenantId, string state, IReadOnlyCollection<string> memberIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TplPolicy>>(_rows.Values
                .Where(r => r.TenantId == tenantId && r.State == state && memberIds.Contains(r.MemberId)).ToList());

        public Task<IReadOnlyList<TplPolicy>> GetActiveBySourceAsync(string tenantId, string state, string source, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TplPolicy>>(_rows.Values
                .Where(r => r.TenantId == tenantId && r.State == state && r.Source == source && r.Status == TplPolicyStatus.Active).ToList());

        public Task UpsertManyAsync(IReadOnlyCollection<TplPolicy> policies, CancellationToken ct = default)
        {
            foreach (var p in policies) _rows[p.Id] = p;
            return Task.CompletedTask;
        }
    }
}
