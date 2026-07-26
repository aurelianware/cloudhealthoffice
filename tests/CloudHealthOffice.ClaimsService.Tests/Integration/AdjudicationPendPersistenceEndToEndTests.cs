using ClaimsService.Adapters;
using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Models.Messaging;
using ClaimsService.Repositories;
using ClaimsService.Services;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.CobEngine.Domain;
using CloudHealthOffice.CobEngine.Services;
using CloudHealthOffice.Infrastructure.Messaging;
using CloudHealthOffice.NcciEngine.Configuration;
using CloudHealthOffice.NcciEngine.Domain;
using CloudHealthOffice.NcciEngine.Persistence;
using CloudHealthOffice.NcciEngine.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Integration;

/// <summary>
/// Pend-persistence defect fix — end-to-end orchestrator coverage (task
/// requirement 2). Unlike <see cref="AdjudicationWithNcciEndToEndTests"/> and
/// <see cref="AdjudicationWithCobEndToEndTests"/> (which stand in a
/// no-op <c>TestPersistenceStage</c>), these tests wire the REAL
/// <see cref="PersistenceStage"/> against a repository substitute that
/// applies the SAME precedence predicate the real Cosmos/Mongo
/// repositories use (<see cref="ClaimRepository.IsFinalDisposition"/> —
/// production code, not a re-implementation) to a simple in-memory claim
/// state. This proves the orchestrator → PersistenceStage → repository
/// call chain actually reaches <c>ClaimStatus.Pended</c> + <c>PendDetails</c>
/// for a submitted claim fixture, with no live Cosmos/Mongo/cluster.
/// </summary>
public class AdjudicationPendPersistenceEndToEndTests
{
    private const string TenantId = "tenant-1";
    private const string MemberId = "MEM-1";

    private readonly IClaimAdapter _adapter = Substitute.For<IClaimAdapter>();
    private readonly IBenefitPlanResolver _planResolver = Substitute.For<IBenefitPlanResolver>();
    private readonly IMemberResolver _memberResolver = Substitute.For<IMemberResolver>();
    private readonly ICoverageResolver _coverageResolver = Substitute.For<ICoverageResolver>();
    private readonly IClaimVersionEventPublisher _eventPublisher = Substitute.For<IClaimVersionEventPublisher>();
    private readonly IMessageBus _messageBus = Substitute.For<IMessageBus>();
    private readonly ClaimAdapterFactory _factory;

