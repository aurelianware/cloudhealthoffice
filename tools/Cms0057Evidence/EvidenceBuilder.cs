namespace Cms0057Evidence;

public sealed class EvidenceReconciliationException : Exception
{
    public EvidenceReconciliationException(string message) : base(message) { }
}

/// <summary>
/// Reconciles the manifest (declared capability), the suite's traits (which
/// tests cover which scenario/backend), and the TRX (test execution) into a
/// deterministic <see cref="EvidenceReport"/>.
///
/// Core invariant: declared capability status and test execution status are
/// kept SEPARATE. A passing GAP-assertion test proves the gap; it never turns a
/// GAP into PASSABLE. Conversely, a scenario declared PASSABLE must be backed by
/// a passing non-GAP test or reconciliation fails.
/// </summary>
public static class EvidenceBuilder
{
    public static EvidenceReport Build(
        ManifestDocument manifest,
        IReadOnlyList<ScenarioTrait> traits,
        TestRunSummary trx,
        EvidenceIdentity identity)
    {
        var manifestIds = manifest.Scenarios.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

        // Reconciliation: every trait scenario must exist in the manifest.
        var unknown = traits.Select(t => t.ScenarioId).Distinct()
            .Where(id => !manifestIds.Contains(id)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
            throw new EvidenceReconciliationException(
                $"Test traits reference unknown scenario id(s) not in the manifest: {string.Join(", ", unknown)}");

        // Reconciliation: no scenario may silently lose all its tests.
        var testedIds = traits.Select(t => t.ScenarioId).ToHashSet(StringComparer.Ordinal);
        var untested = manifest.Scenarios.Select(s => s.Id)
            .Where(id => !testedIds.Contains(id)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (untested.Count > 0)
            throw new EvidenceReconciliationException(
                $"Manifest scenario(s) have no acceptance test: {string.Join(", ", untested)}");

        var outcomeByTest = BuildOutcomeLookup(trx);

        var scenarios = new List<ScenarioEvidence>();
        var gaps = new List<GapEntry>();

        foreach (var s in manifest.Scenarios.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            var backends = new List<BackendEvidence>();

            // Replace (product capability) is always emitted first.
            var replaceTests = traits.Where(t => t.ScenarioId == s.Id && t.Backend == ScenarioTrait.ReplaceBackend).ToList();
            backends.Add(BuildBackend(s.Id, BackendIds.Replace, s.Replace, replaceTests, outcomeByTest));

            // Augment backends (integration capability), sorted by key.
            var augmentTests = traits.Where(t => t.ScenarioId == s.Id && t.Backend == ScenarioTrait.AugmentBackend).ToList();
            foreach (var key in s.Augment.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                backends.Add(BuildBackend(s.Id, BackendIds.Augment(key), s.Augment[key], augmentTests, outcomeByTest));
            }

            foreach (var b in backends)
            {
                if (b.DeclaredStatus is Status.Partial or Status.Gap)
                    gaps.Add(new GapEntry { ScenarioId = s.Id, Backend = b.Backend, Status = b.DeclaredStatus, Rationale = b.Rationale });
            }

            scenarios.Add(new ScenarioEvidence
            {
                Id = s.Id, Name = s.Name, Capability = s.Capability, Backends = backends,
            });
        }

        return new EvidenceReport
        {
            SchemaVersion = manifest.SchemaVersion,
            Identity = identity,
            TestSummary = new TestExecutionSummary
            {
                Passed = trx.Passed, Failed = trx.Failed, Skipped = trx.Skipped, Total = trx.Total,
            },
            Scenarios = scenarios,
            KnownGaps = gaps
                .OrderBy(g => g.ScenarioId, StringComparer.Ordinal)
                .ThenBy(g => g.Backend, StringComparer.Ordinal)
                .ToList(),
        };
    }

    private static BackendEvidence BuildBackend(
        string scenarioId, string backendId, ManifestBackend declared,
        List<ScenarioTrait> tests, IReadOnlyDictionary<string, TestOutcome> outcomeByTest)
    {
        var supporting = tests
            .Select(t => t.TestName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var exec = AggregateExecution(tests, outcomeByTest);

        // A PASSABLE claim must be proven by a passing, non-GAP test.
        if (declared.Status == Status.Passable)
        {
            var provingTest = tests.Any(t => !t.IsGap
                && outcomeByTest.TryGetValue(t.TestName, out var o) && o == TestOutcome.Passed);
            if (!provingTest)
                throw new EvidenceReconciliationException(
                    $"Scenario {scenarioId} declares {backendId} = PASSABLE but has no passing non-GAP test to prove it.");
        }

        return new BackendEvidence
        {
            Backend = backendId,
            DeclaredStatus = declared.Status,
            TestExecutionStatus = exec,
            SupportingTests = supporting,
            Rationale = declared.Rationale,
        };
    }

    private static string AggregateExecution(
        List<ScenarioTrait> tests, IReadOnlyDictionary<string, TestOutcome> outcomeByTest)
    {
        var outcomes = tests
            .Select(t => outcomeByTest.TryGetValue(t.TestName, out var o) ? (TestOutcome?)o : null)
            .Where(o => o is not null)
            .Select(o => o!.Value)
            .ToList();

        if (outcomes.Count == 0) return ExecutionStatus.NotRun;
        if (outcomes.Any(o => o == TestOutcome.Failed)) return ExecutionStatus.Failed;
        if (outcomes.All(o => o == TestOutcome.Passed)) return ExecutionStatus.Passed;
        return ExecutionStatus.NotRun; // only skipped
    }

    private static IReadOnlyDictionary<string, TestOutcome> BuildOutcomeLookup(TestRunSummary trx)
    {
        // A test can appear once; if duplicated (theory rows normalized to the
        // same name), a single failure makes the whole method Failed.
        var map = new Dictionary<string, TestOutcome>(StringComparer.Ordinal);
        foreach (var r in trx.Results)
        {
            if (!map.TryGetValue(r.TestName, out var existing))
                map[r.TestName] = r.Outcome;
            else if (r.Outcome == TestOutcome.Failed || existing == TestOutcome.Failed)
                map[r.TestName] = TestOutcome.Failed;
            else if (r.Outcome == TestOutcome.Passed)
                map[r.TestName] = TestOutcome.Passed;
        }
        return map;
    }
}
