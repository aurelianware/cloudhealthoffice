using System.Net;
using FluentAssertions;

namespace DaVinciInterop.Tests.Scenarios;

/// <summary>
/// BR-CRD-001 — Coverage Requirements Discovery against the pinned HL7 Da Vinci
/// burden-reduction payer reference implementation.
///
/// Direction
/// ---------
/// CHO is the provider-side CRD client; br-payer is the payer CDS service. The
/// harness discovers the payer's CDS services, invokes one with a synthetic order,
/// and validates what the payer decided. The payer is never mocked, and its rules
/// are never reimplemented in CHO.
///
/// Subcases, reported as one BR-CRD-001 result
/// -------------------------------------------
///   001A  discovery — GET /cds-services parses, advertises CRD services, and the
///         service for the chosen hook resolves by hook rather than by position.
///   001B  functional invocation — a synthetic draft order for a billing code the
///         payer's rule fixtures cover comes back with a coverage-information
///         determination of prior authorization required.
///   001C  contrasting behaviour — two further billing codes produce two
///         materially different determinations, proving the payer ran its rule
///         lookup rather than returning a constant.
///
/// Why order-sign
/// --------------
/// Chosen from what the pinned image actually advertises, not from assumption. Of
/// the six CRD services it exposes, order-sign is the one its own rule fixtures
/// (PriorAuthRequired, ExcludedServices, DocumentationRequired) declare as their
/// named trigger event, so it is the hook that exercises real coverage logic. It
/// needs no licensed terminology, no credentials and no prior server state.
/// See docs/interop/davinci.md for the full comparison.
/// </summary>
[Collection(InteropCollection.Name)]
[Trait("Category", "DaVinciInterop")]
[Trait("Scenario", "BR-CRD-001")]
[Trait("Target", "HL7-DaVinci/br-payer")]
public sealed class BrPayerCrdOrderSignTests
{
    private const string ScenarioId = "BR-CRD-001";
    private const string TargetKey = "br-payer";

    /// <summary>The hook the pinned payer's rule fixtures declare as their trigger.</summary>
    private const string Hook = "order-sign";

    /// <summary>
    /// Billing codes selected from the pinned implementation's own rule fixtures.
    /// These are inputs the payer's rules key off — CHO does not compute, encode or
    /// duplicate the decision each one produces.
    /// </summary>
    private const string PriorAuthRequiredCode = "L8000";   // PriorAuthRequired fixture
    private const string NotCoveredCode = "J3490";          // ExcludedServices fixture
    private const string NoMatchingRuleCode = "E0100";      // covered by no fixture

