using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Repositories;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using CloudHealthOffice.CobEngine.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using NcciModels = CloudHealthOffice.NcciEngine.Models;
using NcciServices = CloudHealthOffice.NcciEngine.Services;
using ScrubModels = CloudHealthOffice.ClaimsScrubEngine.Models;
using ScrubServices = CloudHealthOffice.ClaimsScrubEngine.Services;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Stages;

/// <summary>
/// Audit finding: a Deny/Reject persists as <see cref="ClaimStatus.Denied"/>,
/// and the 835 can only remit a denial that carries a CARC. Every built-in
/// stage path that denies or rejects must set
/// <see cref="AdjudicationResult.DenialReasonCode"/> itself (the
/// <see cref="PersistenceStage"/> safety net is for paths nobody anticipated,
/// not a substitute). Table-driven: a new denying path added to one of these
/// stages without a code fails here.
/// </summary>
public class DenialCarcCoverageTests
{
    private const string TenantId = "tenant-1";
    private const string ClaimVersionId = "ver-carc-1";
    private const string MemberId = "MEM-1";
    private const string Npi = "1234567890";

    public sealed record Scenario(
        string Name,
        ClaimAdjudicationOutcome ExpectedOutcome,
        string ExpectedCarc,
        string? ExpectedRarc,
        Func<Task<(ClaimAdjudicationStageResult Result, ClaimAdjudicationContext Context)>> Run)
    {
        public override string ToString() => Name;
    }

