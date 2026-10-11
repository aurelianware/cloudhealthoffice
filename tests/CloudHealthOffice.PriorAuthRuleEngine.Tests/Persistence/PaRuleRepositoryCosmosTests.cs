using System.Net;
using CloudHealthOffice.Infrastructure.Serialization;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
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
/// The client uses the serializer production hosts use:
/// <see cref="CosmosSystemTextJsonSerializer"/>, which <c>AddChoDatabase</c>
/// configures (camelCase, nulls omitted, enums as camelCase strings). An
/// earlier version of these tests used a Newtonsoft camelCase client, under
/// which ints-for-enums and explicit nulls hid that <c>GetRulesAsync</c>
/// matched nothing on the real shape.
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

        var client = _cosmos.CreateClient(serializer: new CosmosSystemTextJsonSerializer());
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

    // ── GetRulesAsync on the production document shape (review of #1294) ────
    //
    // Under the production serializer a platform rule has NO tenantId field and
    // a rule with no program has NO program field (nulls are omitted), and LOB
    // is stored as the string "medicaid". The query matched IS_NULL(...) and an
    // int LOB, so it found nothing at all: every Cosmos-backed decision ran with
    // an empty rule set.

    [SkippableFact]
    public async Task GetRules_PlatformKey_ReturnsThePlatformRule()
    {
        _cosmos.SkipIfUnavailable();
        await _repo.UpsertAsync(IsolationRules.Rule(null, "platform rule"));
        await _repo.UpsertAsync(IsolationRules.Rule(IsolationRules.TenantA, "A's rule"));

        var rules = await _repo.GetRulesAsync(IsolationRules.Key(null));

        rules.Should().ContainSingle().Which.RuleName.Should().Be("platform rule");
    }

    [SkippableFact]
    public async Task GetRules_TenantKey_ReturnsOnlyThatTenantsRule()
    {
        _cosmos.SkipIfUnavailable();
        await _repo.UpsertAsync(IsolationRules.Rule(null, "platform rule"));
        await _repo.UpsertAsync(IsolationRules.Rule(IsolationRules.TenantA, "A's rule"));
        await _repo.UpsertAsync(IsolationRules.Rule(IsolationRules.TenantB, "B's rule"));

        var rules = await _repo.GetRulesAsync(IsolationRules.Key(IsolationRules.TenantA));

        rules.Should().ContainSingle().Which.RuleName.Should().Be("A's rule");
    }

    [SkippableFact]
    public async Task GetRules_MatchesProgramAndLineOfBusiness()
    {
        _cosmos.SkipIfUnavailable();
        var star = IsolationRules.Rule(null, "STAR rule", ruleId: "TX-STAR-1");
        star.Program = "STAR";
        var exchange = IsolationRules.Rule(null, "Exchange rule", ruleId: "TX-EXCH-1");
        exchange.Lob = PaLineOfBusiness.Exchange;
        await _repo.UpsertAsync(star);
        await _repo.UpsertAsync(exchange);
        await _repo.UpsertAsync(IsolationRules.Rule(null, "any-program Medicaid rule", ruleId: "TX-ANY-1"));

        (await _repo.GetRulesAsync(IsolationRules.Key(null) with { Program = "STAR" }))
            .Should().ContainSingle().Which.RuleName.Should().Be("STAR rule");
        (await _repo.GetRulesAsync(IsolationRules.Key(null) with { Lob = PaLineOfBusiness.Exchange }))
            .Should().ContainSingle().Which.RuleName.Should().Be("Exchange rule");
        (await _repo.GetRulesAsync(IsolationRules.Key(null)))
            .Should().ContainSingle().Which.RuleName.Should().Be("any-program Medicaid rule");
    }

    [SkippableFact]
    public async Task GetRules_ExcludesDisabledRules_AndOrdersByBandThenPriority()
    {
        _cosmos.SkipIfUnavailable();
        var clinical = IsolationRules.Rule(null, "clinical", ruleId: "R-CLIN");
        var exemption = IsolationRules.Rule(null, "exemption", ruleId: "R-EXEMPT");
        exemption.Category = RuleCategory.RegulatoryExemption;
        var disabled = IsolationRules.Rule(null, "disabled", ruleId: "R-OFF");
        disabled.IsEnabled = false;
        await _repo.UpsertAsync(clinical);
        await _repo.UpsertAsync(exemption);
        await _repo.UpsertAsync(disabled);

        var rules = await _repo.GetRulesAsync(IsolationRules.Key(null));

        // Band order is the enum's numeric order (RegulatoryExemption = 0 runs
        // first), not the alphabetical order of the stored strings.
        rules.Select(r => r.RuleName).Should().Equal("exemption", "clinical");
    }
}
