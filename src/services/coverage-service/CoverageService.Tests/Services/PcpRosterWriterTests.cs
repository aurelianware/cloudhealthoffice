using CoverageService.Exceptions;
using CoverageService.Models;
using CoverageService.Repositories;
using CoverageService.Services;
using EphemeralMongo;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace CoverageService.Tests.Services;

/// <summary>Real replica set; supported writers use the same guard as production DI.</summary>
public sealed class PcpRosterWriterTests : IDisposable
{
    private const string Npi = "1234567890";
    private readonly IMongoRunner _runner;
    private readonly MongoClient _client;
    private readonly IMongoDatabase _db;
    public PcpRosterWriterTests()
    {
        _runner = MongoRunner.Run(new MongoRunnerOptions
        { Version = MongoVersion.V7, Edition = MongoEdition.Community, UseSingleNodeReplicaSet = true });
        _client = new MongoClient(_runner.ConnectionString);
        _db = _client.GetDatabase("pcp_writers_" + Guid.NewGuid().ToString("N"));
    }
    public void Dispose() { _client.Dispose(); _runner.Dispose(); }

    private (CoverageRepositoryMongo Repo, TransactionalPcpAssignmentService Service) Scope(
        bool unavailable = false, int? participationEnd = null, IMongoDatabase? database = null)
    {
        var db = database ?? _db;
        var context = new PcpMongoSession();
        var provider = new Mock<IProviderServiceClient>();
        if (unavailable)
            provider.Setup(p => p.GetByNpiAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ProviderDirectoryUnavailableException("synthetic", 503));
        else
            provider.Setup(p => p.GetByNpiAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProviderDto
                {
                    NPI = Npi, Status = ProviderStatusDto.Active, CredentialingStatus = CredentialingStatusDto.Approved,
                    NetworkParticipations = new() { new()
                    {
                        PlanId = "plan", LineOfBusiness = LineOfBusiness.Medicaid, PanelLimit = 1,
                        EffectiveDate = DateTime.UtcNow.Date.AddYears(-5),
                        TerminationDate = participationEnd.HasValue ? DateTime.UtcNow.Date.AddDays(participationEnd.Value) : null,
                    } },
                });
        var guard = new PcpRosterWriteGuard(db, context, new PcpWriteTransaction(db, context), provider.Object);
        var repo = new CoverageRepositoryMongo(db, context, guard);
        var history = new PcpAssignmentRepositoryMongo(db, context);
        var inner = new PcpAssignmentService(repo, history, provider.Object, new MongoPcpPanelCounter(repo, context), NullLogger<PcpAssignmentService>.Instance);
        return (repo, new TransactionalPcpAssignmentService(inner, db, context));
    }
    private async Task<Coverage> Seed(string member, CoverageStatus status = CoverageStatus.Active,
        bool pcp = true, int start = -5, int? end = null, string tenant = "tenant")
    {
        var row = new Coverage
        {
            TenantId = tenant, MemberId = member, PlanId = "plan", GroupNumber = "group", Status = status,
            LineOfBusiness = LineOfBusiness.Medicaid, InsuranceLineCode = InsuranceLineCodes.Health,
            EffectiveDate = DateTime.UtcNow.Date.AddDays(start),
            TerminationDate = end.HasValue ? DateTime.UtcNow.Date.AddDays(end.Value) : null,
            PcpNpi = pcp ? Npi : null,
        };
        // Legacy states are deliberately seeded below the supported writer boundary.
        await _db.GetCollection<Coverage>(CoverageRepositoryMongo.CollectionName).InsertOneAsync(row);
        return row;
    }

