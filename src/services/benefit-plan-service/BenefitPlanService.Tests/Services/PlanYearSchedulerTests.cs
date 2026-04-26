using BenefitPlanService.Models;
using BenefitPlanService.Services;
using BenefitPlanService.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Services;

public class PlanYearSchedulerTests
{
    private static (PlanYearScheduler scheduler, FakePlanYearTransitionPublisher pub, FakePlanYearScheduleSource src)
        BuildScheduler(DateTime now, int approachingDays = 30)
    {
        var pub = new FakePlanYearTransitionPublisher();
        var src = new FakePlanYearScheduleSource();
        var services = new ServiceCollection();
        services.AddSingleton<IPlanYearTransitionPublisher>(pub);
        services.AddSingleton<IPlanYearScheduleSource>(src);
        var sp = services.BuildServiceProvider();
        var clock = new FakeTimeProvider(now);
        var scheduler = new PlanYearScheduler(
            sp,
            new PlanYearSchedulerOptions { IntervalMinutes = 60, ApproachingDays = approachingDays, StartupDelaySeconds = 0 },
            clock,
            NullLogger<PlanYearScheduler>.Instance);
        return (scheduler, pub, src);
    }

    private static BenefitPlan PlanWith(PlanYearDefinition def, string planId = "plan-1", string tenantId = "tenant-1")
        => new()
        {
            TenantId = tenantId,
            PlanId = planId,
            PlanName = "Test Plan",
            Payer = "Test Payer",
            EffectiveDate = def.PlanYearStart,
            PlanType = PlanType.PPO,
            VersionId = $"v-{planId}",
            VersionNumber = 1,
            VersionState = PlanVersionState.Published,
            PlanYearDefinition = def
        };

    [Fact]
    public async Task Sweep_emits_ApproachingTransition_when_within_window()
    {
        // Plan year ends Dec 31 2026; today is Dec 10 2026 → 21 days out, inside the
        // 30-day approaching window.
        var now = new DateTime(2026, 12, 10, 0, 0, 0, DateTimeKind.Utc);
        var (scheduler, pub, src) = BuildScheduler(now);

        src.Plans.Add(PlanWith(new PlanYearDefinition
        {
            PlanYearStart = new DateTime(2026, 1, 1),
            PlanYearEnd = new DateTime(2026, 12, 31),
            PlanYearType = PlanYearType.CalendarYear
        }));

        var result = await scheduler.SweepOnceAsync(CancellationToken.None);

        result.ApproachingAttempted.Should().Be(1);
        result.TransitionsAttempted.Should().Be(0);
        pub.Events.Should().HaveCount(1);
        pub.Events[0].TransitionType.Should().Be(PlanYearTransitionType.ApproachingTransition);
        pub.Events[0].FromPlanYearEnd.Should().Be(new DateTime(2026, 12, 31));
        pub.Events[0].ToPlanYearStart.Should().Be(new DateTime(2027, 1, 1));
    }

    [Fact]
    public async Task Sweep_emits_Transition_after_planYearEnd_passes()
    {
        // Plan year ended Dec 31 2025; today is Jan 5 2026 → past end, no carryover.
        var now = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        var (scheduler, pub, src) = BuildScheduler(now);

        // Anchor at 2025-01-01 so the window containing now (Jan 5 2026)
        // is the 2026 calendar year — but ComputeWindow snaps calendar
        // plans to asOf's year. To exercise the post-end transition we
        // use a contract-year plan whose end falls just before now.
        src.Plans.Add(PlanWith(new PlanYearDefinition
        {
            PlanYearStart = new DateTime(2025, 1, 1),
            PlanYearEnd = new DateTime(2025, 12, 31),
            PlanYearType = PlanYearType.ContractYear,
            CarryoverDays = 0
        }));

        var result = await scheduler.SweepOnceAsync(CancellationToken.None);

        result.TransitionsAttempted.Should().Be(1);
        result.ApproachingAttempted.Should().Be(0);
        pub.Events.Should().ContainSingle(e => e.TransitionType == PlanYearTransitionType.Transition);
    }

