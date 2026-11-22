using FluentAssertions;
using FhirService.Models;
using FhirService.Services;
using Hl7.Fhir.Model;

namespace CloudHealthOffice.FhirService.Tests.Services;

/// <summary>
/// The PAS data a response hands back (<see cref="PasResponseDetail"/>): the
/// submitter's own identifiers, trace numbers and dates, and what CHO decided
/// about each line. Each element comes from the request or the stored
/// decision; none is defaulted.
/// </summary>
public class PasResponseDetailTests
{
    private const string Sd = "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/";
    private const string Base = "http://example.org/fhir";

    private readonly PasResponseBuilder _builder = new();

    [Fact]
    public void Approved_EchoesTheSubmittersIdentifiersAndTransmissionIdentifiersTurnedRound()
    {
        var (claim, request) = Request();
        var response = ClaimResponseOf(_builder.BuildApprovedResponse(
            claim, Approved(), Services(), request, Base, humanReviewed: false));

        var trace = response.Identifier.Single(i => i.System == "urn:trnorg:9012345678");
        trace.Value.Should().Be("111099");
        trace.Extension.Select(e => e.Url).Should().BeEquivalentTo(
            Sd + "extension-identifierJurisdiction", Sd + "extension-identifierSubDepartment");
        response.Identifier.Should().Contain(i =>
            i.System == PasResponseDetail.ChoAuthorizationSystem && i.Value == "PAS-1");

        var transmission = response.Extension.Single(e => e.Url == Sd + "extension-TransmissionIdentifiers");
        Part(transmission, "applicationSenderCode").Should().Be("PAYER-APP");
        Part(transmission, "applicationReceiverCode").Should().Be("PROVIDER-APP");
        Part(transmission, "interchangeSenderID").Should().Be("PAYER-ISA");
        Part(transmission, "interchangeReceiverID").Should().Be("PROVIDER-ISA");
    }

    [Fact]
    public void AutomatedApproval_SaysNoPersonReviewedIt_AndStatesTheDecisionOnTheHeader()
    {
        var (claim, request) = Request();
        var response = ClaimResponseOf(_builder.BuildApprovedResponse(
            claim, Approved(), Services(), request, Base, humanReviewed: false));

        var reviewer = response.Extension.Single(e => e.Url == Sd + "extension-claimResponseReviewer");
        ((FhirBoolean)reviewer.Extension.Single(e => e.Url == "wasHumanReviewedFlag").Value).Value.Should().BeFalse();

        var header = response.Adjudication.Single();
        header.Category.Coding.Single().Code.Should().Be("submitted");
        ActionCode(header).Should().Be("A1");

        var note = response.ProcessNote.Single();
        note.Number.Should().Be(1);
        note.Text.Should().Contain("PAS-1").And.Contain("2026-12-01").And.Contain("2026-12-31");
        response.Item.Should().OnlyContain(i => i.NoteNumber.SequenceEqual(new int?[] { 1 }));
    }

    [Fact]
    public void CertifiedLine_CarriesItsTraceDatesNumberProviderDiagnosisAndAuthorizedDetail()
    {
        var (claim, request) = Request();
        var response = ClaimResponseOf(_builder.BuildApprovedResponse(
            claim, Approved(), Services(), request, Base, humanReviewed: false));
        var line = response.Item.Single(i => i.ItemSequence == 1);

        Ext<Identifier>(line, "extension-itemTraceNumber").Value.Should().Be("1122334");
        Ext<FhirDateTime>(line, "extension-itemRequestedServiceDate").Value.Should().Be("2026-12-05");
        Ext<FhirString>(line, "extension-authorizationNumber").Value.Should().Be("PAS-1");
        Ext<Date>(line, "extension-itemPreAuthIssueDate").Value.Should().NotBeNullOrEmpty();
        Ext<Period>(line, "extension-itemPreAuthPeriod").End.Should().Be("2026-12-31");
        Ext<CodeableConcept>(line, "extension-communicatedDiagnosis").Coding.Single().Code.Should().Be("G89.4");
        Ext<Period>(line, "extension-admissionDates").Start.Should().Be("2026-12-05");
        Ext<Date>(line, "extension-dischargeDate").Value.Should().Be("2026-12-07");

        // The line's care team member is a PractitionerRole: PAS takes the practitioner.
        var provider = line.Extension.Single(e => e.Url == Sd + "extension-itemAuthorizedProvider");
        ((ResourceReference)provider.Extension.Single(e => e.Url == "provider").Value).Reference
            .Should().Be("Practitioner/doc-1");

        var detail = line.Extension.Single(e => e.Url == Sd + "extension-itemAuthorizedDetail");
        ((CodeableConcept)detail.Extension.Single(e => e.Url == "productOrServiceCode").Value).Coding.Single().Code
            .Should().Be("72148");
        ((Quantity)detail.Extension.Single(e => e.Url == "quantity").Value).Value.Should().Be(2);
        ((Money)detail.Extension.Single(e => e.Url == "unitPrice").Value).Value.Should().Be(450);

        // The whole request's authorized provider: the claim-scoped care team member.
        response.Extension.Should().Contain(e => e.Url == Sd + "extension-itemAuthorizedProvider");
        // And the practitioner it names travels in the response Bundle.
    }

