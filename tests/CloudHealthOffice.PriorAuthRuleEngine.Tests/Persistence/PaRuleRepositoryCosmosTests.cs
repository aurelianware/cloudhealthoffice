using System.Net;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.PriorAuthRuleEngine.Persistence;
using CloudHealthOffice.Testing.Cosmos;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.PriorAuthRuleEngine.Tests.Persistence;

/// <summary>
/// D-16 and D-17 against a real Cosmos DB (the Linux emulator). Skipped when no
/// emulator is reachable; CI sets COSMOS_EMULATOR_REQUIRED=true.
///
/// The client is configured camelCase because that is the shape every query in
/// <see cref="PaRuleRepositoryCosmos"/> is written against (<c>c.stateCode</c>,
/// <c>c.ruleId</c>, <c>c.tenantId</c>, and the document's <c>id</c>).
/// </summary>
[Collection(CosmosEmulatorFixture.CollectionName)]
[Trait("Category", CosmosEmulator.Category)]
public sealed class PaRuleRepositoryCosmosTests : IAsyncLifetime
{
    private const string ContainerName = "prior-auth-rules";

    private readonly CosmosEmulatorFixture _cosmos;
    private Container _container = null!;
    private PaRuleRepositoryCosmos _repo = null!;

    public PaRuleRepositoryCosmosTests(CosmosEmulatorFixture cosmos) => _cosmos = cosmos;

    public async Task InitializeAsync()
    {
        if (!_cosmos.Available) return;

        var client = _cosmos.CreateClient(serializerOptions: new CosmosSerializationOptions
        {
            PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase,
        });
        var database = await _cosmos.CreateDatabaseAsync(client, "pa_rules");
        _container = await CosmosEmulatorFixture.CreateContainerAsync(database, ContainerName, "/stateCode");

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CosmosDb:DatabaseName"] = database.Id,
            ["PriorAuthRuleEngine:RulesContainer"] = ContainerName,
        }).Build();
        _repo = new PaRuleRepositoryCosmos(client, configuration, NullLogger<PaRuleRepositoryCosmos>.Instance);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Reads the stored document directly, bypassing the repository's queries.</summary>
    private async Task<PaRuleDocument?> StoredAsync(string? tenantId)
    {
        var id = IsolationRules.DocumentId(IsolationRules.Rule(tenantId, "probe"));
        try
        {
            return (await _container.ReadItemAsync<PaRuleDocument>(id, new PartitionKey(IsolationRules.State))).Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // ── D-16: writes stay in their scope ──────────────────────────────────────

    [SkippableFact]
    public async Task Upsert_ByTenantA_DoesNotTouchTenantBOrPlatform()
    {
        _cosmos.SkipIfUnavailable();
        await _repo.UpsertAsync(IsolationRules.Rule(null, "platform rule"));
        await _repo.UpsertAsync(IsolationRules.Rule(IsolationRules.TenantB, "B's rule"));

        await _repo.UpsertAsync(IsolationRules.Rule(IsolationRules.TenantA, "A's rule"));

        (await StoredAsync(null))!.RuleName.Should().Be("platform rule");
        (await StoredAsync(IsolationRules.TenantB))!.RuleName.Should().Be("B's rule");
        (await StoredAsync(IsolationRules.TenantA))!.RuleName.Should().Be("A's rule");
    }

    [SkippableFact]
    public async Task Delete_ByTenantA_RemovesOnlyTenantAsRule()
    {
        _cosmos.SkipIfUnavailable();
        await _repo.UpsertAsync(IsolationRules.Rule(null, "platform rule"));
        await _repo.UpsertAsync(IsolationRules.Rule(IsolationRules.TenantB, "B's rule"));
        await _repo.UpsertAsync(IsolationRules.Rule(IsolationRules.TenantA, "A's rule"));

        await _repo.DeleteAsync(IsolationRules.RuleId, IsolationRules.State, IsolationRules.TenantA);

        (await StoredAsync(IsolationRules.TenantA)).Should().BeNull();
        (await StoredAsync(IsolationRules.TenantB)).Should().NotBeNull("tenant A's delete must not reach tenant B");
        (await StoredAsync(null)).Should().NotBeNull("tenant A's delete must not reach the platform rule");
    }

    [SkippableFact]
    public async Task Delete_OfAPlatformRule_LeavesTenantOverridesAlone()
    {
        _cosmos.SkipIfUnavailable();
        await _repo.UpsertAsync(IsolationRules.Rule(null, "platform rule"));
        await _repo.UpsertAsync(IsolationRules.Rule(IsolationRules.TenantB, "B's rule"));

        await _repo.DeleteAsync(IsolationRules.RuleId, IsolationRules.State, tenantId: null);

        (await StoredAsync(null)).Should().BeNull();
        (await StoredAsync(IsolationRules.TenantB)).Should().NotBeNull();
    }

    // ── D-17: ListAsync runs the query it built ───────────────────────────────

    [SkippableFact]
    public async Task List_ByTenant_ReturnsOnlyThatTenantsRules()
    {
        _cosmos.SkipIfUnavailable();
        await _repo.UpsertAsync(IsolationRules.Rule(null, "platform rule"));
        await _repo.UpsertAsync(IsolationRules.Rule(IsolationRules.TenantA, "A's rule"));
        await _repo.UpsertAsync(IsolationRules.Rule(IsolationRules.TenantB, "B's rule"));

        var rules = await _repo.ListAsync(tenantId: IsolationRules.TenantA);

        rules.Should().ContainSingle().Which.RuleName.Should().Be("A's rule");
    }

    [SkippableFact]
    public async Task List_ByTenantAndState_AppliesBothFilters()
    {
        _cosmos.SkipIfUnavailable();
        await _repo.UpsertAsync(IsolationRules.Rule(IsolationRules.TenantA, "A's TX rule"));
        var other = IsolationRules.Rule(IsolationRules.TenantA, "A's FL rule");
        other.StateCode = "FL";
        await _repo.UpsertAsync(other);
        await _repo.UpsertAsync(IsolationRules.Rule(IsolationRules.TenantB, "B's TX rule"));

        var rules = await _repo.ListAsync(tenantId: IsolationRules.TenantA, stateCode: IsolationRules.State);

        rules.Should().ContainSingle().Which.RuleName.Should().Be("A's TX rule");
    }

    [SkippableFact]
    public async Task List_ByState_ReturnsEveryScopeInThatState()
    {
        _cosmos.SkipIfUnavailable();
        await _repo.UpsertAsync(IsolationRules.Rule(null, "platform rule"));
        await _repo.UpsertAsync(IsolationRules.Rule(IsolationRules.TenantA, "A's rule"));

        var rules = await _repo.ListAsync(stateCode: IsolationRules.State);

        rules.Select(r => r.RuleName).Should().BeEquivalentTo("platform rule", "A's rule");
    }
}
