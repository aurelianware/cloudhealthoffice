using FluentAssertions;

namespace DaVinciInterop.Tests.Scenarios;

/// <summary>
/// INFERNO-PDEX-SERVER-001 — the Inferno PDex payer-server suite judging
/// CHO's HRex <c>Patient/$member-match</c> as the old payer of a
/// Payer-to-Payer exchange (gap-matrix G-35, G-60).
///
/// Direction
/// ---------
/// CHO is the system under test and the kit is the new payer. The kit
/// authenticates itself with SMART Backend Services as a backend client the
/// harness registers with <c>system/PayerToPayer.read</c> and a registered
/// payer, so the payer binding and the consent check are CHO's real ones.
///
/// Which groups
/// ------------
/// The <c>$member-match</c> workflow group and the no-match failure case. Not
/// the multiple-match case, and not the PDex 2.0 groups that read a member's
/// data by query with the requesting payer's token: CHO's Payer-to-Payer data
/// path is the PDex 2.1 bulk exchange. See the scenario's description in
/// interop/scenarios.json.
/// </summary>
[Collection(InteropCollection.Name)]
[Trait("Category", "DaVinciInterop")]
[Trait("Scenario", "INFERNO-PDEX-SERVER-001")]
[Trait("Target", "inferno-framework/davinci-pdex-test-kit")]
public sealed class InfernoPdexServerTests
{
    private const string ScenarioId = "INFERNO-PDEX-SERVER-001";

    [InteropFact]
    public async Task Inferno_judges_chos_hrex_member_match()
    {
        var outcome = await InfernoSuiteScenario.RunAsync(ScenarioId);

        outcome.Runs.Should().NotBeEmpty(
            "the scenario runs the $member-match group and its no-match case, so a run that executed nothing has proven nothing");

        outcome.Result.ParsedStatus.Should().Be(
            InteropStatus.Passed,
            "Inferno reported: {0}",
            string.Join("; ", outcome.Result.Findings.Select(f => $"[{f.Severity}] {f.Summary}")));
    }
}
