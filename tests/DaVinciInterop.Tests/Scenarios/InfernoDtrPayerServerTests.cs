using FluentAssertions;

namespace DaVinciInterop.Tests.Scenarios;

/// <summary>
/// INFERNO-DTR-PAYER-001 — the Inferno DTR 2.2.0 payer-server suite judging
/// CHO as the payer a provider's DTR client (an EHR with native DTR, or a DTR
/// app) gets its documentation templates from.
///
/// Direction
/// ---------
/// CHO is the system under test and the kit is the DTR client. The kit
/// obtains its own token with SMART Backend Services as a client the harness
/// registers for the seeded provider (DTR spec-120), so the token path is
/// CHO's real one.
///
/// What it covers
/// --------------
/// Discovery of the DTR operations; Backend Services; $questionnaire-package
/// by canonical, by CRD context id and by order; an adaptive form completed
/// through $next-question; ValueSet/$expand; package validation and must
/// support; the error cases; and $log-questionnaire-errors. See the scenario's
/// description in interop/scenarios.json and DtrPayerServerRequests for the
/// requests.
/// </summary>
[Collection(InteropCollection.Name)]
[Trait("Category", "DaVinciInterop")]
[Trait("Scenario", "INFERNO-DTR-PAYER-001")]
[Trait("Target", "inferno-framework/davinci-dtr-test-kit")]
public sealed class InfernoDtrPayerServerTests
{
    private const string ScenarioId = "INFERNO-DTR-PAYER-001";

    [InteropFact]
    public async Task Inferno_judges_chos_dtr_payer_service()
    {
        var outcome = await InfernoSuiteScenario.RunAsync(ScenarioId);

        outcome.Runs.Should().NotBeEmpty(
            "the scenario runs the suite's four top-level groups, so a run that executed nothing has proven nothing");

        outcome.Result.ParsedStatus.Should().Be(
            InteropStatus.Passed,
            "Inferno reported: {0}",
            string.Join("; ", outcome.Result.Findings.Select(f => $"[{f.Severity}] {f.Summary}")));
    }
}
