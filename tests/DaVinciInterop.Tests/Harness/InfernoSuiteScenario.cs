using FluentAssertions;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// Runs one Inferno conformance kit against CHO and turns what it reported into
/// interop evidence.
///
/// <para>
/// The direction is the opposite of the br-payer scenarios. There, CHO drives
/// an independent payer and the harness judges the answers. Here CHO is the
/// system under test: the kit drives CHO and the verdict is the kit's, not the
/// harness's. The harness supplies the endpoint, a token and the seeded data,
/// then reports what came back — it never reinterprets a <c>fail</c>, and it
/// never turns a suite that skipped everything into a pass.
/// </para>
///
/// <para>
/// Every kit scenario shares this because they differ only in data: which suite,
/// which groups, which inputs. That is inventory, and it lives in
/// interop/scenarios.json. Two copies of this orchestration would be two places
/// for the Inferno API contract to rot.
/// </para>
/// </summary>
public static class InfernoSuiteScenario
{
    /// <summary>What the kit reported, after it ran.</summary>
    public sealed record Outcome(InteropResult Result, IReadOnlyList<InfernoTestRun> Runs);

    /// <summary>
    /// Starts the kit and the <c>interop-cho</c> profile, seeds CHO, runs the
    /// suite or the groups the scenario names, and writes evidence. Evidence is
    /// written on the failure path too, so a run that fell over still says how
    /// far it got and against which pinned kit.
    /// </summary>
    public static async Task<Outcome> RunAsync(string scenarioId, CancellationToken cancellationToken = default)
    {
        if (!await DockerCompose.IsDockerAvailableAsync())
        {
            throw new InteropEnvironmentException(
                "No Docker daemon is reachable. Inferno conformance kits run as containers alongside CHO's " +
                "own services; start Docker, or unset CHO_INTEROP_ENABLED to skip them.");
        }

        var versions = InteropVersions.Load();
        var inventory = InteropScenarioInventory.Load();
        var scenario = inventory.Scenario(scenarioId);
        var target = versions.Target(scenario.ExternalTarget);
        var kit = target.Inferno
                  ?? throw new InteropEnvironmentException(
                      $"'{target.Key}' declares no inferno block in interop/versions.json, so there is no API " +
                      "base URL to drive it through.");

        kit.Suites.Should().Contain(scenario.RequiredInfernoSuite,
            "interop/scenarios.json names a suite the pinned kit does not ship; suites at this pin: [{0}]",
            string.Join(", ", kit.Suites));

        var run = new InteropScenarioRun(scenario, target);
        var writer = new InteropEvidenceWriter();
        var runs = new List<InfernoTestRun>();

        await using var environment = InteropEnvironment.For(
            [scenario.ExternalTarget], [ChoUnderTest.Profile]);
        using var client = new InteropHttpClient(target.Name, InteropSettings.Timeouts.ProtocolCall);

        InteropResult result;

        try
        {
            // buildImages: the kits publish no upstream image and CHO's services
            // are built from this repository, so this profile pair is the one
            // case where the harness builds rather than pulls a pin.
            await environment.StartAsync(ReadinessStage.HttpReachable, buildImages: true, cancellationToken);

            await SeedAsync(run, cancellationToken);
            await PreflightAsync(run, client, scenario.ChoSurface, cancellationToken);

            var runner = new InfernoRunner(kit.ApiBaseUrl);
            var fixture = ChoSeedFixture.Load();
            var inputs = BuildInputs(scenario, fixture);

            var suiteId = scenario.RequiredInfernoSuite;
            var sessionId = await runner.CreateSessionAsync(suiteId, inputs, cancellationToken);

            if (scenario.InfernoGroups.Count == 0)
            {
                runs.Add(await ExecuteAsync(
                    runner, () => runner.StartSuiteRunAsync(sessionId, suiteId, inputs, cancellationToken),
                    cancellationToken));
            }
            else
            {
                var suite = await runner.DescribeSuiteAsync(suiteId, cancellationToken);

                foreach (var declaredGroupId in scenario.InfernoGroups)
                {
                    var groupId = InfernoRunner.ResolveGroupId(suite, declaredGroupId);

                    run.Record(InteropFinding.Info(
                        $"inferno.group.resolved.{declaredGroupId}",
                        $"'{declaredGroupId}' resolved to runnable group '{groupId}', read from the kit's own " +
                        "suite description rather than composed from a naming rule."));

                    runs.Add(await ExecuteAsync(
                        runner, () => runner.StartGroupRunAsync(sessionId, groupId, inputs, cancellationToken),
                        cancellationToken));
                }
            }

            RecordResults(run, runs);

            run.RecordCompatibility(
                scenario.Protocol,
                cho: ChoDeclaredVersion(scenario.Protocol),
                external: target.EvidenceVersion,
                note: "The external version is the pinned conformance kit, not an IG version. The IG each " +
                      "suite was generated from is recorded in interop/versions.json.");

            result = run.Complete(
                RollUp(runs),
                client.Interactions,
                externalRole: "conformance-runner");
        }
        catch (Exception ex)
        {
            await environment.CaptureLogsAsync(CancellationToken.None);
            result = run.Complete(
                InteropStatus.Failed, client.Interactions,
                $"{ex.GetType().Name}: {ex.Message}",
                externalRole: "conformance-runner");
            WriteEvidence(writer, versions, inventory, result, client, environment);
            throw;
        }

        WriteEvidence(writer, versions, inventory, result, client, environment);
        return new Outcome(result, runs);
    }

