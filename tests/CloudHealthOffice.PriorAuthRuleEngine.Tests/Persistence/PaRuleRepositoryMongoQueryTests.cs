using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.PriorAuthRuleEngine.Persistence;
using CloudHealthOffice.Testing.Mongo;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace CloudHealthOffice.PriorAuthRuleEngine.Tests.Persistence;

/// <summary>
/// <see cref="PaRuleRepositoryMongo"/> reads against a real mongod
/// (EphemeralMongo), ported from the Cosmos emulator suite (D-17 and the
/// review of #1294): <c>ListAsync</c> applies its tenant and state filters,
/// and <c>GetRulesAsync</c> returns the platform or the tenant rule set for
/// the key, matches program and line of business, drops disabled rules and
/// orders by band then priority.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class PaRuleRepositoryMongoQueryTests : IAsyncLifetime
{
    private readonly MongoRunnerFixture _mongo;
    private readonly IMongoDatabase _database;
    private readonly PaRuleRepositoryMongo _repo;

    public PaRuleRepositoryMongoQueryTests(MongoRunnerFixture mongo)
    {
        _mongo = mongo;
        _database = mongo.CreateDatabase("pa_rules_query");
        _repo = new PaRuleRepositoryMongo(
            _database, new ConfigurationBuilder().Build(), NullLogger<PaRuleRepositoryMongo>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    // ── ListAsync applies the filters it was given ─────────────────────────

    [Fact]
    public async Task List_ByTenant_ReturnsOnlyThatTenantsRules()
    {
        await _repo.UpsertAsync(IsolationRules.Rule(null, "platform rule"));
        await _repo.UpsertAsync(IsolationRules.Rule(IsolationRules.TenantA, "A's rule"));
        await _repo.UpsertAsync(IsolationRules.Rule(IsolationRules.TenantB, "B's rule"));

        var rules = await _repo.ListAsync(tenantId: IsolationRules.TenantA);

        rules.Should().ContainSingle().Which.RuleName.Should().Be("A's rule");
    }

    [Fact]
    public async Task List_ByTenantAndState_AppliesBothFilters()
    {
        await _repo.UpsertAsync(IsolationRules.Rule(IsolationRules.TenantA, "A's TX rule"));
        var other = IsolationRules.Rule(IsolationRules.TenantA, "A's FL rule");
        other.StateCode = "FL";
        await _repo.UpsertAsync(other);
        await _repo.UpsertAsync(IsolationRules.Rule(IsolationRules.TenantB, "B's TX rule"));

        var rules = await _repo.ListAsync(tenantId: IsolationRules.TenantA, stateCode: IsolationRules.State);

        rules.Should().ContainSingle().Which.RuleName.Should().Be("A's TX rule");
    }

    [Fact]
    public async Task List_ByState_ReturnsEveryScopeInThatState()
    {
        await _repo.UpsertAsync(IsolationRules.Rule(null, "platform rule"));
        await _repo.UpsertAsync(IsolationRules.Rule(IsolationRules.TenantA, "A's rule"));

        var rules = await _repo.ListAsync(stateCode: IsolationRules.State);

        rules.Select(r => r.RuleName).Should().BeEquivalentTo("platform rule", "A's rule");
    }

    // ── GetRulesAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetRules_PlatformKey_ReturnsThePlatformRule()
    {
        await _repo.UpsertAsync(IsolationRules.Rule(null, "platform rule"));
        await _repo.UpsertAsync(IsolationRules.Rule(IsolationRules.TenantA, "A's rule"));

        var rules = await _repo.GetRulesAsync(IsolationRules.Key(null));

        rules.Should().ContainSingle().Which.RuleName.Should().Be("platform rule");
    }

    [Fact]
    public async Task GetRules_TenantKey_ReturnsOnlyThatTenantsRule()
    {
        await _repo.UpsertAsync(IsolationRules.Rule(null, "platform rule"));
        await _repo.UpsertAsync(IsolationRules.Rule(IsolationRules.TenantA, "A's rule"));
        await _repo.UpsertAsync(IsolationRules.Rule(IsolationRules.TenantB, "B's rule"));

        var rules = await _repo.GetRulesAsync(IsolationRules.Key(IsolationRules.TenantA));

        rules.Should().ContainSingle().Which.RuleName.Should().Be("A's rule");
    }

    [Fact]
    public async Task GetRules_MatchesProgramAndLineOfBusiness()
    {
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

    [Fact]
    public async Task GetRules_ExcludesDisabledRules_AndOrdersByBandThenPriority()
    {
        var clinical = IsolationRules.Rule(null, "clinical", ruleId: "R-CLIN");
        var exemption = IsolationRules.Rule(null, "exemption", ruleId: "R-EXEMPT");
        exemption.Category = RuleCategory.RegulatoryExemption;
        var disabled = IsolationRules.Rule(null, "disabled", ruleId: "R-OFF");
        disabled.IsEnabled = false;
        await _repo.UpsertAsync(clinical);
        await _repo.UpsertAsync(exemption);
        await _repo.UpsertAsync(disabled);

        var rules = await _repo.GetRulesAsync(IsolationRules.Key(null));

        // Band order is the enum's numeric order (RegulatoryExemption = 0 runs first).
        rules.Select(r => r.RuleName).Should().Equal("exemption", "clinical");
    }
    [Fact]
    public async Task ConcurrentFirstUpserts_KeepOneRulePerScope_WithoutStartupFailures()
    {
        for (var round = 0; round < 12; round++)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writes = new List<Task>();
            foreach (var tenant in new string?[] { null, IsolationRules.TenantA, IsolationRules.TenantB })
            for (var writer = 0; writer < 24; writer++)
            {
                var rule = IsolationRules.Rule(tenant, tenant ?? "platform");
                rule.RuleId = "CONCURRENT-SEED-" + round;
                writes.Add(Task.Run(async () => { await gate.Task; await _repo.UpsertAsync(rule); }));
            }
            gate.SetResult();
            await Task.WhenAll(writes);
        }
        foreach (var tenant in new string?[] { null, IsolationRules.TenantA, IsolationRules.TenantB })
        {
            var rules = (await _repo.ListAsync(tenantId: tenant)).Where(r => r.TenantId == tenant).ToList();
            rules.Should().HaveCount(12);
            rules.Select(r => r.RuleId).Distinct().Should().HaveCount(12);
            rules.Should().OnlyContain(r => r.RuleName == (tenant ?? "platform"));
        }
    }

    [Fact]
    public async Task DuplicateIdInAnotherTenant_RemainsAnError_AndDoesNotOverwriteIt()
    {
        var platform = IsolationRules.Rule(null, "platform");
        var foreign = IsolationRules.Rule(IsolationRules.TenantB, "foreign-untouched");
        foreign.Id = $"{platform.StateCode}:{(int)platform.Lob}:{platform.Program ?? "any"}:platform:{platform.RuleId}";
        await _database.GetCollection<PaRuleDocument>("prior_auth_rules").InsertOneAsync(foreign);
        var act = async () => await _repo.UpsertAsync(platform);
        await act.Should().ThrowAsync<MongoWriteException>();
        var remaining = await _database.GetCollection<PaRuleDocument>("prior_auth_rules").Find(r => r.Id == foreign.Id).SingleAsync();
        remaining.TenantId.Should().Be(IsolationRules.TenantB);
        remaining.RuleName.Should().Be("foreign-untouched");
    }
}
