using System.Text.Json;
using FluentAssertions;

namespace DaVinciInterop.Tests.Unit;

/// <summary>
/// Evidence rules the harness must not break: unexecuted scenarios are NotRun,
/// the CMS vocabulary never appears, and nothing credential-shaped lands on disk.
/// </summary>
[Trait("Category", "DaVinciInteropUnit")]
public sealed class InteropEvidenceWriterTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "cho-interop-evidence-" + Guid.NewGuid().ToString("N"));

    private static readonly InteropVersions Versions = InteropVersions.Load();
    private static readonly InteropScenarioInventory Inventory = InteropScenarioInventory.Load();

    [Fact]
    public void Scenarios_that_did_not_run_are_reported_NotRun_rather_than_omitted()
    {
        var run = InteropEvidenceWriter.BuildRun(Versions, Inventory, [PassedResult("BR-PAS-SUBMIT-001")]);

        run.NotRunScenarios.Select(r => r.ScenarioId)
            .Should().BeEquivalentTo(Inventory.Scenarios
                .Where(s => s.Id != "BR-PAS-SUBMIT-001")
                .Select(s => s.Id));
        run.NotRunScenarios.Should().OnlyContain(r => r.ParsedStatus == InteropStatus.NotRun);
        run.Summary.Passed.Should().Be(1);
        run.Summary.Failed.Should().Be(0);
        run.Summary.Total.Should().Be(Inventory.Scenarios.Count);
    }

    [Fact]
    public void The_evidence_never_borrows_the_cms_acceptance_vocabulary()
    {
        var run = InteropEvidenceWriter.BuildRun(Versions, Inventory, [PassedResult("BR-PAS-SUBMIT-001")]);

        var json = JsonSerializer.Serialize(run);

        json.Should().NotContain("PASSABLE");
        json.Should().NotContain("PARTIAL");
        json.Should().NotContain("\"GAP\"");
        run.EvidenceKind.Should().Be("davinci-interoperability");
        run.RelationshipToCmsAcceptance.Should().Contain("never change a CMS-0057-F scenario status");
    }

    [Fact]
    public void Each_target_carries_its_exact_pin_into_the_evidence()
    {
        var run = InteropEvidenceWriter.BuildRun(Versions, Inventory, [PassedResult("BR-PAS-SUBMIT-001")]);

        var payer = run.Targets.Single(t => t.Key == "br-payer");
        payer.Version.Should().Be(Versions.Target("br-payer").Pin.Digest);
        payer.PinReference.Should().Contain("@sha256:");
        payer.License.Should().Be("Apache-2.0");
        payer.UpstreamRepository.Should().Be("https://github.com/HL7-DaVinci/br-payer");
    }

    [Fact]
    public void Captured_bodies_are_redacted_on_write()
    {
        var writer = new InteropEvidenceWriter(_tempRoot);
        var run = InteropEvidenceWriter.BuildRun(Versions, Inventory, [PassedResult("BR-PAS-SUBMIT-001")]);

        writer.Write(
            run,
            capturedBodies: new Dictionary<string, string>
            {
                ["responses/001-200.json"] = """{"access_token":"leaked-token-value"}""",
            },
            serviceLogs: new Dictionary<string, string>
            {
                ["br-payer"] = "startup ok; Authorization: Bearer leaked-header-value",
            });

        var responseBody = File.ReadAllText(Path.Combine(_tempRoot, "responses", "001-200.json"));
        responseBody.Should().NotContain("leaked-token-value");

        var log = File.ReadAllText(Path.Combine(_tempRoot, "service-logs", "br-payer.log"));
        log.Should().NotContain("leaked-header-value");
    }

    [Fact]
    public void The_package_contains_run_json_and_junit_xml()
    {
        var writer = new InteropEvidenceWriter(_tempRoot);
        var run = InteropEvidenceWriter.BuildRun(Versions, Inventory, [PassedResult("BR-PAS-SUBMIT-001")]);

        var runPath = writer.Write(run);

        File.Exists(runPath).Should().BeTrue();
        var junit = File.ReadAllText(Path.Combine(_tempRoot, "junit.xml"));
        junit.Should().Contain("BR-PAS-SUBMIT-001");
        junit.Should().Contain("<skipped");
    }

    [Fact]
    public void Artifacts_are_written_without_a_byte_order_mark()
    {
        var writer = new InteropEvidenceWriter(_tempRoot);
        var run = InteropEvidenceWriter.BuildRun(Versions, Inventory, [PassedResult("BR-PAS-SUBMIT-001")]);

        var runPath = writer.Write(run);

        // A BOM makes run.json unreadable to jq and to Python's json module, which
        // is exactly how CI and the publication pipeline consume it.
        File.ReadAllBytes(runPath).Take(3).Should().NotBeEquivalentTo(new byte[] { 0xEF, 0xBB, 0xBF });
    }

    [Fact]
    public void A_failed_scenario_renders_its_interactions_into_the_junit_failure()
    {
        var failed = PassedResult("BR-PAS-SUBMIT-001") with
        {
            Status = nameof(InteropStatus.Failed),
            StatusReason = "ClaimResponse was missing a review action",
            Interactions =
            [
                new InteropInteraction
                {
                    Sequence = 1,
                    Method = "POST",
                    Url = "http://127.0.0.1:18081/fhir/Claim/$submit",
                    StatusCode = 400,
                    ResponseResourceType = "OperationOutcome",
                    OperationOutcomeIssues = ["error/invalid: Claim.item.category is required for item 1"],
                },
            ],
        };

        var junit = InteropEvidenceWriter.BuildJUnit(
            InteropEvidenceWriter.BuildRun(Versions, Inventory, [failed]));

        junit.Should().Contain("ClaimResponse was missing a review action");
        junit.Should().Contain("Claim.item.category is required");
        junit.Should().Contain("HTTP 400");
    }

    private static InteropResult PassedResult(string scenarioId)
    {
        var scenario = Inventory.Scenario(scenarioId);
        var target = Versions.Target(scenario.ExternalTarget);
        return new InteropScenarioRun(scenario, target)
            .Complete(InteropStatus.Passed, Array.Empty<InteropInteraction>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