    public AdjudicationPendPersistenceEndToEndTests()
    {
        _adapter.Platform.Returns("cho");
        var cache = new ClaimTenantConfigCache(
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IConfiguration>(),
            NullLogger<ClaimTenantConfigCache>.Instance);
        _factory = new ClaimAdapterFactory(new[] { _adapter }, cache, NullLogger<ClaimAdapterFactory>.Instance);

        _memberResolver.GetMemberAsync(TenantId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedMember
            {
                MemberId = MemberId,
                DateOfBirth = new DateTime(1980, 6, 15, 0, 0, 0, DateTimeKind.Utc),
                EffectiveDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
    }

    [Fact]
    public async Task Ncci_bundled_pair_orchestrator_run_persists_Pended_status_and_PendDetails()
    {
        SetupAdapterReturningClaim(BuildBundledPairClaim());

        var state = new FakeClaimState();
        var ncciStage = new NcciEditsStage(
            BuildSeededNcciEngine(),
            Options.Create(new TenantEnforcementPolicyOptions { NcciMode = NcciEnforcementMode.PendForReview }),
            NullLogger<NcciEditsStage>.Instance);
        var orch = BuildOrchestrator(new IClaimAdjudicationStage[] { ncciStage }, state);

        await orch.AdjudicateAsync(BuildSubmittedMessage("ver-persist-ncci"), BuildMessageContext("ver-persist-ncci"), CancellationToken.None);

        Assert.Equal(ClaimStatus.Pended, state.Status);
        Assert.NotNull(state.PendDetails);
        Assert.Equal("NCCI", state.PendDetails!.PendCode);
        Assert.Single(state.PendDetails.EditFailures);
    }

    [Fact]
    public async Task Cob_secondary_detected_orchestrator_run_persists_Pended_status_and_PendDetails()
    {
        SetupAdapterReturningClaim(BuildCobClaim());

        var coverageClient = Substitute.For<ICoverageClient>();
        coverageClient.GetCobEntriesAsync(TenantId, MemberId, Arg.Any<DateTime>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new CobEntry { PayerName = "Aetna", PayerId = "AET", CoverageSequence = "P" },
            });
        var cobStage = new CoordinationOfBenefitsStage(
            coverageClient,
            new PayerOrderService(),
            Options.Create(new TenantEnforcementPolicyOptions { CobMode = CobEnforcementMode.PendForSecondary }),
            NullLogger<CoordinationOfBenefitsStage>.Instance);

        var state = new FakeClaimState();
        var orch = BuildOrchestrator(new IClaimAdjudicationStage[] { cobStage }, state);

        await orch.AdjudicateAsync(BuildSubmittedMessage("ver-persist-cob"), BuildMessageContext("ver-persist-cob"), CancellationToken.None);

        Assert.Equal(ClaimStatus.Pended, state.Status);
        Assert.NotNull(state.PendDetails);
        Assert.Equal("COB", state.PendDetails!.PendCode);
        Assert.Contains("Aetna", state.PendDetails.PendReason);
    }

    [Fact]
    public async Task Ncci_deny_mode_orchestrator_run_persists_Denied_status_not_Pended()
    {
        // Deny outweighs Pend in the resolved outcome even though NCCI still
        // populates PendDetails as an audit-trail snapshot — the claim must
        // NOT be left in ClaimStatus.Pended. Async terminal outcomes should
        // still become observable, so Deny projects to ClaimStatus.Denied.
        SetupAdapterReturningClaim(BuildBundledPairClaim());

        var state = new FakeClaimState();
        var ncciStage = new NcciEditsStage(
            BuildSeededNcciEngine(),
            Options.Create(new TenantEnforcementPolicyOptions { NcciMode = NcciEnforcementMode.Deny }),
            NullLogger<NcciEditsStage>.Instance);
        var orch = BuildOrchestrator(new IClaimAdjudicationStage[] { ncciStage }, state);

        await orch.AdjudicateAsync(BuildSubmittedMessage("ver-persist-ncci-deny"), BuildMessageContext("ver-persist-ncci-deny"), CancellationToken.None);

        Assert.NotEqual(ClaimStatus.Pended, state.Status);
        Assert.Equal(ClaimStatus.Denied, state.Status);
    }

    private void SetupAdapterReturningClaim(AdapterClaim claim) =>
        _adapter.GetClaimAsync(Arg.Any<ClaimAdapterRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ClaimAdapterResponse { Platform = "cho", Claim = claim });

    private ClaimAdjudicationOrchestrator BuildOrchestrator(
        IReadOnlyList<IClaimAdjudicationStage> detectionStages,
        FakeClaimState state)
    {
        var stages = detectionStages.Append(new PersistenceStage(BuildRepository(state), NullLogger<PersistenceStage>.Instance)).ToArray();

        return new ClaimAdjudicationOrchestrator(
            _factory,
            _planResolver,
            _memberResolver,
            _coverageResolver,
            stages,
            _eventPublisher,
            _messageBus,
            new AdjudicationTenantContext(),
            Substitute.For<IClaimAdjustmentService>(),
            Options.Create(new AdjudicationPipelineOptions()),
            NullLogger<ClaimAdjudicationOrchestrator>.Instance);
    }

    /// <summary>
    /// Applies the SAME precedence predicate the real Cosmos/Mongo
    /// repositories use (<see cref="ClaimRepository.IsFinalDisposition"/>)
    /// to a simple in-memory claim record — not a re-implementation of the
    /// repository, just enough state to observe the effect of the
    /// isPend/pendDetails arguments PersistenceStage passes.
    /// </summary>
    private static IClaimRepository BuildRepository(FakeClaimState state)
    {
        var repo = Substitute.For<IClaimRepository>();
        repo.UpdateAdjudicationProjectionAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<AdjudicationResult>(),
                Arg.Any<IReadOnlyList<LineAdjudicationResult>>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<PendDetails?>(),
                Arg.Any<bool>(),
                Arg.Any<ClaimStatus?>())
            .Returns(ci =>
            {
                var pendDetails = ci.ArgAt<PendDetails?>(5);
                var isPend = ci.ArgAt<bool>(6);
                var resolvedStatus = ci.ArgAt<ClaimStatus?>(7);

                if (pendDetails is not null)
                {
                    state.PendDetails = pendDetails;
                }

                if (isPend && !ClaimRepository.IsFinalDisposition(state.Status))
                {
                    state.Status = ClaimStatus.Pended;
                }
                else if (resolvedStatus is not null && !ClaimRepository.BlocksSynchronousWriteback(state.Status))
                {
                    state.Status = resolvedStatus.Value;
                }

                return true;
            });
        return repo;
    }

