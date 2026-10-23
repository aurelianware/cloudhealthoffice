using CloudHealthOffice.PlanConfiguration;
using CloudHealthOffice.Testing.Mongo;
using MongoDB.Driver;
using TenantService.Hierarchy;
using TenantModels = TenantService.Models;

namespace CloudHealthOffice.TenantService.Tests.Hierarchy;

/// <summary>
/// The carrier and program store against a real mongod: the unique codes, the
/// revision guard, and tenant scoping.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class HierarchyStoreMongoTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private IMongoDatabase _db = null!;
    private MongoHierarchyStore _store = null!;

    public async Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("hierarchy");
        _store = new MongoHierarchyStore(_db);
        await _store.EnsureIndexesAsync();
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private static Carrier C(string tenant, string code) => new()
    {
        Id = Carrier.IdFor(tenant, code), TenantId = tenant, Code = code, Name = code + " entity",
    };

    private static PlanProgram P(string tenant, string carrierCode, string code, string state = "TX", string? lob = null) => new()
    {
        Id = PlanProgram.IdFor(tenant, carrierCode, code),
        TenantId = tenant,
        CarrierId = Carrier.IdFor(tenant, carrierCode),
        Code = code,
        Name = code,
        StateCode = state,
        LineOfBusiness = lob ?? LinesOfBusiness.Medicaid,
    };

    [Fact]
    public async Task A_carrier_survives_a_round_trip_and_is_tenant_scoped()
    {
        var carrier = C("t1", "HMO");
        carrier.PayerIds["837"] = "ACME837";
        carrier.CorrespondenceAddress = new TenantModels.Address { City = "Austin", State = "TX" };

        (await _store.TryInsertCarrierAsync(carrier)).Should().BeTrue();

        var read = await _store.GetCarrierAsync("t1", carrier.Id);
        read!.PayerIds.Should().ContainKey("837");
        read.CorrespondenceAddress!.City.Should().Be("Austin");
        (await _store.GetCarrierAsync("t2", carrier.Id)).Should().BeNull();
    }

    [Fact]
    public async Task One_tenant_cannot_hold_two_carriers_with_one_code_but_two_tenants_can()
    {
        (await _store.TryInsertCarrierAsync(C("t1", "DEFAULT"))).Should().BeTrue();
        (await _store.TryInsertCarrierAsync(C("t1", "DEFAULT"))).Should().BeFalse();
        (await _store.TryInsertCarrierAsync(C("t2", "DEFAULT"))).Should().BeTrue();
    }

    [Fact]
    public async Task Concurrent_default_carrier_inserts_produce_one_carrier()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => _store.TryInsertCarrierAsync(C("t1", "DEFAULT")))));

        results.Count(ok => ok).Should().Be(1);
        (await _store.ListCarriersAsync("t1")).Should().ContainSingle();
    }

    [Fact]
    public async Task A_replace_against_a_stale_revision_changes_nothing()
    {
        var carrier = C("t1", "HMO");
        await _store.TryInsertCarrierAsync(carrier);
        var stale = carrier.Revision;
        carrier.Name = "First";
        (await _store.TryReplaceCarrierAsync(carrier, stale)).Should().BeTrue();

        var loser = C("t1", "HMO");
        loser.Name = "Second";
        (await _store.TryReplaceCarrierAsync(loser, stale)).Should().BeFalse();

        (await _store.GetCarrierAsync("t1", carrier.Id))!.Name.Should().Be("First");
    }

    [Fact]
    public async Task Programs_are_unique_per_carrier_and_listed_by_carrier()
    {
        await _store.TryInsertCarrierAsync(C("t1", "DEFAULT"));
        await _store.TryInsertCarrierAsync(C("t1", "INS"));

        (await _store.TryInsertProgramAsync(P("t1", "DEFAULT", "STAR"))).Should().BeTrue();
        (await _store.TryInsertProgramAsync(P("t1", "DEFAULT", "STAR"))).Should().BeFalse();
        // The same program code under another carrier is a different program.
        (await _store.TryInsertProgramAsync(P("t1", "INS", "STAR"))).Should().BeTrue();
        await _store.TryInsertProgramAsync(P("t1", "DEFAULT", "STARKIDS"));

        (await _store.ListProgramsAsync("t1", null)).Select(p => p.Id).Should().HaveCount(3);
        (await _store.ListProgramsAsync("t1", Carrier.IdFor("t1", "INS"))).Should().ContainSingle();
        (await _store.ListProgramsAsync("t2", null)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_programs_descriptor_round_trips_what_other_services_read()
    {
        await _store.TryInsertCarrierAsync(C("t1", "DEFAULT"));
        await _store.TryInsertProgramAsync(P("t1", "DEFAULT", "STAR"));

        var descriptor = (await _store.GetProgramAsync("t1", PlanProgram.IdFor("t1", "DEFAULT", "STAR")))!.ToDescriptor();

        descriptor.Should().Match<ProgramDescriptor>(d =>
            d.Code == "STAR" && d.StateCode == "TX" && d.LineOfBusiness == LinesOfBusiness.Medicaid
            && d.CarrierId == Carrier.IdFor("t1", "DEFAULT") && d.Active);
    }
}
