using FluentAssertions;

namespace DaVinciInterop.Tests.Scenarios;

/// <summary>
/// INFERNO-PLANNET-SERVER-001 — the Inferno Da Vinci PDex Plan-Net server
/// suite judging CHO's public Provider Directory (gap-matrix G-36).
///
/// Direction
/// ---------
/// CHO is the system under test and the kit is the judge, as in
/// INFERNO-USCORE-SERVER-001. The kit is pointed at the published directory
/// (<c>/fhir/r4/directory/interop</c>), not at the authenticated FHIR base,
/// and is given no token: the directory is public by regulation, and a run
/// with credentials would not prove that it answers an anonymous client.
///
/// The kit's IG version
/// --------------------
/// The pinned kit is generated from Plan-Net 1.1.0 and CHO serves 1.2.0. The
/// canonicals are unversioned and identical in both, so the kit matches CHO's
/// resources; a finding that turns on a rule 1.2.0 changed is a kit-version
/// artifact. That is recorded as <c>knownMismatches</c> on
/// <c>inferno-plan-net</c> in interop/versions.json so the next pin bump
/// re-checks it rather than rediscovering it.
///
/// Which groups
/// ------------
/// All of them. CHO claims every Plan-Net resource type, so there is no group
/// whose omission would be an honest narrowing of the claim.
/// </summary>
[Collection(InteropCollection.Name)]
[Trait("Category", "DaVinciInterop")]
[Trait("Scenario", "INFERNO-PLANNET-SERVER-001")]
[Trait("Target", "inferno-framework/davinci-plan-net-test-kit")]
public sealed class InfernoPlanNetServerTests
{
    private const string ScenarioId = "INFERNO-PLANNET-SERVER-001";

    [InteropFact]
    public async Task Inferno_judges_chos_public_provider_directory()
    {
        var outcome = await InfernoSuiteScenario.RunAsync(ScenarioId);

        outcome.Runs.Should().NotBeEmpty(
            "the scenario runs the whole suite, so a run that executed nothing has proven nothing");

        outcome.Result.ParsedStatus.Should().Be(
            InteropStatus.Passed,
            "Inferno reported: {0}",
            string.Join("; ", outcome.Result.Findings.Select(f => $"[{f.Severity}] {f.Summary}")));
    }
}
