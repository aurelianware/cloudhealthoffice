using FluentAssertions;
using FhirService.Services;
using FhirService.Services.Cdex;
using FhirService.Services.Pas;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using FhirTask = Hl7.Fhir.Model.Task;

namespace CloudHealthOffice.FhirService.Tests.Services;

/// <summary>
/// PAS-07: what a pended PAS request asks the provider for, and how the PAS
/// response carries that ask (Task, CommunicationRequest) from the CDex
/// request rfai-service holds.
/// </summary>
public class PasAdditionalInformationTests
{
    private const string ServiceTypeSystem = "https://codesystem.x12.org/005010/1365";
    private const string MriForm = "https://cloudhealthoffice.com/fhir/Questionnaire/pa-imaging-mri";
    private const string Base = "http://example.org/fhir";

    private static PasAdditionalInformationOptions Options(params string[] questionnaires) => new()
    {
        Requirements =
        [
            new PasDocumentationRequirement
            {
                ServiceCodes = ["2"],
                ServiceCodeSystem = ServiceTypeSystem,
                Attachments =
                [
                    new PasRequestedAttachment
                    {
                        LoincCode = "28570-0", Description = "Procedure note", PwkCode = "09", ContentModifier = "18804-5",
                    },
                ],
                Questionnaires = questionnaires.ToList(),
            },
        ],
    };

    private static Claim Claim() => new()
    {
        Diagnosis =
        [
            new Claim.DiagnosisComponent { Sequence = 1, Diagnosis = new CodeableConcept("http://hl7.org/fhir/sid/icd-10-cm", "M54.5") },
            new Claim.DiagnosisComponent { Sequence = 2, Diagnosis = new CodeableConcept("http://hl7.org/fhir/sid/icd-10-cm", "G89.4") },
        ],
        Item =
        [
            new Claim.ItemComponent
            {
                Sequence = 1, DiagnosisSequence = [2],
                ProductOrService = new CodeableConcept(ServiceTypeSystem, "2"),
            },
            new Claim.ItemComponent
            {
                Sequence = 2, DiagnosisSequence = [1],
                ProductOrService = new CodeableConcept(ServiceTypeSystem, "3"),
            },
        ],
    };

    // ── What is asked for ─────────────────────────────────────────────

    [Fact]
    public void CoveredLine_IsAskedForItsDocumentsAndForms_OthersForNothing()
    {
        var items = PasAdditionalInformationPlanner.Plan(Claim(), Options(MriForm + "|1.0.0"), []);

        items.Should().HaveCount(2);
        var document = items[0];
        document.LoincCode.Should().Be("28570-0");
        document.Code.Should().Be("09");
        document.ContentModifier.Should().Be("18804-5");
        document.ServiceLineNumber.Should().Be(1);
        document.ServiceLineProcedureCode.Should().Be("2");
        document.DiagnosisCode.Should().Be("G89.4");

        var form = items[1];
        form.Questionnaire.Should().Be(MriForm + "|1.0.0");
        form.ServiceLineNumber.Should().Be(1);
        form.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void FormTheRequestAnswered_IsNotAskedForAgain_WhateverItsVersion()
    {
        var items = PasAdditionalInformationPlanner.Plan(Claim(), Options(MriForm + "|1.0.0"), [MriForm + "|0.9"]);

        items.Should().ContainSingle().Which.LoincCode.Should().Be("28570-0");
    }

    [Fact]
    public void FormsDtrServesForTheLine_AreAskedForToo()
    {
        var items = PasAdditionalInformationPlanner.Plan(Claim(), Options(), [],
            new Dictionary<int, IReadOnlyList<string>> { [1] = [MriForm + "|1.0.0"], [2] = ["http://example.org/Questionnaire/other"] });

        items.Select(i => i.Questionnaire).Should().Equal(null, MriForm + "|1.0.0");
    }

    [Fact]
    public void NoRequirements_AskForNothing()
        => PasAdditionalInformationPlanner.Plan(Claim(), new PasAdditionalInformationOptions(), []).Should().BeEmpty();

    [Fact]
    public void CodeInAnotherSystem_IsNotCovered()
    {
        var claim = Claim();
        claim.Item[0].ProductOrService = new CodeableConcept("http://www.ama-assn.org/go/cpt", "2");

        PasAdditionalInformationPlanner.Plan(claim, Options(), []).Should().BeEmpty();
    }

    [Fact]
    public void DueDate_IsTheResponseWindowAfterTheDecision()
        => PasAdditionalInformationPlanner.DueDate(new DateTime(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc), new PasAdditionalInformationOptions())
            .Should().Be(new DateTime(2026, 12, 6, 0, 0, 0, DateTimeKind.Utc));

    // ── How the response carries it ───────────────────────────────────

    private static CdexAdditionalInformationRequest Case(CdexAdditionalInformationStatus status = CdexAdditionalInformationStatus.Open) => new()
    {
        TenantId = "tenant-a",
        Id = "case-1",
        AuthNumber = "PAS-1",
        TrackingId = "RFAI-TRK-1",
        Status = status,
        RequestingProviderNpi = "1234567893",
        CreatedAt = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc),
        DueDate = new DateTime(2026, 12, 6, 10, 0, 0, DateTimeKind.Utc),
        RequestedItems =
        [
            new CdexRequestedItem
            {
                Code = "09", LoincCode = "28570-0", Description = "Procedure note", ServiceLineNumber = 1,
                ContentModifier = "18804-5", DiagnosisCode = "G89.4",
            },
            new CdexRequestedItem
            {
                Description = "Complete the documentation questionnaire for this service.",
                Questionnaire = MriForm + "|1.0.0", ServiceLineNumber = 1,
            },
        ],
    };