    [Fact]
    public async Task Sweep_respects_CarryoverDays_before_emitting_Transition()
    {
        // Plan year ended 2025-12-31 with 30-day carryover. Today is
        // 2026-01-15 → 15 days into carryover → must NOT yet emit
        // Transition (retro claims still allowed).
        var now = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var (scheduler, pub, src) = BuildScheduler(now);

        src.Plans.Add(PlanWith(new PlanYearDefinition
        {
            PlanYearStart = new DateTime(2025, 1, 1),
            PlanYearEnd = new DateTime(2025, 12, 31),
            PlanYearType = PlanYearType.ContractYear,
            CarryoverDays = 30
        }));

        var result = await scheduler.SweepOnceAsync(CancellationToken.None);

        result.TransitionsAttempted.Should().Be(0);
        result.ApproachingAttempted.Should().Be(0);
        pub.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_is_idempotent_on_rerun()
    {
        var now = new DateTime(2026, 12, 15, 0, 0, 0, DateTimeKind.Utc);
        var (scheduler, pub, src) = BuildScheduler(now);

        src.Plans.Add(PlanWith(new PlanYearDefinition
        {
            PlanYearStart = new DateTime(2026, 1, 1),
            PlanYearEnd = new DateTime(2026, 12, 31),
            PlanYearType = PlanYearType.CalendarYear
        }));

        await scheduler.SweepOnceAsync(CancellationToken.None);
        await scheduler.SweepOnceAsync(CancellationToken.None);
        await scheduler.SweepOnceAsync(CancellationToken.None);

        // Three sweeps, one event — publisher dedups on EventId.
        pub.Events.Should().HaveCount(1);
        pub.Events[0].Version.Should().Be(1);
    }

    [Fact]
    public async Task Sweep_skips_plans_without_PlanYearDefinition()
    {
        var now = new DateTime(2026, 12, 15, 0, 0, 0, DateTimeKind.Utc);
        var (scheduler, pub, src) = BuildScheduler(now);

        src.Plans.Add(new BenefitPlan
        {
            TenantId = "t",
            PlanId = "p",
            PlanName = "Legacy",
            Payer = "X",
            EffectiveDate = new DateTime(2026, 1, 1),
            PlanType = PlanType.HMO,
            PlanYearDefinition = null
        });

        var result = await scheduler.SweepOnceAsync(CancellationToken.None);

        result.Inspected.Should().Be(1);
        result.ApproachingAttempted.Should().Be(0);
        result.TransitionsAttempted.Should().Be(0);
        pub.Events.Should().BeEmpty();
    }

    [Theory]
    [InlineData(PlanYearType.CalendarYear, "2026-01-01", "2026-12-31", "2026-12-15")]
    [InlineData(PlanYearType.ContractYear, "2024-04-01", "2025-03-31", "2026-03-20")]
    [InlineData(PlanYearType.FiscalYear, "2023-10-01", "2024-09-30", "2026-09-15")]
    [InlineData(PlanYearType.EnrollmentAnniversary, "2024-08-12", "2025-08-11", "2026-07-25")]
    public async Task Sweep_handles_each_plan_year_type(PlanYearType planYearType, string anchorStart, string anchorEnd, string asOf)
    {
        var now = DateTime.SpecifyKind(DateTime.Parse(asOf), DateTimeKind.Utc);
        var (scheduler, pub, src) = BuildScheduler(now);

        src.Plans.Add(PlanWith(new PlanYearDefinition
        {
            PlanYearStart = DateTime.Parse(anchorStart),
            PlanYearEnd = DateTime.Parse(anchorEnd),
            PlanYearType = planYearType
        }, planId: $"plan-{planYearType}"));

        var result = await scheduler.SweepOnceAsync(CancellationToken.None);

        result.ApproachingAttempted.Should().Be(1, $"asOf {asOf} sits inside the 30-day approach window for {planYearType}");
        pub.Events.Should().ContainSingle();
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTime utcNow) => _now = new DateTimeOffset(utcNow, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
