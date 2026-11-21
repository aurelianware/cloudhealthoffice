using FluentAssertions;

namespace DaVinciInterop.Tests.Scenarios;

/// <summary>
/// INFERNO-SMART-SERVER-001 — the Inferno SMART App Launch 2.2 suite judging cho-smart-auth-service, with the kit as the app.
/// See the scenario's description in interop/scenarios.json for what it
/// runs and what the harness supplies.
/// </summary>
[Collection(InteropCollection.Name)]
[Trait("Category", "DaVinciInterop")]
[Trait("Scenario", "INFERNO-SMART-SERVER-001")]
[Trait("Target", "inferno-framework/smart-app-launch-test-kit")]
public sealed class InfernoSmartServerTests
{
    private const string ScenarioId = "INFERNO-SMART-SERVER-001";

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