    private static Bundle PendedResponse()
    {
        var claim = new Claim
        {
            Id = "claim-1",
            Patient = new ResourceReference("Patient/p"),
            Insurer = new ResourceReference("Organization/payer"),
            Provider = new ResourceReference("Organization/clinic"),
            Item = [new Claim.ItemComponent { Sequence = 1, ProductOrService = new CodeableConcept(ServiceTypeSystem, "2") }],
        };
        var request = new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Entry =
            [
                new Bundle.EntryComponent { FullUrl = $"{Base}/Claim/claim-1", Resource = claim },
                new Bundle.EntryComponent { FullUrl = $"{Base}/Patient/p", Resource = new Patient { Id = "p" } },
                new Bundle.EntryComponent
                {
                    FullUrl = $"{Base}/Organization/payer",
                    Resource = new Organization { Id = "payer", Identifier = [new Identifier("urn:oid:2.16.840.1.113883.4.4", "PAYER-1")] },
                },
                new Bundle.EntryComponent
                {
                    FullUrl = $"{Base}/Organization/clinic",
                    Resource = new Organization { Id = "clinic", Identifier = [new Identifier("http://hl7.org/fhir/sid/us-npi", "1234567893")] },
                },
            ],
        };

        return new PasResponseBuilder().BuildPendedResponse(claim, "PAS-1", [], request, Base, "Pended");
    }

    private static readonly string SubmitAttachment = $"{Base}/$submit-attachment";

    [Fact]
    public void Pend_CarriesATaskPerKindOfAsk_WithTheTrackingIdTheProviderQuotes()
    {
        var bundle = PendedResponse();

        PasAdditionalInformationResources.Attach(bundle, Case(), SubmitAttachment, withCommunicationRequests: true);

        var tasks = bundle.Entry.Select(e => e.Resource).OfType<FhirTask>().ToList();
        tasks.Select(t => t.Code.Coding.Single().Code).Should().Equal("attachment-request-code", "attachment-request-questionnaire");
        foreach (var task in tasks)
        {
            task.Meta.Profile.Should().Equal(PasAdditionalInformationResources.TaskProfile);
            task.Identifier.Single().Value.Should().Be("RFAI-TRK-1");
            task.Status.Should().Be(FhirTask.TaskStatus.Requested);
            task.StatusReason.Text.Should().NotBeNullOrWhiteSpace();
            task.Intent.Should().Be(FhirTask.TaskIntent.Order);
            task.For.Reference.Should().Be("Patient/p");
            task.Requester.Reference.Should().Be("Organization/payer");
            task.Requester.Identifier.Value.Should().Be("PAYER-1");
            task.Owner.Reference.Should().Be("Organization/clinic");
            task.Owner.Identifier.Value.Should().Be("1234567893");
            task.ReasonCode.Coding.Single().Code.Should().Be("priorAuthorization");
            task.ReasonReference.Should().NotBeNull();
            task.Restriction.Period.End.Should().StartWith("2026-12-06");
            var payerUrl = task.Input.First();
            payerUrl.Type.Coding.Single().Code.Should().Be("payer-url");
            ((FhirUrl)payerUrl.Value).Value.Should().Be(SubmitAttachment);
        }

        var attachments = tasks[0].Input.Single(i => i.Type.Coding.Single().Code == "attachments-needed");
        ((CodeableConcept)attachments.Value).Coding.Single().Code.Should().Be("28570-0");
        ((PositiveInt)attachments.Extension.Single(e => e.Url == PasAdditionalInformationResources.ServiceLineNumberUrl).Value).Value.Should().Be(1);
        ((CodeableConcept)attachments.Extension.Single(e => e.Url == PasAdditionalInformationResources.ContentModifierUrl).Value)
            .Coding.Single().Code.Should().Be("18804-5");

        var form = tasks[1].Input.Single(i => i.Type.Coding.Single().Code == "questionnaire-context");
        ((FhirString)form.Value).Value.Should().Be(MriForm + "|1.0.0");
        form.Extension.Should().ContainSingle(e => e.Url == PasAdditionalInformationResources.ServiceLineNumberUrl);
    }

    [Fact]
    public void Pend_CarriesACommunicationRequestPerDocument_ReferencedFromTheClaimResponse()
    {
        var bundle = PendedResponse();

        PasAdditionalInformationResources.Attach(bundle, Case(), SubmitAttachment, withCommunicationRequests: true);

        var claimResponse = (ClaimResponse)bundle.Entry[0].Resource;
        var communicationRequest = bundle.Entry.Select(e => e.Resource).OfType<CommunicationRequest>().Single();
        claimResponse.CommunicationRequest.Single().Reference
            .Should().Be(bundle.Entry.Single(e => e.Resource == communicationRequest).FullUrl);

        communicationRequest.Status.Should().Be(RequestStatus.Active);
        communicationRequest.Identifier.Single().Value.Should().Be("RFAI-TRK-1-1");
        communicationRequest.Category.Single().Coding.Single().Code.Should().Be("09");
        communicationRequest.Medium.Single().Coding.Single().Code.Should().Be("CDEX");
        communicationRequest.Requester.Reference.Should().Be("Organization/payer");
        communicationRequest.Sender.Reference.Should().Be("Organization/clinic");
        communicationRequest.Recipient.Single().Reference.Should().Be("Organization/clinic");
        ((PositiveInt)communicationRequest.Extension.Single().Value).Value.Should().Be(1);
        var payload = communicationRequest.Payload.Single();
        ((FhirString)payload.Content).Value.Should().Be("28570-0");
        payload.Extension.Select(e => e.Url).Should().BeEquivalentTo(
            PasAdditionalInformationResources.CommunicatedDiagnosisUrl, PasAdditionalInformationResources.ContentModifierUrl);
    }

    [Fact]
    public void AnsweredRequest_ReadsAsReceived()
    {
        var bundle = PendedResponse();

        PasAdditionalInformationResources.Attach(bundle, Case(CdexAdditionalInformationStatus.DocsReceived), SubmitAttachment,
            withCommunicationRequests: true);

        bundle.Entry.Select(e => e.Resource).OfType<FhirTask>().Should().OnlyContain(t => t.Status == FhirTask.TaskStatus.Received);
        bundle.Entry.Select(e => e.Resource).OfType<CommunicationRequest>().Single().Status.Should().Be(RequestStatus.Completed);
    }

    [Fact]
    public void WithoutCommunicationRequests_OnlyTheTasksAreAdded()
    {
        var bundle = PendedResponse();

        PasAdditionalInformationResources.Attach(bundle, Case(), SubmitAttachment, withCommunicationRequests: false);

        bundle.Entry.Select(e => e.Resource).OfType<CommunicationRequest>().Should().BeEmpty();
        ((ClaimResponse)bundle.Entry[0].Resource).CommunicationRequest.Should().BeEmpty();
        bundle.Entry.Select(e => e.Resource).OfType<FhirTask>().Should().HaveCount(2);
    }

    [Fact]
    public void TheResponseStillSerializesAsFhir()
    {
        var bundle = PendedResponse();
        PasAdditionalInformationResources.Attach(bundle, Case(), SubmitAttachment, withCommunicationRequests: true);

        var json = new FhirJsonSerializer().SerializeToString(bundle);
        var parsed = new FhirJsonParser().Parse<Bundle>(json);

        parsed.Entry.Select(e => e.Resource.TypeName).Should().Contain(["Task", "CommunicationRequest"]);
    }
}
