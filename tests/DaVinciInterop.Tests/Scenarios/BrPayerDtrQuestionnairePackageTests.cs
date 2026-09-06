using System.Net;
using FluentAssertions;
using Hl7.Fhir.Model;

namespace DaVinciInterop.Tests.Scenarios;

/// <summary>
/// BR-DTR-001 — DTR <c>$questionnaire-package</c> against the pinned HL7 Da Vinci
/// burden-reduction payer, entered from the payer's own CRD determination.
///
/// Direction
/// ---------
/// CHO is the provider-side DTR client; br-payer is the payer DTR server.
///
/// The chain this proves
/// ---------------------
/// <code>
///   CHO ──CRD order-sign──▶ br-payer
///       ◀─ coverage-information: pa-needed=auth-needed + questionnaire canonical
///   CHO ──$questionnaire-package(that canonical)──▶ br-payer
///       ◀─ Parameters: packagebundle containing that Questionnaire
/// </code>
///
/// The questionnaire is never chosen by CHO. The payer decides which one applies
/// when it evaluates coverage, and the scenario follows that decision into the
/// payer's DTR surface. That is what makes this independent evidence: CHO does
/// not reimplement, mirror or second-guess the payer's questionnaire selection —
/// it consumes it, which is exactly what a provider system has to do in
/// production.
///
/// Subcases, reported as one BR-DTR-001 result
/// -------------------------------------------
///   001A  the operation is advertised in the payer's CapabilityStatement under
///         the DTR OperationDefinition canonical.
///   001B  the CRD → DTR chain for the prior-authorization path: the canonical
///         CRD returned is the canonical requested and the canonical returned.
///   001C  a second CRD path names a different questionnaire, and DTR returns
///         that one — the chain follows the payer's choice rather than a constant.
///
/// Not in scope
/// ------------
/// The packages this payer returns declare the DTR *standard* questionnaire
/// profile, so they are usable as delivered and <c>$next-question</c> is not
/// required to complete them. Adaptive progression is recorded as a finding and
/// left to a later scenario rather than expanded into here.
/// </summary>
[Collection(InteropCollection.Name)]
[Trait("Category", "DaVinciInterop")]
[Trait("Scenario", "BR-DTR-001")]
[Trait("Target", "HL7-DaVinci/br-payer")]
public sealed class BrPayerDtrQuestionnairePackageTests
{
    private const string ScenarioId = "BR-DTR-001";
    private const string LinkedScenarioId = "BR-CRD-001";
    private const string TargetKey = "br-payer";
    private const string CrdHook = "order-sign";

    /// <summary>
    /// Billing codes whose CRD determinations name a questionnaire. Inputs the
    /// payer's rules key off — CHO does not encode which questionnaire each
    /// produces; that is the payer's answer and the scenario reads it at runtime.
    /// </summary>
    private const string PriorAuthCode = "L8000";
    private const string DocumentationCode = "E0466";

    [InteropFact]
    public async Task Cho_follows_the_payers_crd_determination_into_its_dtr_questionnaire_package()
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
        using var fhirCallbackWatch = FhirCallbackWatch.Start();
        var writer = new InteropEvidenceWriter();

        InteropResult result;
        CancellationTokenSource? cancellation = null;
        string? chainedCanonical = null;

