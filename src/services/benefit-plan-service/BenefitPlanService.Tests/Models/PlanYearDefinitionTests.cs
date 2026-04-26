using BenefitPlanService.Models;

namespace BenefitPlanService.Tests.Models;

public class PlanYearDefinitionTests
{
    [Fact]
    public void CalendarYear_window_snaps_to_jan1_regardless_of_anchor()
    {
        var def = new PlanYearDefinition
        {
            PlanYearStart = new DateTime(2020, 1, 1),
            PlanYearEnd = new DateTime(2020, 12, 31),
            PlanYearType = PlanYearType.CalendarYear
        };

        var (start, end) = def.ComputeWindow(new DateTime(2026, 7, 15));

        start.Should().Be(new DateTime(2026, 1, 1));
        end.Should().Be(new DateTime(2026, 12, 31));
    }

    [Fact]
    public void ContractYear_window_rolls_forward_to_contain_asOf()
    {
        // Contract anchor: April 1 2024. As-of mid-2026 should land in
        // the 2026-04-01 → 2027-03-31 window.
        var def = new PlanYearDefinition
        {
            PlanYearStart = new DateTime(2024, 4, 1),
            PlanYearEnd = new DateTime(2025, 3, 31),
            PlanYearType = PlanYearType.ContractYear
        };

        var (start, end) = def.ComputeWindow(new DateTime(2026, 7, 15));

        start.Should().Be(new DateTime(2026, 4, 1));
        end.Should().Be(new DateTime(2027, 3, 31));
    }

    [Fact]
    public void FiscalYear_window_anchored_to_payer_fiscal_start()
    {
        // Federal fiscal year: October 1 → September 30.
        var def = new PlanYearDefinition
        {
            PlanYearStart = new DateTime(2023, 10, 1),
            PlanYearEnd = new DateTime(2024, 9, 30),
            PlanYearType = PlanYearType.FiscalYear
        };

        var (start, end) = def.ComputeWindow(new DateTime(2026, 4, 26));

        start.Should().Be(new DateTime(2025, 10, 1));
        end.Should().Be(new DateTime(2026, 9, 30));
    }

    [Fact]
    public void EnrollmentAnniversary_window_rolls_per_member_anchor()
    {
        // Member enrolled August 12, 2024. As-of April 26 2026 should
        // sit in the 2025-08-12 → 2026-08-11 window.
        var def = new PlanYearDefinition
        {
            PlanYearStart = new DateTime(2024, 8, 12),
            PlanYearEnd = new DateTime(2025, 8, 11),
            PlanYearType = PlanYearType.EnrollmentAnniversary
        };

        var (start, end) = def.ComputeWindow(new DateTime(2026, 4, 26));

        start.Should().Be(new DateTime(2025, 8, 12));
        end.Should().Be(new DateTime(2026, 8, 11));
    }

    [Fact]
    public void Window_rolls_backward_when_asOf_precedes_anchor()
    {
        // Defensive: scheduler should still produce a stable window even
        // if it runs against a stale snapshot.
        var def = new PlanYearDefinition
        {
            PlanYearStart = new DateTime(2030, 1, 1),
            PlanYearEnd = new DateTime(2030, 12, 31),
            PlanYearType = PlanYearType.ContractYear
        };

        var (start, end) = def.ComputeWindow(new DateTime(2026, 4, 26));

        start.Should().Be(new DateTime(2026, 1, 1));
        end.Should().Be(new DateTime(2026, 12, 31));
    }

    [Fact]
    public void EventId_builder_produces_deterministic_idempotency_key()
    {
        var planYearEnd = new DateTime(2026, 12, 31);

        var id1 = PlanYearTransitionEvent.BuildEventId(
            PlanYearTransitionType.Transition, "tenant-1", "plan-A", planYearEnd);
        var id2 = PlanYearTransitionEvent.BuildEventId(
            PlanYearTransitionType.Transition, "tenant-1", "plan-A", planYearEnd);

        id1.Should().Be(id2);
        id1.Should().Be("transition:tenant-1:plan-A:20261231");
    }

    [Fact]
    public void AccumulatorTarget_defaults_to_ResetAtPlanYearEnd()
    {
        var t = new AccumulatorTarget { BenefitCategory = "Deductible", Limit = 1500m };
        t.ResetBehavior.Should().Be(PlanYearResetBehavior.ResetAtPlanYearEnd);
        t.Unit.Should().Be("USD");
    }
}
