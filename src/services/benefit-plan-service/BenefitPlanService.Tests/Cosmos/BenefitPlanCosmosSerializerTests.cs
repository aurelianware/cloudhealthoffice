using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BenefitPlanService.Models;
using BenefitPlanService.Models.Benefits;
using BenefitPlanService.Repositories;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.BenefitEngine.Persistence;
using CloudHealthOffice.BenefitEngine.Services;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Infrastructure.Serialization;
using CloudHealthOffice.NcciEngine.Domain;
using CloudHealthOffice.NcciEngine.Models;
using CloudHealthOffice.NcciEngine.Persistence;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.PriorAuthRuleEngine.Persistence;
using CloudHealthOffice.PriorAuthRuleEngine.SeedRules;
using CloudHealthOffice.ProviderEnrollmentService.Cache;
using CloudHealthOffice.ProviderEnrollmentService.Models;
using CloudHealthOffice.Testing.Cosmos;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using EnrollmentLob = CloudHealthOffice.ProviderEnrollmentService.Models.LineOfBusiness;
using PlanLob = BenefitPlanService.Models.LineOfBusiness;
using PlanType = BenefitPlanService.Models.PlanType;
using FamilyAccumulatorModel = BenefitPlanService.Models.FamilyAccumulatorModel;
using NetworkTier = BenefitPlanService.Models.NetworkTier;

namespace BenefitPlanService.Tests.Cosmos;

/// <summary>
/// Which <see cref="CosmosClient"/> the benefit-plan host resolves in Cosmos
/// mode (D-31). No emulator needed: the client is only built, never called.
/// </summary>
public sealed class BenefitPlanCosmosClientRegistrationTests
{
    [Fact]
    public void Host_ResolvesTheSharedSystemTextJsonClient()
    {
        using var host = new BenefitPlanCosmosHost("unused", endpoint: "http://localhost:1/");

        _ = host.Services.GetRequiredService<CosmosClient>();

        Assert.True(host.HostClientBuilt);
        Assert.IsType<CosmosSystemTextJsonSerializer>(host.HostSerializer);
    }
}

/// <summary>
/// D-31: every Cosmos repository the benefit-plan host resolves, written and
/// read back on the emulator through the host's own service collection and
/// <see cref="CosmosClient"/> registration (see <see cref="BenefitPlanCosmosHost"/>).
/// Each test also reads the stored document raw and checks its shape: an
/// <c>"id"</c> property, camelCase names, string enums and no stored nulls,
/// which is what <see cref="CosmosSystemTextJsonSerializer"/> writes and
/// what the repositories' queries and partition keys expect.
/// </summary>
[Trait("Category", CosmosEmulator.Category)]
[Collection(CosmosEmulatorFixture.CollectionName)]
public sealed class BenefitPlanCosmosSerializerTests(CosmosEmulatorFixture cosmos) : IAsyncLifetime
{
    private readonly string _tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
    private Database _db = null!;
    private BenefitPlanCosmosHost _host = null!;

    public async Task InitializeAsync()
    {
        cosmos.SkipIfUnavailable();
        var admin = cosmos.CreateClient(new CosmosSystemTextJsonSerializer());
        _db = await cosmos.CreateDatabaseAsync(admin, "bp_d31");
        _host = new BenefitPlanCosmosHost(_db.Id);
    }

    public Task DisposeAsync()
    {
        _host?.Dispose();
        return Task.CompletedTask;
    }

    private async Task Containers(params (string Name, string PartitionKeyPath)[] containers)
    {
        foreach (var (name, path) in containers)
            await CosmosEmulatorFixture.CreateContainerAsync(_db, name, path);
    }

    private T Resolve<T>(IServiceScope scope) where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    /// <summary>The stored document as JSON, read without any serializer.</summary>
    private async Task<JsonElement> Raw(string container, string id, string partitionKey)
    {
        using var response = await _db.GetContainer(container).ReadItemStreamAsync(id, new PartitionKey(partitionKey));
        Assert.True(response.IsSuccessStatusCode, $"raw read of {container}/{id} returned {(int)response.StatusCode}");
        using var doc = await JsonDocument.ParseAsync(response.Content);
        return doc.RootElement.Clone();
    }

