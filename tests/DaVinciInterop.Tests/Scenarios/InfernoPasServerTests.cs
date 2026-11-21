using FluentAssertions;

namespace DaVinciInterop.Tests.Scenarios;

/// <summary>
/// INFERNO-PAS-SERVER-001 — the Inferno PAS 2.2.1 server suite judging CHO's Claim/$submit, Claim/$inquire and pended-decision notifications.
/// See the scenario's description in interop/scenarios.json for what it
/// runs and what the harness supplies.
/// </summary>
[Collection(InteropCollection.Name)]
[Trait("Category", "DaVinciInterop")]
[Trait("Scenario", "INFERNO-PAS-SERVER-001")]
[Trait("Target", "inferno-framework/davinci-pas-test-kit")]
public sealed class InfernoPasServerTests
{
    private const string ScenarioId = "INFERNO-PAS-SERVER-001";

    [InteropFact]
    public async Task Inferno_judges_cho()
    {
        var outcome = await InfernoSuiteScenario.RunAsync(ScenarioId);

        outcome.Runs.Should().NotBeEmpty("a run that executed nothing has proven nothing");

        outcome.Result.ParsedStatus.Should().Be(
            InteropStatus.Passed,
            "Inferno reported: {0}",
            string.Join("; ", outcome.Result.Findings.Select(f => $"[{f.Severity}] {f.Summary}")));
    }
}
