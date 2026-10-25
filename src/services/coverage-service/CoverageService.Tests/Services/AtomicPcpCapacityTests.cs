using CoverageService.Models;
using CoverageService.Repositories;
using CoverageService.Services;
using EphemeralMongo;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace CoverageService.Tests.Services;

/// <summary>Real replica-set transactions; distinct service scopes model different pods.</summary>
public sealed class AtomicPcpCapacityTests : IDisposable
{
    private readonly IMongoRunner _runner;
    private readonly MongoClient _client;
    private readonly IMongoDatabase _database;
    private const string Npi = "1234567890";

    public AtomicPcpCapacityTests()
    {
        _runner = MongoRunner.Run(new MongoRunnerOptions
        {
            Version = MongoVersion.V7, Edition = MongoEdition.Community,
            UseSingleNodeReplicaSet = true, ConnectionTimeout = TimeSpan.FromSeconds(30),
        });
        _client = new MongoClient(_runner.ConnectionString);
        _database = _client.GetDatabase("pcp_atomic_" + Guid.NewGuid().ToString("N"));
    }
    public void Dispose() { _client.Dispose(); _runner.Dispose(); }

    private TransactionalPcpAssignmentService Service(int limit = 1, bool failCoverageWrite = false, IMongoDatabase? database = null)
    {
        var db = database ?? _database;
        var context = new PcpMongoSession();
        var coverage = new CoverageRepositoryMongo(db, context);
        var history = new PcpAssignmentRepositoryMongo(db, context);
        var provider = new Mock<IProviderServiceClient>();
        provider.Setup(p => p.GetByNpiAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string npi, CancellationToken _) => new ProviderDto
            {
                Id = npi, NPI = npi, FullName = "Synthetic PCP", Status = ProviderStatusDto.Active,
                CredentialingStatus = CredentialingStatusDto.Approved,
                NetworkParticipations = new() { new()
                {
                    PlanId = "plan", LineOfBusiness = LineOfBusiness.Medicaid,
                    EffectiveDate = DateTime.UtcNow.AddYears(-1), AcceptingNewPatients = true, PanelLimit = limit,
                } },
            });
        ICoverageRepository writer = coverage;
        if (failCoverageWrite)
        {
            var broken = new Mock<ICoverageRepository>();
            broken.Setup(c => c.GetActiveCoverageByMemberIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), null))
                .Returns((string tenant, string member, DateTime date, string? line) => coverage.GetActiveCoverageByMemberIdAsync(tenant, member, date, line));
            broken.Setup(c => c.UpdateAsync(It.IsAny<Coverage>())).ThrowsAsync(new InvalidOperationException("synthetic write failure"));
            writer = broken.Object;
        }
        var inner = new PcpAssignmentService(writer, history, provider.Object,
            new MongoPcpPanelCounter(coverage, context), NullLogger<PcpAssignmentService>.Instance);
        return new(inner, db, context);
    }

    private Task Seed(string member, string tenant = "tenant", string? assignedNpi = null) =>
        _database.GetCollection<Coverage>(CoverageRepositoryMongo.CollectionName).InsertOneAsync(new Coverage
        {
            Id = tenant + member, TenantId = tenant, MemberId = member, GroupNumber = "group", PlanId = "plan",
            LineOfBusiness = LineOfBusiness.Medicaid, InsuranceLineCode = InsuranceLineCodes.Health,
            EffectiveDate = DateTime.UtcNow.AddYears(-1), Status = CoverageStatus.Active, PcpNpi = assignedNpi,
        });
    private static AssignPcpCommand Command(string npi = Npi) => new()
    { ProviderNpi = npi, EffectiveDate = DateTime.UtcNow.Date };

    [Fact]
    public async Task TwoReplicas_OneRemainingSlot_ExactlyOneSucceeds()
    {
        await Seed("one"); await Seed("two");
        var first = Service(); var second = Service();
        var results = await Task.WhenAll(first.AssignAsync("tenant", "one", Command()), second.AssignAsync("tenant", "two", Command()));
        results.Count(r => r.IsSuccess).Should().Be(1);
        results.Single(r => !r.IsSuccess).Error!.Code.Should().Be(PcpValidationCodes.PanelFull);
        (await _database.GetCollection<Coverage>(CoverageRepositoryMongo.CollectionName)
            .CountDocumentsAsync(c => c.PcpNpi == Npi)).Should().Be(1);
        (await _database.GetCollection<PcpAssignment>("PcpAssignments").CountDocumentsAsync(a => a.EndDate == null)).Should().Be(1);
    }

    [Fact]
    public async Task SameMemberConcurrentRetries_DoNotDuplicateHistory_AtFullCapacity()
    {
        await Seed("one");
        var results = await Task.WhenAll(Service().AssignAsync("tenant", "one", Command()), Service().AssignAsync("tenant", "one", Command()));
        results.Should().OnlyContain(r => r.IsSuccess);
        results[0].Assignment!.Id.Should().Be(results[1].Assignment!.Id);
        (await _database.GetCollection<PcpAssignment>("PcpAssignments").CountDocumentsAsync(Builders<PcpAssignment>.Filter.Empty)).Should().Be(1);
    }

    [Fact]
    public async Task CoverageFailure_AbortsHistoryAndRoster_AndDoesNotLeakSlot()
    {
        await Seed("one"); await Seed("two");
        var act = () => Service(failCoverageWrite: true).AssignAsync("tenant", "one", Command());
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await _database.GetCollection<PcpAssignment>("PcpAssignments").CountDocumentsAsync(Builders<PcpAssignment>.Filter.Empty)).Should().Be(0);
        (await _database.GetCollection<Coverage>(CoverageRepositoryMongo.CollectionName).CountDocumentsAsync(c => c.PcpNpi == Npi)).Should().Be(0);
        (await Service().AssignAsync("tenant", "two", Command())).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Reassignment_ReleasesOldPanelAndPreservesHistory()
    {
        await Seed("one"); await Seed("two");
        (await Service().AssignAsync("tenant", "one", Command())).IsSuccess.Should().BeTrue();
        (await Service().AssignAsync("tenant", "one", Command("0987654321"))).IsSuccess.Should().BeTrue();
        (await Service().AssignAsync("tenant", "two", Command())).IsSuccess.Should().BeTrue();
        var rows = await _database.GetCollection<PcpAssignment>("PcpAssignments").Find(a => a.MemberId == "one").ToListAsync();
        rows.Should().HaveCount(2); rows.Count(a => a.EndDate == null).Should().Be(1);
    }

    [Fact]
    public async Task UnsupportedStandaloneDatabase_RefusesAssignmentWithoutHistoryWrites()
    {
        using var standalone = MongoRunner.Run(new MongoRunnerOptions
        { Version = MongoVersion.V7, Edition = MongoEdition.Community, UseSingleNodeReplicaSet = false });
        using var client = new MongoClient(standalone.ConnectionString);
        var db = client.GetDatabase("pcp_standalone");
        var act = () => Service(database: db).AssignAsync("tenant", "one", Command());
        await act.Should().ThrowAsync<PanelCountUnavailableException>();
        (await db.GetCollection<PcpAssignment>("PcpAssignments").CountDocumentsAsync(Builders<PcpAssignment>.Filter.Empty)).Should().Be(0);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task ScheduledOrHistoricalMutation_IsExplicitlyUnsupported(int days)
    {
        await Seed("one");
        var command = Command(); command.EffectiveDate = command.EffectiveDate.AddDays(days);
        var result = await Service().AssignAsync("tenant", "one", command);
        result.Error!.Code.Should().Be("PCP_EFFECTIVE_DATE_UNSUPPORTED");
        (await _database.GetCollection<PcpAssignment>("PcpAssignments").CountDocumentsAsync(Builders<PcpAssignment>.Filter.Empty)).Should().Be(0);
    }

    [Fact]
    public async Task TenantPanels_AreIndependent()
    {
        await Seed("one", "a"); await Seed("one", "b");
        var results = await Task.WhenAll(Service().AssignAsync("a", "one", Command()), Service().AssignAsync("b", "one", Command()));
        results.Should().OnlyContain(r => r.IsSuccess);
    }
}
