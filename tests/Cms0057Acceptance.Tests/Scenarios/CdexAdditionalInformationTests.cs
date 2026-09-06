using System.Security.Claims;
using System.Text;
using AuthorizationService.Consumers;
using AuthorizationService.Models;
using AuthorizationService.Services.Rfai;
using FhirService.Controllers;
using FhirService.Models;
using FhirService.Services.Cdex;
using FluentAssertions;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using RfaiService.Models;
using RfaiService.Services;
using FhirTask = Hl7.Fhir.Model.Task;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// PAS-07 — the Da Vinci CDex additional-information round trip on a pended
/// prior authorization, executed against the REAL services end to end:
///
/// <code>
///   A4 review decision
///     → PendedAuthorizationRfaiCoordinator   (authorization-service)
///       → RfaiCaseService / RfaiCaseLifecycle (rfai-service — the ONE store)
///         → CdexTaskMapper                    (fhir-service: the request, as a CDex Task)
///           → CdexAttachmentSubmissionService (fhir-service: $submit-attachment)
///             → RfaiCaseService               (response recorded, announcement raised)
///               → RfaiDocsReceivedConsumer    (authorization-service: back to REVIEW)
///                 → PasResponseBuilder        (Claim/$inquire reflects the lifecycle)
/// </code>
///
/// Only the HTTP hops between the services are elided — every rule under test is
/// production code. There is no acceptance-only implementation of the lifecycle,
/// the correlation rules, the idempotency or the state transitions.
///
/// Traceability:
///   trigger      src/services/authorization-service/Services/Rfai/PendedAuthorizationRfaiCoordinator.cs
///   aggregate    src/services/rfai-service/Services/RfaiCaseLifecycle.cs, Services/RfaiCaseService.cs
///   request      src/services/fhir-service/Services/Cdex/CdexTaskMapper.cs
///   retrieval    src/services/fhir-service/Controllers/TaskController.cs
///   response     src/services/fhir-service/Controllers/CdexController.cs
///   intake       src/services/fhir-service/Services/Cdex/CdexAttachmentSubmissionService.cs
///   resume       src/services/authorization-service/Consumers/RfaiDocsReceivedConsumer.cs
/// </summary>
[Trait("Backend", "Replace")]
public class CdexAdditionalInformationTests
{
    private const string AuthNumber = "PAS-20260906-ABCD1234";
    private const string Member = "pat-001";
    private const string ProviderNpi = "1234567890";
    private const string OtherProviderNpi = "9876543210";
    private const string OtherTenant = "other-tenant";
    private const string Caller = "client-provider-system";

    private static readonly byte[] Pdf = Encoding.UTF8.GetBytes("%PDF-1.7 synthetic discharge summary");
    private static readonly byte[] OtherPdf = Encoding.UTF8.GetBytes("%PDF-1.7 a second synthetic document");

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static Authorization PendedAuthorization(
        string tenant = AcceptanceContext.TenantId,
        string authNumber = AuthNumber,
        string reviewDecision = "A4",
        AuthorizationStatus status = AuthorizationStatus.Pended) => new()
    {
        TenantId = tenant,
        Id = "auth-internal-id",
        AuthorizationNumber = authNumber,
        MemberId = Member,
        RequestingProviderNPI = ProviderNpi,
        Status = status,
        ReviewDecision = reviewDecision,
        PendReason = "Medical necessity for the requested imaging is not established.",
        FollowUpAction = "Submit the most recent imaging report and the treating note.",
        SubmittedDate = DateTime.UtcNow.AddDays(-2),
    };

    /// <summary>
    /// What the reviewer is asking for — CODED, per item. This is what turns a
    /// pended status into an answerable request.
    /// </summary>
    private static List<RequestedInformationItem> RequestedDocumentation() =>
    [
        new()
        {
            Code = "AS",
            LoincCode = "18842-5",
            Description = "Discharge summary",
            Required = true,
            ServiceLineProcedureCode = "70553",
            DiagnosisCode = "M54.5",
        },
        new()
        {
            Code = "03",
            LoincCode = "11502-2",
            Description = "Laboratory report",
            Required = false,
        },
    ];

    private static async Task<(AdditionalInformationHarness Harness, Authorization Auth, RfaiCase Case)>
        PendedWithRequestAsync(string tenant = AcceptanceContext.TenantId)
    {
        var harness = new AdditionalInformationHarness();
        var auth = PendedAuthorization(tenant);

        await harness.Coordinator().EnsureRequestForDecisionAsync(
            auth, RequestedDocumentation(), DateTime.UtcNow.AddDays(14), "CTRL-0001");

        var cases = await harness.Repository.GetByAuthNumberAsync(tenant, auth.AuthorizationNumber);
        return (harness, auth, cases.Single());
    }

