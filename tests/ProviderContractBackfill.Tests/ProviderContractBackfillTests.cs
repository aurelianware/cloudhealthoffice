using CloudHealthOffice.Testing.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;
using ProviderContractBackfill;
using EngineDomain = CloudHealthOffice.FeeScheduleEngine.Domain;
using EngineModels = CloudHealthOffice.FeeScheduleEngine.Models;
using LegalModels = ProviderContractsService.Models;

namespace ProviderContractBackfill.Tests;

/// <summary>
/// tools/ProviderContractBackfill on a real mongod (ADR 016 phase 3): the four
/// stores a provider's rates live in today, seeded as they actually look, and
/// the canonical contract the backfill builds from them.
///
/// <para>
/// The load-bearing tests are the conflicts. A backfill that silently picked a
/// winner where the stores disagree would change what a provider is paid
/// without anyone deciding to, so each disagreement must come back blocked,
/// named, and with the tenant's data untouched.
/// </para>
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class ProviderContractBackfillTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "t1";
    private const string Npi = "1234567890";
    private const string PlanId = "plan-a";
    private const string CommercialSchedule = "comm-2026";
    private const string OrthoSchedule = "ortho-2026";
    private const string MpfsSchedule = "mpfs-2026";

    private IMongoDatabase _db = null!;
    private string _dir = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("pcbackfill");
        _dir = Directory.CreateTempSubdirectory("pcbackfill").FullName;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await mongo.DropDatabaseAsync(_db);
        Directory.Delete(_dir, recursive: true);
    }

    // ═══════════════════════════════════════════════════════════════════
    // BUILDING THE CANONICAL CONTRACT
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AnFfsRateConfig_BecomesTheContractsDefaultTerm()
    {
        await SeedSchedulesAsync(CommercialSchedule);
        var legal = await SeedLegalAsync();
        await SeedFfsAsync(legal.Id, CommercialSchedule, percentage: 1.10m);

        var row = Single(await PlanAsync());

        Assert.Equal(PlanDecision.Ready, row.Decision);
        Assert.Equal($"{Tenant}:{Npi}:{PlanId}", row.CanonicalId);
        Assert.False(row.Replaces);

        var term = Assert.Single(row.Contract!.ReimbursementTerms);
        Assert.Equal(EngineModels.ReimbursementMethod.FeeScheduleRef, term.Method);
        Assert.Equal(CommercialSchedule, term.FeeScheduleId);
        Assert.Equal(1.10m, term.Multiplier);
        Assert.True(term.Scope.IsUnconditional);
        Assert.False(term.ModifierRules!.MultipleProcedureReduction is false); // defaulted true
    }

    [Fact]
    public async Task AnFfsRateConfigsModifierToggles_BecomeTheTermsModifierRules()
    {
        await SeedSchedulesAsync(CommercialSchedule);
        var legal = await SeedLegalAsync();
        await SeedFfsAsync(legal.Id, CommercialSchedule, multipleProcedureReduction: false, bilateral: false);

        var term = Assert.Single(Single(await PlanAsync()).Contract!.ReimbursementTerms);

        Assert.False(term.ModifierRules!.MultipleProcedureReduction);
        Assert.False(term.ModifierRules!.BilateralAdjustment);
    }

    [Fact]
    public async Task EngineContractLines_BecomeScopedTermsAheadOfTheDefault()
    {
        await SeedSchedulesAsync(CommercialSchedule, OrthoSchedule);
        var legal = await SeedLegalAsync();
        await SeedEngineAsync(CommercialSchedule, lines:
        [
            new EngineModels.ProviderContractLine
            {
                ProcedureCodeFrom = "27400", ProcedureCodeTo = "27599", FeeScheduleId = OrthoSchedule,
            },
        ]);

        var row = Single(await PlanAsync());

        Assert.Equal(PlanDecision.Ready, row.Decision);
        Assert.True(row.Replaces);

        var terms = row.Contract!.ReimbursementTerms;
        Assert.Equal(2, terms.Count);
        // The carve-out must come first, or the default would answer every line.
        Assert.Equal(OrthoSchedule, terms[0].FeeScheduleId);
        Assert.Equal("27400", terms[0].Scope.ProcedureCodeFrom);
        Assert.Equal("27599", terms[0].Scope.ProcedureCodeTo);
        Assert.Equal(CommercialSchedule, terms[1].FeeScheduleId);
        Assert.True(terms[1].Scope.IsUnconditional);
    }

    [Fact]
    public async Task TheLegalContractsIdentityAndScope_AreCarriedOntoTheCanonicalContract()
    {
        await SeedSchedulesAsync(CommercialSchedule);
        var legal = await SeedLegalAsync(planIds: [PlanId, "plan-b"]);
        await SeedFfsAsync(legal.Id, CommercialSchedule);

        var rows = await PlanAsync();

        // One canonical contract per plan: the engine looks a contract up by one.
        Assert.Equal(2, rows.Count);
        Assert.Equal([$"{Tenant}:{Npi}:plan-a", $"{Tenant}:{Npi}:plan-b"], rows.Select(r => r.CanonicalId));

        var contract = rows[0].Contract!;
        Assert.Equal(legal.Id, contract.LegalContractId);
        Assert.Equal(legal.ContractNumber, contract.ContractNumber);
        Assert.Equal("Commercial", contract.LineOfBusiness);
        Assert.Equal(["plan-a", "plan-b"], contract.PlanIds);
        Assert.Equal("legal", contract.Origin);
        Assert.Equal(EngineDomain.NetworkStatus.InNetwork, contract.NetworkStatus);
        Assert.Equal(legal.EffectiveDate, contract.EffectiveDate);

        // The terms are the rate sheet, so the legacy fields stay empty.
        Assert.Empty(contract.FeeScheduleId);
        Assert.Empty(contract.ContractLines);
    }

    [Fact]
    public async Task ALegalContractThatAlreadyCarriesItsOwnTerms_IsUsedAsItIs()
    {
        await SeedSchedulesAsync(MpfsSchedule);
        var legal = await SeedLegalAsync();
        legal.ReimbursementTerms =
        [
            new EngineModels.ReimbursementTerm
            {
                Id = "term-1",
                Method = EngineModels.ReimbursementMethod.PercentOfMedicare,
                BaseScheduleId = MpfsSchedule,
                Percent = 0.85m,
            },
        ];
        await Legal().ReplaceOneAsync(c => c.Id == legal.Id, legal);
        // A stale FFS config that the finished sheet supersedes.
        await SeedFfsAsync(legal.Id, CommercialSchedule);

        var row = Single(await PlanAsync());

        Assert.Equal(PlanDecision.Ready, row.Decision);
        var term = Assert.Single(row.Contract!.ReimbursementTerms);
        Assert.Equal(EngineModels.ReimbursementMethod.PercentOfMedicare, term.Method);
        Assert.Equal(0.85m, term.Percent);
    }

    [Fact]
    public async Task ACapitatedContract_BecomesACapitationTerm()
    {
        await SeedSchedulesAsync(CommercialSchedule);
        var legal = await SeedLegalAsync(methodology: LegalModels.PaymentMethodology.FullCapitation);
        await SeedCapitationAsync(legal.Id);

        var term = Assert.Single(Single(await PlanAsync()).Contract!.ReimbursementTerms);

        Assert.Equal(EngineModels.ReimbursementMethod.Capitation, term.Method);
        Assert.Equal("cap-1", term.CapitationRateConfigId);
    }

    [Fact]
    public async Task TheSameRateInTwoStores_IsOneTerm()
    {
        // The FFS config and the engine contract agree: one rate, recorded twice.
        await SeedSchedulesAsync(CommercialSchedule);
        var legal = await SeedLegalAsync();
        await SeedFfsAsync(legal.Id, CommercialSchedule, percentage: 1.0m);
        await SeedEngineAsync(CommercialSchedule);

        var row = Single(await PlanAsync());

        Assert.Equal(PlanDecision.Ready, row.Decision);
        Assert.Single(row.Contract!.ReimbursementTerms);
    }

    [Fact]
    public async Task TwoRatesInSuccession_AreARateHistory_NotAConflict()
    {
        await SeedSchedulesAsync(CommercialSchedule, OrthoSchedule);
        var legal = await SeedLegalAsync();
        await SeedFfsAsync(legal.Id, CommercialSchedule,
            effective: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            termination: new DateTime(2026, 5, 31, 0, 0, 0, DateTimeKind.Utc),
            number: "FFS-1");
        await SeedFfsAsync(legal.Id, OrthoSchedule,
            effective: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            number: "FFS-2");

        var row = Single(await PlanAsync());

        Assert.Equal(PlanDecision.Ready, row.Decision);
        Assert.Equal(2, row.Contract!.ReimbursementTerms.Count);
    }

    [Fact]
    public async Task AnEngineContractWithNoLegalAgreement_IsCanonicalAndFlaggedForReview()
    {
        await SeedSchedulesAsync(CommercialSchedule);
        await SeedEngineAsync(CommercialSchedule);

        var row = Single(await PlanAsync());

        Assert.Equal(PlanDecision.Review, row.Decision);
        Assert.Equal("engine-only", row.Contract!.Origin);
        Assert.Null(row.Contract!.LegalContractId);
        Assert.Contains(row.Notes, n => n.Contains("no legal agreement"));
        Assert.Equal(CommercialSchedule, Assert.Single(row.Contract!.ReimbursementTerms).FeeScheduleId);
    }

    [Fact]
    public async Task AContractWithNoRatesAnywhere_IsSkipped()
    {
        await SeedSchedulesAsync(CommercialSchedule);
        await SeedLegalAsync();

        var row = Single(await PlanAsync());

        Assert.Equal(PlanDecision.Skipped, row.Decision);
        Assert.Null(row.Contract);
    }

    [Fact]
    public async Task ADraftContract_IsWrittenButFlagged()
    {
        await SeedSchedulesAsync(CommercialSchedule);
        var legal = await SeedLegalAsync(status: LegalModels.ProviderContractStatus.Draft);
        await SeedFfsAsync(legal.Id, CommercialSchedule);

        var row = Single(await PlanAsync());

        Assert.Equal(PlanDecision.Review, row.Decision);
        Assert.NotNull(row.Contract);
        Assert.Contains(row.Notes, n => n.Contains("Draft, not Active"));
    }

    [Fact]
    public async Task ATieredExceptionContract_IsFlagged_AndProjectsAsInNetwork()
    {
        await SeedSchedulesAsync(CommercialSchedule);
        var legal = await SeedLegalAsync(network: LegalModels.NetworkParticipationStatus.TieredException);
        await SeedFfsAsync(legal.Id, CommercialSchedule);

        var row = Single(await PlanAsync());

        Assert.Equal(PlanDecision.Review, row.Decision);
        Assert.Equal(EngineDomain.NetworkStatus.InNetwork, row.Contract!.NetworkStatus);
        Assert.Contains(row.Notes, n => n.Contains("TieredException"));
    }

    [Fact]
    public async Task LesserOfBilled_IsCarriedFromTheEngineContract()
    {
        await SeedSchedulesAsync(CommercialSchedule);
        var legal = await SeedLegalAsync();
        await SeedFfsAsync(legal.Id, CommercialSchedule);
        await SeedEngineAsync(CommercialSchedule, lesserOfBilled: true);

        Assert.True(Single(await PlanAsync()).Contract!.LesserOfBilledCharges);
    }

    // ═══════════════════════════════════════════════════════════════════
    // CONFLICTS — NOTHING IS GUESSED
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TwoStoresNamingDifferentDefaultSchedules_IsBlocked()
    {
        await SeedSchedulesAsync(CommercialSchedule, OrthoSchedule);
        var legal = await SeedLegalAsync();
        await SeedFfsAsync(legal.Id, CommercialSchedule);
        await SeedEngineAsync(OrthoSchedule);

        var row = Single(await PlanAsync());

        Assert.Equal(PlanDecision.Blocked, row.Decision);
        Assert.Null(row.Contract);
        Assert.Contains(row.Conflicts, c => c.Contains("more than one rate prices every claim line"));
    }

    [Fact]
    public async Task TheSameScheduleAtDifferentPercentages_IsBlocked()
    {
        await SeedSchedulesAsync(CommercialSchedule);
        var legal = await SeedLegalAsync();
        await SeedFfsAsync(legal.Id, CommercialSchedule, percentage: 1.10m, number: "FFS-1");
        await SeedFfsAsync(legal.Id, CommercialSchedule, percentage: 1.25m, number: "FFS-2");

        Assert.Equal(PlanDecision.Blocked, Single(await PlanAsync()).Decision);
    }

    [Fact]
    public async Task AHybridContractWithBothKindsOfRate_IsBlocked()
    {
        await SeedSchedulesAsync(CommercialSchedule);
        var legal = await SeedLegalAsync(methodology: LegalModels.PaymentMethodology.Hybrid);
        await SeedFfsAsync(legal.Id, CommercialSchedule);
        await SeedCapitationAsync(legal.Id);

        var row = Single(await PlanAsync());

        Assert.Equal(PlanDecision.Blocked, row.Decision);
        Assert.Contains(row.Conflicts, c => c.Contains("hybrid contract"));
    }

    [Fact]
    public async Task ATermNamingAScheduleTheTenantDoesNotHave_IsBlocked()
    {
        await SeedSchedulesAsync(CommercialSchedule);
        var legal = await SeedLegalAsync();
        await SeedFfsAsync(legal.Id, "no-such-schedule");

        var row = Single(await PlanAsync());

        Assert.Equal(PlanDecision.Blocked, row.Decision);
        Assert.Contains(row.Conflicts, c => c.Contains("no-such-schedule"));
    }

    [Fact]
    public async Task ALegalContractWithNoPlanIds_IsBlocked()
    {
        await SeedSchedulesAsync(CommercialSchedule);
        var legal = await SeedLegalAsync(planIds: []);
        await SeedFfsAsync(legal.Id, CommercialSchedule);

        var row = Single(await PlanAsync());

        Assert.Equal(PlanDecision.Blocked, row.Decision);
        Assert.Contains(row.Conflicts, c => c.Contains("names no plan ids"));
    }

    [Fact]
    public async Task TwoLegalContractsForTheSameProviderAndPlan_AreBothBlocked()
    {
        await SeedSchedulesAsync(CommercialSchedule);
        var first = await SeedLegalAsync(id: "legal-1");
        var second = await SeedLegalAsync(id: "legal-2");
        await SeedFfsAsync(first.Id, CommercialSchedule);
        await SeedFfsAsync(second.Id, CommercialSchedule);

        var rows = await PlanAsync();

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(PlanDecision.Blocked, r.Decision));
        Assert.All(rows, r => Assert.Contains(r.Conflicts, c => c.Contains("more than one legal")));
    }

    [Fact]
    public async Task AnFfsRateConfigWithNoFeeSchedule_IsBlocked()
    {
        await SeedSchedulesAsync(CommercialSchedule);
        var legal = await SeedLegalAsync();
        await SeedFfsAsync(legal.Id, feeScheduleId: null);

        var row = Single(await PlanAsync());

        Assert.Equal(PlanDecision.Blocked, row.Decision);
        Assert.Contains(row.Conflicts, c => c.Contains("names no fee schedule"));
    }

    // ═══════════════════════════════════════════════════════════════════
    // PLAN AND APPLY
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Apply_WithoutExecute_WritesNothing()
    {
        await SeedSchedulesAsync(CommercialSchedule);
        var legal = await SeedLegalAsync();
        await SeedFfsAsync(legal.Id, CommercialSchedule);

        var plan = await WritePlanAsync();
        var exit = await Program.ApplyAsync(Stores(), Line($"apply --plan {plan} --confirm-database {_db.DatabaseNamespace.DatabaseName}"));

        Assert.Equal(0, exit);
        Assert.Equal(0, await Engine().CountDocumentsAsync(FilterDefinition<EngineModels.ProviderContract>.Empty));
    }

    [Fact]
    public async Task Apply_WithExecute_WritesTheCanonicalContract()
    {
        await SeedSchedulesAsync(CommercialSchedule);
        var legal = await SeedLegalAsync();
        await SeedFfsAsync(legal.Id, CommercialSchedule, percentage: 1.10m);

        var plan = await WritePlanAsync();
        var exit = await Program.ApplyAsync(Stores(), Execute(plan));

        Assert.Equal(0, exit);
        var stored = await Engine().Find(c => c.Id == $"{Tenant}:{Npi}:{PlanId}").SingleAsync();
        Assert.Equal(1.10m, Assert.Single(stored.ReimbursementTerms).Multiplier);
        Assert.Equal("legal", stored.Origin);
    }

    [Fact]
    public async Task Apply_RunTwice_ReplacesRatherThanDuplicates()
    {
        await SeedSchedulesAsync(CommercialSchedule);
        var legal = await SeedLegalAsync();
        await SeedFfsAsync(legal.Id, CommercialSchedule);

        var plan = await WritePlanAsync();
        await Program.ApplyAsync(Stores(), Execute(plan));
        await Program.ApplyAsync(Stores(), Execute(plan));

        Assert.Equal(1, await Engine().CountDocumentsAsync(FilterDefinition<EngineModels.ProviderContract>.Empty));
    }

    [Fact]
    public async Task Apply_WithTheWrongHash_ChangesNothing()
    {
        await SeedSchedulesAsync(CommercialSchedule);
        var legal = await SeedLegalAsync();
        await SeedFfsAsync(legal.Id, CommercialSchedule);

        var plan = await WritePlanAsync();
        var line = Line($"apply --plan {plan} --confirm-database {_db.DatabaseNamespace.DatabaseName} " +
                        "--execute --sha256 0000 --operator tester");

        await Assert.ThrowsAsync<PlanHashMismatchException>(() => Program.ApplyAsync(Stores(), line));
        Assert.Equal(0, await Engine().CountDocumentsAsync(FilterDefinition<EngineModels.ProviderContract>.Empty));
    }

    [Fact]
    public async Task Apply_AgainstTheWrongDatabase_ChangesNothing()
    {
        await SeedSchedulesAsync(CommercialSchedule);
        var legal = await SeedLegalAsync();
        await SeedFfsAsync(legal.Id, CommercialSchedule);

        var plan = await WritePlanAsync();

        await Assert.ThrowsAsync<EnvironmentMismatchException>(() =>
            Program.ApplyAsync(Stores(), Line($"apply --plan {plan} --confirm-database some-other-database")));
        Assert.Equal(0, await Engine().CountDocumentsAsync(FilterDefinition<EngineModels.ProviderContract>.Empty));
    }

    [Fact]
    public async Task Apply_WritesWhatIsReady_AndReportsWhatIsBlocked()
    {
        await SeedSchedulesAsync(CommercialSchedule, OrthoSchedule);

        var good = await SeedLegalAsync(id: "legal-good", npi: "1111111111");
        await SeedFfsAsync(good.Id, CommercialSchedule);

        var bad = await SeedLegalAsync(id: "legal-bad", npi: "2222222222");
        await SeedFfsAsync(bad.Id, CommercialSchedule, number: "FFS-A");
        await SeedEngineAsync(OrthoSchedule, npi: "2222222222");

        var plan = await WritePlanAsync();
        var exit = await Program.ApplyAsync(Stores(), Execute(plan));

        // Exit 1: something was blocked, so the run is not clean even though the
        // writable contracts were written.
        Assert.Equal(1, exit);
        var stored = await Engine().Find(FilterDefinition<EngineModels.ProviderContract>.Empty).ToListAsync();
        Assert.Equal([$"{Tenant}:1111111111:{PlanId}", $"{Tenant}:2222222222:{PlanId}"],
            stored.Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal));
        // The blocked provider's engine contract is untouched: still the legacy shape.
        var untouched = stored.Single(c => c.ProviderNpi == "2222222222");
        Assert.Equal(OrthoSchedule, untouched.FeeScheduleId);
        Assert.Empty(untouched.ReimbursementTerms);
    }

    [Fact]
    public async Task ThePlanRoundTripsThroughJson_AndSummarizesAsCsv()
    {
        await SeedSchedulesAsync(CommercialSchedule);
        var legal = await SeedLegalAsync();
        await SeedFfsAsync(legal.Id, CommercialSchedule, percentage: 1.10m);

        var plan = await new Backfill(Stores(), _ => { }).PlanAsync([]);
        var reread = BackfillPlan.FromJson(plan.ToJson());

        Assert.Equal(plan.DatabaseName, reread.DatabaseName);
        var row = Assert.Single(reread.Contracts);
        Assert.Equal(PlanDecision.Ready, row.Decision);
        Assert.Equal(1.10m, Assert.Single(row.Contract!.ReimbursementTerms).Multiplier);

        var csv = plan.ToCsv();
        Assert.StartsWith("tenantId,decision,canonicalId", csv);
        Assert.Contains($"\"{Tenant}:{Npi}:{PlanId}\"", csv);
        Assert.Contains("\"Ready\"", csv);
    }

    [Fact]
    public async Task ThePlanIsScopedToTheTenantsItIsGiven()
    {
        await SeedSchedulesAsync(CommercialSchedule);
        var mine = await SeedLegalAsync(id: "legal-t1");
        await SeedFfsAsync(mine.Id, CommercialSchedule);

        var other = await SeedLegalAsync(id: "legal-t2", tenantId: "t2");
        await SeedFfsAsync(other.Id, CommercialSchedule, tenantId: "t2");

        var all = await PlanAsync();
        var justT1 = await PlanAsync(Tenant);

        Assert.Equal(2, all.Count);
        Assert.Equal(Tenant, Assert.Single(justT1).TenantId);
    }

    [Fact]
    public async Task Plan_WritesNothingToTheDatabase()
    {
        await SeedSchedulesAsync(CommercialSchedule);
        var legal = await SeedLegalAsync();
        await SeedFfsAsync(legal.Id, CommercialSchedule);

        await WritePlanAsync();

        Assert.Equal(0, await Engine().CountDocumentsAsync(FilterDefinition<EngineModels.ProviderContract>.Empty));
    }

    // ═══════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════

    private string DatabaseName => _db.DatabaseNamespace.DatabaseName;

    private Stores Stores() => new(mongo.Client, DatabaseName, Collections.Default);

    private IMongoCollection<LegalModels.ProviderContract> Legal()
        => _db.GetCollection<LegalModels.ProviderContract>("provider_contracts");

    private IMongoCollection<EngineModels.ProviderContract> Engine()
        => _db.GetCollection<EngineModels.ProviderContract>("ProviderContracts");

    private static CommandLine Line(string command) => CommandLine.Parse(command.Split(' '));

    private static CommandLine Execute(string plan)
        => Line($"apply --plan {plan} --execute --sha256 {BackfillPlan.Sha256Of(plan)} --operator tester " +
                $"--confirm-database {Path.GetFileName(Path.GetDirectoryName(plan))}");

    private async Task<List<PlannedContract>> PlanAsync(params string[] tenants)
        => (await new Backfill(Stores(), _ => { }).PlanAsync(tenants)).Contracts;

    private static PlannedContract Single(List<PlannedContract> rows) => Assert.Single(rows);

    /// <summary>The plan on disk, in a directory named after the database so <see cref="Execute"/> can confirm it.</summary>
    private async Task<string> WritePlanAsync()
    {
        var directory = Path.Combine(_dir, DatabaseName);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "plan.json");

        var exit = await Program.PlanAsync(Stores(),
            Line($"plan --out {path} --confirm-database {DatabaseName}"));
        Assert.Equal(0, exit);

        return path;
    }

    private async Task SeedSchedulesAsync(params string[] ids)
    {
        var collection = _db.GetCollection<BsonDocument>("FeeSchedules");
        foreach (var id in ids)
            await collection.InsertOneAsync(new BsonDocument { { "_id", id }, { "TenantId", Tenant } });
    }

    private async Task<LegalModels.ProviderContract> SeedLegalAsync(
        string id = "legal-1",
        string npi = Npi,
        string tenantId = Tenant,
        List<string>? planIds = null,
        LegalModels.PaymentMethodology methodology = LegalModels.PaymentMethodology.FeeForService,
        LegalModels.ProviderContractStatus status = LegalModels.ProviderContractStatus.Active,
        LegalModels.NetworkParticipationStatus network = LegalModels.NetworkParticipationStatus.Participating)
    {
        var contract = new LegalModels.ProviderContract
        {
            Id = id,
            TenantId = tenantId,
            ContractNumber = $"CTR-{npi}-2026",
            ProviderNPI = npi,
            ProviderName = "Dr. Chen",
            ProviderTin = "123456789",
            ProviderType = LegalModels.ProviderType.Individual,
            LineOfBusiness = LegalModels.LineOfBusiness.Commercial,
            PaymentMethodology = methodology,
            NetworkStatus = network,
            Status = status,
            PlanIds = planIds ?? [PlanId],
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        await Legal().InsertOneAsync(contract);
        return contract;
    }

    private Task SeedFfsAsync(
        string contractId,
        string? feeScheduleId = CommercialSchedule,
        string tenantId = Tenant,
        decimal percentage = 1.0m,
        bool multipleProcedureReduction = true,
        bool bilateral = true,
        DateTime? effective = null,
        DateTime? termination = null,
        string number = "FFS-1")
    {
        var document = new BsonDocument
        {
            { "_id", $"{contractId}:{number}" },
            { "TenantId", tenantId },
            { "ContractId", contractId },
            { "RateConfigNumber", number },
            { "FeeSchedulePercentage", BsonDecimal128.Create(percentage) },
            { "MultipleProcedureReduction", multipleProcedureReduction },
            { "BilateralAdjustment", bilateral },
            { "EffectiveDate", effective ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
        };

        if (feeScheduleId is not null)
            document["FeeScheduleId"] = feeScheduleId;
        if (termination is not null)
            document["TerminationDate"] = termination.Value;

        return _db.GetCollection<BsonDocument>("ffs_rate_configs").InsertOneAsync(document);
    }

    private Task SeedCapitationAsync(string contractId, string id = "cap-1")
        => _db.GetCollection<BsonDocument>("capitation_rate_configs").InsertOneAsync(new BsonDocument
        {
            { "_id", id },
            { "TenantId", Tenant },
            { "ContractId", contractId },
            { "RateConfigNumber", "CAP-1" },
            { "EffectiveDate", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
        });

    private Task SeedEngineAsync(
        string feeScheduleId,
        string npi = Npi,
        List<EngineModels.ProviderContractLine>? lines = null,
        bool lesserOfBilled = false)
        => Engine().InsertOneAsync(new EngineModels.ProviderContract
        {
            Id = EngineModels.ProviderContract.MakeId(Tenant, npi, PlanId),
            TenantId = Tenant,
            ProviderNpi = npi,
            PlanId = PlanId,
            NetworkStatus = EngineDomain.NetworkStatus.InNetwork,
            FeeScheduleId = feeScheduleId,
            ContractLines = lines ?? [],
            LesserOfBilledCharges = lesserOfBilled,
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
}