    [Fact]
    public void AuthorizedProvider_IsIncludedInTheResponseBundle()
    {
        var (claim, request) = Request();
        var bundle = _builder.BuildApprovedResponse(claim, Approved(), Services(), request, Base, humanReviewed: false);
        bundle.Entry.Select(e => e.Resource).OfType<Practitioner>().Should().Contain(p => p.Id == "doc-1");
    }

    [Fact]
    public void DeniedLine_CarriesTheAdministrationReferenceNumber_NotAnAuthorization()
    {
        var (claim, request) = Request();
        var response = ClaimResponseOf(_builder.BuildDeniedResponse(claim, new PasDecisionResult
        {
            HasDecision = true, Decision = "denied", AuthorizationNumber = "PAS-2",
            DenialReasonCode = "NOTCOVERED", DenialReason = "Not a covered benefit.",
        }, Services(), request, Base, humanReviewed: false));
        var line = response.Item.Single(i => i.ItemSequence == 1);

        Ext<FhirString>(line, "extension-administrationReferenceNumber").Value.Should().Be("PAS-2");
        line.Extension.Should().NotContain(e => e.Url == Sd + "extension-authorizationNumber"
                                                || e.Url == Sd + "extension-itemAuthorizedDetail"
                                                || e.Url == Sd + "extension-itemPreAuthPeriod");
        ActionCode(response.Adjudication.Single()).Should().Be("A3");
        line.NoteNumber.Should().Equal(1);
    }

    [Fact]
    public void Pended_SaysWhy_AndStatesNoReviewerOrLineAuthorization()
    {
        var (claim, request) = Request();
        var response = ClaimResponseOf(_builder.BuildPendedResponse(
            claim, "PAS-3", Services(), request, Base, pendReason: "Pended for clinical review."));

        response.ProcessNote.Single().Text.Should().Be("Pended for clinical review.");
        response.Extension.Should().NotContain(e => e.Url == Sd + "extension-claimResponseReviewer");
        var line = response.Item.Single(i => i.ItemSequence == 1);
        line.Extension.Should().Contain(e => e.Url == Sd + "extension-itemTraceNumber");
        line.Extension.Should().NotContain(e => e.Url == Sd + "extension-authorizationNumber"
                                                || e.Url == Sd + "extension-itemAuthorizedProvider");
    }

    [Theory]
    [InlineData("automated:AutoApproveList;lob=Medicaid;state=TX", false)]
    [InlineData("benefit-exclusion", false)]
    [InlineData(null, true)]
    [InlineData("import", null)]
    public void Inquiry_SaysWhetherAPersonMadeTheStoredDecision(string? source, bool? human)
    {
        var (claim, request) = Request(inquiry: true);
        var record = new PriorAuthorizationRecord
        {
            AuthorizationNumber = "PAS-4", Status = PriorAuthorizationStatus.Approved, ReviewDecision = "A1",
            ReviewedDate = new DateTime(2026, 11, 20), ApprovedServiceDateFrom = new DateTime(2026, 12, 1),
            ApprovedServiceDateTo = new DateTime(2026, 12, 31), RequestedServices = Services(),
            StatusHistory =
            [
                new() { Status = PriorAuthorizationStatus.Submitted },
                new() { Status = PriorAuthorizationStatus.Approved, Source = source },
            ],
        };
        var response = ClaimResponseOf(_builder.BuildInquiryResponse(record, request, Base));

        var reviewer = response.Extension.SingleOrDefault(e => e.Url == Sd + "extension-claimResponseReviewer");
        if (human is null)
        {
            reviewer.Should().BeNull();
        }
        else
        {
            ((FhirBoolean)reviewer!.Extension.Single().Value).Value.Should().Be(human);
        }

        Ext<Date>(response.Item.Single(i => i.ItemSequence == 1), "extension-itemPreAuthIssueDate")
            .Value.Should().Be("2026-11-20");
        response.Identifier.Count(i => i.System == PasResponseDetail.ChoAuthorizationSystem).Should().Be(1);
    }

