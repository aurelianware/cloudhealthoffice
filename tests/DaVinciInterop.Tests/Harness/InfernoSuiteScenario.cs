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

            if (scenario.InfernoAuth == InfernoAuthModes.BackendServices)
            {
                run.Record(InteropFinding.Info(
                    "inferno.auth.backendClient",
                    await InfernoBackendClient.RegisterAsync(
                        kit.ApiBaseUrl, scenario.RequiredInfernoSuite, fixture, cancellationToken)));
            }

            string? patientToken = null;
            if (scenario.InfernoAuth == InfernoAuthModes.PatientApp)
            {
                var (accessToken, patient) = await InfernoPatientApp.ObtainAsync(fixture, cancellationToken);
                patientToken = accessToken;
                run.Record(InteropFinding.Info(
                    "inferno.auth.patientApp",
                    $"patient app '{fixture.PatientApp.ClientId}' authorised by the member through a SMART " +
                    $"patient launch; smart-auth-service bound the token to Patient/{patient}"));
            }

            var inputs = BuildInputs(scenario, fixture, patientToken);

            var suiteId = scenario.RequiredInfernoSuite;
            var sessionId = await runner.CreateSessionAsync(suiteId, cancellationToken);

            var suite = await runner.DescribeSuiteAsync(suiteId, cancellationToken);

            if (scenario.InfernoGroups.Count == 0)
            {
                var suiteInputs = WithKitDefaults(run, suiteId, inputs, suite.Inputs);
                runs.Add(await ExecuteAsync(
                    runner, () => runner.StartSuiteRunAsync(sessionId, suiteId, suiteInputs, cancellationToken),
                    cancellationToken));
            }
            else
            {
                foreach (var declaredGroupId in scenario.InfernoGroups)
                {
                    var groupId = InfernoRunner.ResolveGroupId(suite, declaredGroupId);

                    run.Record(InteropFinding.Info(
                        $"inferno.group.resolved.{declaredGroupId}",
                        $"'{declaredGroupId}' resolved to runnable group '{groupId}', read from the kit's own " +
                        "suite description rather than composed from a naming rule."));

                    var group = suite.AllGroups().Single(g => g.Id == groupId);
                    var groupInputs = WithKitDefaults(run, groupId, inputs, group.Inputs);
                    if (runs.Count > 0)
                    {
                        groupInputs = await WithSessionAuthAsync(run, runner, sessionId, groupId, groupInputs,
                            cancellationToken);
                    }

                    runs.Add(await ExecuteAsync(
                        runner, () => runner.StartGroupRunAsync(sessionId, groupId, groupInputs, cancellationToken),
                        cancellationToken));
                }
            }

            await RecordResultsAsync(run, runner, runs, scenario.InfernoKnownKitIssues, cancellationToken);

            run.RecordCompatibility(
                scenario.Protocol,
                cho: ChoDeclaredVersion(scenario.Protocol),
                external: target.EvidenceVersion,
                note: "The external version is the pinned conformance kit, not an IG version. The IG each " +
                      "suite was generated from is recorded in interop/versions.json.");

            result = run.Complete(
                RollUp(runs, scenario.InfernoKnownKitIssues),
                client.Interactions,
                externalRole: "conformance-runner") with
            {
                InfernoTests = InfernoTestCounts.From(runs, scenario.InfernoKnownKitIssues),
            };
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

        // A kit's failure is often explained only in CHO's own logs (a 500
        // whose cause the kit never sees), so a run that did not pass keeps
        // them, as a run that threw already does.
        if (result.ParsedStatus != InteropStatus.Passed)
        {
            await environment.CaptureLogsAsync(CancellationToken.None);
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
        InteropScenarioDefinition scenario, ChoSeedFixture fixture, string? patientToken = null)
    {
        var token = scenario.InfernoAuth == InfernoAuthModes.PatientApp
            ? patientToken ?? throw new InteropEnvironmentException(
                $"Scenario '{scenario.Id}' authenticates as the patient app, and no patient token was obtained.")
            : fixture.DevelopmentToken();

        return scenario.InfernoInputs.Select(name => name switch
        {
            "url" or "bulk_server_url" => new InfernoInput { Name = name, Value = ChoUnderTest.ContainerBaseUrl(scenario.ChoSurface) },
            "patient_ids" => new InfernoInput { Name = "patient_ids", Value = fixture.Member.MemberId },
            "smart_auth_info" => SmartAuthInfo(scenario, fixture, token),
            "group_id" => new InfernoInput { Name = "group_id", Value = fixture.BackendClient.ProviderId },
            "bulk_timeout" => new InfernoInput { Name = "bulk_timeout", Value = BulkTimeoutSeconds },
            "lines_to_validate" => new InfernoInput { Name = "lines_to_validate", Value = BulkLinesToValidate },
            "since_timestamp" => new InfernoInput { Name = "since_timestamp", Value = BulkSinceTimestamp },
            _ => throw new InteropEnvironmentException(
                $"Scenario '{scenario.Id}' declares an input '{name}' the harness does not know how to " +
                "supply. Add it here deliberately: an input silently left unset makes the suite test " +
                "something other than what the scenario claims."),
        }).ToList();
    }

    /// <summary>
    /// How long the Bulk Data kit polls an export before giving up, in seconds.
    /// The interop data set is a handful of records, so an export that takes
    /// longer than this is a defect rather than a slow server.
    /// </summary>
    public const string BulkTimeoutSeconds = "300";

    /// <summary>Lines of each NDJSON file the kit validates. Every line, at this data size.</summary>
    public const string BulkLinesToValidate = "1000";

    /// <summary>
    /// The <c>_since</c> the kit's parameter tests export with. Fixed, so a
    /// run is reproducible, and before every seeded record, so the filtered
    /// export is not empty for want of data.
    /// </summary>
    public const string BulkSinceTimestamp = "2026-01-01T00:00:00+00:00";

    /// <summary>
    /// The scenario's inputs plus the kit's defaults for any required input it
    /// leaves unset, recording which were defaulted so the evidence says so.
    /// </summary>
    private static List<InfernoInput> WithKitDefaults(
        InteropScenarioRun run, string runnableId, IReadOnlyList<InfernoInput> inputs,
        IEnumerable<InfernoDeclaredInput> declared)
    {
        var (withDefaults, defaulted) = InfernoRunner.WithDeclaredDefaults(inputs, declared);
        if (defaulted.Count > 0)
        {
            run.Record(InteropFinding.Info(
                $"inferno.inputs.defaulted.{runnableId}",
                $"'{runnableId}' ran with the kit's own defaults for {string.Join(", ", defaulted)}, as " +
                "Inferno's UI would pre-fill them."));
        }

        return withDefaults;
    }

    private static async Task<List<InfernoInput>> WithSessionAuthAsync(
        InteropScenarioRun run,
        InfernoRunner runner,
        string sessionId,
        string runnableId,
        List<InfernoInput> inputs,
        CancellationToken cancellationToken)
    {
        var session = await runner.ReadSessionDataAsync(sessionId, cancellationToken);
        var (carried, names) = InfernoRunner.WithSessionAuth(inputs, session);
        if (names.Count > 0)
        {
            run.Record(InteropFinding.Info(
                $"inferno.inputs.carried.{runnableId}",
                $"'{runnableId}' ran with the session's {string.Join(", ", names)}, as an earlier group left " +
                "it, so the credentials the kit obtained itself reach the groups that use them."));
        }

        return carried;
    }

    private static InfernoInput SmartAuthInfo(InteropScenarioDefinition scenario, ChoSeedFixture fixture, string token)
        => scenario.InfernoAuth switch
        {
            InfernoAuthModes.ChoToken or InfernoAuthModes.PatientApp => InfernoInput.AccessToken("smart_auth_info", token),
            InfernoAuthModes.BackendServices => InfernoInput.BackendServices(
                "smart_auth_info",
                ChoUnderTest.ContainerTokenUrl,
                fixture.BackendClient.ClientId,
                string.Join(' ', fixture.BackendClient.Scopes)),
            _ => throw new InteropEnvironmentException(
                $"Scenario '{scenario.Id}' declares infernoAuth '{scenario.InfernoAuth}', which the harness " +
                $"does not know. Known: {InfernoAuthModes.ChoToken}, {InfernoAuthModes.BackendServices}, " +
                $"{InfernoAuthModes.PatientApp}."),
        };

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
    ///
    /// <para>
    /// A failure carries its reasons, not only its headline: the error
    /// messages the test logged (a profile failure's headline only counts
    /// resources; the validator's issues are in its messages) and, for a
    /// request CHO refused, what CHO answered. Without them every CHO finding
    /// had to be reproduced by hand before it could be fixed.
    /// </para>
    /// </summary>
    private static async Task RecordResultsAsync(
        InteropScenarioRun run, InfernoRunner runner, IReadOnlyList<InfernoTestRun> runs,
        IReadOnlyList<InfernoKnownKitIssue> knownKitIssues, CancellationToken cancellationToken)
    {
        foreach (var (groupRun, result) in runs.SelectMany(r => r.Results.Select(result => (r, result))))
        {
            var status = InfernoRunner.MapStatus(result.Result);
            if (status == InteropStatus.Passed)
            {
                continue;
            }

            if (status == InteropStatus.Failed
                && knownKitIssues.FirstOrDefault(issue => issue.Matches(result.TestId)) is { } issue)
            {
                run.Record(InteropFinding.Info(
                    "inferno.result.knownKitIssue",
                    Redaction.Body($"Inferno reported '{result.Result}' for {result.TestId}, a known kit issue " +
                                   $"not counted against CHO: {issue.Reason}")));
                continue;
            }

            if (status == InteropStatus.Failed && result.TestId is null
                && InfernoRunner.RollUp(groupRun, knownKitIssues) != InteropStatus.Failed)
            {
                // A group or suite row failed by tests this run did not fail: an
                // excused test under it, or a test of a group run earlier in the
                // session, which that group's run reports.
                run.Record(InteropFinding.Info(
                    "inferno.result.failedByKnownKitIssue",
                    $"Inferno reported '{result.Result}' for {result.TestGroupId ?? result.TestSuiteId ?? result.Id}, " +
                    "only because of a known kit issue under it or a test reported with its own group."));
                continue;
            }

            var where = result.TestId ?? result.TestGroupId ?? result.TestSuiteId ?? result.Id;
            var summary = new System.Text.StringBuilder(
                $"Inferno reported '{result.Result}' for {where}: {result.ResultMessage ?? "(no message)"}");

            if (status == InteropStatus.Failed && result.TestId is not null)
            {
                foreach (var message in result.Messages
                             .Where(m => m.Type == "error")
                             .Select(m => m.Message)
                             .Distinct(StringComparer.Ordinal)
                             .Take(MaxMessagesPerResult))
                {
                    summary.Append(" | ").Append(Clip(message, 400));
                }

                foreach (var request in result.Requests
                             .Where(r => r.Status is >= 400)
                             .Take(MaxRefusedRequestsPerResult))
                {
                    var body = await runner.ReadResponseBodyAsync(request.Id, cancellationToken);
                    summary.Append($" | {request.Verb?.ToUpperInvariant()} {request.Url} -> {request.Status}");
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        summary.Append(": ").Append(Clip(body, 600));
                    }
                }
            }

            run.Record(status == InteropStatus.Failed
                ? InteropFinding.Error("inferno.result.failed", Redaction.Body(summary.ToString()))
                : InteropFinding.Info("inferno.result.notRun", Redaction.Body(summary.ToString())));
        }
    }

    private const int MaxMessagesPerResult = 8;
    private const int MaxRefusedRequestsPerResult = 2;

    private static string Clip(string text, int length)
    {
        var flat = text.ReplaceLineEndings(" ");
        return flat.Length <= length ? flat : flat[..length] + "…";
    }

    /// <summary>
    /// Rolls every group run up into one scenario status. Any failing group
    /// fails the scenario, and a set of runs that produced no pass at all is
    /// never reported as one.
    /// </summary>
    private static InteropStatus RollUp(IReadOnlyList<InfernoTestRun> runs, IReadOnlyList<InfernoKnownKitIssue> knownKitIssues)
    {
        if (runs.Count == 0)
        {
            return InteropStatus.NotRun;
        }

        var statuses = runs.Select(r => InfernoRunner.RollUp(r, knownKitIssues)).ToList();

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
        "Bulk Data" => "2.0.0",
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
