using ClaimsService.Adapters;
using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Models.Messaging;
using ClaimsService.Services;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Resolution;
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
/// Capability 5.7 — pipeline-level integration that wires the REAL
/// <see cref="NcciEditsStage"/> backed by the REAL
/// <c>CloudHealthOffice.NcciEngine</c> against an in-memory
/// <see cref="INcciRepository"/> seeded with one NCCI pair and one MUE
/// entry. Verifies the cross-stage contract: bundled procedure pair
/// produces a <c>Pend</c> outcome with PendDetails recorded; MUE
/// over-units produces a Pend; <c>NcciMode=Deny</c> produces a
/// short-circuiting Deny.
/// </summary>
public class AdjudicationWithNcciEndToEndTests
{
    private const string TenantId = "tenant-1";

    private readonly IClaimAdapter _adapter = Substitute.For<IClaimAdapter>();
    private readonly IBenefitPlanResolver _planResolver = Substitute.For<IBenefitPlanResolver>();
    private readonly IMemberResolver _memberResolver = Substitute.For<IMemberResolver>();
    private readonly ICoverageResolver _coverageResolver = Substitute.For<ICoverageResolver>();
    private readonly IClaimVersionEventPublisher _eventPublisher = Substitute.For<IClaimVersionEventPublisher>();
    private readonly IMessageBus _messageBus = Substitute.For<IMessageBus>();
    private readonly ClaimAdapterFactory _factory;

    public AdjudicationWithNcciEndToEndTests()
    {
        _adapter.Platform.Returns("cho");
        var cache = new ClaimTenantConfigCache(
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IConfiguration>(),
            NullLogger<ClaimTenantConfigCache>.Instance);
        _factory = new ClaimAdapterFactory(
            new[] { _adapter }, cache,
            NullLogger<ClaimAdapterFactory>.Instance);

        _memberResolver.GetMemberAsync(TenantId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedMember
            {
                MemberId = "MEM-1",
                DateOfBirth = new DateTime(1980, 6, 15, 0, 0, 0, DateTimeKind.Utc),
            });
    }

    [Fact]
    public async Task Bundled_pair_in_PendForReview_produces_Pend_with_NCCI_PendDetails()
    {
        SetupAdapterReturningClaim(BuildBundledPairClaim());

        var captured = CaptureAdjudicatedClaim();
        var sbCapture = CaptureAdjudicatedMessage();
        var orch = BuildOrchestrator(NcciEnforcementMode.PendForReview);

        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildMessageContext(), CancellationToken.None);