    public static TheoryData<string> ScenarioNames()
    {
        var data = new TheoryData<string>();
        foreach (var s in Scenarios()) data.Add(s.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public async Task Every_denying_stage_path_sets_a_valid_CARC(string name)
    {
        var scenario = Scenarios().Single(s => s.Name == name);

        var (result, ctx) = await scenario.Run();

        // The scenario must still deny/reject exactly as before (codes only).
        Assert.Equal(scenario.ExpectedOutcome, result.Outcome);
        Assert.False(result.Continue);

        var code = ctx.AdjudicationResult.DenialReasonCode;
        Assert.False(string.IsNullOrWhiteSpace(code), $"{name}: no DenialReasonCode");
        Assert.Equal(scenario.ExpectedCarc, code);
        // X12 CARC shape: 1-3 digits, or a letter + digit(s) (e.g. B7, B20); no "CO-" prefix.
        Assert.Matches("^([0-9]{1,3}|[A-Z][0-9]{1,2})$", code!);
        Assert.False(string.IsNullOrWhiteSpace(ctx.AdjudicationResult.DenialReason), $"{name}: no DenialReason");

        if (scenario.ExpectedRarc is { } rarc)
            Assert.Contains(rarc, ctx.AdjudicationResult.RemarkCodes);
    }

    private static IEnumerable<Scenario> Scenarios()
    {
        // ── Scrubbing ───────────────────────────────────────────────
        yield return new Scenario("Scrubbing structural error", ClaimAdjudicationOutcome.Reject, "16", "MA130", async () =>
        {
            var engine = Substitute.For<ScrubServices.IClaimRoutingService>();
            engine.ScrubAndRouteAsync(Arg.Any<ScrubModels.ClaimsScrubRequest>(), Arg.Any<CancellationToken>())
                .Returns(ScrubResponseWithError());
            var ctx = NewContext();
            var r = await new ScrubbingStage(engine, NullLogger<ScrubbingStage>.Instance).ExecuteAsync(ctx, default);
            return (r, ctx);
        });
        yield return new Scenario("Scrubbing engine threw", ClaimAdjudicationOutcome.Reject, "16", "MA130", async () =>
        {
            var engine = Substitute.For<ScrubServices.IClaimRoutingService>();
            engine.ScrubAndRouteAsync(Arg.Any<ScrubModels.ClaimsScrubRequest>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("down"));
            var ctx = NewContext();
            var r = await new ScrubbingStage(engine, NullLogger<ScrubbingStage>.Instance).ExecuteAsync(ctx, default);
            return (r, ctx);
        });

        // ── NetworkCredentialing ────────────────────────────────────
        yield return new Scenario("Network missing billing NPI", ClaimAdjudicationOutcome.Reject, "16", "N257", async () =>
        {
            var ctx = NewContext(c => c.BillingProviderNPI = null!);
            var r = await NewNetworkStage(out _, out _).ExecuteAsync(ctx, default);
            return (r, ctx);
        });
        yield return new Scenario("Network out of network (FailClosed)", ClaimAdjudicationOutcome.Deny, "242", null, async () =>
        {
            var stage = NewNetworkStage(out var membership, out _);
            membership.GetMembershipAsync(TenantId, Arg.Any<string>(), Npi, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
                .Returns(new NetworkMembership { Npi = Npi, IsActiveMember = false });
            var ctx = NewContext(tiers: new[] { new ResolvedNetworkTier { TierName = "InNetwork", TierLevel = 1, NetworkId = "net-1" } });
            var r = await stage.ExecuteAsync(ctx, default);
            return (r, ctx);
        });
        yield return new Scenario("Network no tier configured", ClaimAdjudicationOutcome.Deny, "242", null, async () =>
        {
            var ctx = NewContext(tiers: Array.Empty<ResolvedNetworkTier>());
            var r = await NewNetworkStage(out _, out _).ExecuteAsync(ctx, default);
            return (r, ctx);
        });
        yield return new Scenario("Credentialing not active (FailClosed)", ClaimAdjudicationOutcome.Deny, "B7", null, async () =>
        {
            var stage = NewNetworkStage(out var membership, out var credentialing);
            membership.GetMembershipAsync(TenantId, "net-1", Npi, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
                .Returns(new NetworkMembership
                {
                    NetworkId = "net-1", Npi = Npi, ProviderId = "p-1", IsActiveMember = true, AsOfDate = DateTime.UtcNow,
                });
            credentialing.GetStatusAsOfAsync(TenantId, "p-1", Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
                .Returns(new CredentialingStatusSnapshot { ProviderId = "p-1", Status = "Pending" });
            var ctx = NewContext(tiers: new[] { new ResolvedNetworkTier { TierName = "InNetwork", TierLevel = 1, NetworkId = "net-1" } });
            var r = await stage.ExecuteAsync(ctx, default);
            return (r, ctx);
        });

        // ── BenefitCalculation ──────────────────────────────────────
        yield return new Scenario("Benefit missing plan id", ClaimAdjudicationOutcome.Reject, "16", "MA130", async () =>
        {
            var ctx = NewContext(c => c.BenefitPlanId = null);
            var r = await NewBenefitStage(out _).ExecuteAsync(ctx, default);
            return (r, ctx);
        });
        yield return new Scenario("Benefit non-GUID plan id", ClaimAdjudicationOutcome.Reject, "16", "MA130", async () =>
        {
            var ctx = NewContext(c => c.BenefitPlanId = "not-a-guid");
            var r = await NewBenefitStage(out _).ExecuteAsync(ctx, default);
            return (r, ctx);
        });
        yield return new Scenario("Benefit engine threw", ClaimAdjudicationOutcome.Reject, "16", "MA130", async () =>
        {
            var stage = NewBenefitStage(out var engine);
            engine.CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>())
                .Throws(new InvalidOperationException("engine down"));
            var ctx = NewBenefitContext();
            var r = await stage.ExecuteAsync(ctx, default);
            return (r, ctx);
        });
        yield return new Scenario("Benefit engine denial without a code", ClaimAdjudicationOutcome.Deny, "96", "N130", async () =>
        {
            var stage = NewBenefitStage(out var engine);
            engine.CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>())
                .Returns(new BenefitResolutionResult { Success = false, Totals = new ClaimTotals() });
            var ctx = NewBenefitContext();
            var r = await stage.ExecuteAsync(ctx, default);
            return (r, ctx);
        });
        yield return new Scenario("Benefit engine denial keeps engine CARC", ClaimAdjudicationOutcome.Deny, "204", null, async () =>
        {
            var stage = NewBenefitStage(out var engine);
            engine.CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>())
                .Returns(new BenefitResolutionResult
                {
                    Success = false, DenialReasonCode = "204", DenialReasonDescription = "Not covered under plan",
                    Totals = new ClaimTotals(),
                });
            var ctx = NewBenefitContext();
            var r = await stage.ExecuteAsync(ctx, default);
            return (r, ctx);
        });

        // ── NcciEdits (Deny mode) ───────────────────────────────────
        yield return new Scenario("NCCI PTP edit (Deny mode)", ClaimAdjudicationOutcome.Deny, "B20", "N519", async () =>
        {
            var stage = NewNcciStage(out var engine);
            engine.ScrubAsync(Arg.Any<NcciModels.NcciScrubRequest>(), Arg.Any<CancellationToken>())
                .Returns(NcciResult(new NcciModels.NcciEditFailure
                {
                    EditType = NcciModels.NcciEditType.NcciPair, RuleId = "NE001", Message = "bundled",
                    Column1Code = "99213", Column2Code = "99214", AffectedLineNumbers = new List<int> { 1 },
                    SuggestedCarc = "B20", SuggestedRarc = "N519",
                }));
            var ctx = NewContext();
            var r = await stage.ExecuteAsync(ctx, default);
            return (r, ctx);
        });
        yield return new Scenario("MUE edit (Deny mode)", ClaimAdjudicationOutcome.Deny, "151", "N115", async () =>
        {
            var stage = NewNcciStage(out var engine);
            engine.ScrubAsync(Arg.Any<NcciModels.NcciScrubRequest>(), Arg.Any<CancellationToken>())
                .Returns(NcciResult(new NcciModels.NcciEditFailure
                {
                    EditType = NcciModels.NcciEditType.Mue, RuleId = "NE002", Message = "units",
                    Column2Code = "99213", AffectedLineNumbers = new List<int> { 1 }, UnitsBilled = 5m, MueMaxUnits = 3,
                    SuggestedCarc = "151", SuggestedRarc = "N115",
                }));
            var ctx = NewContext();
            var r = await stage.ExecuteAsync(ctx, default);
            return (r, ctx);
        });
        yield return new Scenario("NCCI edit without suggested CARC (Deny mode)", ClaimAdjudicationOutcome.Deny, "236", null, async () =>
        {
            var stage = NewNcciStage(out var engine);
            engine.ScrubAsync(Arg.Any<NcciModels.NcciScrubRequest>(), Arg.Any<CancellationToken>())
                .Returns(NcciResult(new NcciModels.NcciEditFailure
                {
                    EditType = NcciModels.NcciEditType.NcciPair, RuleId = "NE001", Message = "bundled",
                    Column1Code = "99213", Column2Code = "99214", AffectedLineNumbers = new List<int> { 1 },
                }));
            var ctx = NewContext();
            var r = await stage.ExecuteAsync(ctx, default);
            return (r, ctx);
        });
        yield return new Scenario("NCCI engine threw (Deny mode)", ClaimAdjudicationOutcome.Deny, "236", null, async () =>
        {
            var stage = NewNcciStage(out var engine);
            engine.ScrubAsync(Arg.Any<NcciModels.NcciScrubRequest>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("down"));
            var ctx = NewContext();
            var r = await stage.ExecuteAsync(ctx, default);
            return (r, ctx);
        });

        // ── CoordinationOfBenefits ──────────────────────────────────
        yield return new Scenario("COB missing member id", ClaimAdjudicationOutcome.Reject, "16", "N382", async () =>
        {
            var ctx = NewContext(c => c.MemberId = null!);
            var r = await NewCobStage(out _).ExecuteAsync(ctx, default);
            return (r, ctx);
        });
        yield return new Scenario("COB other payer primary (Deny mode)", ClaimAdjudicationOutcome.Deny, "22", "MA04", async () =>
        {
            var stage = NewCobStage(out var coverage);
            coverage.GetCobEntriesAsync(TenantId, MemberId, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
                .Returns(new[] { new CobEntry { PayerName = "Aetna", PayerId = "AET", CoverageSequence = "P" } });
            var ctx = NewContext();
            var r = await stage.ExecuteAsync(ctx, default);
            return (r, ctx);
        });
    }

    // ── PersistenceStage safety net ─────────────────────────────────

    [Theory]
    [InlineData(ClaimAdjudicationOutcome.Reject, "16", "MA130")]
    [InlineData(ClaimAdjudicationOutcome.Deny, "96", "N130")]
    public async Task Persistence_assigns_default_code_and_warns_when_a_denial_has_none(
        ClaimAdjudicationOutcome outcome, string expectedCarc, string expectedRarc)
    {
        var repository = Substitute.For<IClaimRepository>();
        StubPersistence(repository);
        var logger = new CapturingLogger<PersistenceStage>();
        var ctx = NewContext();
        ctx.StageResults.Add(outcome == ClaimAdjudicationOutcome.Reject
            ? ClaimAdjudicationStageResult.Reject("SomeNewStage", "it threw")
            : ClaimAdjudicationStageResult.Deny("SomeNewStage", "it denied"));

        await new PersistenceStage(repository, logger).ExecuteAsync(ctx, default);

        await repository.Received(1).UpdateAdjudicationProjectionAsync(
            TenantId, ClaimVersionId,
            Arg.Is<AdjudicationResult>(a => a.DenialReasonCode == expectedCarc && a.RemarkCodes.Contains(expectedRarc)),
            Arg.Any<IReadOnlyList<LineAdjudicationResult>>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<PendDetails?>(),
            Arg.Any<bool>(),
            Arg.Is<ClaimStatus?>(s => s == ClaimStatus.Denied),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<AccumulatorOutboxItem?>());
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("SomeNewStage", warning.Message);
        Assert.Contains(expectedCarc, warning.Message);
        Assert.Equal(outcome == ClaimAdjudicationOutcome.Reject ? "it threw" : "it denied", ctx.AdjudicationResult.DenialReason);
    }

    [Fact]
    public async Task Persistence_keeps_the_stage_code_and_does_not_warn()
    {
        var repository = Substitute.For<IClaimRepository>();
        StubPersistence(repository);
        var logger = new CapturingLogger<PersistenceStage>();
        var ctx = NewContext();
        ctx.AdjudicationResult.DenialReasonCode = "18";
        ctx.StageResults.Add(ClaimAdjudicationStageResult.Deny(DuplicateClaimStage.StageName, "dup"));

        await new PersistenceStage(repository, logger).ExecuteAsync(ctx, default);

        Assert.Equal("18", ctx.AdjudicationResult.DenialReasonCode);
        Assert.Empty(ctx.AdjudicationResult.RemarkCodes);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Theory]
    [InlineData(ClaimAdjudicationOutcome.Pass)]
    [InlineData(ClaimAdjudicationOutcome.Pend)]
    public async Task Persistence_sets_no_code_on_pass_or_pend(ClaimAdjudicationOutcome outcome)
    {
        var repository = Substitute.For<IClaimRepository>();
        StubPersistence(repository);
        var ctx = NewContext();
        ctx.StageResults.Add(outcome == ClaimAdjudicationOutcome.Pend
            ? ClaimAdjudicationStageResult.Pend("NcciEdits", "review")
            : ClaimAdjudicationStageResult.Pass("NcciEdits"));

        await new PersistenceStage(repository, NullLogger<PersistenceStage>.Instance).ExecuteAsync(ctx, default);

        Assert.Null(ctx.AdjudicationResult.DenialReasonCode);
    }

    // ── helpers ─────────────────────────────────────────────────────

    private static void StubPersistence(IClaimRepository repository) =>
        repository.UpdateAdjudicationProjectionAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AdjudicationResult>(),
                Arg.Any<IReadOnlyList<LineAdjudicationResult>>(), Arg.Any<CancellationToken>(),
                Arg.Any<PendDetails?>(), Arg.Any<bool>(), Arg.Any<ClaimStatus?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<AccumulatorOutboxItem?>())
            .Returns(true);

    private static NetworkCredentialingStage NewNetworkStage(
        out IProviderMembershipClient membership, out ICredentialingStatusClient credentialing)
    {
        membership = Substitute.For<IProviderMembershipClient>();
        credentialing = Substitute.For<ICredentialingStatusClient>();
        return new NetworkCredentialingStage(
            membership, credentialing,
            Options.Create(new TenantEnforcementPolicyOptions
            {
                NetworkMode = NetworkEnforcementMode.FailClosed,
                CredentialingMode = CredentialingEnforcementMode.FailClosed,
            }),
            NullLogger<NetworkCredentialingStage>.Instance);
    }

    private static BenefitCalculationStage NewBenefitStage(out IBenefitCalculationEngine engine)
    {
        engine = Substitute.For<IBenefitCalculationEngine>();
        return new BenefitCalculationStage(
            engine,
            Substitute.For<IMemberResolver>(),
            Substitute.For<IAuthorizationValidationClient>(),
            NullLogger<BenefitCalculationStage>.Instance);
    }

    private static NcciEditsStage NewNcciStage(out NcciServices.INcciEditService engine)
    {
        engine = Substitute.For<NcciServices.INcciEditService>();
        return new NcciEditsStage(
            engine,
            Options.Create(new TenantEnforcementPolicyOptions { NcciMode = NcciEnforcementMode.Deny }),
            NullLogger<NcciEditsStage>.Instance);
    }

    private static CoordinationOfBenefitsStage NewCobStage(out ICoverageClient coverage)
    {
        coverage = Substitute.For<ICoverageClient>();
        return new CoordinationOfBenefitsStage(
            coverage,
            new PayerOrderService(),
            Options.Create(new TenantEnforcementPolicyOptions { CobMode = CobEnforcementMode.Deny }),
            NullLogger<CoordinationOfBenefitsStage>.Instance);
    }

    private static ClaimAdjudicationContext NewBenefitContext()
    {
        var ctx = NewContext(c => c.BenefitPlanId = Guid.NewGuid().ToString());
        ctx.PricingResult = new PricingOutcome
        {
            AllowedAmounts = ctx.Claim.ClaimLines.ToDictionary(l => l.LineNumber, _ => 72m),
        };
        ctx.ResolvedMember = new ResolvedMember { MemberId = MemberId, IsSubscriber = true };
        return ctx;
    }

    private static ClaimAdjudicationContext NewContext(
        Action<AdapterClaim>? mutate = null,
        IReadOnlyList<ResolvedNetworkTier>? tiers = null)
    {
        var serviceDate = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc);
        var claim = new AdapterClaim
        {
            TenantId = TenantId,
            Id = ClaimVersionId,
            ClaimVersionId = ClaimVersionId,
            ClaimNumber = "CLM-CARC-1",
            MemberId = MemberId,
            SubscriberId = MemberId,
            SubscriberFirstName = "Pat",
            SubscriberLastName = "Roe",
            BenefitPlanId = "plan-1",
            BillingProviderNPI = Npi,
            LineOfBusiness = LineOfBusiness.Commercial,
            ClaimType = ClaimType.Professional,
            PlaceOfServiceCode = "11",
            TotalChargeAmount = 100m,
            ServiceDateFrom = serviceDate,
            ServiceDateTo = serviceDate,
            DiagnosisCodes = new List<AdapterDiagnosisCode> { new() { Code = "Z00.00", PointerNumber = 1 } },
            ClaimLines = new List<AdapterClaimLine>
            {
                new() { LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 100m, Units = 1,
                        ServiceDateFrom = serviceDate, ServiceDateTo = serviceDate },
            },
        };
        mutate?.Invoke(claim);
        return new ClaimAdjudicationContext
        {
            TenantId = TenantId,
            ClaimVersionId = ClaimVersionId,
            Claim = claim,
            ResolvedPlan = new ResolvedBenefitPlan { Id = "plan-1", NetworkTiers = tiers ?? Array.Empty<ResolvedNetworkTier>() },
            ResolvedMember = new ResolvedMember
            {
                MemberId = MemberId,
                DateOfBirth = new DateTime(1980, 6, 15, 0, 0, 0, DateTimeKind.Utc),
                EffectiveDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
        };
    }

    private static ScrubModels.ClaimsScrubResponse ScrubResponseWithError() => new()
    {
        Result = new ScrubModels.ClaimValidationResult
        {
            ClaimId = ClaimVersionId,
            ClaimType = ScrubModels.ClaimType.Professional,
            PatientControlNumber = "CLM-CARC-1",
            Status = "rejected",
            RulesExecuted = 21,
            RulesPassed = 20,
            RulesFailed = 1,
            ErrorCount = 1,
            WarningCount = 0,
            Results = new List<ScrubModels.ValidationResult>
            {
                new()
                {
                    RuleId = "R001", RuleName = "NPI required", Passed = false,
                    Severity = ScrubModels.ValidationSeverity.Error, Message = "NPI required failed",
                },
            },
            ValidatedAt = DateTime.UtcNow.ToString("o"),
            Routing = new ScrubModels.ClaimRoutingDecision { Destination = "work-queue", Reason = "Errors." },
        },
    };

    private static NcciModels.NcciScrubResult NcciResult(NcciModels.NcciEditFailure failure) => new()
    {
        ClaimId = ClaimVersionId,
        NcciPairsChecked = 1,
        MueChecked = 1,
        EditFailures = new List<NcciModels.NcciEditFailure> { failure },
    };

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
