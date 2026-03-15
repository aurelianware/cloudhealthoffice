using FluentAssertions;
using FhirService.Mappers;
using FhirService.Models;

namespace CloudHealthOffice.FhirService.Tests;

/// <summary>
/// Unit tests for PatientAccessMapper — port of the TypeScript
/// patient-access-mapper.test.ts test suite.
/// </summary>
public class PatientAccessMapperTests
{
    // ── patientsToBundle ─────────────────────────────────────────────────────

    [Fact]
    public void PatientsToBundle_CreatesPatientBundleWithCorrectStructure()
    {
        var members = new List<ChoMember>
        {
            new()
            {
                MemberId = "123",
                FirstName = "Jane",
                LastName = "Doe",
                Dob = "2000-01-01",
                Gender = "female"
            }
        };

        var bundle = PatientAccessMapper.PatientsToBundle(members, "https://api.example/bundle");

        bundle.Entry.Should().HaveCount(1);
        bundle.Entry![0].FullUrl.Should().Be("Patient/123");
        bundle.Link![0].Url.Should().Be("https://api.example/bundle");
        bundle.Total.Should().Be(1);
        bundle.Type.Should().Be("searchset");

        var patient = bundle.Entry[0].Resource.Should().BeOfType<FhirPatient>().Subject;
        patient.Id.Should().Be("123");
        patient.Identifier.Should().ContainSingle(i => i.Value == "123");
    }

    [Fact]
    public void PatientsToBundle_MapsPatientFieldsCorrectly()
    {
        var members = new List<ChoMember>
        {
            new()
            {
                MemberId = "456",
                FirstName = "John",
                LastName = "Smith",
                MiddleName = "A",
                Dob = "1990-05-15",
                Gender = "M",
                Address = new ChoAddress
                {
                    Street1 = "123 Main St",
                    City = "Nashville",
                    State = "TN",
                    Zip = "37201"
                },
                Phone = "615-555-0101",
                Email = "john@example.com"
            }
        };

        var bundle = PatientAccessMapper.PatientsToBundle(members, "self");
        var patient = (FhirPatient)bundle.Entry![0].Resource!;

        patient.Active.Should().BeTrue();
        patient.Name![0].Family.Should().Be("Smith");
        patient.Name[0].Given.Should().ContainInOrder("John", "A");
        patient.Gender.Should().Be("male");
        patient.BirthDate.Should().Be("1990-05-15");
        patient.Address![0].Line.Should().Contain("123 Main St");
        patient.Address[0].City.Should().Be("Nashville");
        patient.Address[0].State.Should().Be("TN");
        patient.Address[0].PostalCode.Should().Be("37201");
        patient.Telecom.Should().HaveCount(2);
        patient.Telecom![0].System.Should().Be("phone");
        patient.Telecom[0].Value.Should().Be("615-555-0101");
        patient.Telecom[1].System.Should().Be("email");
        patient.Meta!.Profile.Should().Contain("http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient");
    }

    // ── coverageToBundle ─────────────────────────────────────────────────────

    [Fact]
    public void CoverageToBundle_CreatesCoverageBundleWithCorrectIds()
    {
        var members = new List<ChoMember>
        {
            new()
            {
                MemberId = "999",
                FirstName = "Alex",
                LastName = "Smith",
                Dob = "1995-03-15",
                Gender = "male"
            }
        };

        var bundle = PatientAccessMapper.CoverageToBundle(members, "self-link");

        bundle.Entry.Should().HaveCount(1);
        bundle.Entry![0].FullUrl.Should().Be("Coverage/999-COV");
        bundle.Total.Should().Be(1);

        var coverage = bundle.Entry[0].Resource.Should().BeOfType<FhirCoverage>().Subject;
        coverage.ResourceType.Should().Be("Coverage");
        coverage.Id.Should().Be("999-COV");
        coverage.Status.Should().Be("active");
        coverage.Beneficiary!.Reference.Should().Be("Patient/999");
        coverage.SubscriberId.Should().Be("999");
        coverage.Payor![0].Display.Should().Be("Cloud Health Office Plan");
        coverage.Type!.Coding![0].System.Should().Be("http://terminology.hl7.org/CodeSystem/v3-ActCode");
        coverage.Type.Coding[0].Code.Should().Be("SUBSCR");
    }

