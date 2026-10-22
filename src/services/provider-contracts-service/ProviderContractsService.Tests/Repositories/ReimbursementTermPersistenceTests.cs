using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using ProviderContractsService.Models;
using ProviderContractsService.Repositories;
using EngineModels = CloudHealthOffice.FeeScheduleEngine.Models;

namespace ProviderContractsService.Tests.Repositories;

/// <summary>
/// A contract's rate sheet survives a round trip through MongoDB (ADR 016
/// phase 2). The terms are the pricing engine's own documents, so this is the
/// test that the shape this service writes is the shape the engine reads: a
/// term's computed members are not persisted, its defaults come back as
/// defaults, and a document written without a field this build knows still
/// deserializes.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class ReimbursementTermPersistenceTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "tenant-pc-terms";
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("pc_terms");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private static ICurrentActor Actor
        => Mock.Of<ICurrentActor>(a => a.UserId == "token-user" && a.TenantId == Tenant);

    private MongoProviderContractRepository Repo()
        => new(_db, Actor, Mock.Of<ILogger<MongoProviderContractRepository>>());

    [Fact]
    public async Task EveryMethodAndOption_RoundTrips()
    {
        var contract = await Repo().CreateAsync(Contract("c-terms-1"));
        contract.ReimbursementTerms =
        [
            new EngineModels.ReimbursementTerm
            {
                Id = "term-ortho",
                Description = "Orthopaedic carve-out at 115%",
                Method = EngineModels.ReimbursementMethod.FeeScheduleRef,
                FeeScheduleId = "ortho-2026",
                Multiplier = 1.15m,
                EffectiveDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                TermDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
                CarveOut = true,
                LesserOfBilled = true,
                Scope = new EngineModels.ReimbursementTermScope
                {
                    ProcedureCodeFrom = "27400",
                    ProcedureCodeTo = "27599",
                    RevenueCodes = ["0450"],
                    PlacesOfService = ["21", "22"],
                    ClaimType = EngineModels.ContractClaimType.Professional,
                },
                ModifierRules = new EngineModels.TermModifierRules
                {
                    MultipleProcedureReduction = false,
                    BilateralAdjustment = false,
                },
            },
            new EngineModels.ReimbursementTerm
            {
                Id = "term-medicare",
                Method = EngineModels.ReimbursementMethod.PercentOfMedicare,
                BaseScheduleId = "mpfs-2026",
                Percent = 0.80m,
            },
            new EngineModels.ReimbursementTerm
            {
                Id = "term-drg",
                Method = EngineModels.ReimbursementMethod.CaseRate,
                FeeScheduleId = "drg-2026",
                Scope = new EngineModels.ReimbursementTermScope
                {
                    ClaimType = EngineModels.ContractClaimType.InstitutionalInpatient,
                },
                StopLoss = new EngineModels.StopLossTerm
                {
                    ThresholdBilled = 150_000m,
                    PercentAboveThreshold = 0.80m,
                },
            },
            new EngineModels.ReimbursementTerm
            {
                Id = "term-cap",
                Method = EngineModels.ReimbursementMethod.Capitation,
                CapitationRateConfigId = "cap-1",
            },
        ];

        await Repo().UpdateAsync(contract);
        var stored = (await Repo().GetByIdAsync("c-terms-1"))!;

        stored.ReimbursementTerms.Select(t => t.Id)
            .Should().Equal("term-ortho", "term-medicare", "term-drg", "term-cap");

        var ortho = stored.ReimbursementTerms[0];
        ortho.Method.Should().Be(EngineModels.ReimbursementMethod.FeeScheduleRef);
        ortho.FeeScheduleId.Should().Be("ortho-2026");
        ortho.Multiplier.Should().Be(1.15m);
        ortho.EffectiveDate.Should().Be(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        ortho.TermDate.Should().Be(new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc));
        ortho.CarveOut.Should().BeTrue();
        ortho.LesserOfBilled.Should().BeTrue();
        ortho.Scope.ProcedureCodeFrom.Should().Be("27400");
        ortho.Scope.ProcedureCodeTo.Should().Be("27599");
        ortho.Scope.RevenueCodes.Should().Equal("0450");
        ortho.Scope.PlacesOfService.Should().Equal("21", "22");
        ortho.Scope.ClaimType.Should().Be(EngineModels.ContractClaimType.Professional);
        ortho.ModifierRules!.MultipleProcedureReduction.Should().BeFalse();
        ortho.ModifierRules!.BilateralAdjustment.Should().BeFalse();

        var medicare = stored.ReimbursementTerms[1];
        medicare.BaseScheduleId.Should().Be("mpfs-2026");
        medicare.Percent.Should().Be(0.80m);
        // A method's unused fields stay unset, so a reader cannot mistake one.
        medicare.FeeScheduleId.Should().BeNull();
        medicare.StopLoss.Should().BeNull();

        var drg = stored.ReimbursementTerms[2];
        drg.Scope.ClaimType.Should().Be(EngineModels.ContractClaimType.InstitutionalInpatient);
        drg.StopLoss!.ThresholdBilled.Should().Be(150_000m);
        drg.StopLoss!.PercentAboveThreshold.Should().Be(0.80m);
        // Stored, not evaluated: the engine pends a line priced by this term.
        drg.UnevaluatedProvisionReason.Should().Contain("stop-loss");

        stored.ReimbursementTerms[3].CapitationRateConfigId.Should().Be("cap-1");
    }

    [Fact]
    public async Task ATermStoredWithNoOptions_ComesBackWithTheEngineDefaults()
    {
        var contract = await Repo().CreateAsync(Contract("c-terms-2"));
        contract.ReimbursementTerms =
        [
            new EngineModels.ReimbursementTerm
            {
                Id = "term-plain",
                Method = EngineModels.ReimbursementMethod.FeeScheduleRef,
                FeeScheduleId = "comm-2026",
            },
        ];

        await Repo().UpdateAsync(contract);
        var term = (await Repo().GetByIdAsync("c-terms-2"))!.ReimbursementTerms.Single();

        // 1.0 is "the schedule's own rate", which is what makes a term-shaped
        // contract price identically to the legacy one it was built from.
        term.Multiplier.Should().Be(1.0m);
        term.Scope.IsUnconditional.Should().BeTrue();
        term.ModifierRules.Should().BeNull();
        term.LesserOfBilled.Should().BeNull();
        term.CarveOut.Should().BeFalse();
        term.UnevaluatedProvisionReason.Should().BeNull();
    }

    [Fact]
    public async Task AContractStoredBeforeTermsExisted_ReadsAsOneWithNoRates()
    {
        // The field is additive: every contract in the store today predates it,
        // and must read as a contract the engine prices the legacy way.
        var collection = _db.GetCollection<ProviderContract>("provider_contracts");
        var legacy = Contract("c-terms-3");
        legacy.TenantId = Tenant;
        await collection.InsertOneAsync(legacy);

        var stored = (await Repo().GetByIdAsync("c-terms-3"))!;

        stored.ReimbursementTerms.Should().BeEmpty();
    }

    private static ProviderContract Contract(string id) => new()
    {
        Id = id,
        ContractNumber = "CTR-1234567890-2026",
        ProviderNPI = "1234567890",
        ProviderName = "Dr. Chen",
        ProviderType = ProviderType.Individual,
        LineOfBusiness = LineOfBusiness.Commercial,
        PaymentMethodology = PaymentMethodology.FeeForService,
        NetworkStatus = NetworkParticipationStatus.Participating,
        Status = ProviderContractStatus.Active,
        EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };
}