    // ── Request creation ─────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_A4DecisionNamingDocumentation_RaisesADurableRequest()
    {
        var (harness, auth, rfai) = await PendedWithRequestAsync();

        rfai.Status.Should().Be(RfaiStatus.Open, "the payer is waiting on the provider");
        rfai.RequestSource.Should().Be(RfaiRequestSources.ReviewDecisionA4,
            "the request must name the decision that caused it, not merely a pended state");
        rfai.ReviewDecision.Should().Be("A4");

        // The correlation chain: tenant → authorization → request.
        rfai.TenantId.Should().Be(AcceptanceContext.TenantId);
        rfai.AuthNumber.Should().Be(AuthNumber);
        rfai.AuthorizationId.Should().Be(auth.Id);
        rfai.MemberId.Should().Be(Member);
        rfai.RequestingProviderNpi.Should().Be(ProviderNpi);
        rfai.Sequence.Should().Be(1);

        // The authorization keeps the HANDLE, not a copy of the request.
        auth.RFAIIssued.Should().BeTrue();
        auth.RFAIReference.Should().Be(rfai.TrackingId);
        auth.RFAIIssuedDate.Should().NotBeNull();
        auth.Status.Should().Be(AuthorizationStatus.Pended, "raising a request decides nothing");

        harness.Repository.CreateCount.Should().Be(1);
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_RequestIsStructuredNotProse()
    {
        var (_, _, rfai) = await PendedWithRequestAsync();

        rfai.RequestedItems.Should().HaveCount(2);

        var discharge = rfai.RequestedItems[0];
        discharge.Code.Should().Be("AS", "the X12 PWK code, for the 277/275 wire");
        discharge.LoincCode.Should().Be("18842-5", "the LOINC code, for the FHIR/CDex wire");
        discharge.ServiceLineProcedureCode.Should().Be("70553");
        discharge.DiagnosisCode.Should().Be("M54.5");
        discharge.Required.Should().BeTrue();

        rfai.RequestedItems[1].Required.Should().BeFalse();

        // Free text SUPPLEMENTS the codes; it does not replace them.
        rfai.Notes.Should().Be("Submit the most recent imaging report and the treating note.");
        rfai.ReasonDescription.Should().Contain("Medical necessity");
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_PendedWithoutNamedDocumentation_RaisesNoRequest()
    {
        // A pend that asks the provider for NOTHING is not a documentation
        // request. Manufacturing one would put a question to the provider that no
        // reviewer posed.
        var harness = new AdditionalInformationHarness();
        var auth = PendedAuthorization();

        var stamped = await harness.Coordinator().EnsureRequestForDecisionAsync(
            auth, [], dueDate: null, decisionControlNumber: "CTRL-0001");

        stamped.Should().BeFalse();
        harness.Repository.All.Should().BeEmpty();
        auth.RFAIIssued.Should().BeFalse();
        auth.RFAIReference.Should().BeNull();
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_ADecisionWithAnUnusableItemRaisesNothingRatherThanFailingLater()
    {
        // The eligibility rule here and the validation rule in rfai-service are
        // the SAME rule: every named item must be usable. A predicate that
        // accepted a mixed list would call the decision eligible and then have
        // the request rejected downstream — a request the reviewer believes they
        // raised and the provider never receives.
        var harness = new AdditionalInformationHarness();
        var auth = PendedAuthorization();

        List<RequestedInformationItem> mixed =
        [
            new() { Code = "AS", Description = "Discharge summary" },
            new() { Code = "03", Description = "   " },
        ];

        var stamped = await harness.Coordinator().EnsureRequestForDecisionAsync(
            auth, mixed, dueDate: null, decisionControlNumber: "CTRL-0001");

        stamped.Should().BeFalse();
        harness.Gateway.Calls.Should().Be(0, "nothing is sent that would be refused on arrival");
        harness.Repository.All.Should().BeEmpty();
        auth.RFAIIssued.Should().BeFalse();

        // And the rules genuinely agree — the aggregate would have refused it.
        RfaiCaseLifecycle.Validate(new RfaiCreationRequest
        {
            TenantId = AcceptanceContext.TenantId,
            AuthNumber = AuthNumber,
            RequestedItems = mixed
                .Select(i => new RequestedItem { Code = i.Code, Description = i.Description })
                .ToList(),
        }).IsValid.Should().BeFalse();
    }

    [Theory]
    [Trait("Scenario", "PAS-07")]
    [InlineData("A1", AuthorizationStatus.Approved)]
    [InlineData("A2", AuthorizationStatus.Modified)]
    [InlineData("A3", AuthorizationStatus.Denied)]
    [InlineData(null, AuthorizationStatus.InReview)]
    public async Task PAS07_Replace_NonA4DecisionsRaiseNoRequest(
        string? reviewDecision, AuthorizationStatus status)
    {
        // Only a pended-for-information decision asks for documentation. An
        // approval, a denial or an ordinary in-review state does not, however
        // much documentation a reviewer might privately want.
        var harness = new AdditionalInformationHarness();
        var auth = PendedAuthorization(reviewDecision: reviewDecision ?? string.Empty, status: status);

        var stamped = await harness.Coordinator().EnsureRequestForDecisionAsync(
            auth, RequestedDocumentation(), dueDate: null, decisionControlNumber: "CTRL-0001");

        stamped.Should().BeFalse();
        harness.Repository.All.Should().BeEmpty();
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_RedeliveredA4EventCreatesNoSecondRequest()
    {
        var harness = new AdditionalInformationHarness();
        var auth = PendedAuthorization();
        var coordinator = harness.Coordinator();

        await coordinator.EnsureRequestForDecisionAsync(
            auth, RequestedDocumentation(), null, "CTRL-0001");
        var first = harness.Repository.All.Single();

        // The SAME decision, delivered again.
        await coordinator.EnsureRequestForDecisionAsync(
            auth, RequestedDocumentation(), null, "CTRL-0001");

        harness.Repository.All.Should().HaveCount(1);
        harness.Repository.All.Single().TrackingId.Should().Be(first.TrackingId,
            "a replay must not re-issue the handle the provider was already given");
        auth.RFAIReference.Should().Be(first.TrackingId);
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_TwoWorkersOnOneDecisionCreateOneRequest()
    {
        // Concurrency, at the point it actually bites: both workers derive the
        // same document id from the same decision, both attempt the insert, and
        // the conditional create lets exactly one through.
        var harness = new AdditionalInformationHarness();
        var auth = PendedAuthorization();
        var coordinator = harness.Coordinator();

        var raced = false;
        harness.Repository.OnBeforeCreate = candidate =>
        {
            if (raced) return;
            raced = true;
            // The other worker gets there first, between this one's read and write.
            harness.Repository.CreateIfAbsentAsync(candidate).GetAwaiter().GetResult();
        };

        await coordinator.EnsureRequestForDecisionAsync(
            auth, RequestedDocumentation(), null, "CTRL-0001");

        harness.Repository.All.Should().HaveCount(1,
            "one A4 decision produces one request however many workers see it");
        harness.Repository.ConflictCount.Should().Be(1, "the loser read back the winner's case");
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_ADifferentDecisionWhileOneCycleIsOpenReusesThatCycle()
    {
        // Two open requests would leave the provider guessing which one their
        // documents answer.
        var harness = new AdditionalInformationHarness();
        var auth = PendedAuthorization();
        var coordinator = harness.Coordinator();

        await coordinator.EnsureRequestForDecisionAsync(
            auth, RequestedDocumentation(), null, "CTRL-0001");
        await coordinator.EnsureRequestForDecisionAsync(
            auth, RequestedDocumentation(), null, "CTRL-0002");

        harness.Repository.All.Should().HaveCount(1);
        harness.Repository.All.Single().IsOpen.Should().BeTrue();
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_AnUnreachableRfaiServiceLeavesARecoverableState()
    {
        // No outbox exists, so this does not pretend to atomicity. What it does
        // guarantee: the decision stands, nothing is half-created, and the retry
        // is idempotent because the correlation key is unchanged.
        var harness = new AdditionalInformationHarness();
        var auth = PendedAuthorization();
        var coordinator = harness.Coordinator();

        harness.Gateway.Unavailable = true;
        var stamped = await coordinator.EnsureRequestForDecisionAsync(
            auth, RequestedDocumentation(), null, "CTRL-0001");

        stamped.Should().BeFalse();
        auth.RFAIIssued.Should().BeFalse("the recoverable state is 'pended, no request yet'");
        auth.Status.Should().Be(AuthorizationStatus.Pended, "the decision itself stands");
        harness.Repository.All.Should().BeEmpty();

        // The retry — the same decision, once rfai-service is back.
        harness.Gateway.Unavailable = false;
        await coordinator.EnsureRequestForDecisionAsync(
            auth, RequestedDocumentation(), null, "CTRL-0001");

        harness.Repository.All.Should().HaveCount(1, "the retry creates exactly one request");
        auth.RFAIIssued.Should().BeTrue();
    }

    // ── Standards representation: the CDex Task ──────────────────────────────

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_OutstandingRequestIsRetrievableAsACdexTask()
    {
        var (harness, _, rfai) = await PendedWithRequestAsync();

        var task = await ReadTaskAsync(harness, rfai.Id);

        task.Meta!.Profile.Should().Contain(CdexCanonicalUrls.TaskAttachmentRequestProfile,
            "the request is served on the CDex profile it is actually shaped for");
        task.Status.Should().Be(FhirTask.TaskStatus.Requested);
        task.Intent.Should().Be(FhirTask.TaskIntent.Order);
        task.Code!.Coding.Should().ContainSingle(c =>
            c.System == CdexCanonicalUrls.TempCodeSystem
            && c.Code == CdexCanonicalUrls.AttachmentRequestCode);

        // CHO's own state survives the translation into FHIR's narrower vocabulary.
        task.BusinessStatus!.Coding.Should().ContainSingle(c => c.Code == "Open");

        // The tracking id is what a submission quotes.
        task.Identifier.Should().Contain(i =>
            i.System == CdexCanonicalUrls.TrackingIdSystem && i.Value == rfai.TrackingId);

        // …and the request points back at the prior authorization, by identifier
        // as well as by reference.
        task.Focus!.Reference.Should().Be($"Claim/{AuthNumber}");
        task.Focus.Identifier!.Value.Should().Be(AuthNumber);
        task.For!.Reference.Should().Be($"Patient/{Member}");
        task.Owner!.Identifier!.System.Should().Be(CdexCanonicalUrls.UsNpi);
        task.Owner.Identifier.Value.Should().Be(ProviderNpi);

        // The A4 decision that caused it, on the request itself.
        task.ReasonCode!.Coding.Should().Contain(c =>
            c.System == CdexCanonicalUrls.X12ReviewDecision && c.Code == "A4");

        task.Restriction!.Period!.End.Should().NotBeNull("the due date is structured, not prose");
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_TheCdexTaskCarriesCodedInputsNotOnlyText()
    {
        var (harness, _, rfai) = await PendedWithRequestAsync();
        var task = await ReadTaskAsync(harness, rfai.Id);

        var attachmentInputs = task.Input
            .Where(i => i.Type?.Coding?.Any(c => c.Code == CdexCanonicalUrls.AttachmentCode) == true)
            .Select(i => i.Value)
            .OfType<CodeableConcept>()
            .ToList();

        attachmentInputs.SelectMany(c => c.Coding)
            .Should().Contain(c => c.System == CdexCanonicalUrls.Loinc && c.Code == "18842-5")
            .And.Contain(c => c.System == CdexCanonicalUrls.X12AttachmentReportType && c.Code == "AS");

        // The diagnosis the question is about has its OWN input type. Typing it
        // as an attachment code would make a consumer reading Task.input by type
        // take the diagnosis for a document being requested.
        var diagnosis = task.Input.Should().ContainSingle(i =>
            i.Type!.Coding.Any(c => c.System == CdexCanonicalUrls.ChoTaskInputCodeSystem
                                    && c.Code == CdexCanonicalUrls.DiagnosisContext)).Subject;

        (diagnosis.Value as CodeableConcept)!.Coding.Should().ContainSingle(c =>
            c.System == CdexCanonicalUrls.Icd10Cm && c.Code == "M54.5");

        attachmentInputs.SelectMany(c => c.Coding)
            .Should().NotContain(c => c.System == CdexCanonicalUrls.Icd10Cm,
                "a diagnosis is not one of the documents being asked for");

        // The service line the question is about.
        task.Input.Should().Contain(i =>
            i.Type!.Coding.Any(c => c.Code == CdexCanonicalUrls.LineItem));

        // Purpose of use is stated, not left to inference.
        var purpose = task.Input.Single(i =>
            i.Type!.Coding.Any(c => c.Code == CdexCanonicalUrls.PurposeOfUse)).Value as CodeableConcept;
        purpose!.Coding.Should().ContainSingle(c =>
            c.Code == CdexCanonicalUrls.CoverageAuthPurposeOfUse);

        // So is whether a signature is required.
        task.Input.Should().Contain(i =>
            i.Type!.Coding.Any(c => c.Code == CdexCanonicalUrls.SignatureFlag));
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_RequestIsDiscoverableFromTheAuthorizationNumber()
    {
        // The provider learns from $inquire that the decision is pended for
        // information (X12 A4), and finds the structured request by the
        // authorization number they already hold.
        var (harness, _, rfai) = await PendedWithRequestAsync();

        var bundle = await SearchTasksAsync(harness, new AppealTaskSearchParams
        {
            Code = CdexCanonicalUrls.AttachmentRequestCode,
            Focus = $"Claim/{AuthNumber}",
        });

        var task = bundle.Entry.Should().ContainSingle().Subject.Resource.Should().BeOfType<FhirTask>().Subject;
        task.Id.Should().Be(rfai.Id);

        // …or by the tracking id itself.
        var byTracking = await SearchTasksAsync(harness, new AppealTaskSearchParams
        {
            Identifier = rfai.TrackingId,
        });
        byTracking.Entry.Should().ContainSingle();
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_DeliveringTheRequestIsRecordedAsProvenance()
    {
        var (harness, _, rfai) = await PendedWithRequestAsync();

        (await harness.Repository.GetByIdAsync(AcceptanceContext.TenantId, rfai.Id))!
            .FirstDeliveredAt.Should().BeNull("nothing has been handed over yet");

        await ReadTaskAsync(harness, rfai.Id);

        var delivered = (await harness.Repository.GetByIdAsync(AcceptanceContext.TenantId, rfai.Id))!;
        delivered.FirstDeliveredAt.Should().NotBeNull();
        delivered.DeliveryCount.Should().Be(1);
    }

    [Theory]
    [Trait("Scenario", "PAS-07")]
    [InlineData(RfaiStatus.Open, false, "Requested", "Open")]
    [InlineData(RfaiStatus.DocsReceived, true, "Completed", "DocsReceived")]
    [InlineData(RfaiStatus.Closed, true, "Completed", "Closed")]
    [InlineData(RfaiStatus.Closed, false, "Failed", "Closed")]
    [InlineData(RfaiStatus.Cancelled, false, "Cancelled", "Cancelled")]
    public void PAS07_Replace_EveryRfaiStateProjectsToATaskStatus(
        RfaiStatus state, bool answered, string expectedTaskStatus, string expectedBusinessStatus)
    {
        // Total over the lifecycle, and lossless: FHIR's narrower Task.status
        // carries the workflow answer while businessStatus keeps CHO's own state
        // name. A cycle closed WITHOUT the information must not read as satisfied.
        var request = new CdexAdditionalInformationRequest
        {
            TenantId = AcceptanceContext.TenantId,
            Id = "rfai-x",
            AuthNumber = AuthNumber,
            TrackingId = "RFAI-20260906-AAAABBBBCCCC",
            Status = Enum.Parse<CdexAdditionalInformationStatus>(state.ToString()),
            ReceivedAttachments = answered
                ? [new CdexReceivedArtifact { SubmissionId = "s1", ReceivedAt = DateTime.UtcNow }]
                : [],
        };

        var task = new CdexTaskMapper().ToAttachmentRequestTask(request);

        task.Status.ToString().Should().Be(expectedTaskStatus);
        task.BusinessStatus!.Coding.Should().ContainSingle(c => c.Code == expectedBusinessStatus);
    }

    // ── Response intake ──────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_CorrelatedDocumentationIsAcceptedAndLinked()
    {
        var (harness, _, rfai) = await PendedWithRequestAsync();

        var result = await SubmitAsync(harness, rfai.TrackingId, AuthNumber);

        result.Outcome.Should().Be(CdexSubmissionOutcome.Accepted);
        result.Recorded.Should().Be(1);
        result.ResumedReview.Should().BeTrue();

        var stored = (await harness.Repository.GetByIdAsync(AcceptanceContext.TenantId, rfai.Id))!;
        stored.Status.Should().Be(RfaiStatus.DocsReceived);
        stored.RespondedAt.Should().NotBeNull();

        var artifact = stored.ReceivedAttachments.Should().ContainSingle().Subject;
        artifact.AttachmentControlNumber.Should().Be(rfai.TrackingId,
            "the artifact is linked to the request it answers");
        artifact.SubmittedBy.Should().Be(Caller);
        artifact.Channel.Should().Be(RfaiResponseChannels.CdexSubmitAttachment);
        artifact.StorageKey.Should().NotBeNullOrWhiteSpace();
        artifact.FileHash.Should().NotBeNullOrWhiteSpace();

        // The bytes are in the attachment content store, not on the case record.
        harness.Content.Count.Should().Be(1);
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_ReplayingTheSameSubmissionChangesNothing()
    {
        var (harness, _, rfai) = await PendedWithRequestAsync();

        await SubmitAsync(harness, rfai.TrackingId, AuthNumber);
        var replay = await SubmitAsync(harness, rfai.TrackingId, AuthNumber);

        replay.Outcome.Should().Be(CdexSubmissionOutcome.DuplicateReplay);
        replay.Recorded.Should().Be(0);
        replay.ResumedReview.Should().BeFalse("a replay must not restart review a second time");

        var stored = (await harness.Repository.GetByIdAsync(AcceptanceContext.TenantId, rfai.Id))!;
        stored.ReceivedAttachments.Should().ContainSingle("a retry does not duplicate a document");

        harness.Kafka.DocsReceivedMessages.Should().ContainSingle(
            "the resume-review announcement is raised on the transition, not on every call");
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_ADifferentDocumentIsAnAdditionalResponseNotAnOverwrite()
    {
        var (harness, _, rfai) = await PendedWithRequestAsync();

        await SubmitAsync(harness, rfai.TrackingId, AuthNumber);
        var second = await SubmitAsync(harness, rfai.TrackingId, AuthNumber, content: OtherPdf);

        second.Outcome.Should().Be(CdexSubmissionOutcome.Accepted);

        var stored = (await harness.Repository.GetByIdAsync(AcceptanceContext.TenantId, rfai.Id))!;
        stored.ReceivedAttachments.Should().HaveCount(2,
            "a materially different response is appended, never silently overwritten");
        stored.ReceivedAttachments.Select(a => a.SubmissionId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_DocumentsCannotBeAttachedToAnArbitraryAuthorization()
    {
        // Knowing an authorization number is not enough. This is the check that
        // stops a caller pointing a valid tracking id at someone else's
        // authorization.
        var (harness, _, rfai) = await PendedWithRequestAsync();

        var result = await SubmitAsync(harness, rfai.TrackingId, attachTo: "PAS-20260906-ZZZZ9999");

        result.Outcome.Should().Be(CdexSubmissionOutcome.AuthorizationMismatch);
        result.Disclosure.Should().Be(CdexSubmissionDisclosure.Unavailable);
        harness.Content.Count.Should().Be(0, "nothing is stored for a refused submission");
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_AnUnrelatedProviderCannotAnswerSomeoneElsesRequest()
    {
        var (harness, _, rfai) = await PendedWithRequestAsync();

        var result = await SubmitAsync(
            harness, rfai.TrackingId, AuthNumber, providerNpi: OtherProviderNpi);

        result.Outcome.Should().Be(CdexSubmissionOutcome.ProviderMismatch);
        result.Disclosure.Should().Be(CdexSubmissionDisclosure.Unavailable);
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_AnotherTenantCannotAnswerOrEvenSeeTheRequest()
    {
        var (harness, _, rfai) = await PendedWithRequestAsync();

        // Tenant comes from the authenticated context, never from the payload.
        var submission = await SubmitAsync(
            harness, rfai.TrackingId, AuthNumber, tenant: OtherTenant);
        submission.Outcome.Should().Be(CdexSubmissionOutcome.NotFound);

        // And the request itself is invisible to that tenant.
        var read = await ReadTaskResultAsync(harness, rfai.Id, tenant: OtherTenant);
        read.Should().BeOfType<ObjectResult>().Subject.StatusCode.Should().Be(404);

        var search = await SearchTasksAsync(harness, new AppealTaskSearchParams
        {
            Identifier = rfai.TrackingId,
        }, tenant: OtherTenant);
        search.Entry.Should().BeEmpty();
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_UnknownWrongTenantWrongAuthAndWrongProviderAllLookTheSame()
    {
        // Anti-enumeration: a tracking id must not become a probe for which
        // requests exist, whose they are, or which authorization they belong to.
        var (harness, _, rfai) = await PendedWithRequestAsync();

        var refusals = new[]
        {
            await SubmitAsync(harness, "RFAI-20260906-000000000000", AuthNumber),
            await SubmitAsync(harness, rfai.TrackingId, AuthNumber, tenant: OtherTenant),
            await SubmitAsync(harness, rfai.TrackingId, attachTo: "PAS-20260906-ZZZZ9999"),
            await SubmitAsync(harness, rfai.TrackingId, AuthNumber, providerNpi: OtherProviderNpi),
        };

        refusals.Should().OnlyContain(r => r.Disclosure == CdexSubmissionDisclosure.Unavailable);

        // The distinguishing category survives for AUDIT — it just never reaches
        // the caller. A request in another tenant reads as "not found" because
        // the lookup itself is tenant-scoped: the isolation is in the query, not
        // in a comparison made after the record was already fetched.
        refusals.Select(r => r.Outcome).Should().BeEquivalentTo(
        [
            CdexSubmissionOutcome.NotFound,
            CdexSubmissionOutcome.NotFound,
            CdexSubmissionOutcome.AuthorizationMismatch,
            CdexSubmissionOutcome.ProviderMismatch,
        ]);
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_ARefusedRecordLookupIsOneIdenticalHttpAnswer()
    {
        var (harness, _, rfai) = await PendedWithRequestAsync();

        var bodies = new List<string?>();
        foreach (var tracking in new[] { "RFAI-20260906-000000000000", rfai.TrackingId })
        {
            var response = await SubmitViaControllerAsync(
                harness, tracking, AuthNumber,
                providerNpi: tracking == rfai.TrackingId ? OtherProviderNpi : ProviderNpi);

            var result = response.Should().BeOfType<ObjectResult>().Subject;
            result.StatusCode.Should().Be(404);
            bodies.Add((result.Value as OperationOutcome)?.Issue[0].Diagnostics);
        }

        bodies.Distinct().Should().ContainSingle(
            "unknown and not-yours must be indistinguishable to the caller");
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_AClosedRequestRefusesAResponseExplicitly()
    {
        // Once the caller has proven the request is theirs, saying WHY it cannot
        // take a response reveals nothing they did not already know.
        var (harness, _, rfai) = await PendedWithRequestAsync();

        var stored = (await harness.Repository.GetByIdAsync(AcceptanceContext.TenantId, rfai.Id))!;
        RfaiCaseLifecycle.Close(stored, "reviewer-1", "Decision made without the documentation.", DateTime.UtcNow);
        await harness.Repository.UpdateAsync(stored);

        var result = await SubmitAsync(harness, rfai.TrackingId, AuthNumber);

        result.Outcome.Should().Be(CdexSubmissionOutcome.RequestNotOpen);
        result.Disclosure.Should().Be(CdexSubmissionDisclosure.Conflict);
        harness.Content.Count.Should().Be(0);
    }

    [Theory]
    [Trait("Scenario", "PAS-07")]
    [InlineData(null, AuthNumber, CdexSubmissionOutcome.MissingTrackingId)]
    [InlineData("RFAI-20260906-AAAABBBBCCCC", null, CdexSubmissionOutcome.MissingAttachTo)]
    public async Task PAS07_Replace_RequestShapeDefectsAreDescribedPlainly(
        string? trackingId, string? attachTo, CdexSubmissionOutcome expected)
    {
        var (harness, _, _) = await PendedWithRequestAsync();

        var result = await harness.Submissions().SubmitAsync(
            BuildParameters(trackingId, attachTo, ProviderNpi, [Pdf]),
            AcceptanceContext.TenantId, Caller);

        result.Outcome.Should().Be(expected);
        result.Disclosure.Should().Be(CdexSubmissionDisclosure.BadRequest,
            "a defect in the request is the caller's to fix and says nothing about what exists");
    }

    // ── Payload handling ─────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_UnsupportedMediaTypeIsRejected()
    {
        var (harness, _, rfai) = await PendedWithRequestAsync();

        var result = await harness.Submissions().SubmitAsync(
            BuildParameters(rfai.TrackingId, AuthNumber, ProviderNpi, [Pdf],
                contentType: "application/x-msdownload"),
            AcceptanceContext.TenantId, Caller);

        result.Outcome.Should().Be(CdexSubmissionOutcome.UnsupportedContentType);
        result.Disclosure.Should().Be(CdexSubmissionDisclosure.UnprocessableContent);
        harness.Content.Count.Should().Be(0);
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_OversizedAndOvercountedPayloadsAreRejected()
    {
        var (harness, _, rfai) = await PendedWithRequestAsync();
        var submissions = harness.Submissions();

        var oversized = new byte[CdexAttachmentPolicy.MaxAttachmentBytes + 1];
        var tooBig = await submissions.SubmitAsync(
            BuildParameters(rfai.TrackingId, AuthNumber, ProviderNpi, [oversized]),
            AcceptanceContext.TenantId, Caller);
        tooBig.Outcome.Should().Be(CdexSubmissionOutcome.AttachmentTooLarge);

        var many = Enumerable
            .Range(0, CdexAttachmentPolicy.MaxAttachmentsPerSubmission + 1)
            .Select(i => Encoding.UTF8.GetBytes($"%PDF-1.7 doc {i}"))
            .ToArray();
        var tooMany = await submissions.SubmitAsync(
            BuildParameters(rfai.TrackingId, AuthNumber, ProviderNpi, many),
            AcceptanceContext.TenantId, Caller);
        tooMany.Outcome.Should().Be(CdexSubmissionOutcome.TooManyAttachments);

        harness.Content.Count.Should().Be(0, "a refused payload stores nothing");
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_ACallerSuppliedUrlIsRefusedNeverFetched()
    {
        // Dereferencing it would make the payer's server fetch whatever the
        // submitter points it at.
        var (harness, _, rfai) = await PendedWithRequestAsync();

        var parameters = new Parameters();
        parameters.Add(CdexSubmitAttachmentParameters.TrackingIdParameter, new FhirString(rfai.TrackingId));
        parameters.Add(CdexSubmitAttachmentParameters.AttachToParameter,
            new Identifier(CdexCanonicalUrls.AuthorizationNumberSystem, AuthNumber));
        parameters.Add(CdexSubmitAttachmentParameters.ProviderParameter, new ResourceReference
        {
            Identifier = new Identifier(CdexCanonicalUrls.UsNpi, ProviderNpi),
        });

        var attachment = new Parameters.ParameterComponent
        {
            Name = CdexSubmitAttachmentParameters.AttachmentParameter,
        };
        attachment.Part.Add(new Parameters.ParameterComponent
        {
            Name = CdexSubmitAttachmentParameters.ContentPart,
            Value = new Attachment
            {
                ContentType = "application/pdf",
                Url = "http://169.254.169.254/latest/meta-data/",
            },
        });
        parameters.Parameter.Add(attachment);

        var result = await harness.Submissions().SubmitAsync(
            parameters, AcceptanceContext.TenantId, Caller);

        result.Outcome.Should().Be(CdexSubmissionOutcome.ExternalContentRejected);
        harness.Content.Count.Should().Be(0);
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_ContentScreeningRefusesBeforeAnythingIsStored()
    {
        var (harness, _, rfai) = await PendedWithRequestAsync();

        var result = await harness.Submissions(new RejectingAttachmentContentScanner())
            .SubmitAsync(
                BuildParameters(rfai.TrackingId, AuthNumber, ProviderNpi, [Pdf]),
                AcceptanceContext.TenantId, Caller);

        result.Outcome.Should().Be(CdexSubmissionOutcome.ContentRejected);
        harness.Content.Count.Should().Be(0);

        var stored = (await harness.Repository.GetByIdAsync(AcceptanceContext.TenantId, rfai.Id))!;
        stored.ReceivedAttachments.Should().BeEmpty();
        stored.Status.Should().Be(RfaiStatus.Open, "a refused response does not consume the request");
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_OneBadAttachmentRejectsTheWholeCall()
    {
        // All-or-nothing: a call whose second attachment is unacceptable must not
        // leave the first stored and half-recorded.
        var (harness, _, rfai) = await PendedWithRequestAsync();

        var parameters = BuildParameters(rfai.TrackingId, AuthNumber, ProviderNpi, [Pdf]);
        AddAttachment(parameters, new byte[CdexAttachmentPolicy.MaxAttachmentBytes + 1], "application/pdf");

        var result = await harness.Submissions().SubmitAsync(
            parameters, AcceptanceContext.TenantId, Caller);

        result.Outcome.Should().Be(CdexSubmissionOutcome.AttachmentTooLarge);
        harness.Content.Count.Should().Be(0);
        (await harness.Repository.GetByIdAsync(AcceptanceContext.TenantId, rfai.Id))!
            .ReceivedAttachments.Should().BeEmpty();
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_TheCaseRecordHoldsPointersNotClinicalContent()
    {
        var (harness, _, rfai) = await PendedWithRequestAsync();

        await SubmitAsync(harness, rfai.TrackingId, AuthNumber,
            title: "../../etc/passwd\r\nDischarge summary");

        var stored = (await harness.Repository.GetByIdAsync(AcceptanceContext.TenantId, rfai.Id))!;
        var artifact = stored.ReceivedAttachments.Single();

        // A pointer, a hash and receipt metadata — no bytes, and no base64.
        var serialized = System.Text.Json.JsonSerializer.Serialize(stored);
        serialized.Should().NotContain(Convert.ToBase64String(Pdf));
        serialized.Should().NotContain("%PDF");

        // The caller's title is kept as metadata only, sanitized, and never as a path.
        artifact.Title.Should().NotContain("..").And.NotContain("/");
        artifact.StorageKey.Should().NotContain("passwd");
        artifact.StorageKey.Should().NotContain("..");
    }

    // ── State transitions ────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_DocumentationReturnsTheAuthorizationToReviewAndNeverApprovesIt()
    {
        // The whole point of the round trip, and the one thing it must never do.
        var (harness, auth, rfai) = await PendedWithRequestAsync();

        var authorizations = new InMemoryAuthorizationRepository();
        await authorizations.CreateAsync(auth);

        auth.Status.Should().Be(AuthorizationStatus.Pended);

        await SubmitAsync(harness, rfai.TrackingId, AuthNumber);

        var announcement = harness.Kafka.DocsReceivedMessages.Should().ContainSingle().Subject;
        await harness.ResumeConsumer(authorizations).ProcessMessageAsync(announcement);

        var resumed = (await authorizations.GetByAuthorizationNumberAsync(AuthNumber))!;
        resumed.Status.Should().Be(AuthorizationStatus.InReview,
            "documents arriving means a reviewer can look again — not that the answer is yes");
        resumed.Status.Should().NotBe(AuthorizationStatus.Approved);
        resumed.RFAIResponseDate.Should().NotBeNull();
        resumed.SlaResumedAt.Should().NotBeNull();
        resumed.StatusHistory.Should().Contain(h =>
            h.Status == AuthorizationStatus.InReview
            && h.Reason!.Contains("Additional information received"));
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_AReplayedAnnouncementDoesNotResumeReviewTwice()
    {
        var (harness, auth, rfai) = await PendedWithRequestAsync();

        var authorizations = new InMemoryAuthorizationRepository();
        await authorizations.CreateAsync(auth);

        await SubmitAsync(harness, rfai.TrackingId, AuthNumber);
        var announcement = harness.Kafka.DocsReceivedMessages.Single();
        var consumer = harness.ResumeConsumer(authorizations);

        await consumer.ProcessMessageAsync(announcement);
        await consumer.ProcessMessageAsync(announcement);

        var resumed = (await authorizations.GetByAuthorizationNumberAsync(AuthNumber))!;
        resumed.Status.Should().Be(AuthorizationStatus.InReview);
        resumed.StatusHistory.Count(h =>
            h.Reason == "Additional information received; returned to review.")
            .Should().Be(1, "an event replay records the transition once");
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_ADecidedAuthorizationIsNotReopenedByLateDocuments()
    {
        var (harness, auth, rfai) = await PendedWithRequestAsync();

        var authorizations = new InMemoryAuthorizationRepository();
        auth.Status = AuthorizationStatus.Denied;
        auth.ReviewDecision = "A3";
        await authorizations.CreateAsync(auth);

        await SubmitAsync(harness, rfai.TrackingId, AuthNumber);
        var announcement = harness.Kafka.DocsReceivedMessages.Single();
        await harness.ResumeConsumer(authorizations).ProcessMessageAsync(announcement);

        (await authorizations.GetByAuthorizationNumberAsync(AuthNumber))!
            .Status.Should().Be(AuthorizationStatus.Denied,
                "a decided authorization is not reopened by documents arriving late");
    }

    // ── $inquire reflects the lifecycle ──────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_InquiryReportsPendedForInformationBeforeAndInReviewAfter()
    {
        // PAS-04's $inquire must not be left permanently reporting A4 once the
        // requested data has been received.
        var (harness, auth, rfai) = await PendedWithRequestAsync();

        var authorizations = new InMemoryAuthorizationRepository();
        await authorizations.CreateAsync(auth);

        var builder = new FhirService.Services.PasResponseBuilder();

        var before = InquiryResponse(builder, await authorizations.GetByAuthorizationNumberAsync(AuthNumber));
        before.Disposition.Should().Be("pended-additional-information");
        before.Extension.Should().Contain(e =>
            e.Url == "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-reviewAction");

        await SubmitAsync(harness, rfai.TrackingId, AuthNumber);
        var announcement = harness.Kafka.DocsReceivedMessages.Single();
        await harness.ResumeConsumer(authorizations).ProcessMessageAsync(announcement);

        var after = InquiryResponse(builder, await authorizations.GetByAuthorizationNumberAsync(AuthNumber));
        after.Disposition.Should().Be("pending",
            "once the information is in, the authorization is back in review — not still waiting on the provider");
        after.Outcome.Should().Be(ClaimProcessingCodes.Queued);
        after.Disposition.Should().NotBe("approved");
    }

    // ── Multiple cycles ──────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_ALaterCycleDoesNotOverwriteTheFirst()
    {
        var (harness, auth, first) = await PendedWithRequestAsync();

        await SubmitAsync(harness, first.TrackingId, AuthNumber);

        var answered = (await harness.Repository.GetByIdAsync(AcceptanceContext.TenantId, first.Id))!;
        RfaiCaseLifecycle.Close(answered, "reviewer-1", "First cycle satisfied.", DateTime.UtcNow);
        await harness.Repository.UpdateAsync(answered);

        // A second question, later in the same authorization's life.
        auth.RFAIIssued = false;
        await harness.Coordinator().EnsureRequestForDecisionAsync(
            auth,
            [new RequestedInformationItem { Code = "OZ", Description = "Operative report" }],
            null, "CTRL-0002");

        var cycles = await harness.Repository.GetByAuthNumberAsync(AcceptanceContext.TenantId, AuthNumber);
        cycles.Should().HaveCount(2);

        var preserved = cycles.Single(c => c.Id == first.Id);
        preserved.Sequence.Should().Be(1);
        preserved.Status.Should().Be(RfaiStatus.Closed);
        preserved.ReceivedAttachments.Should().ContainSingle(
            "the first cycle's evidence survives a later request");
        preserved.ClosedAt.Should().NotBeNull();

        var second = cycles.Single(c => c.Id != first.Id);
        second.Sequence.Should().Be(2);
        second.Status.Should().Be(RfaiStatus.Open);
        second.TrackingId.Should().NotBe(preserved.TrackingId);
    }

    // ── Security ─────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_SubmitAttachmentRequiresAWriteScope()
    {
        // A read scope is not enough to put documents into a payer's record, and
        // an operation path that names no resource type must not fall through the
        // scope check unenforced.
        (await ScopeCheckStatusAsync("system/Task.read")).Should().Be(403);
        (await ScopeCheckStatusAsync("patient/*.read")).Should().Be(403);
        (await ScopeCheckStatusAsync("system/Task.write")).Should().Be(200);
        (await ScopeCheckStatusAsync("system/*.write")).Should().Be(200);
        (await ScopeCheckStatusAsync("user/Task.write")).Should().Be(200);

        // …and a PATIENT-context token is not an acceptable caller however it is
        // scoped: this is a provider/system transaction with a payer.
        (await ScopeCheckStatusAsync("patient/Task.write")).Should().Be(403);
        (await ScopeCheckStatusAsync("patient/*.write")).Should().Be(403);
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public async Task PAS07_Replace_SubmitAttachmentRequiresAuthentication()
    {
        (await ScopeCheckStatusAsync(scope: null, authenticated: false)).Should().Be(401);
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public void PAS07_Replace_TenantIsNeverTakenFromTheSubmissionPayload()
    {
        // Structural: the Parameters reader exposes no tenant accessor at all, so
        // there is nothing for a caller to set. Tenant reaches the submission
        // service as an argument from the authenticated context.
        typeof(CdexSubmitAttachmentParameters)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Should().NotContain(m => m.Name.Contains("Tenant", StringComparison.OrdinalIgnoreCase));

        typeof(ICdexAttachmentSubmissionService)
            .GetMethod(nameof(ICdexAttachmentSubmissionService.SubmitAsync))!
            .GetParameters().Should().Contain(p => p.Name == "tenantId");
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public void PAS07_Replace_EveryStoreLookupIsTenantScoped()
    {
        // Structural guard: a lookup that does not name a tenant cannot exist on
        // the seam, so a future call site cannot forget one.
        typeof(ICdexAdditionalInformationStore)
            .GetMethods()
            .Should().OnlyContain(m => m.GetParameters().Any(p => p.Name == "tenantId"));
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public void PAS07_Replace_TheCapabilityStatementAdvertisesWhatIsActuallyServed()
    {
        var metadata = new MetadataController(AcceptanceContext.DemoConfig()).WithTenant();
        var cs = (metadata.GetCapabilityStatement() as OkObjectResult)!
            .Value.Should().BeOfType<CapabilityStatement>().Subject;

        var rest = cs.Rest.Single();

        rest.Operation.Should().Contain(o =>
            o.Name == CdexCanonicalUrls.SubmitAttachmentOperationName
            && o.Definition == CdexCanonicalUrls.SubmitAttachmentOperation);

        var task = rest.Resource.Single(r => r.Type == "Task");
        task.SupportedProfile.Should().Contain(CdexCanonicalUrls.TaskAttachmentRequestProfile);
        task.SearchParam.Select(p => p.Name).Should().Contain(["code", "identifier", "focus"]);

        // The route the CapabilityStatement claims is the route the controller
        // actually serves.
        typeof(CdexController)
            .GetMethod(nameof(CdexController.SubmitAttachment))!
            .GetCustomAttributes(typeof(HttpPostAttribute), false)
            .Cast<HttpPostAttribute>()
            .Should().ContainSingle(a => a.Template == CdexCanonicalUrls.SubmitAttachmentRoute);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ClaimResponse InquiryResponse(
        FhirService.Services.PasResponseBuilder builder, Authorization? authorization)
    {
        var record = new FhirService.Services.PriorAuthorizationRecord
        {
            TenantId = authorization!.TenantId,
            Id = authorization.Id,
            AuthorizationNumber = authorization.AuthorizationNumber,
            MemberId = authorization.MemberId,
            RequestingProviderNpi = authorization.RequestingProviderNPI,
            Status = Enum.Parse<FhirService.Services.PriorAuthorizationStatus>(
                authorization.Status.ToString()),
            ReviewDecision = authorization.ReviewDecision,
            PendReason = authorization.PendReason,
            LastUpdatedDate = authorization.LastUpdatedDate,
        };

        return (ClaimResponse)builder.BuildInquiryResponse(record).Entry[0].Resource;
    }

    private static async Task<FhirTask> ReadTaskAsync(AdditionalInformationHarness harness, string id)
    {
        var result = await ReadTaskResultAsync(harness, id);
        return (result as OkObjectResult)!.Value.Should().BeOfType<FhirTask>().Subject;
    }

    private static async Task<IActionResult> ReadTaskResultAsync(
        AdditionalInformationHarness harness, string id, string tenant = AcceptanceContext.TenantId)
        => await TaskControllerFor(harness, tenant).Read(id, CancellationToken.None);

    private static async Task<Bundle> SearchTasksAsync(
        AdditionalInformationHarness harness,
        AppealTaskSearchParams search,
        string tenant = AcceptanceContext.TenantId)
    {
        var result = await TaskControllerFor(harness, tenant).Search(search, CancellationToken.None);
        return (result as OkObjectResult)!.Value.Should().BeOfType<Bundle>().Subject;
    }

    private static TaskController TaskControllerFor(
        AdditionalInformationHarness harness, string tenant)
    {
        var controller = new TaskController(
            new FhirService.Services.MockFhirAppealAdapter(),
            new FhirService.Services.FhirAppealMapper(),
            new FhirService.Services.FhirBundleBuilder(AcceptanceContext.DemoConfig()),
            harness.Store,
            harness.TaskMapper,
            AcceptanceContext.Logger<TaskController>());

        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantId"] = tenant;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static Task<CdexSubmissionResult> SubmitAsync(
        AdditionalInformationHarness harness,
        string trackingId,
        string? attachTo = AuthNumber,
        string tenant = AcceptanceContext.TenantId,
        string providerNpi = ProviderNpi,
        byte[]? content = null,
        string? title = "Discharge summary")
        => harness.Submissions().SubmitAsync(
            BuildParameters(trackingId, attachTo, providerNpi, [content ?? Pdf], title: title),
            tenant, Caller);

    private static async Task<IActionResult> SubmitViaControllerAsync(
        AdditionalInformationHarness harness,
        string trackingId,
        string attachTo,
        string providerNpi = ProviderNpi,
        string tenant = AcceptanceContext.TenantId)
    {
        var controller = new CdexController(
            harness.Submissions(), AcceptanceContext.Logger<CdexController>());

        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantId"] = tenant;
        httpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity([new System.Security.Claims.Claim("sub", Caller)], "test"));
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return await controller.SubmitAttachment(
            BuildParameters(trackingId, attachTo, providerNpi, [Pdf]));
    }

    private static Parameters BuildParameters(
        string? trackingId,
        string? attachTo,
        string providerNpi,
        IReadOnlyList<byte[]> contents,
        string contentType = "application/pdf",
        string? title = "Discharge summary")
    {
        var parameters = new Parameters();

        if (trackingId is not null)
            parameters.Add(CdexSubmitAttachmentParameters.TrackingIdParameter, new FhirString(trackingId));

        if (attachTo is not null)
        {
            parameters.Add(CdexSubmitAttachmentParameters.AttachToParameter,
                new Identifier(CdexCanonicalUrls.AuthorizationNumberSystem, attachTo));
        }

        parameters.Add(CdexSubmitAttachmentParameters.OrganizationParameter,
            new ResourceReference { Display = "CHO Payer" });

        parameters.Add(CdexSubmitAttachmentParameters.ProviderParameter, new ResourceReference
        {
            Type = "Organization",
            Identifier = new Identifier(CdexCanonicalUrls.UsNpi, providerNpi),
        });

        foreach (var content in contents)
            AddAttachment(parameters, content, contentType, title);

        return parameters;
    }

    private static void AddAttachment(
        Parameters parameters, byte[] content, string contentType, string? title = "Discharge summary")
    {
        var attachment = new Parameters.ParameterComponent
        {
            Name = CdexSubmitAttachmentParameters.AttachmentParameter,
        };

        attachment.Part.Add(new Parameters.ParameterComponent
        {
            Name = CdexSubmitAttachmentParameters.CodePart,
            Value = new CodeableConcept(CdexCanonicalUrls.Loinc, "18842-5", "Discharge summary"),
        });

        attachment.Part.Add(new Parameters.ParameterComponent
        {
            Name = CdexSubmitAttachmentParameters.ContentPart,
            Value = new Attachment
            {
                ContentType = contentType,
                Data = content,
                Title = title,
            },
        });

        parameters.Parameter.Add(attachment);
    }

    /// <summary>
    /// Drives the REAL SMART scope middleware over the operation route and
    /// reports the status it produced. 200 means the request reached the terminal
    /// delegate — i.e. the middleware allowed it.
    /// </summary>
    private static async Task<int> ScopeCheckStatusAsync(string? scope, bool authenticated = true)
    {
        var reached = false;

        var middleware = new FhirService.Middleware.SmartScopeEnforcementMiddleware(
            _ => { reached = true; return System.Threading.Tasks.Task.CompletedTask; },
            AcceptanceContext.Logger<FhirService.Middleware.SmartScopeEnforcementMiddleware>());

        var context = new DefaultHttpContext();
        context.Request.Path = $"/fhir/r4/{CdexCanonicalUrls.SubmitAttachmentRoute}";
        context.Request.Method = HttpMethods.Post;
        context.Response.Body = new MemoryStream();

        if (authenticated)
        {
            var claims = new List<System.Security.Claims.Claim>
            {
                new("sub", Caller),
            };
            if (scope is not null) claims.Add(new System.Security.Claims.Claim("scope", scope));
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }

        await middleware.InvokeAsync(context);

        return reached ? 200 : context.Response.StatusCode;
    }
}
