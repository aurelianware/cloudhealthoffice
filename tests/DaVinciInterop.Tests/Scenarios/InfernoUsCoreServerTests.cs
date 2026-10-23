using FluentAssertions;

namespace DaVinciInterop.Tests.Scenarios;

/// <summary>
/// INFERNO-USCORE-SERVER-001 — the ONC Inferno US Core 3.1.1 server suite
/// judging CHO.
///
/// Direction
/// ---------
/// The reverse of the br-payer scenarios. There CHO drives an independent payer
/// and this harness judges the answers; here the independent implementation is
/// the judge and CHO is the system under test. Nothing in this file decides
/// whether CHO conforms — Inferno does, and the harness reports its verdict.
///
/// Why this scenario exists
/// ------------------------
/// Gap-matrix G-14 and G-60 are both "no FHIR projection validated against the
/// profile it declares". CHO's own tests assert that it sets
/// <c>meta.profile</c>; they cannot assert that the resource underneath
/// satisfies the profile, because they are written against the same
/// understanding that produced the resource. An external validator is the only
/// thing that closes that circle, and US Core 3.1.1 is the floor CMS-0057-F
/// sets.
///
/// Which groups, and why not all of them
/// -------------------------------------
/// The groups come from interop/scenarios.json, not from this file: which
/// groups run is a claim about what CHO serves, and a claim belongs in the
/// inventory where a reader can check it against the description. US Core
/// 3.1.1 covers roughly thirty clinical resource types and CHO is a payer that
/// publishes almost none of them, so a whole-suite run would bury the Patient
/// and Encounter verdicts under failures for resources CHO has never claimed.
/// Naming the groups narrows what is tested; it does not soften the verdict on
/// what is.
///
/// What a pass here does and does not mean
/// ---------------------------------------
/// A pass is evidence that an implementation CHO does not own, running a suite
/// CHO did not write, found CHO's Patient and Encounter projections conformant
/// to US Core 3.1.1 for the seeded record. It is not CMS certification, and it
/// changes no CMS-0057-F scenario status on its own.
/// </summary>
[Collection(InteropCollection.Name)]
[Trait("Category", "DaVinciInterop")]
[Trait("Scenario", "INFERNO-USCORE-SERVER-001")]
[Trait("Target", "inferno-framework/us-core-test-kit")]
public sealed class InfernoUsCoreServerTests
{
    private const string ScenarioId = "INFERNO-USCORE-SERVER-001";

    [InteropFact]
    public async Task Inferno_judges_chos_us_core_patient_and_encounter_projections()
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
