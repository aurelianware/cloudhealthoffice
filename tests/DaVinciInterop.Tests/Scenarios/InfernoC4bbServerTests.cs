using FluentAssertions;

namespace DaVinciInterop.Tests.Scenarios;

/// <summary>
/// INFERNO-C4BB-SERVER-001 — the ONC Inferno CARIN Blue Button 2.0.0 server
/// suite judging CHO's financial ExplanationOfBenefit surface.
///
/// Direction
/// ---------
/// CHO is the system under test and the kit is the judge, as in
/// INFERNO-USCORE-SERVER-001.
///
/// The financial suite, and the one that cannot run
/// ------------------------------------------------
/// This runs <c>c4bb_v200</c> — the financial EOB suite. The kit also ships
/// <c>c4bb_v200devnonfinancial</c>, and at the pinned tag that suite cannot
/// judge CHO at all: it checks the superseded <c>-NonFinancial</c> canonicals
/// from the April 2023 branch snapshot it bundles, while CHO's Provider Access
/// EOBs declare the CARIN BB 2.2.0 <c>-Basis</c> profiles. The canonical URLs
/// differ, so the suite would match nothing in either direction and a reader
/// could easily mistake "no match" for "fails conformance".
///
/// The resolution is a kit release regenerated against CARIN BB 2.2.0 — never
/// renaming CHO's profiles to match a superseded draft, which would make the
/// suite pass by making CHO wrong against the current IG. That mismatch is
/// recorded as <c>knownMismatches</c> on <c>inferno-c4bb</c> in
/// interop/versions.json so the next pin bump re-checks it rather than
/// rediscovering it.
///
/// Which groups
/// ------------
/// From interop/scenarios.json, for the reason given in
/// <see cref="InfernoUsCoreServerTests"/>: RelatedPerson is not run because CHO
/// publishes none, the SMART launch group is not run because the harness
/// supplies an already-issued token rather than driving a launch, and the
/// visual inspection group is manual by construction.
/// </summary>
[Collection(InteropCollection.Name)]
[Trait("Category", "DaVinciInterop")]
[Trait("Scenario", "INFERNO-C4BB-SERVER-001")]
[Trait("Target", "inferno-framework/carin-for-blue-button-test-kit")]
public sealed class InfernoC4bbServerTests
{
    private const string ScenarioId = "INFERNO-C4BB-SERVER-001";

    [InteropFact]
    public async Task Inferno_judges_chos_carin_blue_button_financial_surface()
    {
        var outcome = await InfernoSuiteScenario.RunAsync(ScenarioId);

        outcome.Runs.Should().NotBeEmpty(
            "the scenario names groups to run, so a run that executed none of them has proven nothing");

        outcome.Result.ParsedStatus.Should().Be(
            InteropStatus.Passed,
            "Inferno reported: {0}",
            string.Join("; ", outcome.Result.Findings.Select(f => $"[{f.Severity}] {f.Summary}")));
    }
}
