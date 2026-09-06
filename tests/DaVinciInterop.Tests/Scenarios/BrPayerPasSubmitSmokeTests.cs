using System.Net;
using FhirService.Controllers;
using FluentAssertions;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace DaVinciInterop.Tests.Scenarios;

/// <summary>
/// BR-PAS-SUBMIT-001 — the proving scenario for the harness.
///
/// Why this exchange
/// -----------------
/// The smallest exchange that is still *meaningful* rather than a liveness check.
/// Fetching `/fhir/metadata` alone proves only that a container booted. A PAS
/// `Claim/$submit` is the smallest Da Vinci operation the pinned br-payer image
/// answers with no prior state, no seeded fixtures, no CQL rule content and no
/// licensed terminology: the payer RI validates the request bundle against the PAS
/// request profile, evaluates coverage, and returns a PAS response bundle carrying
/// a ClaimResponse with an X12 review action. CRD discovery was the alternative,
/// but discovery is still metadata; DTR `$questionnaire-package` and PAS
/// `$inquire` both need content or prior state, which makes them a worse first
/// proof. See docs/interop/davinci.md for the full comparison.
///
/// What crosses the boundary
/// -------------------------
/// Nothing here is mocked, faked or replayed. The request is serialized by the
/// same Hl7.Fhir serializer the CHO FHIR service uses, sent over TCP to a
/// third-party container running upstream HL7 code pinned by image digest, and the
/// response is parsed and asserted with that same library. No response is rewritten
/// before validation, and no upstream fixture is copied into CHO and called
/// external validation.
///
/// Every identifier sent is synthetic (<see cref="SyntheticInteropData"/>).
/// </summary>
[Trait("Category", "DaVinciInterop")]
[Trait("Scenario", "BR-PAS-SUBMIT-001")]
[Trait("Target", "HL7-DaVinci/br-payer")]
public sealed class BrPayerPasSubmitSmokeTests
{
    private const string ScenarioId = "BR-PAS-SUBMIT-001";
    private const string TargetKey = "br-payer";

    /// <summary>PAS defines the submit operation under this canonical URL.</summary>
    private const string PasSubmitOperation =
        "http://hl7.org/fhir/us/davinci-pas/OperationDefinition/Claim-submit";

    private const string PasResponseBundleProfile =
        "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-pas-response-bundle";

    private const string PasClaimResponseProfile =
        "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-claimresponse";

    private const string ReviewActionExtension =
        "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-reviewAction";

    private const string ReviewActionCodeExtension =
        "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-reviewActionCode";

    private const string X12ReviewActionSystem = "https://codesystem.x12.org/005010/306";

    [InteropFact]
    public async Task Cho_submits_a_synthetic_prior_authorization_to_the_independent_payer_implementation()
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
        var writer = new InteropEvidenceWriter();

