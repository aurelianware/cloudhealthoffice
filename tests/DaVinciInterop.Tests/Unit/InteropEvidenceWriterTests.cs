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

    [Fact]
    public void A_captured_body_is_capped_by_encoded_bytes_not_characters()
    {
        var writer = new InteropEvidenceWriter(_tempRoot);
        var run = InteropEvidenceWriter.BuildRun(Versions, Inventory, [PassedResult("BR-PAS-SUBMIT-001")]);

        // Three bytes per character in UTF-8. A character-based cap would let the
        // file reach roughly three times the limit it claims to enforce.
        var oversized = new string('\u4e2d', 1_500_000);

        writer.Write(run, capturedBodies: new Dictionary<string, string>
        {
            ["responses/001-200.json"] = oversized,
        });

        var written = new FileInfo(Path.Combine(_tempRoot, "responses", "001-200.json"));
        written.Length.Should().BeLessThan(3 * 1024 * 1024,
            "the cap is on bytes on disk, so a multi-byte body must not blow past it");
        File.ReadAllText(written.FullName).Should().Contain("truncated at");
    }

    [Fact]
    public void Truncation_never_splits_a_character()
    {
        var writer = new InteropEvidenceWriter(_tempRoot);
        var run = InteropEvidenceWriter.BuildRun(Versions, Inventory, [PassedResult("BR-PAS-SUBMIT-001")]);

        // An emoji is a surrogate pair in UTF-16 and four bytes in UTF-8: cutting
        // mid-character would leave a replacement character in the artifact.
        var oversized = string.Concat(Enumerable.Repeat("\U0001F9EA", 700_000));

        writer.Write(run, capturedBodies: new Dictionary<string, string>
        {
            ["responses/001-200.json"] = oversized,
        });

        var written = File.ReadAllText(Path.Combine(_tempRoot, "responses", "001-200.json"));
        written.Should().NotContain("\uFFFD", "no character may be cut in half by the cap");
    }

    [Fact]
    public void A_body_within_the_cap_is_written_untouched()
    {
        var writer = new InteropEvidenceWriter(_tempRoot);
        var run = InteropEvidenceWriter.BuildRun(Versions, Inventory, [PassedResult("BR-PAS-SUBMIT-001")]);
        const string body = """{"resourceType":"Bundle","type":"collection"}""";

        writer.Write(run, capturedBodies: new Dictionary<string, string>
        {
            ["responses/001-200.json"] = body,
        });

        File.ReadAllText(Path.Combine(_tempRoot, "responses", "001-200.json"))
            .Should().Be(body, "an ordinary response must reach the artifact verbatim");
    }

    [Fact]
    public void A_second_scenario_does_not_erase_the_first_ones_result()
    {
        var writer = new InteropEvidenceWriter(_tempRoot);

        // First scenario writes.
        writer.Write(InteropEvidenceWriter.BuildRun(
            Versions, Inventory, [PassedResult("BR-PAS-SUBMIT-001")]));

        // Second scenario merges rather than clobbering — without this the run
        // document would claim the first scenario never ran.
        var merged = writer.MergeWithPrevious([PassedResult("BR-CRD-001")]);
        writer.Write(InteropEvidenceWriter.BuildRun(Versions, Inventory, merged));

        var run = JsonSerializer.Deserialize<InteropEvidenceRun>(
            File.ReadAllText(Path.Combine(_tempRoot, "run.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        run.Targets.SelectMany(t => t.Results).Select(r => r.ScenarioId)
            .Should().BeEquivalentTo(["BR-PAS-SUBMIT-001", "BR-CRD-001"]);
        run.Summary.Passed.Should().Be(2);
        run.Summary.NotRun.Should().Be(Inventory.Scenarios.Count - 2);
    }

    [Fact]
    public void Re_running_a_scenario_replaces_its_result_rather_than_duplicating_it()
    {
        var writer = new InteropEvidenceWriter(_tempRoot);
        writer.Write(InteropEvidenceWriter.BuildRun(Versions, Inventory, [PassedResult("BR-CRD-001")]));

        var rerun = PassedResult("BR-CRD-001") with
        {
            Status = nameof(InteropStatus.Failed),
            StatusReason = "second attempt failed",
        };
        var merged = writer.MergeWithPrevious([rerun]);

        merged.Should().ContainSingle(r => r.ScenarioId == "BR-CRD-001")
            .Which.ParsedStatus.Should().Be(InteropStatus.Failed, "newest wins");
    }

    [Fact]
    public void NotRun_rows_are_regenerated_rather_than_carried_forward_as_results()
    {
        var writer = new InteropEvidenceWriter(_tempRoot);
        writer.Write(InteropEvidenceWriter.BuildRun(Versions, Inventory, [PassedResult("BR-CRD-001")]));

        // The first write recorded five NotRun rows. They must not come back as
        // "previously recorded results" and be mistaken for executed scenarios.
        writer.PreviouslyRecordedResults().Should().ContainSingle()
            .Which.ScenarioId.Should().Be("BR-CRD-001");
    }

    [Fact]
    public void Merging_against_no_previous_run_returns_just_the_new_results()
    {
        new InteropEvidenceWriter(_tempRoot).MergeWithPrevious([PassedResult("BR-CRD-001")])
            .Should().ContainSingle().Which.ScenarioId.Should().Be("BR-CRD-001");
    }

    [Fact]
    public void A_corrupt_previous_run_document_never_loses_the_result_being_recorded()
    {
        Directory.CreateDirectory(_tempRoot);
        File.WriteAllText(Path.Combine(_tempRoot, "run.json"), "{ not valid json");

        var writer = new InteropEvidenceWriter(_tempRoot);

        writer.MergeWithPrevious([PassedResult("BR-CRD-001")])
            .Should().ContainSingle().Which.ScenarioId.Should().Be("BR-CRD-001");
    }

    [Fact]
    public void A_crd_result_carries_the_direction_of_the_exchange()
    {
        var scenario = Inventory.Scenario("BR-CRD-001");
        var result = new InteropScenarioRun(scenario, Versions.Target(scenario.ExternalTarget))
            .Complete(InteropStatus.Passed, Array.Empty<InteropInteraction>(), externalRole: "payer-server");

        result.ChoRole.Should().Be("Client");
        result.ExternalRole.Should().Be("payer-server");
        result.Protocol.Should().Be("CRD");
    }

    [Fact]
    public void Cds_hooks_interactions_are_labelled_by_kind_and_hook()
    {
        var result = PassedResult("BR-CRD-001") with
        {
            Interactions =
            [
                new InteropInteraction { Sequence = 1, Kind = "cds-hooks-discovery", Method = "GET", StatusCode = 200 },
                new InteropInteraction
                {
                    Sequence = 2, Kind = "cds-hooks-invoke", Hook = "order-sign",
                    ServiceId = "order-sign-crd", Method = "POST", StatusCode = 200,
                },
            ],
        };

        var json = JsonSerializer.Serialize(
            InteropEvidenceWriter.BuildRun(Versions, Inventory, [result]));

        json.Should().Contain("cds-hooks-discovery").And.Contain("cds-hooks-invoke").And.Contain("order-sign");
    }

    [Fact]
    public void A_chained_scenario_records_what_it_consumed_and_where_it_came_from()
    {
        var scenario = Inventory.Scenario("BR-DTR-001");
        const string canonical = "http://example.org/fhir/Questionnaire/PriorAuthRequired";

        var result = new InteropScenarioRun(scenario, Versions.Target(scenario.ExternalTarget))
            .Complete(
                InteropStatus.Passed, Array.Empty<InteropInteraction>(),
                externalRole: "payer-server",
                linkedFromScenario: "BR-CRD-001",
                linkedArtifact: canonical);

        // Evidence should show a workflow — CRD determined, DTR followed — rather
        // than two unrelated green rows.
        result.LinkedFromScenario.Should().Be("BR-CRD-001");
        result.LinkedArtifact.Should().Be(canonical);
        result.Protocol.Should().Be("DTR");

        JsonSerializer.Serialize(InteropEvidenceWriter.BuildRun(Versions, Inventory, [result]))
            .Should().Contain("linkedFromScenario").And.Contain(canonical);
    }

    /// <summary>
    /// `run.sh br-payer all` runs every external scenario in one invocation, each
    /// writing the run document in turn. Merging is what makes the last write a
    /// record of the whole run rather than of whichever scenario finished last —
    /// so adding a fourth scenario must not cost the first three their rows.
    /// </summary>
    [Fact]
    public void All_four_br_payer_scenarios_merge_into_one_run_document()
    {
        var writer = new InteropEvidenceWriter(_tempRoot);
        string[] executed = ["BR-PAS-SUBMIT-001", "BR-CRD-001", "BR-DTR-001", "BR-PAS-INQUIRE-001"];

        foreach (var id in executed)
        {
            var merged = writer.MergeWithPrevious([PassedResult(id)]);
            writer.Write(InteropEvidenceWriter.BuildRun(Versions, Inventory, merged));
        }

        var run = JsonSerializer.Deserialize<InteropEvidenceRun>(
            File.ReadAllText(Path.Combine(_tempRoot, "run.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        run.Summary.Passed.Should().Be(4);
        run.Summary.NotRun.Should().Be(Inventory.Scenarios.Count - 4);
        run.Targets.SelectMany(target => target.Results).Select(result => result.ScenarioId)
            .Should().BeEquivalentTo(executed);
        run.NotRunScenarios.Select(r => r.ScenarioId).Should().NotContain(executed);

        // The Inferno rows are placeholders and must stay NotRun no matter how
        // many br-payer scenarios pass.
        run.NotRunScenarios.Select(r => r.ScenarioId).Should().BeEquivalentTo(
            ["INFERNO-DTR-PAYER-001", "INFERNO-PDEX-SERVER-001", "INFERNO-PDEX-CLIENT-001"]);
    }

    /// <summary>
    /// Running the inquiry scenario after the others must leave their evidence
    /// intact — the failure this guards is a fourth scenario silently truncating
    /// the run document to its own row.
    /// </summary>
    [Fact]
    public void The_inquiry_scenario_does_not_overwrite_earlier_scenarios_evidence()
    {
        var writer = new InteropEvidenceWriter(_tempRoot);

        writer.Write(InteropEvidenceWriter.BuildRun(Versions, Inventory,
        [
            PassedResult("BR-PAS-SUBMIT-001"),
            PassedResult("BR-CRD-001"),
            PassedResult("BR-DTR-001"),
        ]));

        var merged = writer.MergeWithPrevious([PassedResult("BR-PAS-INQUIRE-001")]);

        merged.Select(result => result.ScenarioId).Should().BeEquivalentTo(
            ["BR-PAS-SUBMIT-001", "BR-CRD-001", "BR-DTR-001", "BR-PAS-INQUIRE-001"]);
    }

    /// <summary>
    /// The PAS chain must read as a workflow in the evidence: submit produced an
    /// authorization, inquire consumed that same authorization. The linked
    /// artifact is the payer-issued identity, which is synthetic and carries no
    /// member or clinical content.
    /// </summary>
    [Fact]
    public void A_pas_inquiry_result_records_the_authorization_it_chained_from()
    {
        const string authorizationNumber = "AUTH-0001";

        var scenario = Inventory.Scenario("BR-PAS-INQUIRE-001");
        var result = new InteropScenarioRun(scenario, Versions.Target(scenario.ExternalTarget))
            .Complete(
                InteropStatus.Passed,
                Array.Empty<InteropInteraction>(),
                externalRole: "payer-server",
                linkedFromScenario: "BR-PAS-SUBMIT-001",
                linkedArtifact: authorizationNumber);

        result.LinkedFromScenario.Should().Be("BR-PAS-SUBMIT-001");
        result.LinkedArtifact.Should().Be(authorizationNumber);
        result.Protocol.Should().Be("PAS");
        result.ChoRole.Should().Be("Client");
        result.ExternalRole.Should().Be("payer-server");

        var json = JsonSerializer.Serialize(
            InteropEvidenceWriter.BuildRun(Versions, Inventory, [result]));

        json.Should().Contain("linkedFromScenario").And.Contain(authorizationNumber);
        json.Should().NotContain(SyntheticInteropData.MemberId,
            "the chain is expressed through the payer-issued identity, not through member data");
    }

    /// <summary>
    /// The inventory declares the chain too, so a reader can see the workflow
    /// without running anything.
    /// </summary>
    [Fact]
    public void The_inventory_declares_the_pas_submit_to_inquire_chain()
    {
        Inventory.Scenario("BR-PAS-INQUIRE-001").LinkedFromScenario.Should().Be("BR-PAS-SUBMIT-001");
        Inventory.Scenario("BR-DTR-001").LinkedFromScenario.Should().Be("BR-CRD-001");
        Inventory.Scenario("BR-PAS-SUBMIT-001").LinkedFromScenario.Should().BeNull(
            "the submit scenario enters from nothing");
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
