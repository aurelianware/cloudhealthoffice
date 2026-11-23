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
        // PAS allows the action code only on a line's adjudication: the
        // header's reviewAction names the decision's number.
        HeaderNumber(header).Should().Be("PAS-1");
        header.Extension.Single().Extension.Should().NotContain(e => e.Url == Sd + "extension-reviewActionCode");

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
        var quantity = (Quantity)detail.Extension.Single(e => e.Url == "quantity").Value;
        quantity.Value.Should().Be(2);
        quantity.Code.Should().Be("UN", "a bare count is stated in units, in X12 673 (prof-2, prof-3)");
        quantity.System.Should().Be(PasResponseDetail.X12QuantityUnitSystem);
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
        HeaderNumber(response.Adjudication.Single()).Should().NotBeNullOrEmpty();
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
        Ext<FhirString>(line, "extension-administrationReferenceNumber").Value.Should().Be("PAS-3");
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
    public void Modification_StatesTheAlternativeAsAnAddedLine_RestatingTheRequestLineItReplaces()
    {
        var (claim, request) = Request(inquiry: true);
        var line = claim.Item.Single();
        line.Revenue = new CodeableConcept("https://www.nubc.org/revenue-code", "0105");
        line.Modifier = [new CodeableConcept("http://www.ama-assn.org/go/cpt", "RT")];
        line.Location = new CodeableConcept("https://www.cms.gov/Medicare/Coding/place-of-service-codes/Place_of_Service_Code_Set", "11");
        line.InformationSequence = [4, 5];
        foreach (var (name, value) in new (string, DataType)[]
                 {
                     ("serviceItemRequestType", new CodeableConcept("https://codesystem.x12.org/005010/1525", "SC")),
                     ("certificationType", new CodeableConcept("https://codesystem.x12.org/005010/1322", "I")),
                     ("requestedService", new ResourceReference("ServiceRequest/sr-1")),
                     ("epsdtIndicator", new FhirBoolean(false)),
                     ("nursingHomeResidentialStatus", new CodeableConcept("https://codesystem.x12.org/005010/1345", "2")),
                     ("nursingHomeLevelOfCare", new CodeableConcept("https://codesystem.x12.org/005010/1337", "2")),
                     ("revenueUnitRateLimit", new FhirDecimal(100)),
                     ("productOrServiceCodeEnd", new CodeableConcept("http://www.ama-assn.org/go/cpt", "72149")),
                     ("infoChanged", new Code("changed")),
                 })
            line.Extension.Add(new Extension(Sd + "extension-" + name, value));

        var record = new PriorAuthorizationRecord
        {
            AuthorizationNumber = "PAS-5", Status = PriorAuthorizationStatus.Modified, ReviewDecision = "A2",
            ReviewedDate = new DateTime(2026, 11, 20), ApprovedServiceDateFrom = new DateTime(2026, 12, 1),
            ApprovedServiceDateTo = new DateTime(2026, 12, 31),
            DenialReasonCode = "PREFERRED_ALTERNATIVE", DenialReason = "The plan certifies the preferred alternative.",
            RequestedServices =
            [
                new() { LineNumber = 1, ProcedureCode = "72148", RequestedUnits = 2, ApprovedUnits = 0, ServiceStatus = "A3",
                        DenialReasonCode = "PREFERRED_ALTERNATIVE", DenialReason = "The plan certifies the preferred alternative." },
            ],
            PayerAddedServices =
            [
                new() { ForLineNumber = 1, ProcedureCode = "72141", ProductOrServiceSystem = "http://www.ama-assn.org/go/cpt",
                        ProcedureDescription = "MRI cervical spine without contrast", ApprovedUnits = 2, ServiceStatus = "A1" },
            ],
        };

        var bundle = _builder.BuildInquiryResponse(record, request, Base);
        var response = ClaimResponseOf(bundle);

        response.Outcome.Should().Be(ClaimProcessingCodes.Partial);
        ActionCode(response.Item.Single().Adjudication.First()).Should().Be("A3");
        var added = response.AddItem.Should().ContainSingle().Subject;
        added.ItemSequence.Should().Equal(1);
        added.ProductOrService.Coding.Single().Code.Should().Be("72141");
        added.Quantity.Value.Should().Be(2);
        added.Quantity.Code.Should().Be("UN", "a bare count is in X12 units");
        var adjudication = added.Adjudication.Single();
        ActionCode(adjudication).Should().Be("A1");
        HeaderNumber(adjudication).Should().Be("PAS-5");

        string Url(string name) => Sd + "extension-" + name;
        added.Extension.Select(e => e.Url).Should().Contain(new[]
        {
            Url("itemTraceNumber"), Url("serviceItemRequestType"), Url("certificationType"), Url("requestedService"),
            Url("epsdtIndicator"), Url("nursingHomeResidentialStatus"), Url("nursingHomeLevelOfCare"),
            Url("revenueUnitRateLimit"), Url("productOrServiceCodeEnd"), Url("revenueCode"),
            Url("itemPreAuthIssueDate"), Url("itemPreAuthPeriod"), Url("authorizationNumber"),
            Url("administrationReferenceNumber"), Url("admissionDates"), Url("dischargeDate"),
        });
        var infoChanged = Url("infoChanged");
        added.Extension.Should().NotContain(e => e.Url == infoChanged, "only what PAS defines on an added line");
        var codeEnd = Url("productOrServiceCodeEnd");
        ((CodeableConcept)added.Extension.Single(e => e.Url == codeEnd).Value).Coding.Single().Code
            .Should().Be("72141", "the requested range is restated as the alternative alone");
        added.Modifier.Single().Coding.Single().Code.Should().Be("RT");
        added.UnitPrice.Value.Should().Be(450);
        added.Serviced.Should().BeOfType<Date>();
        added.Location.Should().BeOfType<CodeableConcept>();

        var provider = added.Provider.Should().ContainSingle().Subject;
        provider.Reference.Should().Be("Practitioner/doc-1");
        var providerType = Url("authorizedProviderType");
        ((CodeableConcept)provider.Extension.Single(e => e.Url == providerType).Value).Coding.Single().Code
            .Should().Be("SJ");
        bundle.Entry.Should().Contain(e => e.Resource is Practitioner, "the provider it names is in the response");
    }

    [Theory]
    [InlineData("visit", null, null, "visit", null)]                       // a label is kept
    [InlineData(null, "{visit}", "http://unitsofmeasure.org", "{visit}", null)] // another system's code becomes the label
    [InlineData("days", "DA", "https://codesystem.x12.org/005010/673", "days", "DA")] // X12 673 is kept
    [InlineData(null, null, null, "Unit", "UN")]                           // nothing stated: units
    public void AQuantity_StatesItsUnitAsPasAllows(string? unit, string? code, string? system, string expectedUnit, string? expectedCode)
    {
        var quantity = PasResponseDetail.Units(2, unit, code, system);

        quantity.Unit.Should().Be(expectedUnit);
        quantity.Code.Should().Be(expectedCode);
        quantity.System.Should().Be(expectedCode is null ? null : PasResponseDetail.X12QuantityUnitSystem);
    }

    [Fact]
    public void AnUndecidedRecord_StatesNoAddedLine()
    {
        var (_, request) = Request(inquiry: true);
        var record = new PriorAuthorizationRecord
        {
            AuthorizationNumber = "PAS-6", Status = PriorAuthorizationStatus.Pended, ReviewDecision = "A4",
            RequestedServices = Services(),
            PayerAddedServices = [new() { ForLineNumber = 1, ProcedureCode = "72141", ApprovedUnits = 2, ServiceStatus = "A1" }],
        };

        ClaimResponseOf(_builder.BuildInquiryResponse(record, request, Base)).AddItem.Should().BeEmpty();
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

    private static string? HeaderNumber(ClaimResponse.AdjudicationComponent adjudication)
        => ((FhirString)adjudication.Extension.Single(e => e.Url == Sd + "extension-reviewAction")
            .Extension.Single(e => e.Url == "number").Value).Value;

    private static string? ActionCode(ClaimResponse.AdjudicationComponent adjudication)
        => ((CodeableConcept)adjudication.Extension.Single(e => e.Url == Sd + "extension-reviewAction")
            .Extension.Single(e => e.Url == Sd + "extension-reviewActionCode").Value).Coding.Single().Code;
}
