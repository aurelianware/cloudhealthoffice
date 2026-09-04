using Cms0057Evidence;
using FluentAssertions;

namespace Cms0057Evidence.Tests;

public class EvidenceBuilderTests
{
    [Fact]
    public void Passable_WithPassingNonGapTest_IsAccepted()
    {
        var manifest = TestData.Manifest(TestData.Scenario("PAS-03", "PASSABLE", "GAP"));
        var traits = new[]
        {
            new ScenarioTrait("T.PAS03_Replace", "PAS-03", ScenarioTrait.ReplaceBackend, IsGap: false),
            new ScenarioTrait("T.PAS03_Augment", "PAS-03", ScenarioTrait.AugmentBackend, IsGap: true),
        };
        var trx = TestData.Trx(("T.PAS03_Replace", TestOutcome.Passed), ("T.PAS03_Augment", TestOutcome.Passed));

        var report = EvidenceBuilder.Build(manifest, traits, trx, TestData.Identity());

        var s = report.Scenarios.Should().ContainSingle().Subject;
        var replace = s.Backends.Single(b => b.Backend == BackendIds.Replace);
        replace.DeclaredStatus.Should().Be("PASSABLE");
        replace.TestExecutionStatus.Should().Be(ExecutionStatus.Passed);
        replace.SupportingTests.Should().ContainSingle();
    }

    [Fact]
    public void GapAssertionPassing_StaysGap_AndIsNotPromoted()
    {
        // The single most important behavior: a passing GAP-assertion test
        // confirms the gap; it must not become PASSABLE.
        var manifest = TestData.Manifest(TestData.Scenario("PAS-08", "GAP"));
        var traits = new[] { new ScenarioTrait("T.PAS08_Gap", "PAS-08", ScenarioTrait.ReplaceBackend, IsGap: true) };
        var trx = TestData.Trx(("T.PAS08_Gap", TestOutcome.Passed));

        var report = EvidenceBuilder.Build(manifest, traits, trx, TestData.Identity());

        var replace = report.Scenarios.Single().Backends.Single(b => b.Backend == BackendIds.Replace);
        replace.DeclaredStatus.Should().Be("GAP");                       // declared capability unchanged
        replace.TestExecutionStatus.Should().Be(ExecutionStatus.Passed); // test executed and passed
        report.KnownGaps.Should().ContainSingle(g => g.ScenarioId == "PAS-08" && g.Status == "GAP");
    }

    [Fact]
    public void Partial_IsRecordedAndListedAsGap()
    {
        var manifest = TestData.Manifest(TestData.Scenario("PAS-04", "PARTIAL", "GAP"));
        var traits = new[] { new ScenarioTrait("T.PAS04", "PAS-04", ScenarioTrait.ReplaceBackend, IsGap: false) };
        var trx = TestData.Trx(("T.PAS04", TestOutcome.Passed));

        var report = EvidenceBuilder.Build(manifest, traits, trx, TestData.Identity());

        report.KnownGaps.Should().Contain(g => g.ScenarioId == "PAS-04" && g.Backend == BackendIds.Replace && g.Status == "PARTIAL");
        report.KnownGaps.Should().Contain(g => g.Backend == BackendIds.Augment("qnxt") && g.Status == "GAP");
    }

    [Fact]
    public void Passable_WithOnlyGapTest_FailsReconciliation()
    {
        var manifest = TestData.Manifest(TestData.Scenario("PAS-03", "PASSABLE"));
        var traits = new[] { new ScenarioTrait("T.OnlyGap", "PAS-03", ScenarioTrait.ReplaceBackend, IsGap: true) };
        var trx = TestData.Trx(("T.OnlyGap", TestOutcome.Passed));

        var act = () => EvidenceBuilder.Build(manifest, traits, trx, TestData.Identity());
        act.Should().Throw<EvidenceReconciliationException>().WithMessage("*PASSABLE*no passing non-GAP*");
    }

    [Fact]
    public void Passable_WithFailingProvingTest_FailsReconciliation_AndSummaryShowsFailure()
    {
        var manifest = TestData.Manifest(TestData.Scenario("PAS-03", "PASSABLE"));
        var traits = new[] { new ScenarioTrait("T.PAS03", "PAS-03", ScenarioTrait.ReplaceBackend, IsGap: false) };
        var trx = TestData.Trx(("T.PAS03", TestOutcome.Failed));

        var act = () => EvidenceBuilder.Build(manifest, traits, trx, TestData.Identity());
        act.Should().Throw<EvidenceReconciliationException>();
    }

    [Fact]
    public void UnknownScenarioTrait_FailsReconciliation()
    {
        var manifest = TestData.Manifest(TestData.Scenario("PAS-03", "PASSABLE"));
        var traits = new[]
        {
            new ScenarioTrait("T.PAS03", "PAS-03", ScenarioTrait.ReplaceBackend, IsGap: false),
            new ScenarioTrait("T.Ghost", "PAS-99", ScenarioTrait.ReplaceBackend, IsGap: false),
        };
        var trx = TestData.Trx(("T.PAS03", TestOutcome.Passed), ("T.Ghost", TestOutcome.Passed));

        var act = () => EvidenceBuilder.Build(manifest, traits, trx, TestData.Identity());
        act.Should().Throw<EvidenceReconciliationException>().WithMessage("*unknown scenario id*PAS-99*");
    }

