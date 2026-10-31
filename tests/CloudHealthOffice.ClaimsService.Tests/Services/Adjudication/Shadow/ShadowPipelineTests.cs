using System.Net.Http;
using System.Reflection;
using ClaimsService.Adapters;
using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Models.Messaging;
using ClaimsService.Services;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Shadow;

/// <summary>
/// The shadow run shares the real pipeline but must never reach a stage with a
/// side effect, persist anything, drive the accumulator outbox or emit an event.
/// </summary>
public class ShadowPipelineTests
{
    private readonly IClaimVersionEventPublisher _eventPublisher = Substitute.For<IClaimVersionEventPublisher>();
    private readonly IMessageBus _messageBus = Substitute.For<IMessageBus>();
    private readonly IAccumulatorOutboxProcessor _outbox = Substitute.For<IAccumulatorOutboxProcessor>();
    private readonly IClaimAdjustmentService _adjustments = Substitute.For<IClaimAdjustmentService>();

    [Fact]
    public async Task AShadowRun_ExecutesOnlyTheReadOnlyStages()
    {
        var executed = new List<string>();
        var orchestrator = Build(
        [
            new Recording(ScrubbingStage.StageName, 100, false, executed),
            new Recording(PricingStage.StageName, 250, false, executed),
            new Recording(BenefitCalculationStage.StageName, 300, false, executed),
            new Recording("SomeFutureStage", 500, false, executed),
            new Recording(AiExaminationStage.StageName, 600, false, executed),
            new Recording(PersistenceStage.StageName, 999, true, executed),
        ]);

        var context = await orchestrator.EvaluateShadowAsync("tenant-1", Claim(), "actor", "shadow:run-1", CancellationToken.None);

        Assert.True(context.IsShadow);
        Assert.Equal([ScrubbingStage.StageName, PricingStage.StageName, BenefitCalculationStage.StageName], executed);
    }

    [Fact]
    public async Task AShadowRun_EmitsNothingAndCommitsNothing()
    {
        var orchestrator = Build(
        [
            new Recording(BenefitCalculationStage.StageName, 300, false, []),
            new Recording(PersistenceStage.StageName, 999, true, []),
        ]);

        await orchestrator.EvaluateShadowAsync("tenant-1", Claim(), "actor", null, CancellationToken.None);

        Assert.Empty(_messageBus.ReceivedCalls());
        Assert.Empty(_eventPublisher.ReceivedCalls());
        Assert.Empty(_outbox.ReceivedCalls());
        Assert.Empty(_adjustments.ReceivedCalls());
    }

    [Fact]
    public async Task AShadowRun_StillShortCircuitsOnATerminalStage()
    {
        var executed = new List<string>();
        var orchestrator = Build(
        [
            new Recording(ScrubbingStage.StageName, 100, false, executed,
                _ => ClaimAdjudicationStageResult.Reject(ScrubbingStage.StageName, "defect")),
            new Recording(BenefitCalculationStage.StageName, 300, false, executed),
        ]);

        var context = await orchestrator.EvaluateShadowAsync("tenant-1", Claim(), null, null, CancellationToken.None);

        Assert.Equal([ScrubbingStage.StageName], executed);
        Assert.Equal(ClaimAdjudicationOutcome.Reject, ClaimAdjudicationStageResult.ResolveOutcome(context.StageResults));
    }

    /// <summary>
    /// Every real stage is classified: either read-only (in the allowlist) or
    /// known to have a side effect. A new stage fails this test until someone
    /// decides which it is.
    /// </summary>
    [Fact]
    public void EveryPipelineStage_IsClassifiedForShadowRuns()
    {
        var withSideEffects = new HashSet<string> { PersistenceStage.StageName, AiExaminationStage.StageName };
        var stageNames = typeof(ClaimAdjudicationOrchestrator).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IClaimAdjudicationStage).IsAssignableFrom(t))
            .Select(t => (string?)t.GetField("StageName", BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue())
            .ToList();

        Assert.NotEmpty(stageNames);
        Assert.All(stageNames, name =>
        {
            Assert.NotNull(name);
            Assert.True(
                ClaimAdjudicationOrchestrator.ShadowSafeStages.Contains(name!) ^ withSideEffects.Contains(name!),
                $"Stage '{name}' must be in exactly one of ShadowSafeStages or the side-effect list.");
        });
    }

    private ClaimAdjudicationOrchestrator Build(IEnumerable<IClaimAdjudicationStage> stages)
    {
        var adapter = Substitute.For<IClaimAdapter>();
        adapter.Platform.Returns("cho");
        var cache = new ClaimTenantConfigCache(
            Substitute.For<IHttpClientFactory>(), Substitute.For<IConfiguration>(),
            NullLogger<ClaimTenantConfigCache>.Instance);
        var factory = new ClaimAdapterFactory([adapter], cache, NullLogger<ClaimAdapterFactory>.Instance);

        return new ClaimAdjudicationOrchestrator(
            factory,
            Substitute.For<IBenefitPlanResolver>(),
            Substitute.For<IMemberResolver>(),
            Substitute.For<ICoverageResolver>(),
            stages,
            _eventPublisher,
            _messageBus,
            new AdjudicationTenantContext(),
            _adjustments,
            Options.Create(new AdjudicationPipelineOptions()),
            NullLogger<ClaimAdjudicationOrchestrator>.Instance,
            _outbox);
    }

    private static AdapterClaim Claim()
    {
        var day = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc);
        return new AdapterClaim
        {
            TenantId = "tenant-1",
            Id = "cpcds-1",
            ClaimVersionId = "cpcds-1",
            ClaimNumber = "Q-1",
            MemberId = "MEM-1",
            BillingProviderNPI = "1234567890",
            BenefitPlanId = "PLAN-1",
            ServiceDateFrom = day,
            ServiceDateTo = day,
            ClaimLines = [new() { LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 100m, ServiceDateFrom = day, ServiceDateTo = day }],
        };
    }

    private sealed class Recording(
        string name, int order, bool required, List<string> log,
        Func<ClaimAdjudicationContext, ClaimAdjudicationStageResult>? behavior = null) : IClaimAdjudicationStage
    {
        public string Name => name;
        public int Order => order;
        public bool IsRequired => required;

        public Task<ClaimAdjudicationStageResult> ExecuteAsync(ClaimAdjudicationContext context, CancellationToken ct)
        {
            log.Add(Name);
            return Task.FromResult(behavior?.Invoke(context) ?? ClaimAdjudicationStageResult.Pass(Name));
        }
    }
}
