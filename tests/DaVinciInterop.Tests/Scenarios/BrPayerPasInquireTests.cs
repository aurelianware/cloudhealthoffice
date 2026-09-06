using System.Net;
using FluentAssertions;
using Hl7.Fhir.Model;

namespace DaVinciInterop.Tests.Scenarios;

/// <summary>
/// BR-PAS-INQUIRE-001 — the PAS lifecycle: <c>$submit</c> then <c>$inquire</c>,
/// against the pinned HL7 Da Vinci burden-reduction payer.
///
/// Direction
/// ---------
/// CHO is the provider-side PAS client; br-payer is the payer PAS server.
///
/// The chain this proves
/// ---------------------
/// <code>
///   CHO ──$submit(synthetic prior authorization)──▶ br-payer
///       ◀─ ClaimResponse: a decision, and a payer-issued authorization identity
///   CHO ──$inquire(that identity + the same member/provider)──▶ br-payer
///       ◀─ Parameters: the same authorization, in the same state
/// </code>
///
/// The authorization identity is never chosen by CHO. The payer mints it while
/// adjudicating, CHO reads it out of the response it just received, and quotes it
/// back unchanged. That is what makes this evidence of a lifecycle rather than of
/// two unrelated calls: a scenario that inquired on an identifier from a CHO
/// fixture would prove only that CHO can echo a string it invented, and one that
/// read an identifier off a previous run's <c>run.json</c> would prove only that a
/// file on disk survived. Neither would show that the payer retained anything.
///
/// Self-contained by construction
/// ------------------------------
/// The scenario performs its own submit, in its own container, in the same run.
/// The harness tears the payer's stack down (including volumes) between
/// scenarios, so every execution starts from an empty payer: fresh container,
/// fresh submit, fresh payer-generated identity, fresh inquire. Nothing here can
/// pass because of state some earlier run left behind.
///
/// Why this request differs from BR-PAS-SUBMIT-001's
/// ------------------------------------------------
/// Same code path — same builder, same serializer, same POST helper, same
/// <see cref="PasResponseBundle"/> reader — but a different service.
/// <c>BR-PAS-SUBMIT-001</c> deliberately asks about a code the payer has no rule
/// for, which makes it a content-independent proof of protocol interoperability
/// and gets a "not required" answer carrying no authorization number. An inquiry
/// needs an authorization to inquire about, and a payer only issues an identity
/// for a request its rules actually decided. So this scenario asks about the code
/// <c>BR-CRD-001</c> and <c>BR-DTR-001</c> already prove the payer's rules
/// evaluate. Which way the rules decide is the payer's business and is never
/// asserted.
///
/// Subcases, reported as one BR-PAS-INQUIRE-001 result
/// ---------------------------------------------------
///   001A  the payer advertises $inquire, under the canonical the PAS IG
///         publishes, and CHO advertises the same one.
///   001B  submit establishes an authorization and issues an identity for it.
///   001C  inquire on that identity returns that same authorization, in the same
///         state.
///   001D  repeating the inquiry changes nothing: same authorization, same state,
///         no duplicate created.
///   001E  the same identity with the WRONG member does not return the
///         authorization.
/// </summary>
[Collection(InteropCollection.Name)]
[Trait("Category", "DaVinciInterop")]
[Trait("Scenario", "BR-PAS-INQUIRE-001")]
[Trait("Target", "HL7-DaVinci/br-payer")]
public sealed class BrPayerPasInquireTests
{
    private const string ScenarioId = "BR-PAS-INQUIRE-001";
    private const string LinkedScenarioId = "BR-PAS-SUBMIT-001";
    private const string TargetKey = "br-payer";

    /// <summary>The service whose rule the payer evaluates, so it issues an identity.</summary>
    private static readonly SyntheticInteropData.PasRequestedService Service =
        SyntheticInteropData.PasRequestedService.PriorAuthorizationRequired;

    [InteropFact]
    public async Task Cho_inquires_on_the_authorization_the_payer_issued_for_its_own_submission()
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
        string? chainedIdentity = null;