    // ── claimsToBundle ───────────────────────────────────────────────────────

    [Fact]
    public void ClaimsToBundle_MapsClaimsIntoBundleWithCorrectFullUrl()
    {
        var claims = new List<ChoClaim>
        {
            new()
            {
                ClaimId = "CLM-1",
                MemberId = "999",
                ProviderId = "NPI123",
                ClaimType = "professional",
                ServiceDate = "2025-01-01",
                DiagnosisCodes = [],
                ProcedureCodes = [],
                TotalCharged = 100,
                TotalPaid = 90,
                Status = "active"
            }
        };

        var bundle = PatientAccessMapper.ClaimsToBundle(claims, "claims-link");

        bundle.Entry.Should().HaveCount(1);
        bundle.Entry![0].FullUrl.Should().Be("Claim/CLM-1");

        var claim = bundle.Entry[0].Resource.Should().BeOfType<FhirClaimResource>().Subject;
        claim.ResourceType.Should().Be("Claim");
        claim.Id.Should().Be("CLM-1");
    }

    // ── paymentsToEobBundle ──────────────────────────────────────────────────

    [Fact]
    public void PaymentsToEobBundle_MapsPaymentsToExplanationOfBenefitBundle()
    {
        var payments = new List<ChoPaymentDocument>
        {
            new()
            {
                PaymentId = "PMT-1",
                ClaimId = "CLM-1",
                MemberId = "999",
                PaymentDate = "2025-02-01",
                TotalPaid = 120
            }
        };

        var bundle = PatientAccessMapper.PaymentsToEobBundle(payments, "eob-link");

        bundle.Entry.Should().HaveCount(1);
        bundle.Entry![0].FullUrl.Should().Be("ExplanationOfBenefit/PMT-1");

        var eob = bundle.Entry[0].Resource.Should().BeOfType<FhirExplanationOfBenefit>().Subject;
        eob.ResourceType.Should().Be("ExplanationOfBenefit");
        eob.Id.Should().Be("PMT-1");
        eob.Status.Should().Be("active");
        eob.Use.Should().Be("claim");
        eob.Patient!.Reference.Should().Be("Patient/999");
        eob.Created.Should().Be("2025-02-01");
        eob.Insurer!.Display.Should().Be("Cloud Health Office Plan");
        eob.Provider!.Display.Should().Be("Rendering Provider");
        eob.Outcome.Should().Be("complete");
        eob.Insurance![0].Focal.Should().BeTrue();
        eob.Insurance[0].Coverage!.Reference.Should().Be("Coverage/999-COV");
        eob.Payment!.Amount!.Value.Should().Be(120);
        eob.Payment.Amount.Currency.Should().Be("USD");
        eob.SupportingInfo![0].ValueString.Should().Be("Claim CLM-1");
    }

    // ── MapMemberToPatient — gender mapping ──────────────────────────────────

    [Theory]
    [InlineData("M", "male")]
    [InlineData("MALE", "male")]
    [InlineData("F", "female")]
    [InlineData("FEMALE", "female")]
    [InlineData("O", "other")]
    [InlineData("X", "unknown")]
    public void MapMemberToPatient_MapsGenderCorrectly(string input, string expected)
    {
        var member = new ChoMember
        {
            MemberId = "test",
            FirstName = "Test",
            LastName = "User",
            Dob = "2000-01-01",
            Gender = input
        };

        var patient = PatientAccessMapper.MapMemberToPatient(member);

        patient.Gender.Should().Be(expected);
    }

    // ── Bundle structure ─────────────────────────────────────────────────────

    [Fact]
    public void BuildBundle_SetsResourceTypeAndTypeAndSelfLink()
    {
        var bundle = PatientAccessMapper.PatientsToBundle([], "https://example.com/fhir/Patient");

        bundle.ResourceType.Should().Be("Bundle");
        bundle.Type.Should().Be("searchset");
        bundle.Total.Should().Be(0);
        bundle.Link![0].Relation.Should().Be("self");
        bundle.Link[0].Url.Should().Be("https://example.com/fhir/Patient");
        bundle.Entry.Should().BeEmpty();
    }
}
