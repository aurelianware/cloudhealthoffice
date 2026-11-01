using CloudHealthOffice.Testing.Mongo;
using CoverageService.Models;
using CoverageService.Models.Tpl;
using CoverageService.Repositories;

namespace CoverageService.Tests.Tpl;

/// <summary>TPL rows and the known-member lookup against a real mongod.</summary>
[Collection(MongoRunnerFixture.CollectionName)]
public class TplPolicyRepositoryMongoTests
{
    private readonly MongoRunnerFixture _mongo;

    public TplPolicyRepositoryMongoTests(MongoRunnerFixture mongo) => _mongo = mongo;

    private static TplPolicy Policy(string tenant, string member, string source, TplPolicyStatus status, string policy) => new()
    {
        TenantId = tenant,
        MemberId = member,
        State = "TX",
        Source = source,
        CarrierName = "Carrier",
        PolicyNumber = policy,
        Scope = TplScope.Medical,
        EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Status = status,
        NaturalKey = TplPolicy.BuildNaturalKey(member, "TX", null, "Carrier", policy, new DateTime(2026, 1, 1)),
    };

    [Fact]
    public async Task UpsertAndRead_AreTenantScoped()
    {
        var db = _mongo.CreateDatabase("tpl_repo");
        try
        {
            var repo = new TplPolicyRepositoryMongo(db);
            var mine = Policy("t1", "M1", "tx-tpr", TplPolicyStatus.Active, "P1");
            await repo.UpsertManyAsync(
            [
                mine,
                Policy("t1", "M1", "tx-tpr", TplPolicyStatus.Ended, "P2"),
                Policy("t2", "M1", "tx-tpr", TplPolicyStatus.Active, "P1"),
            ]);

            (await repo.GetByMemberAsync("t1", "M1")).Should().HaveCount(2);
            (await repo.GetActiveBySourceAsync("t1", "TX", "tx-tpr")).Should().ContainSingle().Which.Id.Should().Be(mine.Id);
            (await repo.GetByMembersAsync("t1", "TX", ["M1", "M9"])).Should().HaveCount(2);

            mine.Status = TplPolicyStatus.Removed;
            await repo.UpsertManyAsync([mine]);
            (await repo.GetActiveBySourceAsync("t1", "TX", "tx-tpr")).Should().BeEmpty();
        }
        finally
        {
            await _mongo.DropDatabaseAsync(db);
        }
    }

    [Fact]
    public async Task KnownMemberIds_FindsEnrolledMembersOnly()
    {
        var db = _mongo.CreateDatabase("tpl_known");
        try
        {
            var coverage = new CoverageRepositoryMongo(db);
            await db.GetCollection<Coverage>(CoverageRepositoryMongo.CollectionName).InsertManyAsync(
            [
                new Coverage { TenantId = "t1", MemberId = "M1", GroupNumber = "G", PlanId = "P", EffectiveDate = DateTime.UtcNow },
                new Coverage { TenantId = "t2", MemberId = "M2", GroupNumber = "G", PlanId = "P", EffectiveDate = DateTime.UtcNow },
            ]);

            var known = await coverage.GetKnownMemberIdsAsync("t1", ["M1", "M2", "M3"]);

            known.Should().BeEquivalentTo(["M1"]);
        }
        finally
        {
            await _mongo.DropDatabaseAsync(db);
        }
    }
}