        try
        {
            // The payer's rules run on CQL compiled at startup, so this scenario
            // waits for application readiness rather than for the FHIR endpoint
            // alone — the same bar BR-CRD-001 and BR-DTR-001 wait for.
            await environment.StartAsync(
                ReadinessStage.ApplicationReady, buildImages: false, CancellationToken.None);

            cancellation = new CancellationTokenSource(InteropSettings.Timeouts.Scenario);

            var fhirBase = target.Endpoints.FhirBaseUrl
                ?? throw new InteropEnvironmentException($"'{TargetKey}' declares no fhirBaseUrl.");

            // ── 001A — both sides advertise $inquire, under the IG's canonical ──
            var metadata = await client.GetFhirAsync($"{fhirBase}/metadata", cancellation!.Token);
            metadata.StatusCode.Should().Be(HttpStatusCode.OK);

            var capability = metadata.As<CapabilityStatement>();
            capability.Should().NotBeNull("GET /fhir/metadata must return a CapabilityStatement");

            var externalClaimOperations = capability!.Rest
                .SelectMany(rest => rest.Resource)
                .Where(resource => resource.Type == "Claim")
                .SelectMany(resource => resource.Operation)
                .ToList();

            AssertInquiryCanonicals(run, externalClaimOperations);

            // ── 001B — submit, and read the identity the payer issued ───────────
            //
            // The request identifier is unique per run so that this scenario's own
            // authorization is distinguishable from anything else the payer might
            // hold, without relying on the container being empty.
            var runTag = Guid.NewGuid().ToString("N")[..12];
            var submitted = await SubmitAsync(
                client, fhirBase, $"interop-pa-inq-{runTag}", cancellation.Token);

            submitted.SubmitProtocolViolations().Should().BeEmpty(
                "the submit leg must produce a well-formed PAS response before an inquiry means anything");

            var selection = submitted.SelectAuthorizationIdentity();
            selection.IsResolved.Should().BeTrue(
                "an inquiry correlates on an identity the PAYER issued, and this run has none to quote: {0}. "
                + "The payer answered: {1}",
                selection.Problem, submitted.SafeSummary());

            var identity = selection.Selected!;
            chainedIdentity = identity.Value;

            run.Record(InteropFinding.Info(
                "pas.submit.authorizationIdentityIssued",
                $"The payer issued {identity.SafeSummary()} while answering the submitted request with "
                + PasReviewStatus.SafeSummary(submitted.ReviewActions) + ".",
                external: identity.Value));

            run.Record(InteropFinding.Info(
                "pas.identity.placementAsymmetry",
                "The payer ISSUES the authorization number at "
                + $"{identity.SourcePath}, and PAS contextualizes the extension an inquiry QUOTES it back "
                + $"under ({PasProtocol.AuthorizationNumberExtension}) to Claim.item. Both placements are "
                + "what the PAS IG defines, so this is an IG asymmetry rather than an implementation "
                + "difference — recorded because a reader that expects one shape in both places finds "
                + "nothing.",
                spec: "http://hl7.org/fhir/us/davinci-pas/"));

            // ── 001C — inquire on exactly that identity ─────────────────────────
            var (firstInquiry, firstRaw) = await InquireAsync(
                client, fhirBase, identity.Value, $"interop-inq-{runTag}-1",
                member: null, cancellation.Token);

            firstRaw.StatusCode.Should().Be(HttpStatusCode.OK,
                "the payer accepted the inquiry or reported why not: {0}",
                string.Join("; ", ParametersExtractor.SummarizeIssues(firstRaw.OperationOutcome)));

            firstInquiry.Should().NotBeNull(
                "PAS $inquire returns Parameters; got {0}", firstRaw.Resource?.TypeName ?? "(unparseable body)");

            firstInquiry!.ProtocolViolations().Should().BeEmpty(
                "the inquiry response must be a well-formed PAS inquiry answer");

            var reported = AssertSameAuthorization(run, submitted, firstInquiry, identity);

            // ── Status continuity ──────────────────────────────────────────────
            AssertStatusContinuity(run, submitted, reported);

            // ── 001D — repeating the inquiry is read-only ───────────────────────
            var (secondInquiry, secondRaw) = await InquireAsync(
                client, fhirBase, identity.Value, $"interop-inq-{runTag}-2",
                member: null, cancellation.Token);

            secondRaw.StatusCode.Should().Be(HttpStatusCode.OK,
                "a repeated inquiry must be answerable too");
            secondInquiry.Should().NotBeNull();

            AssertRepeatIsReadOnly(run, firstInquiry, secondInquiry!, identity);

            // ── 001E — the same identity with the wrong corroborating member ────
            await AssertMismatchedMemberIsRefusedAsync(
                run, client, fhirBase, identity, runTag, cancellation.Token);

            run.RecordCompatibility(
                "PAS",
                cho: "2.2.x",
                external: target.ImplementationGuides.GetValueOrDefault("PAS"),
                note: "CHO targets the Da Vinci PAS STU 2.2.x family "
                      + "(docs/compliance/CMS0057-ACCEPTANCE-INVENTORY.md). The external version is the IG "
                      + "package the pinned image installs at startup.");

            result = run.Complete(
                run.HasBlockingFindings ? InteropStatus.Failed : InteropStatus.Passed,
                client.Interactions,
                externalRole: "payer-server",
                linkedFromScenario: LinkedScenarioId,
                linkedArtifact: chainedIdentity);
        }
        catch (Exception ex)
        {
            await environment.CaptureLogsAsync();
            result = run.Complete(
                InteropStatus.Failed, client.Interactions,
                $"{ex.GetType().Name}: {ex.Message}",
                externalRole: "payer-server",
                linkedFromScenario: LinkedScenarioId,
                linkedArtifact: chainedIdentity);
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
    /// Submits a synthetic prior authorization through the same path
    /// <c>BR-PAS-SUBMIT-001</c> uses, and reads the answer with the same reader.
    /// </summary>
    private static async Task<PasResponseBundle> SubmitAsync(
        InteropHttpClient client,
        string fhirBase,
        string requestIdentifier,
        CancellationToken cancellationToken)
    {
        var requestBundle = SyntheticInteropData.PasRequestBundle(
            DateTimeOffset.UtcNow, Service, requestIdentifier);

        var raw = await client.PostFhirAsync(
            $"{fhirBase}/Claim/${PasProtocol.SubmitOperationCode}",
            SyntheticInteropData.AsSubmitParameters(requestBundle),
            cancellationToken);

        raw.StatusCode.Should().Be(HttpStatusCode.OK,
            "the payer accepted the request bundle or reported why not: {0}",
            string.Join("; ", ParametersExtractor.SummarizeIssues(raw.OperationOutcome)));

        var response = PasResponseBundle.From(raw.Resource);
        response.Should().NotBeNull(
            "PAS $submit returns a response Bundle; got {0}", raw.Resource?.TypeName ?? "(unparseable body)");

        return response!;
    }

    /// <summary>
    /// Invokes the real <c>Claim/$inquire</c> on the route the pinned
    /// implementation serves, quoting the payer-issued identity unchanged.
    /// </summary>
    private static async Task<(PasInquiryResponse? Parsed, InteropResponse Raw)> InquireAsync(
        InteropHttpClient client,
        string fhirBase,
        string authorizationIdentity,
        string inquiryIdentifier,
        Patient? member,
        CancellationToken cancellationToken)
    {
        var inquiryBundle = SyntheticInteropData.PasInquiryBundle(
            DateTimeOffset.UtcNow, authorizationIdentity, Service, inquiryIdentifier, member);

        var raw = await client.PostFhirAsync(
            $"{fhirBase}/Claim/${PasProtocol.InquiryOperationCode}",
            SyntheticInteropData.AsInquiryParameters(inquiryBundle),
            cancellationToken);

        return (PasInquiryResponse.From(raw.Resource), raw);
    }

    /// <summary>
    /// 001A. Asserts the inquiry canonical against the published PAS IG on BOTH
    /// sides, rather than comparing the two implementations to each other.
    ///
    /// This is the assertion that closes the discrepancy BR-PAS-SUBMIT-001 first
    /// recorded as a warning. Comparing CHO with the payer could only ever say
    /// "they differ"; comparing each against what the IG publishes says which one
    /// was wrong. Every published PAS version — 1.0.0, 1.1.0, 2.0.1, 2.1.0, 2.2.0
    /// and 2.2.1, the release the pinned image installs — names the definition
    /// <c>Claim-inquiry</c> while giving it the operation code <c>inquire</c>.
    /// </summary>
    private static void AssertInquiryCanonicals(
        InteropScenarioRun run,
        IReadOnlyList<CapabilityStatement.OperationComponent> externalClaimOperations)
    {
        var externalInquire = externalClaimOperations
            .FirstOrDefault(operation => operation.Name == PasProtocol.InquiryOperationCode);

        externalInquire.Should().NotBeNull(
            "the payer RI must advertise Claim/${0}; Claim operations advertised: [{1}]",
            PasProtocol.InquiryOperationCode,
            string.Join(", ", externalClaimOperations.Select(operation => operation.Name)));

        externalInquire!.Definition.Should().Be(PasProtocol.InquiryOperationCanonical,
            "the pinned payer advertises the inquiry operation under the canonical the PAS IG publishes");

        var choInquire = ChoPasSurface.ClaimOperations()
            .SingleOrDefault(operation => operation.Name == PasProtocol.InquiryOperationCode);

        choInquire.Should().NotBeNull("CHO's CapabilityStatement advertises the PAS $inquire operation");

        choInquire!.Definition.Should().Be(PasProtocol.InquiryOperationCanonical,
            "CHO must advertise the canonical the PAS IG publishes. PAS names the OperationDefinition "
            + "'Claim-inquiry' and gives it the operation code 'inquire', so the canonical and the route "
            + "deliberately differ; spelling the canonical from the code yields '{0}', which no published "
            + "PAS version defines",
            PasProtocol.UnpublishedInquiryCanonical);

        run.Record(InteropFinding.Info(
            "pas.operation.inquire.canonicalResolved",
            "Resolved against the published PAS IG rather than by comparing the two implementations. "
            + "PAS publishes the inquiry OperationDefinition as 'Claim-inquiry' in every release to date "
            + "(1.0.0, 1.1.0, 2.0.1, 2.1.0, 2.2.0, 2.2.1) with operation code 'inquire'. The pinned payer "
            + "advertises that canonical and serves that route. CHO previously advertised "
            + $"'{PasProtocol.UnpublishedInquiryCanonical}', which matches no PAS release; that was a CHO "
            + "defect and is corrected in this change. Not a version mismatch and not an upstream defect.",
            cho: choInquire.Definition,
            external: externalInquire.Definition,
            spec: PasProtocol.InquiryOperationCanonical));
    }

    /// <summary>
    /// 001C, the core assertion: the authorization <c>$inquire</c> reported is the
    /// authorization <c>$submit</c> established.
    ///
    /// Compared on stable structural identity — the payer-issued authorization
    /// number, the payer's own reference to the request it stored, its logical id
    /// for the ClaimResponse, and the member and provider it holds — never on
    /// display text and never on position in the result set.
    /// </summary>
    private static PasResponseBundle AssertSameAuthorization(
        InteropScenarioRun run,
        PasResponseBundle submitted,
        PasInquiryResponse inquiry,
        PasAuthorizationIdentity identity)
    {
        inquiry.IsEmpty.Should().BeFalse(
            "the payer must report the authorization it issued {0} for; it returned no match. {1}",
            identity.SafeSummary(), inquiry.SafeSummary());

        var carrying = inquiry.MatchesCarrying(identity.Value);
        carrying.Should().HaveCount(1,
            "exactly one authorization carries the identity the payer issued; got {0}. {1}",
            carrying.Count, inquiry.SafeSummary());

        var reported = carrying[0];
        var submittedResponse = submitted.ClaimResponse!;
        var reportedResponse = reported.ClaimResponse!;

        reported.ClaimResponseId.Should().Be(submitted.ClaimResponseId,
            "the inquiry must report the same ClaimResponse the submit created, by the payer's own id");

        reported.RequestReference.Should().Be(submitted.RequestReference,
            "both operations must point at the same stored Claim — the payer's own reference to the "
            + "request it adjudicated");

        reportedResponse.Patient?.Reference.Should().Be(submittedResponse.Patient?.Reference,
            "the authorization reported must belong to the member the authorization was created for");

        reportedResponse.Requestor?.Reference.Should().Be(submittedResponse.Requestor?.Reference,
            "the authorization reported must name the provider that requested it");

        reportedResponse.Insurer?.Reference.Should().Be(submittedResponse.Insurer?.Reference,
            "the authorization reported must name the same insurer");

        reportedResponse.Use.Should().Be(ClaimUseCode.Preauthorization);
        reportedResponse.Status.Should().Be(FinancialResourceStatusCodes.Active);

        run.Record(InteropFinding.Info(
            "pas.inquire.sameAuthorization",
            $"$inquire on {identity.Kind} '{identity.Value}' returned the authorization $submit created: "
            + $"ClaimResponse/{reported.ClaimResponseId} for request {reported.RequestReference}, "
            + "matching on payer-issued identity, request reference, member, provider and insurer.",
            external: identity.Value));

        var profiles = inquiry.DeclaredBundleProfiles();
        if (!profiles.Contains(PasProtocol.InquiryResponseBundleProfile))
        {
            run.Record(InteropFinding.Warning(
                "pas.inquire.responseBundleProfile",
                "The payer's inquiry result does not declare the PAS inquiry response bundle profile. "
                + $"Declared: [{string.Join(", ", profiles)}]. Representational, not functional: the "
                + "authorization itself matched on stable identifiers.",
                cho: PasProtocol.InquiryResponseBundleProfile,
                external: string.Join(", ", profiles),
                spec: "http://hl7.org/fhir/us/davinci-pas/"));
        }

        foreach (var issue in inquiry.Outcomes.SelectMany(ParametersExtractor.SummarizeIssues))
        {
            run.Record(InteropFinding.Info(
                "pas.inquire.outcomeIssue",
                $"The payer reported alongside the inquiry result: {issue}"));
        }

        return reported;
    }

    /// <summary>
    /// The state <c>$submit</c> established must be the state <c>$inquire</c>
    /// reports.
    ///
    /// Asserted on the X12 review action code per item, not on display wording:
    /// the code is the decision, and a payer that rephrases its prose between two
    /// operations has not changed the authorization.
    ///
    /// A pended item is the one case where equality is not asserted. The pinned
    /// implementation schedules its own resolution of a pend — it moves an item
    /// from pended to certified after a configured delay, with no further request
    /// — so requiring "pended then, pended now" would be asserting a race against
    /// that timer. Where submit pended, the scenario records what the inquiry
    /// found and requires only that the authorization is the same one; where
    /// submit reached a settled state, continuity is asserted strictly.
    /// </summary>
    private static void AssertStatusContinuity(
        InteropScenarioRun run,
        PasResponseBundle submitted,
        PasResponseBundle reported)
    {
        var submittedActions = submitted.ReviewActions;
        var reportedActions = reported.ReviewActions;

        var selfAdvancing = submittedActions.Where(action => action.IsSelfAdvancing).ToList();

        if (selfAdvancing.Count > 0)
        {
            run.Record(InteropFinding.Info(
                "pas.inquire.statusContinuity.selfAdvancing",
                "The payer pended "
                + $"{selfAdvancing.Count} item(s) at submit and resolves a pend on its own schedule, so "
                + "strict state equality would assert a race rather than continuity. Submit reported "
                + $"[{PasReviewStatus.SafeSummary(submittedActions)}]; inquiry reported "
                + $"[{PasReviewStatus.SafeSummary(reportedActions)}]. The authorization identity and the "
                + "authorization itself are asserted; the pended item's code is recorded, not required."));

            reportedActions.Should().NotBeEmpty(
                "even a self-advancing authorization must still report a decision per item");
            return;
        }

        PasReviewStatus.SameDecision(submittedActions, reportedActions).Should().BeTrue(
            "the state $inquire reports must be the state $submit established. Submit: [{0}]. Inquiry: [{1}]",
            PasReviewStatus.SafeSummary(submittedActions),
            PasReviewStatus.SafeSummary(reportedActions));

        run.Record(InteropFinding.Info(
            "pas.inquire.statusContinuity",
            $"State held across the lifecycle: $submit answered [{PasReviewStatus.SafeSummary(submittedActions)}] "
            + $"and $inquire answered [{PasReviewStatus.SafeSummary(reportedActions)}]."));
    }

    /// <summary>
    /// 001D. Inquiry is a read: asking twice must not change the authorization,
    /// duplicate it, or look like a resubmission.
    /// </summary>
    private static void AssertRepeatIsReadOnly(
        InteropScenarioRun run,
        PasInquiryResponse first,
        PasInquiryResponse second,
        PasAuthorizationIdentity identity)
    {
        var firstMatches = first.MatchesCarrying(identity.Value);
        var secondMatches = second.MatchesCarrying(identity.Value);

        secondMatches.Should().HaveCount(firstMatches.Count,
            "a repeated inquiry must not create or reveal an additional authorization for the same "
            + "identity — a second one would mean the inquiry had a submit side effect");

        second.Matches.Should().HaveCount(first.Matches.Count,
            "a repeated inquiry must match the same set of authorizations");

        secondMatches[0].ClaimResponseId.Should().Be(firstMatches[0].ClaimResponseId,
            "the same inquiry must report the same authorization, by the payer's own id");

        secondMatches[0].RequestReference.Should().Be(firstMatches[0].RequestReference,
            "the same inquiry must point at the same stored request");

        var stable = PasReviewStatus.SameDecision(
            firstMatches[0].ReviewActions, secondMatches[0].ReviewActions);

        // A payer that advances a pend on its own timer may legitimately report a
        // different code between two reads seconds apart. That is the payer
        // progressing the authorization, not the inquiry mutating it, so it is
        // recorded rather than failed — the identity, the request and the count
        // asserted above are what prove the read had no side effect.
        if (stable)
        {
            run.Record(InteropFinding.Info(
                "pas.inquire.repeatStable",
                "Repeating $inquire returned the same authorization in the same state, and created no "
                + $"duplicate: [{PasReviewStatus.SafeSummary(secondMatches[0].ReviewActions)}]."));
            return;
        }

        run.Record(InteropFinding.Info(
            "pas.inquire.repeatAdvancedByPayer",
            "Repeating $inquire returned the same authorization with a different decision: "
            + $"[{PasReviewStatus.SafeSummary(firstMatches[0].ReviewActions)}] then "
            + $"[{PasReviewStatus.SafeSummary(secondMatches[0].ReviewActions)}]. The pinned implementation "
            + "advances a pended authorization on its own schedule, so this is the payer progressing the "
            + "authorization between two reads, not the inquiry mutating it: the authorization id, the "
            + "stored request and the match count are unchanged."));
    }

    /// <summary>
    /// 001E. A real payer-issued authorization number, presented with the wrong
    /// member, must not yield the authorization.
    ///
    /// Worth one extra call because it answers a question the positive case
    /// cannot: whether the authorization identifier is a bearer token. It is a
    /// short opaque string, and if quoting it alone were enough, the payer would
    /// be handing authorization state to anyone who guessed one.
    ///
    /// The refusal SHAPE is not asserted, only the refusal. A payer may reject the
    /// request because it cannot resolve the member, or accept it and match
    /// nothing; both are refusals to disclose and both are conformant. Which one
    /// the pinned implementation chose is recorded as a finding, because that is
    /// an observation about an implementation rather than a requirement of the
    /// specification.
    ///
    /// What this does NOT establish: the substituted member is one the payer has
    /// never been told about, so a refusal may come from the payer being unable
    /// to resolve that member at all rather than from an entitlement check
    /// between two members it knows. Proving the latter would need a second
    /// member with their own established authorization, which is an
    /// authorization-security suite rather than a lifecycle scenario. The claim
    /// made here is the narrow one the evidence supports: quoting the
    /// authorization identity is not by itself sufficient to obtain the
    /// authorization.
    /// </summary>
    private static async Task AssertMismatchedMemberIsRefusedAsync(
        InteropScenarioRun run,
        InteropHttpClient client,
        string fhirBase,
        PasAuthorizationIdentity identity,
        string runTag,
        CancellationToken cancellationToken)
    {
        var (parsed, raw) = await InquireAsync(
            client, fhirBase, identity.Value, $"interop-inq-{runTag}-mismatch",
            member: SyntheticInteropData.OtherMember(), cancellationToken);

        if (raw.StatusCode == HttpStatusCode.OK)
        {
            parsed.Should().NotBeNull(
                "a successful inquiry returns Parameters; got {0}",
                raw.Resource?.TypeName ?? "(unparseable body)");

            parsed!.MatchesCarrying(identity.Value).Should().BeEmpty(
                "the authorization must not be disclosed to an inquiry corroborated by a different member. "
                + "The payer returned: {0}", parsed.SafeSummary());

            run.Record(InteropFinding.Info(
                "pas.inquire.corroboration.emptyResult",
                "An inquiry quoting the real payer-issued authorization identity alongside a different "
                + "synthetic member was accepted and matched nothing, so quoting the identity is not by "
                + "itself sufficient to obtain the authorization. The substituted member is unknown to "
                + "the payer, so this does not establish an entitlement check between two members the "
                + "payer knows — see docs/interop/davinci.md.",
                external: $"HTTP {(int)raw.StatusCode}, {parsed.Matches.Count} match(es)"));
            return;
        }

        ((int)raw.StatusCode).Should().BeInRange(400, 499,
            "a payer that will not answer a mismatched inquiry must refuse it as a client error rather "
            + "than fail; it answered HTTP {0}", (int)raw.StatusCode);

        run.Record(InteropFinding.Info(
            "pas.inquire.corroboration.refused",
            "An inquiry quoting the real payer-issued authorization identity alongside a different "
            + $"synthetic member was refused with HTTP {(int)raw.StatusCode}: "
            + $"{string.Join("; ", ParametersExtractor.SummarizeIssues(raw.OperationOutcome))}. "
            + "Quoting the identity is not by itself sufficient to obtain the authorization. The "
            + "substituted member is unknown to the payer, so this does not establish an entitlement "
            + "check between two members the payer knows — see docs/interop/davinci.md.",
            external: $"HTTP {(int)raw.StatusCode}"));
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