    [Fact]
    public async Task ReinstatementAndAssignment_CompeteForOneSlot_ExactlyOneCommits()
    {
        var reinstated = await Seed("old", CoverageStatus.Suspended);
        await Seed("new", pcp: false);
        reinstated.Status = CoverageStatus.Active;
        var scope = Scope();
        async Task<bool> Reinstate()
        {
            try { await scope.Repo.UpdateAsync(reinstated); return true; }
            catch (PcpRosterWriteException ex) when (ex.Code == PcpValidationCodes.PanelFull) { return false; }
        }
        var assignment = Scope().Service.AssignAsync("tenant", "new", new AssignPcpCommand { ProviderNpi = Npi });
        var restored = Reinstate();
        await Task.WhenAll(assignment, restored);
        ((assignment.Result.IsSuccess ? 1 : 0) + (restored.Result ? 1 : 0)).Should().Be(1);
        var rows = await Scope().Repo.GetByPcpNpiAsync("tenant", Npi, CoverageStatus.Active);
        rows.Select(c => c.MemberId).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task TwoIndependentReactivations_DoNotBypassPanelFence()
    {
        var one = await Seed("one", CoverageStatus.Suspended);
        var two = await Seed("two", CoverageStatus.Suspended);
        one.Status = two.Status = CoverageStatus.Active;
        async Task<bool> Write(Coverage c)
        {
            try { await Scope().Repo.UpdateAsync(c); return true; }
            catch (PcpRosterWriteException ex) when (ex.Code == PcpValidationCodes.PanelFull) { return false; }
        }
        (await Task.WhenAll(Write(one), Write(two))).Count(x => x).Should().Be(1);
    }

    [Fact]
    public async Task FutureCoverageReservation_BlocksIndefiniteCurrentAssignment_AndRollsBackHistory()
    {
        await Seed("future", CoverageStatus.Pending, start: 5);
        await Seed("now", pcp: false);
        var result = await Scope().Service.AssignAsync("tenant", "now", new AssignPcpCommand { ProviderNpi = Npi });
        result.Error!.Code.Should().Be(PcpValidationCodes.PanelFull);
        (await _db.GetCollection<PcpAssignment>("PcpAssignments").CountDocumentsAsync(Builders<PcpAssignment>.Filter.Empty)).Should().Be(0);
        (await Scope().Repo.GetCoverageHistoryAsync("tenant", "now")).Single().PcpNpi.Should().BeNull();
    }

    [Fact]
    public async Task AdjacentNonOverlappingSpans_AreAllowed_ButTerminationDayOverlapIsRejected()
    {
        await Seed("current", end: 2);
        var adjacent = await Seed("next", CoverageStatus.Suspended, start: 3);
        adjacent.Status = CoverageStatus.Pending;
        await Scope().Repo.UpdateAsync(adjacent);
        var overlap = await Seed("overlap", CoverageStatus.Suspended, start: 2, end: 2);
        overlap.Status = CoverageStatus.Active;
        await ((Func<Task>)(() => Scope().Repo.UpdateAsync(overlap))).Should().ThrowAsync<PcpRosterWriteException>()
            .Where(e => e.Code == PcpValidationCodes.PanelFull);
    }

    [Fact]
    public async Task SweepRechecksRevision_AndCannotReactivateAnOverfullLegacySpan()
    {
        await Seed("current");
        var one = await Seed("one", CoverageStatus.Suspended);
        await ((Func<Task>)(() => Scope().Repo.SetStatusAsync(one, CoverageStatus.Active, "sweep")))
            .Should().ThrowAsync<PcpRosterWriteException>();
        (await Scope().Repo.GetByIdAsync("tenant", one.Id))!.Status.Should().Be(CoverageStatus.Suspended);
        var stale = await Scope().Repo.GetByIdAsync("tenant", one.Id);
        one.MonthlyPremium = 1; await Scope().Repo.UpdateAsync(one);
        (await Scope().Repo.SetStatusAsync(stale!, CoverageStatus.Active, "sweep")).Should().BeFalse();
    }

    [Fact]
    public async Task DirectPcpEditsAndAssignmentDateEdits_AreRefusedWithoutHistoryBypass()
    {
        var row = await Seed("one", pcp: false);
        row.PcpNpi = Npi;
        await ((Func<Task>)(() => Scope().Repo.UpdateAsync(row))).Should().ThrowAsync<PcpRosterWriteException>()
            .Where(e => e.Code == "PCP_ASSIGNMENT_API_REQUIRED");
        await ((Func<Task>)(() => Scope().Repo.CreateAsync(new Coverage { TenantId = "tenant", MemberId = "two", PcpNpi = Npi })))
            .Should().ThrowAsync<PcpRosterWriteException>();
        row = await Seed("dated"); row.PcpAssignmentDate = DateTime.UtcNow.Date;
        await ((Func<Task>)(() => Scope().Repo.UpdateAsync(row))).Should().ThrowAsync<PcpRosterWriteException>();
    }

    [Fact]
    public async Task UnchangedFullPanel_AllowsPremiumEditsTerminationAndDeletion_WithoutProviderAccess()
    {
        var row = await Seed("one");
        row.MonthlyPremium = 10;
        await Scope(unavailable: true).Repo.UpdateAsync(row);
        row.ApplyTermination(DateTime.UtcNow.Date, DateTime.UtcNow.Date);
        await Scope(unavailable: true).Repo.UpdateAsync(row);
        await Scope(unavailable: true).Repo.DeleteAsync("tenant", row.Id);
        (await Scope().Repo.GetByIdAsync("tenant", row.Id)).Should().BeNull();
    }

    [Fact]
    public async Task ProviderFailureOrParticipationExpiry_LeavesReactivationUnchanged()
    {
        var row = await Seed("one", CoverageStatus.Suspended);
        row.Status = CoverageStatus.Active;
        await ((Func<Task>)(() => Scope(unavailable: true).Repo.UpdateAsync(row))).Should().ThrowAsync<ProviderDirectoryUnavailableException>();
        await ((Func<Task>)(() => Scope(participationEnd: 3).Repo.UpdateAsync(row))).Should().ThrowAsync<PcpRosterWriteException>()
            .Where(e => e.Code == PcpValidationCodes.NoNetworkParticipation);
        (await Scope().Repo.GetByIdAsync("tenant", row.Id))!.Status.Should().Be(CoverageStatus.Suspended);
        row.Revision.Should().Be(0);
    }

    [Fact]
    public async Task TenantIsolation_AndDuplicateMedicalMember_DoNotConsumeExtraSlots()
    {
        await Seed("one");
        var duplicate = await Seed("one", CoverageStatus.Suspended);
        duplicate.Status = CoverageStatus.Active;
        await Scope(unavailable: true).Repo.UpdateAsync(duplicate);
        var other = await Seed("one", CoverageStatus.Suspended, tenant: "other");
        other.Status = CoverageStatus.Active;
        await Scope().Repo.UpdateAsync(other);
    }

    [Fact]
    public async Task UnsupportedStandaloneDatabase_RefusesCoverageWrites()
    {
        using var runner = MongoRunner.Run(new MongoRunnerOptions
        { Version = MongoVersion.V7, Edition = MongoEdition.Community, UseSingleNodeReplicaSet = false });
        using var client = new MongoClient(runner.ConnectionString);
        var db = client.GetDatabase("writer_standalone");
        await ((Func<Task>)(() => Scope(database: db).Repo.CreateAsync(new Coverage { TenantId = "tenant", MemberId = "one" })))
            .Should().ThrowAsync<PanelCountUnavailableException>();
        (await db.GetCollection<Coverage>(CoverageRepositoryMongo.CollectionName).CountDocumentsAsync(Builders<Coverage>.Filter.Empty)).Should().Be(0);
    }
}
