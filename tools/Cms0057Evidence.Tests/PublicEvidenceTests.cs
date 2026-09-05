using Cms0057Evidence;
using FluentAssertions;

namespace Cms0057Evidence.Tests;

public class PublicEvidenceTests
{
    // ── Small builders for isolated projector tests ─────────────────────────────

    private static BackendEvidence Backend(string id, string declared, string exec, params string[] supporting) =>
        new()
        {
            Backend = id,
            DeclaredStatus = declared,
            TestExecutionStatus = exec,
            SupportingTests = supporting.ToList(),
            Rationale = declared is "PARTIAL" or "GAP" ? "internal rationale text (do not publish)" : null,
        };

    private static ScenarioEvidence Scenario(string id, string name, params BackendEvidence[] backends) =>
        new() { Id = id, Name = name, Capability = "PriorAuthorization", Backends = backends.ToList() };

    private static EvidenceReport Report(TestExecutionSummary summary, params ScenarioEvidence[] scenarios) =>
        new()
        {
            SchemaVersion = 1,
            Identity = new EvidenceIdentity
            {
                GeneratedAtUtc = "2026-01-02T03:04:05Z",
                Repository = "aurelianware/cloudhealthoffice",
                CommitSha = "0123456789abcdef0123456789abcdef01234567",
                Ref = "main",
                WorkflowRunId = "SECRET-RUN-9999",
                Environment = "CI",
            },
            TestSummary = summary,
            Scenarios = scenarios.ToList(),
        };

    private static TestExecutionSummary Summary(int passed, int failed = 0, int skipped = 0) =>
        new() { Passed = passed, Failed = failed, Skipped = skipped, Total = passed + failed + skipped };

    // ── Tests ───────────────────────────────────────────────────────────────────

    [Fact]
    public void GapDeclared_StaysGapPublicly_EvenWhenTestExecutionPassed()
    {
        // The single most important guarantee: a passing GAP-assertion test must
        // never surface publicly as a green pass.
        var report = Report(Summary(passed: 1),
            Scenario("PAS-08", "Drug exclusion",
                Backend(BackendIds.Replace, Status.Gap, ExecutionStatus.Passed, "T.Pas08_Gap")));

        var pub = PublicEvidenceProjector.Project(report);

        var s = pub.Scenarios.Should().ContainSingle().Subject;
        s.Replace.Should().Be("GAP");
        pub.ReplaceSummary.Gap.Should().Be(1);
        pub.ReplaceSummary.Passable.Should().Be(0);
    }

    [Fact]
    public void ReplaceAndIntegrationCounts_AreComputedIndependently()
    {
        var report = Report(Summary(passed: 2),
            Scenario("PAS-03", "PAS submit",
                Backend(BackendIds.Replace, Status.Passable, ExecutionStatus.Passed, "T.A"),
                Backend(BackendIds.Augment("qnxt"), Status.Gap, ExecutionStatus.Passed, "T.B")),
            Scenario("PAS-04", "PAS inquiry",
                Backend(BackendIds.Replace, Status.Partial, ExecutionStatus.Passed, "T.C"),
                Backend(BackendIds.Augment("qnxt"), Status.Gap, ExecutionStatus.Passed, "T.D")));

        var pub = PublicEvidenceProjector.Project(report);

        pub.ReplaceSummary.Passable.Should().Be(1);
        pub.ReplaceSummary.Partial.Should().Be(1);
        pub.ReplaceSummary.Gap.Should().Be(0);

        pub.Integrations.Should().ContainKey("qnxt");
        pub.Integrations["qnxt"].Gap.Should().Be(2);
        pub.Integrations["qnxt"].Passable.Should().Be(0);
        pub.Integrations["qnxt"].Partial.Should().Be(0);
    }

    [Fact]
    public void Scenarios_AreDeterministicallyOrdered()
    {
        var report = Report(Summary(passed: 2),
            Scenario("PAS-04", "b", Backend(BackendIds.Replace, Status.Passable, ExecutionStatus.Passed, "T2")),
            Scenario("PAS-01", "a", Backend(BackendIds.Replace, Status.Passable, ExecutionStatus.Passed, "T1")));

        var pub = PublicEvidenceProjector.Project(report);

        pub.Scenarios.Select(s => s.Id).Should().ContainInOrder("PAS-01", "PAS-04");
        pub.ScenarioCount.Should().Be(2);
    }

    [Fact]
    public void PublicJson_ContainsNoPrivateOrInternalFields()
    {
        var report = Report(Summary(passed: 1),
            Scenario("PAS-04", "PAS inquiry",
                Backend(BackendIds.Replace, Status.Partial, ExecutionStatus.Passed, "Cms0057Acceptance.Tests.PasTests.Inquiry_Persists")));

        var json = EvidenceWriters.ToPublicJson(PublicEvidenceProjector.Project(report));

        // No supporting test names, rationales, run ids, environment, or execution
        // status leak into the public snapshot.
        json.Should().NotContain("supportingTests");
        json.Should().NotContain("Cms0057Acceptance.Tests");
        json.Should().NotContain("rationale");
        json.Should().NotContain("do not publish");
        json.Should().NotContain("workflowRunId");
        json.Should().NotContain("SECRET-RUN-9999");
        json.Should().NotContain("testExecutionStatus");
        json.Should().NotContain("\"environment\"");
        // But it does carry the public, safe fields.
        json.Should().Contain("\"replace\": \"PARTIAL\"");
        json.Should().Contain("\"evidenceStatus\": \"validated\"");
        json.Should().Contain("sourceCommitUrl");
    }