    /// <summary>
    /// Seeds CHO before a single suite request is made. A kit pointed at an
    /// empty server reports a wall of failures that say nothing about CHO's
    /// conformance, so an unseeded run is not permitted to happen at all.
    /// </summary>
    private static async Task SeedAsync(InteropScenarioRun run, CancellationToken cancellationToken)
    {
        var output = await ChoSeedRunner.SeedAsync(
            ChoUnderTest.SeedServiceUrls, InteropSettings.Timeouts.Scenario, cancellationToken);

        run.Record(InteropFinding.Info(
            "inferno.seed.applied",
            "The interop-cho profile was seeded and verified through CHO's own APIs before the suite ran: "
            + output.Replace(Environment.NewLine, " | ")));
    }

    /// <summary>
    /// Reads CHO's own CapabilityStatement from the host before handing the kit
    /// the container address.
    ///
    /// <para>
    /// This is not a duplicate of the kit's capability test. It is the check
    /// that distinguishes "CHO answered and the kit judged it" from "the kit
    /// could not reach CHO" — two outcomes Inferno reports identically, as a
    /// failed test, and only one of which is a conformance result.
    /// </para>
    /// </summary>
    private static async Task PreflightAsync(
        InteropScenarioRun run, InteropHttpClient client, string surface, CancellationToken cancellationToken)
    {
        var baseUrl = ChoUnderTest.HostBaseUrl(surface);
        var response = await client.GetFhirAsync($"{baseUrl}/metadata", cancellationToken);

        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            throw new InteropEnvironmentException(
                $"CHO's own FHIR surface answered HTTP {(int)response.StatusCode} at " +
                $"{baseUrl}/metadata, so the kit was never started. A conformance " +
                "suite run against an endpoint that is not serving reports failures about the harness, " +
                "not about CHO.");
        }

