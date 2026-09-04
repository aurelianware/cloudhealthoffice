using Cms0057Evidence;

namespace Cms0057Evidence.Tests;

internal static class TestData
{
    public static EvidenceIdentity Identity() => new() { GeneratedAtUtc = "2026-01-01T00:00:00Z", Environment = "test" };

    public static ManifestScenario Scenario(
        string id, string replaceStatus, string? qnxtStatus = null, string capability = "PriorAuthorization")
    {
        var augment = new Dictionary<string, ManifestBackend>();
        if (qnxtStatus is not null)
            augment["qnxt"] = new ManifestBackend { Status = qnxtStatus, Rationale = qnxtStatus == "GAP" ? "engagement work" : null };
        return new ManifestScenario
        {
            Id = id,
            Name = $"{id} name",
            Capability = capability,
            Replace = new ManifestBackend { Status = replaceStatus, Rationale = replaceStatus is "PARTIAL" or "GAP" ? "reason" : null },
            Augment = augment,
        };
    }

    public static ManifestDocument Manifest(params ManifestScenario[] scenarios) =>
        new() { SchemaVersion = 1, Scenarios = scenarios.ToList() };

    public static TestRunSummary Trx(params (string Test, TestOutcome Outcome)[] rows)
    {
        var results = rows.Select(r => new TestResult(r.Test, r.Outcome)).ToList();
        return new TestRunSummary(
            results.Count(r => r.Outcome == TestOutcome.Passed),
            results.Count(r => r.Outcome == TestOutcome.Failed),
            results.Count(r => r.Outcome == TestOutcome.Skipped),
            results);
    }
}
