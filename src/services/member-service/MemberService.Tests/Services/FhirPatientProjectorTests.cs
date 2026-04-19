using MemberService.Models;
using MemberService.Services;

namespace MemberService.Tests.Services;

public class FhirPatientProjectorTests
{
    private static Member BuildMember() => new()
    {
        TenantId = "t1",
        Id = "guid-1",
        MemberId = "M-001",
        FirstName = "Alice",
        LastName = "Example",
        MiddleName = "Q",
        DateOfBirth = new DateTime(1985, 6, 15),
        Gender = "F",
        Address = "123 Main St",
        City = "Austin",
        State = "TX",
        ZipCode = "78701",
        Phone = "512-555-0000",
        Email = "alice@example.com",
        EffectiveDate = new DateTime(2024, 1, 1),
        Status = EnrollmentStatus.Active,
        LineOfBusiness = LineOfBusiness.Commercial,
        GroupNumber = "GRP-001",
        IsSubscriber = true,
        PreferredLanguage = "en-US",
        Languages = new List<string> { "en-US", "es-MX" },
        BirthSex = "F",
        Pronouns = "she/her",
        Race = new CodedConcept
        {
            System = "urn:oid:2.16.840.1.113883.6.238",
            Code = "2106-3",
            Display = "White"
        },
        Ethnicity = new CodedConcept
        {
            System = "urn:oid:2.16.840.1.113883.6.238",
            Code = "2186-5",
            Display = "Not Hispanic or Latino"
        },
        MaritalStatus = new CodedConcept
        {
            System = "http://terminology.hl7.org/CodeSystem/v3-MaritalStatus",
            Code = "M",
            Display = "Married"
        },
        GenderIdentity = new CodedConcept
        {
            System = "http://snomed.info/sct",
            Code = "446141000124107",
            Display = "Female gender identity"
        },
        Identifiers = new List<MemberIdentifier>
        {
            new() { Type = MemberIdentifierType.MedicareMbi, System = FhirIdentifierSystems.MedicareMbi, Value = "1EG4-TE5-MK73" },
            new() { Type = MemberIdentifierType.SSN, System = FhirIdentifierSystems.SSN, Value = "ciphertext", IsEncrypted = true }
        }
    };

    [Fact]
    public void Project_ProducesPatientResource_WithCoreFields()
    {
        var m = BuildMember();
        var json = new FhirPatientProjector().Project(m);

        json["resourceType"]!.ToString().Should().Be("Patient");
        json["id"]!.ToString().Should().Be(m.Id);
        json["active"]!.GetValue<bool>().Should().BeTrue();
        json["birthDate"]!.ToString().Should().Be("1985-06-15");
        json["gender"]!.ToString().Should().Be("female");
    }

    [Fact]
    public void Project_IncludesUsCoreRaceAndEthnicityExtensions()
    {
        var json = new FhirPatientProjector().Project(BuildMember());
        var extensions = json["extension"]!.AsArray();
        extensions.Any(e => e!["url"]!.ToString() == FhirExtensionBuilder.UsCoreRace).Should().BeTrue();
        extensions.Any(e => e!["url"]!.ToString() == FhirExtensionBuilder.UsCoreEthnicity).Should().BeTrue();
    }

    [Fact]
    public void Project_IncludesBirthSexAndGenderIdentityExtensions()
    {
        var json = new FhirPatientProjector().Project(BuildMember());
        var extensions = json["extension"]!.AsArray();
        var birthSex = extensions.Single(e => e!["url"]!.ToString() == FhirExtensionBuilder.UsCoreBirthSex);
        birthSex["valueCode"]!.ToString().Should().Be("F");

        extensions.Any(e => e!["url"]!.ToString() == FhirExtensionBuilder.UsCoreGenderIdentity).Should().BeTrue();
    }