    [Fact]
    public void SourceCommitUrl_AndShortSha_AreDerived()
    {
        var report = Report(Summary(passed: 1),
            Scenario("PAS-01", "CRD", Backend(BackendIds.Replace, Status.Passable, ExecutionStatus.Passed, "T")));

        var pub = PublicEvidenceProjector.Project(report);

        pub.CommitShaShort.Should().Be("0123456789ab");
        pub.SourceCommitUrl.Should().Be(
            "https://github.com/aurelianware/cloudhealthoffice/commit/0123456789abcdef0123456789abcdef01234567");
    }

    [Fact]
    public void FailedAcceptanceRun_CannotProducePublishableEvidence()
    {
        var report = Report(Summary(passed: 1, failed: 1),
            Scenario("PAS-03", "PAS submit",
                Backend(BackendIds.Replace, Status.Passable, ExecutionStatus.Failed, "T")));

        var act = () => PublicEvidenceProjector.Project(report);

        act.Should().Throw<PublicEvidenceException>().WithMessage("*failed*");
    }

    [Fact]
    public void NullReport_FailsSafely()
    {
        var act = () => PublicEvidenceProjector.Project(null!);
        act.Should().Throw<PublicEvidenceException>();
    }

    [Fact]
    public void UnknownFutureBackend_FlowsThroughWithoutBreakingTheFormat()
    {
        // The projector discovers backend keys from the data, so a backend the
        // writer has never heard of still appears — independently counted.
        var report = Report(Summary(passed: 1),
            Scenario("PAS-03", "PAS submit",
                Backend(BackendIds.Replace, Status.Passable, ExecutionStatus.Passed, "T.A"),
                Backend(BackendIds.Augment("newcore"), Status.Partial, ExecutionStatus.Passed, "T.B")));

        var pub = PublicEvidenceProjector.Project(report);

        pub.Integrations.Should().ContainKey("newcore");
        pub.Integrations["newcore"].Partial.Should().Be(1);
        pub.Scenarios.Single().Integrations["newcore"].Should().Be("PARTIAL");
    }

    [Fact]
    public void UnknownDeclaredStatus_FailsFast_RatherThanMiscounting()
    {
        // A status outside PASSABLE/PARTIAL/GAP/N/A must not be silently dropped
        // from the summary — that would make counts disagree with the matrix.
        var report = Report(Summary(passed: 1),
            Scenario("PAS-03", "PAS submit",
                Backend(BackendIds.Replace, "MAYBE", ExecutionStatus.Passed, "T.A")));

        var act = () => PublicEvidenceProjector.Project(report);

        act.Should().Throw<PublicEvidenceException>().WithMessage("*MAYBE*");
    }

    [Fact]
    public void NaStatus_IsPreservedAndCounted()
    {
        var report = Report(Summary(passed: 1),
            Scenario("PAS-02", "DTR",
                Backend(BackendIds.Replace, Status.Passable, ExecutionStatus.Passed, "T.A"),
                Backend(BackendIds.Augment("qnxt"), Status.NotApplicable, ExecutionStatus.NotRun)));

        var pub = PublicEvidenceProjector.Project(report);

        pub.Scenarios.Single().Integrations["qnxt"].Should().Be("N/A");
        pub.Integrations["qnxt"].Na.Should().Be(1);
    }

    [Fact]
    public void EndToEnd_FromBuilder_ProducesConsistentPublicSnapshot()
    {
        // Exercise the real path: manifest + traits + TRX -> report -> public.
        var manifest = TestData.Manifest(
            TestData.Scenario("PAS-03", "PASSABLE", "GAP"),
            TestData.Scenario("PAS-08", "GAP"));
        var traits = new[]
        {
            new ScenarioTrait("T.Pas03_Replace", "PAS-03", ScenarioTrait.ReplaceBackend, IsGap: false),
            new ScenarioTrait("T.Pas03_Qnxt", "PAS-03", ScenarioTrait.AugmentBackend, IsGap: true),
            new ScenarioTrait("T.Pas08_Gap", "PAS-08", ScenarioTrait.ReplaceBackend, IsGap: true),
        };
        var trx = TestData.Trx(
            ("T.Pas03_Replace", TestOutcome.Passed),
            ("T.Pas03_Qnxt", TestOutcome.Passed),
            ("T.Pas08_Gap", TestOutcome.Passed));

        var report = EvidenceBuilder.Build(manifest, traits, trx, TestData.Identity());
        var pub = PublicEvidenceProjector.Project(report);

        pub.ScenarioCount.Should().Be(2);
        pub.Scenarios.Single(s => s.Id == "PAS-03").Replace.Should().Be("PASSABLE");
        pub.Scenarios.Single(s => s.Id == "PAS-03").Integrations["qnxt"].Should().Be("GAP");
        pub.Scenarios.Single(s => s.Id == "PAS-08").Replace.Should().Be("GAP");
        pub.ReplaceSummary.Passable.Should().Be(1);
        pub.ReplaceSummary.Gap.Should().Be(1);
        pub.Integrations["qnxt"].Gap.Should().Be(1); // PAS-03 qnxt GAP (PAS-08 has no qnxt dimension)
    }
}