        try
        {
            await environment.StartAsync(
                ReadinessStage.ApplicationReady, buildImages: false, CancellationToken.None);

            cancellation = new CancellationTokenSource(InteropSettings.Timeouts.Scenario);

            var fhirBase = target.Endpoints.FhirBaseUrl
                ?? throw new InteropEnvironmentException($"'{TargetKey}' declares no fhirBaseUrl.");
            var cdsHooksBase = target.Endpoints.CdsHooksBaseUrl
                ?? throw new InteropEnvironmentException($"'{TargetKey}' declares no cdsHooksBaseUrl.");

            // ── 001A — the payer advertises the DTR operation ───────────────────
            var metadata = await client.GetFhirAsync($"{fhirBase}/metadata", cancellation!.Token);
            metadata.StatusCode.Should().Be(HttpStatusCode.OK);

            var capability = metadata.As<CapabilityStatement>();
            capability.Should().NotBeNull();

            var questionnaireOperations = capability!.Rest
                .SelectMany(rest => rest.Resource)
                .Where(resource => resource.Type == "Questionnaire")
                .SelectMany(resource => resource.Operation)
                .ToList();

            var packageOperation = questionnaireOperations.FirstOrDefault(
                operation => operation.Definition == DtrQuestionnairePackage.OperationCanonical);

            packageOperation.Should().NotBeNull(
                "the payer must advertise $questionnaire-package under the DTR OperationDefinition canonical; " +
                "Questionnaire operations advertised: [{0}]",
                string.Join(", ", questionnaireOperations.Select(o => o.Name)));

            run.Record(InteropFinding.Info(
                "dtr.discovery.operationAdvertised",
                $"The payer advertises Questionnaire/${packageOperation!.Name} under " +
                $"{DtrQuestionnairePackage.OperationCanonical}."));

            // ── 001B — CRD names the questionnaire; DTR is asked for that one ────
            var priorAuth = await FollowCrdIntoDtrAsync(
                client, cdsHooksBase, fhirBase, PriorAuthCode,
                fhirCallbackWatch.BaseUrl, "cccccccc-1111-4111-8111-111111111111", cancellation.Token);

            chainedCanonical = priorAuth.Canonical;

            AssertChain(run, priorAuth, PriorAuthCode);

            // ── 001C — a different CRD path names a different questionnaire ─────
            var documentation = await FollowCrdIntoDtrAsync(
                client, cdsHooksBase, fhirBase, DocumentationCode,
                fhirCallbackWatch.BaseUrl, "dddddddd-2222-4222-8222-222222222222", cancellation.Token);

            AssertChain(run, documentation, DocumentationCode);

            documentation.Canonical.Should().NotBe(priorAuth.Canonical,
                "two different coverage determinations must name two different questionnaires, proving the " +
                "chain follows the payer's decision rather than returning a constant");

            run.Record(InteropFinding.Info(
                "dtr.chain.contrast",
                $"Billing code {PriorAuthCode} led to {priorAuth.Canonical}; " +
                $"{DocumentationCode} led to {documentation.Canonical}."));

            // The payer supplies every dependency through the package, so it has no
            // reason to dereference the FHIR server CHO advertised.
            fhirCallbackWatch.Requests.Should().BeEmpty(
                "no callback to the supplied fhirServer was expected. Observed: {0}",
                string.Join(", ", fhirCallbackWatch.Requests));

            var externalDtrVersion = await DiscoverDtrIgVersionAsync(client, fhirBase, cancellation.Token);
            run.RecordCompatibility(
                "DTR",
                cho: "2.2.x",
                external: externalDtrVersion,
                note: "CHO targets the Da Vinci DTR STU 2.2.x family. The external version is the DTR IG " +
                      "package the pinned image installs at startup.");

            result = run.Complete(
                run.HasBlockingFindings ? InteropStatus.Failed : InteropStatus.Passed,
                client.Interactions,
                externalRole: "payer-server",
                linkedFromScenario: LinkedScenarioId,
                linkedArtifact: chainedCanonical);
        }
        catch (Exception ex)
        {
            await environment.CaptureLogsAsync();
            result = run.Complete(
                InteropStatus.Failed, client.Interactions,
                $"{ex.GetType().Name}: {ex.Message}",
                externalRole: "payer-server",
                linkedFromScenario: LinkedScenarioId,
                linkedArtifact: chainedCanonical);
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

    /// <summary>One CRD determination and the DTR package it led to.</summary>
    private sealed record ChainStep(
        string BillingCode,
        CrdCoverageInformation Determination,
        string Canonical,
        DtrQuestionnairePackage Package);

    /// <summary>
    /// Runs the real chain for one billing code: CRD order-sign, read the
    /// questionnaire canonical the payer returned, then ask the payer's DTR
    /// surface for exactly that canonical.
    /// </summary>
    private static async Task<ChainStep> FollowCrdIntoDtrAsync(
        InteropHttpClient client,
        string cdsHooksBase,
        string fhirBase,
        string billingCode,
        string fhirServer,
        string hookInstance,
        CancellationToken cancellationToken)
    {
        var crdRequest = SyntheticInteropData.CrdOrderRequest(
            CrdHook, billingCode, fhirServer, hookInstance);

        var (crdResponse, crdRaw) = await client.PostCdsHooksAsync(
            $"{cdsHooksBase.TrimEnd('/')}/order-sign-crd",
            crdRequest,
            serviceId: "order-sign-crd",
            cancellationToken: cancellationToken);

        crdRaw.StatusCode.Should().Be(HttpStatusCode.OK,
            "the CRD leg of the chain must succeed before DTR can be entered from it");
        crdResponse.Should().NotBeNull();

        var determinations = CrdCoverageInformation.FromSystemActions(crdResponse!);
        determinations.Should().NotBeEmpty(
            "the payer must return a coverage determination for billing code {0}", billingCode);

        var determination = determinations[0];
        determination.QuestionnaireCanonical.Should().NotBeNullOrWhiteSpace(
            "the payer's determination for {0} must name a DTR questionnaire for the chain to continue; " +
            "it answered: {1}", billingCode, determination.SafeSummary());

        var canonical = determination.QuestionnaireCanonical!;

        // The canonical goes back to the payer exactly as received. No rewriting,
        // no normalisation — a server that answers differently for a canonical it
        // just issued is an interoperability result worth seeing.
        var dtrRaw = await client.PostFhirAsync(
            $"{fhirBase}/Questionnaire/$questionnaire-package",
            SyntheticInteropData.DtrQuestionnairePackageRequest(canonical),
            cancellationToken);

        dtrRaw.StatusCode.Should().Be(HttpStatusCode.OK,
            "the payer accepted the $questionnaire-package request or reported why not: {0}",
            string.Join("; ", ParametersExtractor.SummarizeIssues(dtrRaw.OperationOutcome)));

        var package = DtrQuestionnairePackage.From(dtrRaw.Resource);
        package.Should().NotBeNull(
            "$questionnaire-package returns Parameters; got {0}",
            dtrRaw.Resource?.TypeName ?? "(unparseable body)");

        return new ChainStep(billingCode, determination, canonical, package!);
    }

    /// <summary>Asserts one chain step, and records what the payer returned.</summary>
    private static void AssertChain(InteropScenarioRun run, ChainStep step, string billingCode)
    {
        step.Determination.IsPriorAuthRequired.Should().BeTrue(
            "the chain is entered from a determination that requires prior authorization; " +
            "{0} produced: {1}", billingCode, step.Determination.SafeSummary());

        step.Package.ProtocolViolations().Should().BeEmpty(
            "the returned package must be a well-formed DTR questionnaire package");

        // The link that gives this scenario its value: the canonical the payer
        // named in CRD is the one it packaged in DTR.
        var questionnaire = step.Package.Questionnaire(step.Canonical);
        questionnaire.Should().NotBeNull(
            "the package must contain the questionnaire CRD named ({0}); package carried: [{1}]",
            step.Canonical, string.Join(", ", step.Package.Index.Canonicals));

        questionnaire!.Url.Should().Be(step.Canonical,
            "canonical identity must round-trip from the CRD determination to the packaged Questionnaire");
        questionnaire.Status.Should().NotBeNull("a packaged Questionnaire must declare its status");

        step.Package.PackageBundle!.Type.Should().Be(Bundle.BundleType.Collection);

        // Dependencies are checked as the questionnaire declares them, not against
        // a list of resource types the harness expects. A questionnaire with no
        // Library or ValueSet is complete when it names none.
        var dependencies = PackageResourceIndex.QuestionnaireDependencies(questionnaire);
        step.Package.Index.UnresolvedReferences(dependencies).Should().BeEmpty(
            "every canonical the questionnaire depends on must be resolvable inside the package");

        var versionMismatches = step.Package.Index.VersionMismatches(dependencies);
        foreach (var mismatch in versionMismatches)
        {
            run.Record(InteropFinding.Warning(
                "dtr.package.dependencyVersionMismatch",
                $"A dependency of {step.Canonical} resolved only by disregarding the version it asked for: {mismatch}",
                spec: "http://hl7.org/fhir/us/davinci-dtr/"));
        }

        run.Record(InteropFinding.Info(
            $"dtr.chain.{billingCode}",
            $"CRD ({billingCode}) named {step.Canonical}; $questionnaire-package returned " +
            step.Package.SafeSummary(),
            external: step.Canonical));

        if (dependencies.Count == 0)
        {
            run.Record(InteropFinding.Info(
                $"dtr.package.noDeclaredDependencies.{billingCode}",
                $"{step.Canonical} declares no Library, ValueSet or sub-questionnaire dependencies, so the " +
                "package is complete without them. Recorded so a future upstream change that adds a " +
                "dependency is visible as a change rather than passing silently."));
        }

        if (DtrQuestionnairePackage.IsAdaptive(questionnaire))
        {
            run.Record(InteropFinding.Info(
                "dtr.package.adaptiveQuestionnaire",
                $"{step.Canonical} is an adaptive questionnaire, so completing it needs $next-question. " +
                "Adaptive progression is a separate scenario; this one proves package exchange."));
        }

        foreach (var issue in step.Package.Outcomes.SelectMany(ParametersExtractor.SummarizeIssues))
        {
            run.Record(InteropFinding.Info(
                "dtr.package.outcomeIssue",
                $"The payer reported alongside the package for {billingCode}: {issue}"));
        }
    }

    /// <summary>
    /// The DTR IG version the payer is actually running, read from the version on
    /// the DTR StructureDefinition it has installed.
    ///
    /// Asked of the server rather than taken from the pin: the pin records what
    /// interop/versions.json says the image should install, and evidence is worth
    /// more when it states what the running server reports. Returns null when the
    /// server does not expose it, which is recorded as unknown rather than
    /// backfilled from the pin.
    /// </summary>
    private static async Task<string?> DiscoverDtrIgVersionAsync(
        InteropHttpClient client,
        string fhirBase,
        CancellationToken cancellationToken)
    {
        var url = $"{fhirBase}/StructureDefinition?url=" +
                  Uri.EscapeDataString(DtrQuestionnairePackage.StandardQuestionnaireProfile) +
                  "&_elements=url,version";

        var response = await client.GetFhirAsync(url, cancellationToken);
        return response.As<Bundle>()?.Entry
            .Select(entry => entry.Resource)
            .OfType<StructureDefinition>()
            .Select(definition => definition.Version)
            .FirstOrDefault(version => !string.IsNullOrWhiteSpace(version));
    }

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
