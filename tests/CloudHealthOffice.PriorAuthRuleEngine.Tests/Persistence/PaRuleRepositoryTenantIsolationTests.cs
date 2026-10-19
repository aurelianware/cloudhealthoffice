using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.PriorAuthRuleEngine.Persistence;
using CloudHealthOffice.Testing.Mongo;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace CloudHealthOffice.PriorAuthRuleEngine.Tests.Persistence;

/// <summary>Rule documents shared by the Mongo isolation and query suites.</summary>
internal static class IsolationRules
{
    public const string State = "TX";
    public const string RuleId = "TX-SHARED-001";
    public const string TenantA = "tenant-a";
    public const string TenantB = "tenant-b";

    public static PaRuleDocument Rule(string? tenantId, string name, string ruleId = RuleId) => new()
    {
        RuleId = ruleId,
        RuleName = name,
        StateCode = State,
        Lob = PaLineOfBusiness.Medicaid,
        Program = null,
        TenantId = tenantId,
        Category = RuleCategory.ClinicalCriteria,
        Scope = tenantId is null ? RuleScope.Platform : RuleScope.Tenant,
        Priority = 10,
        RuleType = "ProcedureRequiresAuth",
        ProcedureCodes = ["70553"],
    };

    /// <summary>The repositories' composite document id: state:lob:program:tenant:ruleId.</summary>
    public static string DocumentId(PaRuleDocument r) =>
        $"{r.StateCode}:{(int)r.Lob}:{r.Program ?? "any"}:{r.TenantId ?? "platform"}:{r.RuleId}";

    public static RuleSetKey Key(string? tenantId) => new()
    {
        StateCode = State,
        Lob = PaLineOfBusiness.Medicaid,
        Program = null,
        TenantId = tenantId,
    };
}

/// <summary>
/// D-16 / G-10 / T-5. Rule writes must be confined to the writer's own scope
/// (one tenant, or the platform). The Mongo repository used to filter Upsert,
/// BulkUpsert and Delete on RuleId + StateCode only, so tenant A's write of a
/// RuleId that tenant B (or the platform) also has reached B's document.
/// Runs against a real mongod (EphemeralMongo).
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class PaRuleRepositoryMongoTenantIsolationTests : IAsyncLifetime
{
    private readonly MongoRunnerFixture _mongo;
    private readonly IMongoDatabase _database;
    private readonly PaRuleRepositoryMongo _repo;

    public PaRuleRepositoryMongoTenantIsolationTests(MongoRunnerFixture mongo)
    {
        _mongo = mongo;
        _database = mongo.CreateDatabase("pa_rules_isolation");
        _repo = new PaRuleRepositoryMongo(
            _database, new ConfigurationBuilder().Build(), NullLogger<PaRuleRepositoryMongo>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    /// <summary>
    /// Puts another scope's rule in place directly, NOT through the
    /// repository, so the setup does not depend on the write path under test.
    /// The id is the repository's composite document id.
    /// </summary>
    private Task SeedAsync(PaRuleDocument rule)
    {
        rule.Id = IsolationRules.DocumentId(rule);
        return _database.GetCollection<PaRuleDocument>("prior_auth_rules").InsertOneAsync(rule);
    }

    private async Task<PaRuleDocument?> OnlyRuleAsync(string? tenantId)
        => (await _repo.GetRulesAsync(IsolationRules.Key(tenantId))).SingleOrDefault();

    /// <summary>The name of the scope's one rule, or null when it has none.</summary>
    private async Task<string?> RuleNameAsync(string? tenantId) => (await OnlyRuleAsync(tenantId))?.RuleName;

    [Fact]
    public async Task Upsert_ByTenantA_DoesNotTouchTenantBsRuleWithTheSameRuleId()
    {
        await SeedAsync(IsolationRules.Rule(IsolationRules.TenantB, "B's rule"));

        await _repo.UpsertAsync(IsolationRules.Rule(IsolationRules.TenantA, "A's rule"));

        (await RuleNameAsync(IsolationRules.TenantB)).Should().Be("B's rule");
        (await RuleNameAsync(IsolationRules.TenantA)).Should().Be("A's rule");
    }

    [Fact]
    public async Task Upsert_TenantOverrideOfAPlatformRuleId_LeavesThePlatformRuleAlone()
    {
        await SeedAsync(IsolationRules.Rule(null, "platform rule"));

        await _repo.UpsertAsync(IsolationRules.Rule(IsolationRules.TenantA, "A's override"));

        (await RuleNameAsync(null)).Should().Be("platform rule");
        (await RuleNameAsync(IsolationRules.TenantA)).Should().Be("A's override");
    }

    [Fact]
    public async Task Upsert_SameTenantSameRule_ReplacesItsOwnDocument()
    {
        await _repo.UpsertAsync(IsolationRules.Rule(IsolationRules.TenantA, "v1"));
        await _repo.UpsertAsync(IsolationRules.Rule(IsolationRules.TenantA, "v2"));

        (await _repo.ListAsync(IsolationRules.TenantA)).Should().ContainSingle()
            .Which.RuleName.Should().Be("v2");
    }

    [Fact]
    public async Task BulkUpsert_ByTenantA_DoesNotTouchTenantBOrPlatform()
    {
        await SeedAsync(IsolationRules.Rule(null, "platform rule"));
        await SeedAsync(IsolationRules.Rule(IsolationRules.TenantB, "B's rule"));

        await _repo.BulkUpsertAsync([IsolationRules.Rule(IsolationRules.TenantA, "A's rule")]);

        (await RuleNameAsync(null)).Should().Be("platform rule");
        (await RuleNameAsync(IsolationRules.TenantB)).Should().Be("B's rule");
        (await RuleNameAsync(IsolationRules.TenantA)).Should().Be("A's rule");
    }

    [Fact]
    public async Task Delete_ByTenantA_RemovesOnlyTenantAsRule()
    {
        await SeedAsync(IsolationRules.Rule(null, "platform rule"));
        await SeedAsync(IsolationRules.Rule(IsolationRules.TenantB, "B's rule"));
        await SeedAsync(IsolationRules.Rule(IsolationRules.TenantA, "A's rule"));

        await _repo.DeleteAsync(IsolationRules.RuleId, IsolationRules.State, IsolationRules.TenantA);

        (await OnlyRuleAsync(IsolationRules.TenantA)).Should().BeNull();
        (await RuleNameAsync(IsolationRules.TenantB)).Should().Be("B's rule");
        (await RuleNameAsync(null)).Should().Be("platform rule");
    }

    [Fact]
    public async Task Delete_OfAPlatformRule_LeavesTenantOverridesAlone()
    {
        await SeedAsync(IsolationRules.Rule(null, "platform rule"));
        await SeedAsync(IsolationRules.Rule(IsolationRules.TenantB, "B's rule"));

        await _repo.DeleteAsync(IsolationRules.RuleId, IsolationRules.State, tenantId: null);

        (await OnlyRuleAsync(null)).Should().BeNull();
        (await RuleNameAsync(IsolationRules.TenantB)).Should().Be("B's rule");
    }
}