    [Fact]
    public void ManifestScenarioWithNoTest_FailsReconciliation()
    {
        var manifest = TestData.Manifest(TestData.Scenario("PAS-03", "PASSABLE"), TestData.Scenario("PAS-04", "PARTIAL"));
        var traits = new[] { new ScenarioTrait("T.PAS03", "PAS-03", ScenarioTrait.ReplaceBackend, IsGap: false) };
        var trx = TestData.Trx(("T.PAS03", TestOutcome.Passed));

        var act = () => EvidenceBuilder.Build(manifest, traits, trx, TestData.Identity());
        act.Should().Throw<EvidenceReconciliationException>().WithMessage("*no acceptance test*PAS-04*");
    }

    [Fact]
    public void ReplaceAndAugment_AreSeparate_AugmentPassableNeedsAugmentTest()
    {
        // Augment declared PASSABLE but only a Replace test exists → must fail:
        // a Replace test cannot prove an Augment capability.
        var manifest = TestData.Manifest(TestData.Scenario("PAS-03", "PASSABLE", qnxtStatus: "PASSABLE"));
        var traits = new[] { new ScenarioTrait("T.PAS03_Replace", "PAS-03", ScenarioTrait.ReplaceBackend, IsGap: false) };
        var trx = TestData.Trx(("T.PAS03_Replace", TestOutcome.Passed));

        var act = () => EvidenceBuilder.Build(manifest, traits, trx, TestData.Identity());
        act.Should().Throw<EvidenceReconciliationException>().WithMessage($"*{BackendIds.Augment("qnxt")}*PASSABLE*");
    }

    [Fact]
    public void GenericAugmentTest_DoesNotProve_KeySpecificPassable()
    {
        // A generic [Trait("Backend","Augment")] test must not prove augment.qnxt
        // PASSABLE — only a key-specific augment.qnxt test may.
        var manifest = TestData.Manifest(TestData.Scenario("PAS-03", "PASSABLE", qnxtStatus: "PASSABLE"));
        var traits = new[]
        {
            new ScenarioTrait("T.PAS03_Replace", "PAS-03", ScenarioTrait.ReplaceBackend, IsGap: false),
            new ScenarioTrait("T.PAS03_Augment", "PAS-03", ScenarioTrait.AugmentBackend, IsGap: false),
        };
        var trx = TestData.Trx(("T.PAS03_Replace", TestOutcome.Passed), ("T.PAS03_Augment", TestOutcome.Passed));

        var act = () => EvidenceBuilder.Build(manifest, traits, trx, TestData.Identity());
        act.Should().Throw<EvidenceReconciliationException>().WithMessage($"*{BackendIds.Augment("qnxt")}*PASSABLE*");
    }

    [Fact]
    public void KeySpecificAugmentTest_ProvesThatBackend_Only()
    {
        // augment.qnxt PASSABLE proven by a key-specific test; a second augment
        // backend (facets) with no key-specific test would fail — so each
        // backend is proven independently.
        var scenario = TestData.Scenario("PAS-03", "PASSABLE", qnxtStatus: "PASSABLE");
        var manifest = TestData.Manifest(scenario);
        var traits = new[]
        {
            new ScenarioTrait("T.PAS03_Replace", "PAS-03", ScenarioTrait.ReplaceBackend, IsGap: false),
            new ScenarioTrait("T.PAS03_Qnxt", "PAS-03", BackendIds.Augment("qnxt"), IsGap: false),
        };
        var trx = TestData.Trx(("T.PAS03_Replace", TestOutcome.Passed), ("T.PAS03_Qnxt", TestOutcome.Passed));

        var report = EvidenceBuilder.Build(manifest, traits, trx, TestData.Identity());

        var qnxt = report.Scenarios.Single().Backends.Single(b => b.Backend == BackendIds.Augment("qnxt"));
        qnxt.DeclaredStatus.Should().Be("PASSABLE");
        qnxt.TestExecutionStatus.Should().Be(ExecutionStatus.Passed);
        qnxt.SupportingTests.Should().Contain("T.PAS03_Qnxt");
    }

    [Fact]
    public void Output_IsDeterministicallyOrdered()
    {
        var manifest = TestData.Manifest(
            TestData.Scenario("PAS-04", "PARTIAL", "GAP"),
            TestData.Scenario("PAS-01", "PASSABLE", "GAP"));
        var traits = new[]
        {
            new ScenarioTrait("T.PAS01", "PAS-01", ScenarioTrait.ReplaceBackend, IsGap: false),
            new ScenarioTrait("T.PAS04", "PAS-04", ScenarioTrait.ReplaceBackend, IsGap: false),
        };
        var trx = TestData.Trx(("T.PAS01", TestOutcome.Passed), ("T.PAS04", TestOutcome.Passed));

        var report = EvidenceBuilder.Build(manifest, traits, trx, TestData.Identity());

        report.Scenarios.Select(s => s.Id).Should().ContainInOrder("PAS-01", "PAS-04");
        report.Scenarios[0].Backends[0].Backend.Should().Be(BackendIds.Replace); // replace always first
        report.KnownGaps.Should().BeInAscendingOrder(g => g.ScenarioId);
    }
}