    [Fact]
    public void Project_IdentifiersContainPrimaryAndTypedWithSystemUris()
    {
        var json = new FhirPatientProjector().Project(BuildMember());
        var ids = json["identifier"]!.AsArray();
        ids.Should().HaveCount(3); // MemberId + MBI + SSN
        ids.Any(i => i!["system"]!.ToString() == "urn:cho:member-id").Should().BeTrue();
        ids.Any(i => i!["system"]!.ToString() == "http://hl7.org/fhir/sid/us-mbi").Should().BeTrue();

        var ssn = ids.Single(i => i!["system"]!.ToString() == "http://hl7.org/fhir/sid/us-ssn");
        ssn["value"]!.ToString().Should().Be("[REDACTED]");
    }

    [Fact]
    public void Project_DeceasedDate_PopulatesDeceasedDateTime()
    {
        var m = BuildMember();
        m.Deceased = true;
        m.DeceasedDate = new DateTime(2023, 12, 1, 0, 0, 0, DateTimeKind.Utc);
        var json = new FhirPatientProjector().Project(m);
        json["deceasedDateTime"].Should().NotBeNull();
        json["deceasedBoolean"].Should().BeNull();
    }

    [Fact]
    public void Project_DeceasedBoolean_WithoutDate_PopulatesBool()
    {
        var m = BuildMember();
        m.Deceased = true;
        m.DeceasedDate = null;
        var json = new FhirPatientProjector().Project(m);
        json["deceasedBoolean"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Project_Communication_MarksPreferredLanguage()
    {
        var json = new FhirPatientProjector().Project(BuildMember());
        var comms = json["communication"]!.AsArray();
        var preferred = comms.Single(c => c!["preferred"]!.GetValue<bool>());
        preferred["language"]!["coding"]!.AsArray()[0]!["code"]!.ToString().Should().Be("en-US");
    }

    [Fact]
    public void Project_MaritalStatus_EmitsCodeableConcept()
    {
        var json = new FhirPatientProjector().Project(BuildMember());
        json["maritalStatus"]!["coding"]!.AsArray()[0]!["code"]!.ToString().Should().Be("M");
    }

    [Fact]
    public void Project_Address_EmittedWhenAnyFieldPresent()
    {
        var json = new FhirPatientProjector().Project(BuildMember());
        var address = json["address"]!.AsArray()[0]!;
        address["city"]!.ToString().Should().Be("Austin");
        address["state"]!.ToString().Should().Be("TX");
        address["postalCode"]!.ToString().Should().Be("78701");
    }

    [Fact]
    public void Project_MinimalMember_DoesNotCrashAndProducesValidResourceType()
    {
        var min = new Member
        {
            TenantId = "t", MemberId = "m1",
            GroupNumber = "g", IsSubscriber = true,
            FirstName = "A", LastName = "B",
            DateOfBirth = new DateTime(2000, 1, 1),
            EffectiveDate = new DateTime(2024, 1, 1)
        };
        var json = new FhirPatientProjector().Project(min);
        json["resourceType"]!.ToString().Should().Be("Patient");
        json["identifier"]!.AsArray().Should().ContainSingle();
    }

    [Fact]
    public void Project_WithPcp_EmitsGeneralPractitionerWithNpiIdentifier()
    {
        var json = new FhirPatientProjector().Project(BuildMember(), new MemberService.Controllers.MemberPcpResponse
        {
            ProviderId = "prov-1",
            ProviderName = "Dr. Test, MD",
            NPI = "1234567890",
            Specialty = "Internal Medicine",
            NetworkStatus = "In-Network",
            AssignedDate = DateTime.UtcNow
        });

        var gp = json["generalPractitioner"]!.AsArray();
        gp.Count.Should().Be(1);
        gp[0]!["type"]!.ToString().Should().Be("Practitioner");
        gp[0]!["identifier"]!["system"]!.ToString().Should().Be("http://hl7.org/fhir/sid/us-npi");
        gp[0]!["identifier"]!["value"]!.ToString().Should().Be("1234567890");
        gp[0]!["display"]!.ToString().Should().Be("Dr. Test, MD");
    }

    [Fact]
    public void Project_WithoutPcp_OmitsGeneralPractitioner()
    {
        var json = new FhirPatientProjector().Project(BuildMember(), null);
        json.ContainsKey("generalPractitioner").Should().BeFalse();
    }
}
