using CloudHealthOffice.PlanConfiguration;
using CloudHealthOffice.Testing.Mongo;
using MongoDB.Driver;
using TenantService.Configuration;

namespace CloudHealthOffice.TenantService.Tests.Configuration;

[Collection(MongoRunnerFixture.CollectionName)]
public sealed class ConfigurationStoreMongoTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private IMongoDatabase _db = null!;
    private MongoConfigurationStore _store = null!;
    private static readonly DateTime Day = new(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc);

    public async Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("configuration");
        _store = new MongoConfigurationStore(_db);
        await _store.EnsureIndexesAsync();
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private static SettingValue V(string tenant, SettingLevel level, string scope, string value) => new()
    {
        TenantId = tenant, Key = "enrollment.orphan.graceDays", Level = level, ScopeId = scope, Value = value,
        EffectiveFrom = Day, SetBy = "u", SetAt = Day,
    };

    [Fact]
    public async Task Versions_number_per_node_survive_a_round_trip_and_are_tenant_scoped()
    {
        (await _store.AppendAsync(V("t1", SettingLevel.Sponsor, "ACME", "3"))).Version.Should().Be(1);
        (await _store.AppendAsync(V("t1", SettingLevel.Sponsor, "ACME", "4"))).Version.Should().Be(2);
        (await _store.AppendAsync(V("t1", SettingLevel.Sponsor, "OTHER", "5"))).Version.Should().Be(1);
        (await _store.AppendAsync(V("t2", SettingLevel.Sponsor, "ACME", "9"))).Version.Should().Be(1);
        await _store.AppendAsync(V("t1", SettingLevel.LineOfBusiness, "Commercial", "10"));

        var acme = await _store.GetVersionsAsync("t1", "enrollment.orphan.graceDays", [(SettingLevel.Sponsor, "ACME"), (SettingLevel.LineOfBusiness, "Commercial")]);
        acme.Select(v => v.Value).Should().BeEquivalentTo(["3", "4", "10"]);
        acme.All(v => v.EffectiveFrom == Day && v.Level != SettingLevel.Platform).Should().BeTrue();
        SettingResolver.Resolve(PlanSettings.OrphanGraceDays, new SettingScope { LineOfBusiness = "Commercial", Sponsor = "ACME" }, Day, acme).Value.Should().Be("4");

        (await _store.GetVersionsAtOrBelowAsync("t1", "enrollment.orphan.graceDays", SettingLevel.Sponsor)).Should().HaveCount(3);
        (await _store.GetVersionsAtOrBelowAsync("t2", "enrollment.orphan.graceDays", SettingLevel.Tenant)).Should().ContainSingle();
    }

    [Fact]
    public async Task Concurrent_appends_to_one_node_get_distinct_versions()
    {
        var versions = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => _store.AppendAsync(V("t1", SettingLevel.Sponsor, "ACME", i.ToString()))));
        versions.Select(v => v.Version).Should().OnlyHaveUniqueItems().And.HaveCount(5);
    }

    [Fact]
    public async Task A_pending_change_is_decided_once_and_only_in_its_tenant()
    {
        var pending = new PendingConfigurationChange { TenantId = "t1", Key = "k", Level = SettingLevel.Tenant, Value = "false", RequestedBy = "a", RequestedAt = Day };
        await _store.AddPendingAsync(pending);

        (await _store.GetPendingAsync("t2", pending.Id)).Should().BeNull();
        (await _store.TryClaimApprovalAsync("t2", pending.Id, "b", Day, null)).Should().BeFalse();
        (await _store.ListPendingAsync("t1")).Should().ContainSingle();
        (await _store.TryClaimApprovalAsync("t1", pending.Id, "b", Day, "ok")).Should().BeTrue();
        (await _store.TryClaimApprovalAsync("t1", pending.Id, "c", Day, null)).Should().BeFalse("claimed once");
        (await _store.TryRejectAsync("t1", pending.Id, "c", Day, null)).Should().BeFalse("an approval under way cannot be rejected");
        (await _store.ListPendingAsync("t1")).Should().ContainSingle("an unfinished approval stays listed");

        var version = await _store.AppendAsync(new SettingValue
        {
            TenantId = "t1", Key = "k", Level = SettingLevel.Tenant, ScopeId = "", Value = "false", EffectiveFrom = Day,
            SetBy = "a", SetAt = Day, ApprovedFromPendingId = pending.Id, ApprovedBy = "b",
        });
        (await _store.TryCompleteApprovalAsync("t2", pending.Id, version)).Should().BeFalse();
        (await _store.TryCompleteApprovalAsync("t1", pending.Id, version)).Should().BeTrue();
        (await _store.TryCompleteApprovalAsync("t1", pending.Id, version)).Should().BeFalse("already approved");
        var stored = (await _store.GetPendingAsync("t1", pending.Id))!;
        stored.Status.Should().Be(PendingChangeStatus.Approved);
        stored.AppliedVersion.Should().Be(version.Version);
        stored.AppliedVersionId.Should().Be(version.Id);
        (await _store.ListPendingAsync("t1")).Should().BeEmpty();
    }

    [Fact]
    public async Task An_approval_writes_at_most_one_version_even_when_retried_concurrently()
    {
        SettingValue FromApproval(string value) => new()
        {
            TenantId = "t1", Key = "k", Level = SettingLevel.Tenant, ScopeId = "", Value = value, EffectiveFrom = Day,
            SetBy = "a", SetAt = Day, ApprovedFromPendingId = "pending-1", ApprovedBy = "b",
        };

        var written = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => _store.AppendAsync(FromApproval("false"))));

        written.Select(v => v.Id).Distinct().Should().ContainSingle();
        (await _store.GetVersionsAsync("t1", "k", [(SettingLevel.Tenant, "")])).Should().ContainSingle();
        (await _store.GetVersionForPendingAsync("t1", "pending-1"))!.Id.Should().Be(written[0].Id);
        (await _store.GetVersionForPendingAsync("t2", "pending-1")).Should().BeNull();
        // Ordinary versions (no approval) are unaffected by the approval index.
        SettingValue Plain(string value) { var v = V("t1", SettingLevel.Tenant, "", value); v.Key = "k"; return v; }
        (await _store.AppendAsync(Plain("true"))).Version.Should().Be(2);
        (await _store.AppendAsync(Plain("false"))).Version.Should().Be(3);
    }

    [Fact]
    public async Task An_approvals_change_record_is_stored_once()
    {
        var record = new ConfigurationChangeRecord { Id = "approved-p1", TenantId = "t1", Key = "k", Action = "Approved", At = Day };
        await _store.AddChangeAsync(record);
        await _store.AddChangeAsync(new ConfigurationChangeRecord { Id = "approved-p1", TenantId = "t1", Key = "k", Action = "Approved", At = Day });
        (await _store.ListChangesAsync("t1", "k", 10)).Should().ContainSingle();
    }

    [Fact]
    public async Task The_change_log_is_tenant_scoped_newest_first()
    {
        await _store.AddChangeAsync(new ConfigurationChangeRecord { TenantId = "t1", Key = "k", Action = "Set", At = Day });
        await _store.AddChangeAsync(new ConfigurationChangeRecord { TenantId = "t1", Key = "k", Action = "Cleared", At = Day.AddHours(1) });
        await _store.AddChangeAsync(new ConfigurationChangeRecord { TenantId = "t2", Key = "k", Action = "Set", At = Day });

        (await _store.ListChangesAsync("t1", "k", 10)).Select(c => c.Action).Should().Equal("Cleared", "Set");
        (await _store.ListChangesAsync("t2", null, 10)).Should().ContainSingle();
    }

    [Fact]
    public async Task The_value_index_is_unique_per_node_and_version()
    {
        var indexes = await (await _db.GetCollection<SettingValue>(MongoConfigurationStore.ValuesCollection).Indexes.ListAsync()).ToListAsync();
        indexes.Should().Contain(i => i["name"] == "ix_tenant_key_node_version" && i["unique"].AsBoolean);
        indexes.Should().Contain(i => i["name"] == "ix_tenant_pending" && i["unique"].AsBoolean);
    }
}