        InteropResult result;
        CancellationTokenSource? cancellation = null;
        try
        {
            // ── Start the pinned external implementation and wait for it, in
            //    stages, to actually be able to serve FHIR. No fixed sleeps.
            //
            //    Startup runs under its own bounds (Timeouts.ImagePull,
            //    ContainerStart, Readiness), deliberately not the scenario budget:
            //    pulling several hundred megabytes and waiting out a HAPI server's
            //    IG-package install is not part of the exchange being measured, and
            //    charging it to the scenario clock would make the scenario timeout
            //    mean "the image was slow to download" rather than "the exchange
            //    hung". Each of those bounds still fails with a diagnostic, so
            //    nothing here can hang unbounded.
            await environment.StartAsync(
                ReadinessStage.FhirMetadataAvailable,
                buildImages: false,
                CancellationToken.None);

            // The scenario budget starts once the external implementation is ready,
            // so it bounds the protocol exchange itself rather than being consumed
            // by startup before the first request is sent.
            cancellation = new CancellationTokenSource(InteropSettings.Timeouts.Scenario);

            var fhirBase = target.Endpoints.FhirBaseUrl
                ?? throw new InteropEnvironmentException($"'{TargetKey}' declares no fhirBaseUrl.");

            // ── 1. Read the external CapabilityStatement. Not the proof on its
            //    own — it is how the harness learns what the RI advertises so the
            //    functional exchange below can be checked against it.
            var metadata = await client.GetFhirAsync($"{fhirBase}/metadata", cancellation!.Token);
            metadata.StatusCode.Should().Be(HttpStatusCode.OK,
                "the pinned br-payer image must serve its CapabilityStatement before any operation is attempted");

            var capability = metadata.As<CapabilityStatement>();
            capability.Should().NotBeNull("GET /fhir/metadata must return a CapabilityStatement");
            capability!.FhirVersion.Should().Be(FHIRVersion.N4_0_1,
                "CHO and the burden-reduction reference implementations both target FHIR R4");

            var externalClaimOperations = capability.Rest
                .SelectMany(rest => rest.Resource)
                .Where(resource => resource.Type == "Claim")
                .SelectMany(resource => resource.Operation)
                .ToList();

            externalClaimOperations.Should().Contain(
                operation => operation.Definition == PasSubmitOperation,
                "the payer RI must advertise the PAS $submit operation this scenario invokes");

            // ── 2. Compare what CHO advertises for PAS with what the independent
            //    implementation advertises. This reads CHO's real production
            //    CapabilityStatement (MetadataController), not a restatement of it.
            CompareAdvertisedPasOperations(run, externalClaimOperations);

            // ── 3. The functional exchange. A synthetic PAS prior-authorization
            //    request, serialized by CHO's FHIR stack, submitted to upstream code.
            var requestBundle = SyntheticInteropData.PasRequestBundle(DateTimeOffset.UtcNow);
            var parameters = SyntheticInteropData.AsSubmitParameters(requestBundle);

            var submit = await client.PostFhirAsync($"{fhirBase}/Claim/$submit", parameters, cancellation!.Token);

            submit.StatusCode.Should().Be(HttpStatusCode.OK,
                "the payer RI accepted the request bundle or reported why it did not: {0}",
                DescribeOutcome(submit));

            // ── 4. Validate the response with the same FHIR library CHO runs.
            //    The response is asserted exactly as received; nothing about it is
            //    adjusted to make an assertion pass.
            var responseBundle = submit.As<Bundle>();
            responseBundle.Should().NotBeNull(
                "PAS $submit returns a response Bundle; got {0}", submit.Resource?.TypeName ?? "(unparseable body)");

            responseBundle!.Meta?.Profile.Should().Contain(PasResponseBundleProfile,
                "the payer RI claims conformance to the PAS response bundle profile");

            var claimResponse = responseBundle.Entry
                .Select(entry => entry.Resource)
                .OfType<ClaimResponse>()
                .FirstOrDefault();

            claimResponse.Should().NotBeNull("a PAS response bundle carries a ClaimResponse");
            claimResponse!.Meta?.Profile.Should().Contain(PasClaimResponseProfile);
            claimResponse.Use.Should().Be(ClaimUseCode.Preauthorization,
                "the response must answer the prior authorization that was submitted");
            claimResponse.Status.Should().Be(FinancialResourceStatusCodes.Active);
            claimResponse.Outcome.Should().NotBeNull("ClaimResponse.outcome is required by PAS");

            // ── 5. The PAS-specific payload: an X12 review action per submitted item.
            var reviewActionCodes = ExtractReviewActionCodes(claimResponse);
            reviewActionCodes.Should().NotBeEmpty(
                "PAS requires a review action on each adjudicated item; the harness submitted one item");
            reviewActionCodes.Should().OnlyContain(
                coding => coding.System == X12ReviewActionSystem,
                "review action codes come from the X12 005010/306 code system");
            reviewActionCodes.Should().OnlyContain(
                coding => !string.IsNullOrWhiteSpace(coding.Code),
                "every review action must carry a code");

            // ── 6. The member the harness sent must be the member that came back.
            var echoedPatient = responseBundle.Entry
                .Select(entry => entry.Resource)
                .OfType<Patient>()
                .FirstOrDefault();

            if (echoedPatient is not null)
            {
                echoedPatient.Identifier
                    .Should().Contain(id => id.Value == SyntheticInteropData.MemberId,
                        "the payer RI echoed the synthetic member identifier CHO submitted");
            }

            run.RecordCompatibility(
                "PAS",
                cho: "2.2.x",
                external: target.ImplementationGuides.GetValueOrDefault("PAS"),
                note: "CHO targets the Da Vinci PAS STU 2.2.x family (docs/compliance/CMS0057-ACCEPTANCE-INVENTORY.md). " +
                      "The external version is the IG package the pinned image installs at startup.");

            run.Record(InteropFinding.Info(
                "pas.reviewAction.observed",
                $"The payer RI returned review action code(s) [{string.Join(", ", reviewActionCodes.Select(c => c.Code))}] " +
                "for the submitted synthetic item."));

            result = run.Complete(
                run.HasBlockingFindings ? InteropStatus.Failed : InteropStatus.Passed,
                client.Interactions);
        }
        catch (Exception ex)
        {
            // A failed scenario must still produce evidence and still clean up.
            // Container logs are captured only on failure: on a passing run they
            // add megabytes of upstream startup noise to the artifact bundle
            // without telling a reviewer anything run.json does not already say.
            await environment.CaptureLogsAsync();
            result = run.Complete(InteropStatus.Failed, client.Interactions, $"{ex.GetType().Name}: {ex.Message}");
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

    /// <summary>
    /// Compares the PAS operations CHO advertises with those the external payer
    /// advertises, recording any difference as a finding.
    ///
    /// A difference is recorded, not adjudicated: it may be a CHO bug, an upstream
    /// bug, an IG ambiguity or a version mismatch, and the harness is not the place
    /// to decide which. Only the operation this scenario actually invokes is
    /// asserted; the rest are warnings so a naming difference elsewhere does not
    /// mask a real functional result.
    /// </summary>
    private static void CompareAdvertisedPasOperations(
        InteropScenarioRun run,
        IReadOnlyList<CapabilityStatement.OperationComponent> externalClaimOperations)
    {
        var choCapability = ReadChoCapabilityStatement();
        var choClaimOperations = choCapability.Rest
            .SelectMany(rest => rest.Resource)
            .Where(resource => resource.Type == "Claim")
            .SelectMany(resource => resource.Operation)
            .ToList();

        var choSubmit = choClaimOperations.SingleOrDefault(operation => operation.Name == "submit");
        choSubmit.Should().NotBeNull("CHO's CapabilityStatement advertises the PAS $submit operation");
        choSubmit!.Definition.Should().Be(PasSubmitOperation,
            "CHO and the independent payer implementation must name the same PAS $submit OperationDefinition");

        foreach (var choOperation in choClaimOperations)
        {
            var external = externalClaimOperations
                .FirstOrDefault(operation => operation.Name == choOperation.Name);

            if (external is null)
            {
                run.Record(InteropFinding.Info(
                    $"pas.operation.{choOperation.Name}.notAdvertisedExternally",
                    $"CHO advertises Claim/${choOperation.Name}; the pinned payer RI does not advertise an " +
                    "operation of that name on Claim."));
                continue;
            }

            if (external.Definition != choOperation.Definition)
            {
                run.Record(InteropFinding.Warning(
                    $"pas.operation.{choOperation.Name}.canonicalMismatch",
                    $"CHO and the payer RI advertise different OperationDefinition canonicals for Claim/${choOperation.Name}. " +
                    "Recorded as an interoperability finding; which side matches the PAS IG is for the follow-up PR to " +
                    "establish against the published IG, not for the harness to assume.",
                    cho: choOperation.Definition,
                    external: external.Definition,
                    spec: "http://hl7.org/fhir/us/davinci-pas/"));
            }
        }
    }

    /// <summary>
    /// Reads CHO's production CapabilityStatement from the real
    /// <see cref="MetadataController"/>, following the same
    /// instantiate-the-controller idiom the CMS acceptance suite uses.
    /// </summary>
    private static CapabilityStatement ReadChoCapabilityStatement()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("cho.interop.test");

        var controller = new MetadataController(new ConfigurationBuilder().Build())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        var response = controller.GetCapabilityStatement();
        var payload = response.Should().BeAssignableTo<ObjectResult>().Subject.Value;
        return payload.Should().BeAssignableTo<CapabilityStatement>().Subject;
    }