    [InteropFact]
    public async Task Cho_discovers_and_exercises_the_independent_payer_crd_service()
    {
        if (!await DockerCompose.IsDockerAvailableAsync())
        {
            throw new InteropEnvironmentException(
                "No Docker daemon is reachable. External interoperability scenarios start pinned third-party " +
                "containers; start Docker, or unset CHO_INTEROP_ENABLED to skip them.");
        }

        var versions = InteropVersions.Load();
        var inventory = InteropScenarioInventory.Load();
        var scenario = inventory.Scenario(ScenarioId);
        var target = versions.Target(TargetKey);
        var run = new InteropScenarioRun(scenario, target);

        await using var environment = InteropEnvironment.For(TargetKey);
        using var client = new InteropHttpClient(target.Name, InteropSettings.Timeouts.ProtocolCall);

        // The payer may dereference the FHIR server for anything prefetch does not
        // supply. Rather than pointing it at a placeholder and hoping, the harness
        // points it at a listener it runs and asserts nothing arrived — so the
        // scenario is correct by construction, not by luck.
        using var fhirCallbackWatch = FhirCallbackWatch.Start();
        var writer = new InteropEvidenceWriter();

        InteropResult result;
        CancellationTokenSource? cancellation = null;
        try
        {
            await environment.StartAsync(
                ReadinessStage.ApplicationReady, buildImages: false, CancellationToken.None);

            cancellation = new CancellationTokenSource(InteropSettings.Timeouts.Scenario);

            var cdsHooksBase = target.Endpoints.CdsHooksBaseUrl
                ?? throw new InteropEnvironmentException($"'{TargetKey}' declares no cdsHooksBaseUrl.");

            // ── 001A — discovery ────────────────────────────────────────────────
            var (discovery, discoveryResponse) =
                await client.GetCdsHooksDiscoveryAsync(cdsHooksBase, cancellation!.Token);

            discoveryResponse.StatusCode.Should().Be(HttpStatusCode.OK,
                "the payer must serve its CDS Hooks discovery document");

            CdsHooksServiceSelector.DiscoveryViolations(discovery)
                .Should().BeEmpty("the discovery document must be a valid CDS Hooks discovery response");

            var crdServices = CdsHooksServiceSelector.CrdServices(discovery!);
            crdServices.Should().NotBeEmpty("the payer must advertise at least one CRD-capable service");

            var service = CdsHooksServiceSelector.Select(discovery!, Hook);
            service.Id.Should().NotBeNullOrWhiteSpace();

            var advertisedCrdVersions = CdsHooksServiceSelector.AdvertisedCrdVersions(discovery!);
            var prefetchKeys = CdsHooksServiceSelector.AdvertisedPrefetchKeys(service);

            run.Record(InteropFinding.Info(
                "crd.discovery.services",
                $"The payer advertises {crdServices.Count} CRD service(s): " +
                $"{string.Join(", ", crdServices.Select(s => $"{s.Id}[{s.Hook}]"))}. " +
                $"Selected '{service.Id}' for hook '{Hook}' by hook match, not list position."));

            run.Record(InteropFinding.Info(
                "crd.discovery.prefetch",
                $"Service '{service.Id}' advertises prefetch keys: {string.Join(", ", prefetchKeys)}."));

            // ── 001B — functional invocation, prior authorization required ──────
            var serviceUrl = $"{cdsHooksBase.TrimEnd('/')}/{service.Id}";

            var priorAuth = await InvokeAsync(
                client, serviceUrl, service, PriorAuthRequiredCode,
                fhirCallbackWatch.BaseUrl, "11111111-1111-4111-8111-111111111111", cancellation.Token);

            priorAuth.Raw.StatusCode.Should().Be(HttpStatusCode.OK,
                "the payer accepted the CRD request or reported why it did not");

            priorAuth.Parsed.Should().NotBeNull("the payer must answer with a parseable CDS Hooks response");
            priorAuth.Parsed!.ProtocolViolations()
                .Should().BeEmpty("the response must be a valid CDS Hooks response");

            var priorAuthCoverage = CrdCoverageInformation.FromSystemActions(priorAuth.Parsed);
            priorAuthCoverage.Should().NotBeEmpty(
                "Da Vinci CRD requires a primary hook such as {0} to return coverage information; " +
                "the payer returned {1} card(s) and {2} system action(s)",
                Hook, priorAuth.Parsed.Cards?.Count ?? 0, priorAuth.Parsed.SystemActions?.Count ?? 0);

            var priorAuthDecision = priorAuthCoverage[0];
            priorAuthDecision.CoverageReference.Should().Be($"Coverage/{SyntheticInteropData.CoverageId}",
                "the determination must be tied to the coverage CHO submitted");
            priorAuthDecision.BillingCode.Should().Be(PriorAuthRequiredCode,
                "the determination must be tied to the billing code CHO submitted");
            priorAuthDecision.IsPriorAuthRequired.Should().BeTrue(
                "the payer's own PriorAuthRequired rule fixture covers {0}; it answered: {1}",
                PriorAuthRequiredCode, priorAuthDecision.SafeSummary());

            run.Record(InteropFinding.Info(
                "crd.determination.priorAuthRequired",
                $"Billing code {PriorAuthRequiredCode} -> {priorAuthDecision.SafeSummary()}."));

            if (priorAuthDecision.QuestionnaireCanonical is not null)
            {
                run.Record(InteropFinding.Info(
                    "crd.determination.questionnaireOffered",
                    "The payer named a DTR questionnaire alongside its prior-authorization determination, " +
                    "which is the CRD-to-DTR hand-off a later scenario can follow."));
            }

            // ── 001C — contrasting behaviour ────────────────────────────────────
            var notCovered = await InvokeAsync(
                client, serviceUrl, service, NotCoveredCode,
                fhirCallbackWatch.BaseUrl, "22222222-2222-4222-8222-222222222222", cancellation.Token);

            notCovered.Raw.StatusCode.Should().Be(HttpStatusCode.OK);
            notCovered.Parsed.Should().NotBeNull();
            notCovered.Parsed!.ProtocolViolations().Should().BeEmpty();

            var notCoveredDecisions = CrdCoverageInformation.FromSystemActions(notCovered.Parsed);
            notCoveredDecisions.Should().NotBeEmpty();
            var notCoveredDecision = notCoveredDecisions[0];

            notCoveredDecision.IsNotCovered.Should().BeTrue(
                "the payer's own ExcludedServices rule fixture covers {0}; it answered: {1}",
                NotCoveredCode, notCoveredDecision.SafeSummary());
            notCoveredDecision.IsPriorAuthRequired.Should().BeFalse(
                "an excluded service must not also be reported as merely needing prior authorization");

            var noRule = await InvokeAsync(
                client, serviceUrl, service, NoMatchingRuleCode,
                fhirCallbackWatch.BaseUrl, "33333333-3333-4333-8333-333333333333", cancellation.Token);

            noRule.Raw.StatusCode.Should().Be(HttpStatusCode.OK);
            noRule.Parsed.Should().NotBeNull();
            var noRuleDecisions = CrdCoverageInformation.FromSystemActions(noRule.Parsed!);
            noRuleDecisions.Should().NotBeEmpty();
            var noRuleDecision = noRuleDecisions[0];

            // The point of the third code: three inputs, three distinct answers.
            // Without it, two differing results could still be a coincidence of two
            // hard-coded branches; with it, the payer is demonstrably resolving
            // rules per billing code.
            var determinations = new[]
            {
                priorAuthDecision.SafeSummary(),
                notCoveredDecision.SafeSummary(),
                noRuleDecision.SafeSummary(),
            };
            determinations.Distinct().Should().HaveCount(3,
                "three billing codes must produce three distinct determinations, proving the payer ran its " +
                "rule lookup rather than returning a constant. Got: {0}", string.Join(" | ", determinations));

            run.Record(InteropFinding.Info(
                "crd.determination.contrast",
                $"Three billing codes produced three distinct determinations — " +
                $"{PriorAuthRequiredCode}: {priorAuthDecision.SafeSummary()}; " +
                $"{NotCoveredCode}: {notCoveredDecision.SafeSummary()}; " +
                $"{NoMatchingRuleCode}: {noRuleDecision.SafeSummary()}."));

            // ── The FHIR callback assertion the fhirServer value depends on ─────
            fhirCallbackWatch.Requests.Should().BeEmpty(
                "the scenario supplies every prefetch key the service needs, so the payer should not " +
                "dereference the supplied fhirServer. Observed callbacks: {0}",
                string.Join(", ", fhirCallbackWatch.Requests));

            // ── Version and shape comparison, recorded as findings ──────────────
            run.RecordCompatibility(
                "CRD",
                cho: "2.2.x",
                external: advertisedCrdVersions.Count > 0 ? string.Join(", ", advertisedCrdVersions) : null,
                note: "CHO targets the Da Vinci CRD STU 2.2.x family. The external version is what the payer " +
                      "advertises in its CDS Hooks discovery davinci-crd.version extension.");

            CompareWithChoCrdSurface(run, crdServices);

            result = run.Complete(
                run.HasBlockingFindings ? InteropStatus.Failed : InteropStatus.Passed,
                client.Interactions,
                externalRole: "payer-server");
        }
        catch (Exception ex)
        {
            await environment.CaptureLogsAsync();
            result = run.Complete(
                InteropStatus.Failed, client.Interactions,
                $"{ex.GetType().Name}: {ex.Message}", externalRole: "payer-server");
            WriteEvidence(writer, versions, inventory, result, client, environment);
            throw;
        }
        finally
        {
            cancellation?.Dispose();
        }

        WriteEvidence(writer, versions, inventory, result, client, environment);
        result.ParsedStatus.Should().Be(InteropStatus.Passed);
    }

