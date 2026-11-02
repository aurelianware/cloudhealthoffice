using CloudHealthOffice.Portal.Services.Dtr;
using Hl7.Fhir.Model;

namespace CloudHealthOffice.Portal.Tests.Services.Dtr;

/// <summary>
/// The PAS request the questionnaire page submits: the member as fhir-service
/// files requests (Patient.identifier MB), the provider's NPI, the service,
/// and every saved QuestionnaireResponse in Claim.supportingInfo and in the
/// Bundle, which is how fhir-service attaches them to the record.
/// </summary>
public class PasRequestBuilderTests
{
    private static PriorAuthEntry Entry() => new()
    {
        RequestingNpi = "1234567893",
        RequestingName = "Smile Dental",
        RequestingIsOrganization = true,
        ServiceSystem = PasRequestBuilder.CdtSystem,
        ServiceCode = "D2740",
        ServiceDateFrom = new DateTime(2026, 11, 20),
        DiagnosisCodes = ["k02.52", " ", "K02.52"],
        Urgent = true,
    };

    private static QuestionnaireResponse Saved() => new()
    {
        Id = "qr-abc",
        Questionnaire = DtrFixtures.Canonical,
        Status = QuestionnaireResponse.QuestionnaireResponseStatus.Completed,
        Subject = new ResourceReference("Patient/MBR-1001"),
    };

    [Fact]
    public void The_request_carries_the_member_provider_service_and_forms()
    {
        var bundle = PasRequestBuilder.Build("MBR-1001", DtrFixtures.Member(), DtrFixtures.MemberCoverage(), Entry(), [Saved()],
            new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));

        var resources = bundle.Entry.Select(e => e.Resource).ToList();
        bundle.Entry.Should().OnlyContain(e => e.FullUrl.StartsWith("urn:uuid:"));
        var claim = resources.OfType<Claim>().Single();
        claim.Use.Should().Be(ClaimUseCode.Preauthorization);
        claim.Patient.Reference.Should().Be("Patient/MBR-1001");
        claim.Provider.Reference.Should().Be("Organization/requester");
        claim.Priority.Coding.Single().Code.Should().Be("stat");
        claim.Insurance.Single().Coverage.Reference.Should().Be("Coverage/cov-1");

        var item = claim.Item.Single();
        item.ProductOrService.Coding.Single().Code.Should().Be("D2740");
        item.ProductOrService.Coding.Single().System.Should().Be(PasRequestBuilder.CdtSystem);
        ((Period)item.Serviced).Start.Should().Be("2026-11-20");
        claim.Diagnosis.Should().ContainSingle().Which.Diagnosis.As<CodeableConcept>().Coding.Single().Code.Should().Be("K02.52");

        var patient = resources.OfType<Patient>().Single();
        patient.Identifier.Should().Contain(i => i.Value == "MBR-1001" && i.Type.Coding.Any(c => c.Code == "MB"));
        resources.OfType<Organization>().Single().Identifier.Single().Value.Should().Be("1234567893");

        var info = claim.SupportingInfo.Should().ContainSingle().Subject;
        info.Category.Coding.Single().Code.Should().Be("additionalInformation");
        ((ResourceReference)info.Value).Reference.Should().Be("QuestionnaireResponse/qr-abc");
        resources.OfType<QuestionnaireResponse>().Single().Id.Should().Be("qr-abc");
    }

    [Fact]
    public void An_unsaved_response_cannot_be_attached()
    {
        var unsaved = Saved();
        unsaved.Id = null;

        var build = () => PasRequestBuilder.Build("MBR-1001", DtrFixtures.Member(), DtrFixtures.MemberCoverage(), Entry(), [unsaved], DateTimeOffset.UtcNow);

        build.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validation_names_what_the_provider_must_fix()
    {
        var entry = new PriorAuthEntry { RequestingNpi = "12345", Quantity = 0 };

        PasRequestBuilder.Validate(entry).Should().BeEquivalentTo(
            "Requesting provider NPI must be 10 digits.",
            "Enter the service (procedure) code.",
            "Quantity must be positive.",
            "Enter the planned service date.");
        PasRequestBuilder.Validate(Entry()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("D2740", PasRequestBuilder.CdtSystem)]
    [InlineData("E1390", PasRequestBuilder.HcpcsSystem)]
    [InlineData("72148", PasRequestBuilder.CptSystem)]
    public void A_typed_code_gets_its_code_system(string code, string system)
        => PasRequestBuilder.GuessSystem(code).Should().Be(system);
}
