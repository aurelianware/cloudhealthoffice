using FluentAssertions;

namespace DaVinciInterop.Tests.Scenarios;

/// <summary>
/// INFERNO-BULK-SERVER-001 — the Inferno Bulk Data 2.0.0 suite judging CHO's
/// Group and Patient export, including what the exported files contain
/// (gap-matrix G-60).
///
/// Direction
/// ---------
/// CHO is the system under test and the kit is the judge, as in the other
/// Inferno scenarios. The difference is authentication: the kit is not handed
/// a token. It runs SMART Backend Services itself against
/// cho-smart-auth-service, as a backend client the harness registers with the
/// kit's own public keys and binds to the seeded provider. The token, the
/// provider binding, attribution and the export are all the real path.
///
/// Which groups
/// ------------
/// Group export and Patient export. Not the server TLS group or the SMART
/// Backend Services group, whose TLS checks cannot pass on the plain-HTTP
/// interop network (both passed against a TLS-serving local CHO), and not
/// system export, which CHO does not serve. See <c>knownMismatches</c> on
/// <c>inferno-bulk-data</c> in interop/versions.json.
/// </summary>
[Collection(InteropCollection.Name)]
[Trait("Category", "DaVinciInterop")]
[Trait("Scenario", "INFERNO-BULK-SERVER-001")]
[Trait("Target", "inferno-framework/bulk-data-test-kit")]
public sealed class InfernoBulkDataServerTests
{
    private const string ScenarioId = "INFERNO-BULK-SERVER-001";

    [InteropFact]
    public async Task Inferno_judges_chos_group_and_patient_export()
    {
        var outcome = await InfernoSuiteScenario.RunAsync(ScenarioId);

        outcome.Runs.Should().NotBeEmpty(
            "the scenario runs the Group and Patient export groups, so a run that executed nothing has proven nothing");

        outcome.Result.ParsedStatus.Should().Be(
            InteropStatus.Passed,
            "Inferno reported: {0}",
            string.Join("; ", outcome.Result.Findings.Select(f => $"[{f.Severity}] {f.Summary}")));
    }
}