        Assert.Equal("Pend", sbCapture.Message!.Outcome);
        Assert.NotNull(captured.PublishedClaim);
        Assert.NotNull(captured.PublishedClaim!.PendDetails);
        Assert.Equal("NCCI", captured.PublishedClaim.PendDetails!.PendCode);
        Assert.Single(captured.PublishedClaim.PendDetails.EditFailures);
        Assert.Equal("NE001", captured.PublishedClaim.PendDetails.EditFailures[0].RuleId);
        Assert.Equal("NcciPair", captured.PublishedClaim.PendDetails.EditFailures[0].EditType);
    }

    [Fact]
    public async Task Mue_over_units_produces_Pend_with_MUE_PendDetails()
    {
        SetupAdapterReturningClaim(BuildMueOverUnitsClaim());

        var captured = CaptureAdjudicatedClaim();
        var sbCapture = CaptureAdjudicatedMessage();
        var orch = BuildOrchestrator(NcciEnforcementMode.PendForReview);

        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildMessageContext(), CancellationToken.None);

        Assert.Equal("Pend", sbCapture.Message!.Outcome);
        Assert.NotNull(captured.PublishedClaim!.PendDetails);
        Assert.Equal("MUE", captured.PublishedClaim.PendDetails!.PendCode);
        Assert.Equal("Mue", captured.PublishedClaim.PendDetails.EditFailures[0].EditType);
        Assert.Equal("NE002", captured.PublishedClaim.PendDetails.EditFailures[0].RuleId);
    }

    [Fact]
    public async Task Bundled_pair_in_Deny_mode_short_circuits_with_Deny_outcome()
    {
        SetupAdapterReturningClaim(BuildBundledPairClaim());

        var sbCapture = CaptureAdjudicatedMessage();
        var orch = BuildOrchestrator(NcciEnforcementMode.Deny);

        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildMessageContext(), CancellationToken.None);

        Assert.Equal("Deny", sbCapture.Message!.Outcome);
    }

    [Fact]
    public async Task Clean_claim_passes_NCCI_and_continues_to_persistence()
    {
        SetupAdapterReturningClaim(BuildCleanClaim());

        var sbCapture = CaptureAdjudicatedMessage();
        var orch = BuildOrchestrator(NcciEnforcementMode.PendForReview);

        await orch.AdjudicateAsync(BuildSubmittedMessage(), BuildMessageContext(), CancellationToken.None);

        Assert.Equal("Pass", sbCapture.Message!.Outcome);
    }

    private void SetupAdapterReturningClaim(AdapterClaim claim)
    {
        _adapter
            .GetClaimAsync(Arg.Any<ClaimAdapterRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ClaimAdapterResponse { Platform = "cho", Claim = claim });
    }

    private MessageCapture CaptureAdjudicatedMessage()
    {
        var capture = new MessageCapture();
        _messageBus
            .When(b => b.SendAsync(
                Arg.Any<string>(),
                Arg.Any<ClaimVersionAdjudicatedMessage>(),
                Arg.Any<SendOptions?>(),
                Arg.Any<CancellationToken>()))
            .Do(ci => capture.Message = ci.Arg<ClaimVersionAdjudicatedMessage>());
        return capture;
    }

    private PublishCapture CaptureAdjudicatedClaim()
    {
        var capture = new PublishCapture();
        _eventPublisher
            .When(p => p.PublishVersionAdjudicatedAsync(
                Arg.Any<Claim>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>()))
            .Do(ci => capture.PublishedClaim = ci.Arg<Claim>());
        return capture;
    }

    private ClaimAdjudicationOrchestrator BuildOrchestrator(NcciEnforcementMode mode)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<INcciRepository>(BuildSeededRepository());
        services.AddNcciEngine(); // registers INcciEditService against our seeded repository

        var engine = services.BuildServiceProvider().GetRequiredService<INcciEditService>();

        var ncciStage = new NcciEditsStage(
            engine,
            Options.Create(new TenantEnforcementPolicyOptions { NcciMode = mode }),
            NullLogger<NcciEditsStage>.Instance);

        return new ClaimAdjudicationOrchestrator(
            _factory,
            _planResolver,
            _memberResolver,
            _coverageResolver,
            new IClaimAdjudicationStage[] { ncciStage, new TestPersistenceStage() },
            _eventPublisher,
            _messageBus,
            new AdjudicationTenantContext(),
            Substitute.For<IClaimAdjustmentService>(),
            Options.Create(new AdjudicationPipelineOptions()),
            NullLogger<ClaimAdjudicationOrchestrator>.Instance);
    }

    /// <summary>
    /// In-memory NCCI repository seeded with:
    /// <list type="bullet">
    ///   <item>One bundling pair: Column1=99213, Column2=99214, MI=1
    ///     (modifier-overridable). With no -59/X modifier on the
    ///     Column 2 line the engine emits a NE001 failure.</item>
    ///   <item>One MUE entry: 99213 ⇒ MaxUnits=2, MAI=2 (sum across
    ///     lines for the same code+DOS).</item>
    /// </list>
    /// </summary>
    private static InMemoryNcciRepository BuildSeededRepository()
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
            new[]
            {
                new MueEntry
                {
                    Id = $"{TenantId}_99213_20250101",
                    TenantId = TenantId,
                    ProcedureCode = "99213",
                    MaxUnits = 2,
                    AdjudicationIndicator = MueAdjudicationIndicator.DateOfService,
                    AppliesToProfessional = true,
                    AppliesToOutpatientFacility = true,
                    EffectiveDate = effective,
                },
            });
    }

    private static AdapterClaim BuildBundledPairClaim()
    {
        var serviceDate = DateTime.UtcNow.AddDays(-3).Date;
        var claim = BuildClaimShell(serviceDate);
        // Two procedure codes that match the seeded NCCI pair; no -59
        // modifier on the Column 2 line — engine emits NE001 failure.
        claim.ClaimLines = new List<AdapterClaimLine>
        {
            NewLine(1, "99213", units: 1, serviceDate),
            NewLine(2, "99214", units: 1, serviceDate),
        };
        return claim;
    }

    private static AdapterClaim BuildMueOverUnitsClaim()
    {
        var serviceDate = DateTime.UtcNow.AddDays(-3).Date;
        var claim = BuildClaimShell(serviceDate);
        // One procedure code 99213 billed twice on the same DOS — sum
        // (1 + 2) = 3 exceeds the seeded MUE limit of 2.
        claim.ClaimLines = new List<AdapterClaimLine>
        {
            NewLine(1, "99213", units: 1, serviceDate),
            NewLine(2, "99213", units: 2, serviceDate),
        };
        return claim;
    }

    private static AdapterClaim BuildCleanClaim()
    {
        var serviceDate = DateTime.UtcNow.AddDays(-3).Date;
        var claim = BuildClaimShell(serviceDate);
        // Single line under the MUE limit; no other line so no pair
        // check fires.
        claim.ClaimLines = new List<AdapterClaimLine>
        {
            NewLine(1, "99213", units: 1, serviceDate),
        };
        return claim;
    }

    private static AdapterClaim BuildClaimShell(DateTime serviceDate) => new()
    {
        TenantId = TenantId,
        Id = "ver-ncci-1",
        ClaimNumber = "CLM-NCCI-1",
        ClaimVersionId = "ver-ncci-1",
        VersionNumber = 1,
        VersionState = ClaimVersionState.Submitted,
        MemberId = "MEM-1",
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

    private static ClaimVersionSubmittedMessage BuildSubmittedMessage() => new()
    {
        TenantId = TenantId,
        ClaimId = "ver-ncci-1",
        ClaimVersionId = "ver-ncci-1",
        VersionNumber = 1,
        ActorId = "actor",
        CorrelationId = "corr-ncci-1",
    };

    private static MessageContext BuildMessageContext() => new(
        MessageId: "submitted:ver-ncci-1",
        CorrelationId: "corr-ncci-1",
        DeliveryCount: 1,
        Properties: new Dictionary<string, string> { ["MessageType"] = "ClaimVersionSubmitted" });

    private sealed class MessageCapture
    {
        public ClaimVersionAdjudicatedMessage? Message { get; set; }
    }

    private sealed class PublishCapture
    {
        public Claim? PublishedClaim { get; set; }
    }

    private sealed class TestPersistenceStage : IClaimAdjudicationStage
    {
        public string Name => "Persistence";
        public int Order => 999;
        public bool IsRequired => true;

        public Task<ClaimAdjudicationStageResult> ExecuteAsync(
            ClaimAdjudicationContext context, CancellationToken ct)
            => Task.FromResult(ClaimAdjudicationStageResult.Pass(Name));
    }

    /// <summary>
    /// In-memory <see cref="INcciRepository"/> for E2E tests. Returns
    /// the seeded data on lookups; ignores writes (the engine doesn't
    /// write during ScrubAsync — only ImportQuarterlyUpdateAsync does,
    /// and 5.7 doesn't exercise import).
    /// </summary>
    private sealed class InMemoryNcciRepository : INcciRepository
    {
        private readonly IReadOnlyList<NcciEditPair> _pairs;
        private readonly IReadOnlyList<MueEntry> _mues;

        public InMemoryNcciRepository(
            IReadOnlyList<NcciEditPair> pairs,
            IReadOnlyList<MueEntry> mues)
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

        public Task SaveVersionAsync(
            CloudHealthOffice.NcciEngine.Models.NcciTableVersion version,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