    private static List<Coding> ExtractReviewActionCodes(ClaimResponse claimResponse) =>
        claimResponse.Item
            .SelectMany(item => item.Adjudication)
            .SelectMany(adjudication => adjudication.Extension)
            .Where(extension => extension.Url == ReviewActionExtension)
            .SelectMany(extension => extension.Extension)
            .Where(extension => extension.Url == ReviewActionCodeExtension)
            .Select(extension => extension.Value)
            .OfType<CodeableConcept>()
            .SelectMany(concept => concept.Coding)
            .ToList();

    private static string DescribeOutcome(InteropResponse response) =>
        response.OperationOutcome is { } outcome
            ? string.Join("; ", outcome.Issue.Select(issue =>
                $"{issue.Severity}/{issue.Code}: {issue.Details?.Text ?? issue.Diagnostics}"))
            : response.Resource?.TypeName ?? "(no FHIR body)";

    private static void WriteEvidence(
        InteropEvidenceWriter writer,
        InteropVersions versions,
        InteropScenarioInventory inventory,
        InteropResult result,
        InteropHttpClient client,
        InteropEnvironment environment)
    {
        var run = InteropEvidenceWriter.BuildRun(versions, inventory, [result]);
        writer.Write(run, client.CapturedBodies, environment.ServiceLogs);
    }
}