    private static INcciEditService BuildSeededNcciEngine()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<INcciRepository>(BuildSeededNcciRepository());
        services.AddNcciEngine();
        return services.BuildServiceProvider().GetRequiredService<INcciEditService>();
    }

    private static InMemoryNcciRepository BuildSeededNcciRepository()
    {
        var effective = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return new InMemoryNcciRepository(
            new[]
            {
                new NcciEditPair
                {
                    Id = $"{TenantId}_99213_99214_20250101",
                    TenantId = TenantId,
                    Column1Code = "99213",
                    Column2Code = "99214",
                    ModifierIndicator = NcciModifierIndicator.Allowed,
                    PolicyType = NcciPolicyType.ProcedureToProc,
                    EffectiveDate = effective,
                },
            },
            Array.Empty<MueEntry>());
    }

    private static AdapterClaim BuildBundledPairClaim()
    {
        var serviceDate = DateTime.UtcNow.AddDays(-3).Date;
        var claim = BuildClaimShell(serviceDate);
        claim.ClaimLines = new List<AdapterClaimLine>
        {
            NewLine(1, "99213", 1, serviceDate),
            NewLine(2, "99214", 1, serviceDate),
        };
        return claim;
    }

    private static AdapterClaim BuildCobClaim()
    {
        var serviceDate = DateTime.UtcNow.AddDays(-3).Date;
        var claim = BuildClaimShell(serviceDate);
        claim.ClaimLines = new List<AdapterClaimLine>
        {
            NewLine(1, "99213", 1, serviceDate),
        };
        return claim;
    }

    private static AdapterClaim BuildClaimShell(DateTime serviceDate) => new()
    {
        TenantId = TenantId,
        // The orchestrator takes ClaimAdjudicationContext.ClaimVersionId from
        // the ClaimVersionSubmittedMessage, not from these claim fields (see
        // ClaimAdjudicationOrchestrator.AdjudicateAsync), and the repository
        // substitute below matches on Arg.Any<string>() for both — so these
        // values are never asserted against; they just need to be non-empty
        // for the stages that read them (e.g. NCCI/COB logging).
        Id = "claim-shell-1",
        ClaimNumber = "CLM-PERSIST-1",
        ClaimVersionId = "claim-shell-1",
        VersionNumber = 1,
        VersionState = ClaimVersionState.Submitted,
        MemberId = MemberId,
        BillingProviderNPI = "1234567893",
        ClaimType = ClaimType.Professional,
        ClaimFrequencyCode = "1",
        PlaceOfServiceCode = "11",
        TotalChargeAmount = 200m,
        ServiceDateFrom = serviceDate,
        ServiceDateTo = serviceDate,
        SubmittedDate = serviceDate,
    };

    private static AdapterClaimLine NewLine(int n, string code, decimal units, DateTime serviceDate) => new()
    {
        LineNumber = n,
        ProcedureCode = code,
        Units = units,
        ChargeAmount = 100m,
        ServiceDateFrom = serviceDate,
        ServiceDateTo = serviceDate,
    };

    private static ClaimVersionSubmittedMessage BuildSubmittedMessage(string claimVersionId) => new()
    {
        TenantId = TenantId,
        ClaimId = claimVersionId,
        ClaimVersionId = claimVersionId,
        VersionNumber = 1,
        ActorId = "actor",
        CorrelationId = $"corr-{claimVersionId}",
    };

    private static MessageContext BuildMessageContext(string claimVersionId) => new(
        MessageId: $"submitted:{claimVersionId}",
        CorrelationId: $"corr-{claimVersionId}",
        DeliveryCount: 1,
        Properties: new Dictionary<string, string> { ["MessageType"] = "ClaimVersionSubmitted" });

    private sealed class FakeClaimState
    {
        public ClaimStatus Status { get; set; } = ClaimStatus.Submitted;
        public PendDetails? PendDetails { get; set; }
    }

    /// <summary>Mirrors AdjudicationWithNcciEndToEndTests' in-memory NCCI repository.</summary>
    private sealed class InMemoryNcciRepository : INcciRepository
    {
        private readonly IReadOnlyList<NcciEditPair> _pairs;
        private readonly IReadOnlyList<MueEntry> _mues;

        public InMemoryNcciRepository(IReadOnlyList<NcciEditPair> pairs, IReadOnlyList<MueEntry> mues)
        {
            _pairs = pairs;
            _mues = mues;
        }

        public Task<NcciEditPair?> GetEditPairAsync(
            string tenantId, string column1Code, string column2Code,
            DateOnly serviceDate, CancellationToken ct = default)
        {
            var dos = serviceDate.ToDateTime(TimeOnly.MinValue);
            var hit = _pairs.FirstOrDefault(p =>
                p.TenantId == tenantId
                && p.Column1Code == column1Code
                && p.Column2Code == column2Code
                && p.EffectiveDate <= dos
                && (p.TerminationDate is null || p.TerminationDate > dos));
            return Task.FromResult<NcciEditPair?>(hit);
        }

        public Task<MueEntry?> GetMueEntryAsync(
            string tenantId, string procedureCode,
            DateOnly serviceDate, CancellationToken ct = default)
        {
            var dos = serviceDate.ToDateTime(TimeOnly.MinValue);
            var hit = _mues.FirstOrDefault(m =>
                m.TenantId == tenantId
                && m.ProcedureCode == procedureCode
                && m.EffectiveDate <= dos
                && (m.TerminationDate is null || m.TerminationDate > dos));
            return Task.FromResult<MueEntry?>(hit);
        }

        public Task<(int PairsWritten, int MueWritten)> UpsertQuarterAsync(
            string tenantId, string quarter,
            IReadOnlyList<NcciEditPair> pairs, IReadOnlyList<MueEntry> entries,
            CancellationToken ct = default)
            => Task.FromResult((0, 0));

        public Task<CloudHealthOffice.NcciEngine.Models.NcciTableVersion?> GetCurrentVersionAsync(
            string tenantId, CancellationToken ct = default)
            => Task.FromResult<CloudHealthOffice.NcciEngine.Models.NcciTableVersion?>(null);

        public Task SaveVersionAsync(CloudHealthOffice.NcciEngine.Models.NcciTableVersion version, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