    /// <summary>Shape every document written through the shared serializer has.</summary>
    private static void AssertSharedShape(JsonElement doc)
    {
        Assert.True(doc.TryGetProperty("id", out _), "document has no \"id\": " + doc);
        Assert.False(doc.TryGetProperty("Id", out _), "document has a PascalCase \"Id\": " + doc);
        AssertCamelCaseNoNulls(doc, "$");
    }

    private static void AssertCamelCaseNoNulls(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.StartsWith('_')) continue; // Cosmos system properties
                    Assert.True(char.IsLower(property.Name[0]), $"{path}.{property.Name} is not camelCase");
                    Assert.True(property.Value.ValueKind != JsonValueKind.Null, $"{path}.{property.Name} is stored as null");
                    AssertCamelCaseNoNulls(property.Value, $"{path}.{property.Name}");
                }
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in element.EnumerateArray())
                    AssertCamelCaseNoNulls(item, $"{path}[{i++}]");
                break;
        }
    }

    // ── Prior-auth rules (PriorAuthRuleEngine, container prior-auth-rules, /stateCode) ──

    private Task PaContainer() => Containers(("prior-auth-rules", "/stateCode"));

    [SkippableFact]
    public async Task PaRuleSeeder_SeedsThePlatformRules_ThatThenReadBack()
    {
        await PaContainer();
        var services = _host.Services; // builds the host, which captures the seeder's registration
        Assert.NotNull(_host.PaRuleSeederType);
        var seeder = (IHostedService)ActivatorUtilities.CreateInstance(services, _host.PaRuleSeederType!);

        await seeder.StartAsync(CancellationToken.None);

        var expected = TxMedicaidSeedRules.GetAll();
        using var scope = _host.Services.CreateScope();
        var repo = Resolve<IPaRuleRepository>(scope);
        var listed = await repo.ListAsync(tenantId: null, stateCode: "TX");
        Assert.Equal(
            expected.Select(r => r.RuleId).OrderBy(x => x),
            listed.Select(r => r.RuleId).OrderBy(x => x));

        var first = expected[0];
        var raw = await Raw("prior-auth-rules", first.Id, first.StateCode);
        AssertSharedShape(raw);
        Assert.Equal(first.RuleId, raw.GetProperty("ruleId").GetString());
        Assert.Equal("TX", raw.GetProperty("stateCode").GetString());
        Assert.Equal(JsonValueKind.String, raw.GetProperty("lob").ValueKind);
        Assert.False(raw.TryGetProperty("tenantId", out _), "a platform rule's null tenantId is omitted");

        // The engine's decision-time read finds the platform rule set.
        var star = await repo.GetRulesAsync(new RuleSetKey
        {
            StateCode = "TX", Lob = PaLineOfBusiness.Medicaid, Program = "STAR", TenantId = null,
        });
        Assert.Equal(
            expected.Where(r => r.Program == "STAR" && r.IsEnabled).Select(r => r.RuleId).OrderBy(x => x),
            star.Select(r => r.RuleId).OrderBy(x => x));

        // A second start finds every rule present and writes nothing new.
        await seeder.StartAsync(CancellationToken.None);
        Assert.Equal(listed.Count, (await repo.ListAsync(tenantId: null, stateCode: "TX")).Count);
    }

    [SkippableFact]
    public async Task SeedPlatformEndpoint_Seeds_ThenIsIdempotent()
    {
        await PaContainer();
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ChoDevelopmentAuth.UserToken(_tenant, ChoRolePermissions.TenantAdmin));

        var first = await client.PostAsync("/api/v1/prior-auth-rules/seed-platform", null);
        var firstBody = await first.Content.ReadAsStringAsync();
        Assert.True(first.StatusCode == HttpStatusCode.OK, $"{(int)first.StatusCode}: {firstBody}");
        var total = TxMedicaidSeedRules.GetAll().Count;
        using (var doc = JsonDocument.Parse(firstBody))
        {
            Assert.Equal(total, doc.RootElement.GetProperty("totalPlatformRules").GetInt32());
            Assert.Equal(total, doc.RootElement.GetProperty("seededRules").GetInt32());
        }

        var second = await client.PostAsync("/api/v1/prior-auth-rules/seed-platform", null);
        var secondBody = await second.Content.ReadAsStringAsync();
        Assert.True(second.StatusCode == HttpStatusCode.OK, $"{(int)second.StatusCode}: {secondBody}");
        using (var doc = JsonDocument.Parse(secondBody))
            Assert.Equal(0, doc.RootElement.GetProperty("seededRules").GetInt32());
    }

    [SkippableFact]
    public async Task PaRule_TenantUpsert_ReadsBack_AndDeletes()
    {
        await PaContainer();
        using var scope = _host.Services.CreateScope();
        var repo = Resolve<IPaRuleRepository>(scope);
        var rule = new PaRuleDocument
        {
            RuleId = "TEST-001", RuleName = "Tenant override", StateCode = "TX",
            Lob = PaLineOfBusiness.Medicaid, Program = "STAR", TenantId = _tenant,
            Category = RuleCategory.ClinicalCriteria, Scope = RuleScope.Tenant, Priority = 5,
            RuleType = "ProcedureRequiresAuth", ProcedureCodes = ["70553"], DenialCode = "A1",
        };

        await repo.UpsertAsync(rule);

        var raw = await Raw("prior-auth-rules", rule.Id, "TX");
        AssertSharedShape(raw);
        Assert.Equal(_tenant, raw.GetProperty("tenantId").GetString());

        var rules = await repo.GetRulesAsync(new RuleSetKey
        {
            StateCode = "TX", Lob = PaLineOfBusiness.Medicaid, Program = "STAR", TenantId = _tenant,
        });
        var read = Assert.Single(rules);
        Assert.Equal("TEST-001", read.RuleId);
        Assert.Equal(PaLineOfBusiness.Medicaid, read.Lob);
        Assert.Equal(RuleCategory.ClinicalCriteria, read.Category);
        Assert.Equal(["70553"], read.ProcedureCodes);

        Assert.Single(await repo.ListAsync(tenantId: _tenant, stateCode: "TX"));

        await repo.DeleteAsync("TEST-001", "TX");
        Assert.Empty(await repo.ListAsync(tenantId: _tenant, stateCode: "TX"));
    }

    // ── Benefit plans (BenefitPlanRepository, container BenefitPlans, /tenantId) ──

    private BenefitPlan Plan(string planId) => new()
    {
        TenantId = _tenant,
        PlanId = planId,
        VersionId = PlanVersionId.NewId(),
        VersionNumber = 1,
        PlanName = "Silver PPO " + planId,
        Payer = "Acme",
        EffectiveDate = new DateTime(2026, 1, 1),
        PlanType = PlanType.PPO,
        MetalLevel = MetalLevel.Silver,
        LineOfBusiness = PlanLob.Commercial,
        FamilyAccumulatorModel = FamilyAccumulatorModel.Embedded,
        CostSharing = new CostSharing { IndividualDeductible = 1500m, FamilyDeductible = 3000m, Coinsurance = 0.2m },
        NetworkTiers = { new NetworkTier { TierName = "In-Network", TierLevel = 1 } },
        Documents = { new PlanDocumentReference { DocType = PlanDocumentType.SBC, Location = "documentreference/sbc-1" } },
        PlanYearDefinition = new PlanYearDefinition
        {
            PlanYearStart = new DateTime(2026, 1, 1), PlanYearEnd = new DateTime(2026, 12, 31),
        },
        Benefits =
        {
            new MedicalBenefit { ServiceCategory = "Primary Care", CopayAmount = 25m },
            new PharmacyBenefit { ServiceCategory = "Pharmacy", FormularyTier = "Tier 1", DaysSupply = 30 },
            new DentalBenefit { ServiceCategory = "Dental", IsOrthodontic = true },
        },
    };

    [SkippableFact]
    public async Task BenefitPlan_DraftPublishAndSearch_RoundTrip()
    {
        await Containers((BenefitPlanCosmosHost.BenefitPlansContainer, "/tenantId"));
        using var scope = _host.Services.CreateScope();
        var repo = Resolve<IBenefitPlanRepository>(scope);
        Assert.IsType<BenefitPlanRepository>(repo);

        var draft = await repo.CreateDraftAsync(Plan("plan-a"));

        var raw = await Raw(BenefitPlanCosmosHost.BenefitPlansContainer, draft.Id, _tenant);
        AssertSharedShape(raw);
        Assert.Equal(_tenant, raw.GetProperty("tenantId").GetString());
        Assert.Equal("draft", raw.GetProperty("versionState").GetString());
        Assert.Equal("ppo", raw.GetProperty("planType").GetString());
        Assert.Equal("commercial", raw.GetProperty("lineOfBusiness").GetString());
        Assert.False(raw.TryGetProperty("terminationDate", out _), "a null terminationDate is omitted");
        Assert.Equal("pharmacy", raw.GetProperty("benefits")[1].GetProperty("benefitType").GetString());
        Assert.Equal("Tier 1", raw.GetProperty("benefits")[1].GetProperty("formularyTier").GetString());

        var readDraft = await repo.GetVersionAsync("plan-a", draft.VersionId, _tenant);
        Assert.NotNull(readDraft);
        Assert.Equal(PlanVersionState.Draft, readDraft!.VersionState);
        Assert.Equal(MetalLevel.Silver, readDraft.MetalLevel);
        Assert.Equal(1500m, readDraft.CostSharing.IndividualDeductible);
        Assert.Equal(new DateTime(2026, 12, 31), readDraft.PlanYearDefinition!.PlanYearEnd);
        Assert.Collection(readDraft.Benefits,
            b => Assert.IsType<MedicalBenefit>(b),
            b => Assert.Equal("Tier 1", Assert.IsType<PharmacyBenefit>(b).FormularyTier),
            b => Assert.True(Assert.IsType<DentalBenefit>(b).IsOrthodontic));

        readDraft.VersionState = PlanVersionState.Published;
        readDraft.PublishedAt = DateTime.UtcNow;
        await repo.PublishAndSupersedeAsync(readDraft, predecessor: null);

        var published = await repo.GetLatestPublishedAsync("plan-a", _tenant, DateTime.UtcNow);
        Assert.NotNull(published);
        Assert.Equal(draft.VersionId, published!.VersionId);
        Assert.Equal("published",
            (await Raw(BenefitPlanCosmosHost.BenefitPlansContainer, draft.Id, _tenant)).GetProperty("versionState").GetString());

        Assert.Equal(draft.Id, (await repo.GetByIdAsync(draft.Id, _tenant))!.Id);
        Assert.Single((await repo.ListVersionsAsync("plan-a", _tenant, 10, null)).Items);

        // Filters arrive as the API's enum names.
        Assert.Single(await repo.SearchAsync(_tenant, "Commercial", "PPO", "Silver", 1, 10));
        Assert.Empty(await repo.SearchAsync(_tenant, "Medicaid", null, null, 1, 10));

        Assert.True(await repo.UpdateNetworkTiersAsync(_tenant, "plan-a",
            [new NetworkTier { TierName = "Preferred", TierLevel = 1, NetworkId = "net-1" }]));
        Assert.Equal("net-1", (await repo.GetByIdAsync(draft.Id, _tenant))!.NetworkTiers.Single().NetworkId);
    }

    [SkippableFact]
    public async Task BenefitPlan_AmendSupersedeAndTerminate_RoundTrip()
    {
        await Containers((BenefitPlanCosmosHost.BenefitPlansContainer, "/tenantId"));
        using var scope = _host.Services.CreateScope();
        var repo = Resolve<IBenefitPlanRepository>(scope);

        var v1 = await repo.CreateDraftAsync(Plan("plan-b"));
        v1.VersionState = PlanVersionState.Published;
        await repo.PublishAndSupersedeAsync(v1, null);

        var v2 = Plan("plan-b");
        v2.VersionNumber = 2;
        v2.PredecessorVersionId = v1.VersionId;
        v2 = await repo.CreateDraftAsync(v2);
        v2.VersionState = PlanVersionState.Published;
        v1.VersionState = PlanVersionState.Superseded;
        v1.SupersededByVersionId = v2.VersionId;
        await repo.PublishAndSupersedeAsync(v2, v1);

        Assert.Equal(v2.VersionId, (await repo.GetLatestPublishedAsync("plan-b", _tenant, DateTime.UtcNow))!.VersionId);
        var old = await repo.GetVersionAsync("plan-b", v1.VersionId, _tenant);
        Assert.Equal(PlanVersionState.Superseded, old!.VersionState);
        Assert.Equal(v2.VersionId, old.SupersededByVersionId);

        v2.VersionState = PlanVersionState.Superseded;
        v2.SupersededAt = DateTime.UtcNow;
        v2.IsActive = false;
        Assert.True(await repo.TerminateVersionAsync(v2));
        Assert.Null(await repo.GetLatestPublishedAsync("plan-b", _tenant, DateTime.UtcNow));

        await repo.DeleteAsync(v1.Id, _tenant);
        Assert.Null(await repo.GetByIdAsync(v1.Id, _tenant));
    }

    // ── Plan-version transitions (CosmosPlanVersionTransitionRepository, PlanVersionTransitions, /tenantId) ──

    [SkippableFact]
    public async Task PlanVersionTransition_AppendAndList_RoundTrip()
    {
        await Containers(("PlanVersionTransitions", "/tenantId"));
        using var scope = _host.Services.CreateScope();
        var repo = Resolve<IPlanVersionTransitionRepository>(scope);
        Assert.IsType<CosmosPlanVersionTransitionRepository>(repo);

        var transition = await repo.AppendAsync(new PlanVersionTransition
        {
            TenantId = _tenant, PlanId = "plan-c", ToVersionId = "v1",
            TransitionType = PlanVersionTransitionType.Publish, ActorId = "user-1",
        });

        var raw = await Raw("PlanVersionTransitions", transition.Id, _tenant);
        AssertSharedShape(raw);
        Assert.Equal("publish", raw.GetProperty("transitionType").GetString());
        Assert.False(raw.TryGetProperty("fromVersionId", out _));

        var read = Assert.Single(await repo.ListAsync("plan-c", _tenant));
        Assert.Equal(PlanVersionTransitionType.Publish, read.TransitionType);
        Assert.Equal("v1", read.ToVersionId);
        Assert.Null(read.FromVersionId);
    }

    // ── Service-category mappings (ChoServiceCategoryMappingRepository, ServiceCategoryMappings, /tenantId) ──

    [SkippableFact]
    public async Task ServiceCategoryMapping_CrudAndAppliedRecord_RoundTrip()
    {
        await Containers(("ServiceCategoryMappings", "/tenantId"));
        using var scope = _host.Services.CreateScope();
        var repo = Resolve<ChoServiceCategoryMappingRepository>(scope);
        var planId = Guid.NewGuid();

        var tenantDefault = await repo.CreateAsync(new ServiceCategoryMapping
        {
            TenantId = _tenant, ServiceTypeCode = "98", ServiceTypeDescription = "Professional visit",
            Rules = [new ProcedureCodeRule { Id = Guid.NewGuid(), Priority = 1, CodePattern = "99213" }],
            EffectiveStart = new DateOnly(2026, 1, 1),
        });
        var planOverride = await repo.CreateAsync(new ServiceCategoryMapping
        {
            TenantId = _tenant, BenefitPlanId = planId, ServiceTypeCode = "98", ServiceTypeDescription = "Plan visit",
            Rules = [new ProcedureCodeRule { Id = Guid.NewGuid(), Priority = 1, CodePattern = "992*" }],
        });

        var raw = await Raw("ServiceCategoryMappings", tenantDefault.Id.ToString(), _tenant);
        AssertSharedShape(raw);
        Assert.False(raw.TryGetProperty("benefitPlanId", out _), "a tenant default's null benefitPlanId is omitted");
        Assert.Equal("2026-01-01", raw.GetProperty("effectiveStart").GetString());

        Assert.Equal(tenantDefault.Id, Assert.Single(await repo.ListAsync(_tenant, null)).Id);
        Assert.Equal(planOverride.Id, Assert.Single(await repo.ListAsync(_tenant, planId)).Id);

        var read = await repo.GetByIdAsync(_tenant, tenantDefault.Id);
        Assert.Equal("99213", read!.Rules.Single().CodePattern);
        Assert.Equal(new DateOnly(2026, 1, 1), read.EffectiveStart);

        read.ServiceTypeDescription = "Office visit";
        await repo.UpdateAsync(read);
        Assert.Equal("Office visit", (await repo.GetByIdAsync(_tenant, tenantDefault.Id))!.ServiceTypeDescription);

        Assert.True(await repo.DeleteAsync(_tenant, planOverride.Id));
        Assert.Empty(await repo.ListAsync(_tenant, planId));

        await repo.UpsertAsync(new SystemDefaultsAppliedRecord
        {
            TenantId = _tenant, AppliedSeedVersion = 3, AppliedAt = DateTimeOffset.UtcNow, MappingCount = 12,
        });
        var applied = await repo.GetAsync(_tenant);
        Assert.Equal(3, applied!.AppliedSeedVersion);
        Assert.Equal(12, applied.MappingCount);
        AssertSharedShape(await Raw("ServiceCategoryMappings", $"system-defaults-applied:{_tenant}", _tenant));
    }

    // ── Accumulators (BenefitEngine AccumulatorRepositoryCosmos, Accumulators, /tenantId) ──

    [SkippableFact]
    public async Task Accumulator_CreateReplaceAndDeleteByPlanYear_RoundTrip()
    {
        await Containers(("Accumulators", "/tenantId"));
        using var scope = _host.Services.CreateScope();
        var repo = Resolve<IAccumulatorRepository>(scope);
        Assert.IsType<AccumulatorRepositoryCosmos>(repo);
        var planId = Guid.NewGuid();

        var doc = new AccumulatorDocument
        {
            Id = AccumulatorDocument.MakeId(_tenant, "Individual", "M1", planId, "2026"),
            TenantId = _tenant, OwnerId = "M1", Scope = "Individual", BenefitPlanId = planId, PlanYear = "2026",
            Balances = [new AccumulatorBalance { Type = "IndividualDeductible", NetworkTier = "InNetwork", LimitAmount = 500m, AccumulatedAmount = 100m }],
        };
        var created = await repo.UpsertAsync(doc);
        Assert.Equal(1, created.Version);

        var raw = await Raw("Accumulators", doc.Id, _tenant);
        AssertSharedShape(raw);
        Assert.False(raw.TryGetProperty("cosmosETag", out _), "the ETag is not persisted");

        var read = await repo.GetAsync(_tenant, "M1", AccumulatorScope.Individual, planId, "2026");
        Assert.Equal(100m, read!.Balances.Single().AccumulatedAmount);
        read.Balances.Single().AccumulatedAmount = 150m;
        await repo.UpsertAsync(read);
        Assert.Equal(150m, (await repo.GetAsync(_tenant, "M1", AccumulatorScope.Individual, planId, "2026"))!.Balances.Single().AccumulatedAmount);

        await repo.DeleteByPlanYearAsync(_tenant, planId, "2026");
        Assert.Null(await repo.GetAsync(_tenant, "M1", AccumulatorScope.Individual, planId, "2026"));
    }

    // ── Fee schedules and provider contracts (FeeScheduleEngine, FeeSchedules / ProviderContracts, /tenantId) ──

    [SkippableFact]
    public async Task FeeScheduleAndContract_RoundTrip()
    {
        await Containers(("FeeSchedules", "/tenantId"), ("ProviderContracts", "/tenantId"));
        using var scope = _host.Services.CreateScope();
        var repo = Resolve<FeeScheduleRepositoryCosmos>(scope);

        var schedule = new FeeSchedule
        {
            Id = FeeSchedule.MakeId(_tenant, "Commercial 2026", new DateTime(2026, 1, 1)),
            TenantId = _tenant, Name = "Commercial 2026", Type = FeeScheduleType.Custom,
            EffectiveDate = new DateTime(2026, 1, 1),
            Lines = [new FeeScheduleLine { ProcedureCode = "99213", RateType = FeeScheduleRateType.FlatRate, Rate = 95.5m }],
        };
        await repo.UpsertAsync(schedule);
        var rawSchedule = await Raw("FeeSchedules", schedule.Id, _tenant);
        AssertSharedShape(rawSchedule);
        Assert.Equal(JsonValueKind.String, rawSchedule.GetProperty("type").ValueKind);

        var readSchedule = await repo.GetByIdAsync(_tenant, schedule.Id);
        Assert.Equal(95.5m, readSchedule!.Lines.Single().Rate);
        Assert.Equal(FeeScheduleType.Custom, readSchedule.Type);
        Assert.Single(await repo.ListAsync(_tenant));

        var contract = new ProviderContract
        {
            Id = ProviderContract.MakeId(_tenant, "1234567890", "plan-a"),
            TenantId = _tenant, ProviderNpi = "1234567890", PlanId = "plan-a",
            EffectiveDate = new DateTime(2026, 1, 1), NetworkStatus = NetworkStatus.InNetwork,
            FeeScheduleId = schedule.Id,
        };
        await repo.UpsertAsync(contract);
        AssertSharedShape(await Raw("ProviderContracts", contract.Id, _tenant));

        // An open-ended contract (TermDate null) is in force on the service date.
        var found = await repo.GetContractAsync(_tenant, "1234567890", null, "plan-a", new DateTime(2026, 6, 1));
        Assert.Equal(schedule.Id, found!.FeeScheduleId);
        Assert.Equal(NetworkStatus.InNetwork, found.NetworkStatus);
        Assert.Single(await repo.ListByProviderAsync(_tenant, "1234567890"));
    }

    // ── NCCI (NcciEngine, NcciPairs / MueEntries / NcciVersion / NcciLoadLedger, /tenantId) ──

    [SkippableFact]
    public async Task Ncci_QuarterVersionAndLedger_RoundTrip()
    {
        await Containers(("NcciPairs", "/tenantId"), ("MueEntries", "/tenantId"),
            ("NcciVersion", "/tenantId"), ("NcciLoadLedger", "/tenantId"));
        using var scope = _host.Services.CreateScope();
        var repo = Resolve<INcciRepository>(scope);
        Assert.Equal("NcciRepositoryCosmos", repo.GetType().Name);

        var pair = new NcciEditPair
        {
            Id = "p1", TenantId = _tenant, Column1Code = "29880", Column2Code = "29881",
            ModifierIndicator = NcciModifierIndicator.Allowed, EffectiveDate = new DateTime(2026, 1, 1),
        };
        var mue = new MueEntry
        {
            Id = "m1", TenantId = _tenant, ProcedureCode = "99213", MaxUnits = 1,
            AdjudicationIndicator = MueAdjudicationIndicator.ClaimLine, EffectiveDate = new DateTime(2026, 1, 1),
        };
        await repo.UpsertQuarterAsync(_tenant, "2026Q1", [pair], [mue]);

        var rawPair = await Raw("NcciPairs", "p1", _tenant);
        AssertSharedShape(rawPair);
        Assert.False(rawPair.TryGetProperty("terminationDate", out _));

        Assert.Equal("29881", (await repo.GetEditPairAsync(_tenant, "29880", "29881", new DateOnly(2026, 2, 1)))!.Column2Code);
        Assert.Equal(1, (await repo.GetMueEntryAsync(_tenant, "99213", new DateOnly(2026, 2, 1)))!.MaxUnits);

        await repo.SaveVersionAsync(new NcciTableVersion
        {
            TenantId = _tenant, Quarter = "2026Q1", ImportedAt = DateTime.UtcNow, EffectiveDate = new DateTime(2026, 1, 1),
            NcciPairCount = 1, MueEntryCount = 1,
        });
        Assert.Equal("2026Q1", (await repo.GetCurrentVersionAsync(_tenant))!.Quarter);
        AssertSharedShape(await Raw("NcciVersion", NcciTableVersion.CurrentId, _tenant));

        await repo.SaveLoadRecordAsync(new NcciLoadRecord
        {
            Id = "load-1", TenantId = _tenant, Quarter = "2026Q1", FileKind = NcciCmsFileKind.Ptp,
            Setting = "practitioner", Sha256 = "abc", RowsLoaded = 1, LoadedAt = DateTime.UtcNow,
        });
        Assert.Equal(NcciCmsFileKind.Ptp, (await repo.GetLoadRecordAsync(_tenant, "load-1"))!.FileKind);
        Assert.Single(await repo.ListLoadRecordsAsync(_tenant, "2026Q1"));
        AssertSharedShape(await Raw("NcciLoadLedger", "load-1", _tenant));
    }

    // ── Provider enrollment (ProviderEnrollmentService, enrollment-cache /stateCode, enrollment-tenant-config /tenantId) ──

    [SkippableFact]
    public async Task ProviderEnrollment_CacheAndTenantConfig_RoundTrip()
    {
        await Containers(("enrollment-cache", "/stateCode"), ("enrollment-tenant-config", "/tenantId"));
        using var scope = _host.Services.CreateScope();
        var enrollments = Resolve<IEnrollmentRepository>(scope);
        Assert.IsType<EnrollmentRepositoryCosmos>(enrollments);
        var npi = "9" + Random.Shared.NextInt64(100_000_000, 999_999_999);
        var mco = "mco-" + _tenant;

        await enrollments.UpsertAsync(new StateEnrollmentRecord
        {
            Npi = npi, StateCode = "TX", SourceSystem = "PEMS", Status = EnrollmentStatus.Active,
            EffectiveDate = new DateOnly(2025, 1, 1), ProviderType = ProviderTypeClassification.PhysicianMD,
            SupportedLobs = EnrollmentLob.Medicaid, McoParticipation = [mco],
            RevalidationDueDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10),
        });

        var read = await enrollments.GetAsync(npi, "TX");
        Assert.Equal(EnrollmentStatus.Active, read!.Status);
        Assert.Equal(ProviderTypeClassification.PhysicianMD, read.ProviderType);
        Assert.Single(await enrollments.GetAllStatesAsync(npi));
        Assert.Contains(await enrollments.GetActivePanelByMcoAsync("TX", mco), r => r.Npi == npi);
        Assert.Contains(await enrollments.GetProvidersWithRevalidationDueSoonAsync(30, "TX"), r => r.Npi == npi);

        var configs = Resolve<TenantEnrollmentConfigRepositoryCosmos>(scope);
        await configs.UpsertAsync(new TenantEnrollmentConfig
        {
            TenantId = _tenant, EnabledStateCodes = ["TX"], DefaultGateMode = EnrollmentGateMode.Warn,
        });
        AssertSharedShape(await Raw("enrollment-tenant-config", _tenant, _tenant));
        var config = await configs.GetAsync(_tenant);
        Assert.Equal(EnrollmentGateMode.Warn, config!.DefaultGateMode);
        Assert.Contains(await configs.ListAsync(), c => c.TenantId == _tenant);
        await configs.DeleteAsync(_tenant);
        Assert.Null(await configs.GetAsync(_tenant));

        await enrollments.DeleteAsync(npi, "TX");
        Assert.Null(await enrollments.GetAsync(npi, "TX"));
    }
}
