using BenefitPlanService.Models;

namespace BenefitPlanService.Tests.Models;

public class PlanVersionIdTests
{
    [Fact]
    public void NewId_returns_26_char_crockford_base32()
    {
        var id = PlanVersionId.NewId();
        id.Should().HaveLength(26);
        id.Should().MatchRegex("^[0-9A-HJ-NP-TV-Z]+$");
    }

    [Fact]
    public void NewId_is_unique_across_calls()
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => PlanVersionId.NewId()).ToHashSet();
        ids.Count.Should().Be(1000);
    }

    [Fact]
    public void NewId_is_lexicographically_sortable_by_creation_time()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t1 = new DateTimeOffset(2026, 1, 1, 0, 0, 1, TimeSpan.Zero);
        var earlier = PlanVersionId.NewId(t0);
        var later = PlanVersionId.NewId(t1);
        string.Compare(earlier, later, StringComparison.Ordinal).Should().BeLessThan(0);
    }
}

public class BenefitPlanIdentityDefaultsTests
{
    [Fact]
    public void New_plan_gets_versionId_versionNumber_and_draft_state()
    {
        var plan = new BenefitPlan();
        plan.VersionId.Should().NotBeNullOrEmpty();
        plan.VersionNumber.Should().Be(1);
        plan.VersionState.Should().Be(PlanVersionState.Draft);
        plan.PredecessorVersionId.Should().BeNull();
    }
}
