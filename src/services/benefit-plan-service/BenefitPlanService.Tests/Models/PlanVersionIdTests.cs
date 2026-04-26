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
    public void Default_plan_has_empty_identity_so_legacy_rows_are_distinguishable()
    {
        // Defaults must be "absent" markers so that documents persisted
        // before the version-chain feature can be detected on read and
        // hydrated as Published v1. The service layer is responsible for
        // populating these fields on every new write.
        var plan = new BenefitPlan();
        plan.VersionId.Should().BeEmpty();
        plan.VersionNumber.Should().Be(0);
        plan.VersionState.Should().Be(PlanVersionState.Draft); // = 0; correct initial state for new instances.
        // Note: legacy documents that predate versioning also deserialize to Draft (value 0)
        // because versionState is absent in their persisted JSON, but Hydrate() normalizes
        // them to Published when VersionId is also empty — that combination unambiguously
        // identifies a legacy row.
        plan.PredecessorVersionId.Should().BeNull();
    }
}