    private static async Task<(CdsHooksResponse? Parsed, InteropResponse Raw)> InvokeAsync(
        InteropHttpClient client,
        string serviceUrl,
        CdsHooksService service,
        string billingCode,
        string fhirServer,
        string hookInstance,
        CancellationToken cancellationToken)
    {
        // The hook and the service id both come from what discovery advertised,
        // so the recorded interaction names the service that was actually called.
        var request = SyntheticInteropData.CrdOrderRequest(
            service.Hook, billingCode, fhirServer, hookInstance);
        return await client.PostCdsHooksAsync(
            serviceUrl, request, serviceId: service.Id, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Compares the CRD surface CHO advertises with the payer's, recording
    /// differences as findings.
    ///
    /// This is protocol-shape comparison, not business-rule parity. CHO and the
    /// reference implementation are two different payers with two different rule
    /// sets, so a differing coverage decision would prove nothing about either.
    /// Nothing here fails the scenario: BR-CRD-001 passes on whether CHO can
    /// conduct the exchange, and these observations are for a human to weigh.
    /// </summary>
    private static void CompareWithChoCrdSurface(
        InteropScenarioRun run,
        IReadOnlyList<CdsHooksService> externalCrdServices)
    {
        var choDiscovery = ChoCrdSurface.Discovery();

        var externalHooks = externalCrdServices.Select(s => s.Hook).Distinct().OrderBy(h => h).ToList();
        var choHooks = choDiscovery.Services.Select(s => s.Hook).Distinct().OrderBy(h => h).ToList();

        run.Record(InteropFinding.Info(
            "crd.surface.hooks",
            $"CHO advertises CRD hooks [{string.Join(", ", choHooks)}]; " +
            $"the payer advertises [{string.Join(", ", externalHooks)}]."));

        var sharedHook = choHooks.FirstOrDefault(h => externalHooks.Contains(h));
        if (sharedHook is not null)
        {
            var choService = choDiscovery.Services.First(s => s.Hook == sharedHook);
            var externalService = externalCrdServices.First(s => s.Hook == sharedHook);
            if (choService.Id != externalService.Id)
            {
                run.Record(InteropFinding.Info(
                    "crd.surface.serviceIdDiffers",
                    $"CHO and the payer use different service ids for hook '{sharedHook}'. Service ids are " +
                    "server-chosen and clients resolve them from discovery, so this is expected rather than " +
                    "a defect — recorded because a client that hard-coded an id would break on it.",
                    cho: choService.Id,
                    external: externalService.Id));
            }
        }

        if (!choDiscovery.Services.Any(s => s.AdvertisedCrdVersions.Count > 0))
        {
            run.Record(InteropFinding.Warning(
                "crd.surface.noVersionExtension",
                "CHO's CDS Hooks discovery does not advertise a davinci-crd.version extension, which the " +
                "payer does. A CRD client cannot tell from CHO's discovery which CRD version CHO implements. " +
                "Recorded as an observation for follow-up, not adjudicated here.",
                cho: "no davinci-crd.version extension",
                external: "davinci-crd.version advertised",
                spec: "http://hl7.org/fhir/us/davinci-crd/"));
        }
    }

    private static void WriteEvidence(
        InteropEvidenceWriter writer,
        InteropVersions versions,
        InteropScenarioInventory inventory,
        InteropResult result,
        InteropHttpClient client,
        InteropEnvironment environment)
    {
        // Merge with anything already recorded this invocation, so running the PAS
        // and CRD scenarios together yields one run document describing both.
        var merged = writer.MergeWithPrevious([result]);
        var run = InteropEvidenceWriter.BuildRun(versions, inventory, merged);
        writer.Write(run, client.CapturedBodies, environment.ServiceLogs);
    }
}