    [Fact]
    public void ARequestWithoutTheData_GetsNoneOfIt()
    {
        var claim = new Claim
        {
            Id = "bare", Patient = new ResourceReference("Patient/p"),
            Item = [new Claim.ItemComponent { Sequence = 1 }],
        };
        var response = ClaimResponseOf(_builder.BuildApprovedResponse(claim, Approved(), Services(), humanReviewed: false));
        var line = response.Item.Single();

        line.Extension.Should().NotContain(e => e.Url == Sd + "extension-itemTraceNumber"
                                                || e.Url == Sd + "extension-itemRequestedServiceDate"
                                                || e.Url == Sd + "extension-communicatedDiagnosis");
        response.Extension.Should().NotContain(e => e.Url == Sd + "extension-TransmissionIdentifiers");
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static PasDecisionResult Approved() => new()
    {
        HasDecision = true, Decision = "approved", AuthorizationNumber = "PAS-1",
        EffectiveFrom = new DateTime(2026, 12, 1), EffectiveTo = new DateTime(2026, 12, 31),
    };

    private static List<PriorAuthorizationService> Services() =>
    [
        new() { LineNumber = 1, ProcedureCode = "72148", RequestedUnits = 2 },
    ];

    private static (Claim Claim, Bundle Request) Request(bool inquiry = false)
    {
        var claim = new Claim
        {
            Id = inquiry ? "inquiry-1" : "claim-1",
            Patient = new ResourceReference("Patient/p"),
            Provider = new ResourceReference("Organization/clinic"),
            Identifier =
            [
                new Identifier("urn:trnorg:9012345678", "111099")
                {
                    Extension =
                    [
                        new Extension(Sd + "extension-identifierJurisdiction",
                            new CodeableConcept("https://www.usps.com/", "TX")),
                        new Extension(Sd + "extension-identifierSubDepartment", new FhirString("223412")),
                    ],
                },
            ],
            Extension =
            [
                new Extension
                {
                    Url = Sd + "extension-TransmissionIdentifiers",
                    Extension =
                    [
                        new Extension("applicationSenderCode", new FhirString("PROVIDER-APP")),
                        new Extension("applicationReceiverCode", new FhirString("PAYER-APP")),
                        new Extension("interchangeSenderID", new FhirString("PROVIDER-ISA")),
                        new Extension("interchangeReceiverID", new FhirString("PAYER-ISA")),
                    ],
                },
            ],
            CareTeam =
            [
                new Claim.CareTeamComponent
                {
                    Sequence = 1, Provider = new ResourceReference("PractitionerRole/role-1"),
                    Role = new CodeableConcept("http://terminology.hl7.org/CodeSystem/claimcareteamrole", "primary"),
                    Extension = [new Extension(Sd + "extension-careTeamClaimScope", new FhirBoolean(true))],
                },
            ],
            Diagnosis =
            [
                new Claim.DiagnosisComponent
                {
                    Sequence = 1, Diagnosis = new CodeableConcept("http://hl7.org/fhir/sid/icd-10-cm", "G89.4"),
                },
            ],
            SupportingInfo =
            [
                new Claim.SupportingInformationComponent
                {
                    Sequence = 4,
                    Category = new CodeableConcept("http://hl7.org/fhir/us/davinci-pas/CodeSystem/PASTempCodes", "admissionDates"),
                    Timing = new Period { Start = "2026-12-05", End = "2026-12-07" },
                },
                new Claim.SupportingInformationComponent
                {
                    Sequence = 5,
                    Category = new CodeableConcept("http://hl7.org/fhir/us/davinci-pas/CodeSystem/PASTempCodes", "dischargeDates"),
                    Timing = new Date("2026-12-07"),
                },
            ],
            Item =
            [
                new Claim.ItemComponent
                {
                    Sequence = 1, CareTeamSequence = [1], DiagnosisSequence = [1],
                    ProductOrService = new CodeableConcept("http://www.ama-assn.org/go/cpt", "72148"),
                    Serviced = new Date("2026-12-05"),
                    Quantity = new Quantity { Value = 2 },
                    UnitPrice = new Money { Value = 450, Currency = Money.Currencies.USD },
                    Extension =
                    [
                        new Extension(Sd + "extension-itemTraceNumber",
                            new Identifier("http://example.org/ITEM_TRACE_NUMBER", "1122334")),
                    ],
                },
            ],
        };

        var request = new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Entry =
            [
                new() { FullUrl = $"{Base}/Claim/{claim.Id}", Resource = claim },
                new()
                {
                    FullUrl = $"{Base}/PractitionerRole/role-1",
                    Resource = new PractitionerRole
                    {
                        Id = "role-1", Practitioner = new ResourceReference("Practitioner/doc-1"),
                        Organization = new ResourceReference("Organization/clinic"),
                    },
                },
                new() { FullUrl = $"{Base}/Practitioner/doc-1", Resource = new Practitioner { Id = "doc-1" } },
                new() { FullUrl = $"{Base}/Organization/clinic", Resource = new Organization { Id = "clinic" } },
                new() { FullUrl = $"{Base}/Patient/p", Resource = new Patient { Id = "p" } },
            ],
        };
        return (claim, request);
    }

    private static ClaimResponse ClaimResponseOf(Bundle bundle)
        => bundle.Entry.Select(e => e.Resource).OfType<ClaimResponse>().Single();

    private static T Ext<T>(ClaimResponse.ItemComponent item, string name) where T : DataType
        => (T)item.Extension.Single(e => e.Url == Sd + name).Value;

    private static string? Part(Extension extension, string name)
        => (extension.Extension.Single(e => e.Url == name).Value as FhirString)?.Value;

    private static string? ActionCode(ClaimResponse.AdjudicationComponent adjudication)
        => ((CodeableConcept)adjudication.Extension.Single(e => e.Url == Sd + "extension-reviewAction")
            .Extension.Single(e => e.Url == Sd + "extension-reviewActionCode").Value).Coding.Single().Code;
}