        var capability = response.As<Hl7.Fhir.Model.CapabilityStatement>();
        run.Record(InteropFinding.Info(
            "inferno.preflight.capabilityStatement",
            $"CHO served a CapabilityStatement before the run: FHIR {capability?.FhirVersionElement?.ToString() ?? "unstated"}, " +
            $"{capability?.Rest.SelectMany(rest => rest.Resource).Count() ?? 0} resource types advertised."));
    }

    /// <summary>
    /// Builds the suite inputs the scenario declares, by the names the suite
    /// declares them under.
    /// </summary>
    public static List<InfernoInput> BuildInputs(
        InteropScenarioDefinition scenario, ChoSeedFixture fixture)
    {
        var token = fixture.DevelopmentToken();

        return scenario.InfernoInputs.Select(name => name switch
        {
            "url" => new InfernoInput { Name = "url", Value = ChoUnderTest.ContainerBaseUrl(scenario.ChoSurface) },
            "patient_ids" => new InfernoInput { Name = "patient_ids", Value = fixture.Member.MemberId },
            "smart_auth_info" => InfernoInput.AccessToken("smart_auth_info", token),
            _ => throw new InteropEnvironmentException(
                $"Scenario '{scenario.Id}' declares an input '{name}' the harness does not know how to " +
                "supply. Add it here deliberately: an input silently left unset makes the suite test " +
                "something other than what the scenario claims."),
        }).ToList();
    }

    private static async Task<InfernoTestRun> ExecuteAsync(
        InfernoRunner runner,
        Func<Task<InfernoTestRun>> start,
        CancellationToken cancellationToken)
    {
        var started = await start();
        return await runner.WaitForCompletionAsync(
            started.Id,
            InteropSettings.Timeouts.InfernoRun,
            InteropSettings.Timeouts.InfernoPoll,
            cancellationToken);
    }

    /// <summary>
    /// Records every non-passing Inferno result as a finding, so the evidence
    /// names what failed rather than only that something did. A failure is an
    /// Error — it is the kit's verdict on CHO, and the harness has no standing
    /// to downgrade it.
    /// </summary>
    private static void RecordResults(InteropScenarioRun run, IReadOnlyList<InfernoTestRun> runs)
    {
        foreach (var result in runs.SelectMany(r => r.Results))
        {
            var status = InfernoRunner.MapStatus(result.Result);
            if (status == InteropStatus.Passed)
            {
                continue;
            }

            var where = result.TestId ?? result.TestGroupId ?? result.TestSuiteId ?? result.Id;
            var summary = $"Inferno reported '{result.Result}' for {where}: " +
                          (result.ResultMessage ?? "(no message)");

            run.Record(status == InteropStatus.Failed
                ? InteropFinding.Error("inferno.result.failed", Redaction.Body(summary))
                : InteropFinding.Info("inferno.result.notRun", Redaction.Body(summary)));
        }
    }

    /// <summary>
    /// Rolls every group run up into one scenario status. Any failing group
    /// fails the scenario, and a set of runs that produced no pass at all is
    /// never reported as one.
    /// </summary>
    private static InteropStatus RollUp(IReadOnlyList<InfernoTestRun> runs)
    {
        if (runs.Count == 0)
        {
            return InteropStatus.NotRun;
        }

        var statuses = runs.Select(InfernoRunner.RollUp).ToList();

        if (statuses.Any(s => s == InteropStatus.Failed))
        {
            return InteropStatus.Failed;
        }

        return statuses.All(s => s == InteropStatus.Passed) ? InteropStatus.Passed : InteropStatus.Skipped;
    }

    /// <summary>
    /// The IG version CHO claims for a protocol, for the compatibility row. Null
    /// where CHO states no single version — recorded as unknown rather than
    /// backfilled with a guess.
    /// </summary>
    private static string? ChoDeclaredVersion(string protocol) => protocol switch
    {
        "US Core" => "3.1.1",
        "CARIN BB" => "2.2.0",
        "Plan-Net" => "1.2.0",
        _ => null,
    };

    private static void WriteEvidence(
        InteropEvidenceWriter writer,
        InteropVersions versions,
        InteropScenarioInventory inventory,
        InteropResult result,
        InteropHttpClient client,
        InteropEnvironment environment)
    {
        var merged = writer.MergeWithPrevious([result]);
        var run = InteropEvidenceWriter.BuildRun(versions, inventory, merged);
        writer.Write(run, client.CapturedBodies, environment.ServiceLogs);
    }
}
