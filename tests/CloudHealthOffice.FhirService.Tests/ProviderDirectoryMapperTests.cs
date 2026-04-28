using FluentAssertions;
using FhirService.Mappers;
using FhirService.Models;

namespace CloudHealthOffice.FhirService.Tests;

// CS0618: ProviderDirectoryMapper.MapNppesToPractitioner / EnrichWithVerification
// were marked [Obsolete] in capability 5.7. The Practitioner endpoints now
// proxy to provider-service's projection; these mapper methods linger
// only because this test class exercises them as part of the dying NPPES
// path. The whole NPPES mapper retires once 5.8/5.9 ship.
#pragma warning disable CS0618

/// <summary>
/// Unit tests for ProviderDirectoryMapper — port of the TypeScript
/// provider-directory-api.test.ts test suite.
/// </summary>
public class ProviderDirectoryMapperTests
{
    // ── Mock NPPES data ──────────────────────────────────────────────────────

    private static readonly NppesResult MockPractitionerNppes = new()
    {
        Number = "1234567893",
        EnumerationType = "NPI-1",
        Basic = new NppesBasicInfo
        {
            FirstName = "Jane",
            LastName = "Smith",
            MiddleName = "Marie",
            NamePrefix = "Dr.",
            NameSuffix = "MD",
            Credential = "MD",
            Gender = "F",
            EnumerationDate = "2010-01-15",
            LastUpdated = "2024-01-01",
            Status = "A"
        },
        Addresses =
        [
            new NppesAddress
            {
                AddressPurpose = "LOCATION",
                Address1 = "123 Medical Center Drive",
                Address2 = "Suite 400",
                City = "Boston",
                State = "MA",
                PostalCode = "02101",
                CountryCode = "US",
                TelephoneNumber = "6175551234",
                FaxNumber = "6175551235"
            },
            new NppesAddress
            {
                AddressPurpose = "MAILING",
                Address1 = "PO Box 1234",
                City = "Boston",
                State = "MA",
                PostalCode = "02102",
                CountryCode = "US"
            }
        ],
        Taxonomies =
        [
            new NppesTaxonomy
            {
                Code = "207R00000X",
                Desc = "Internal Medicine",
                Primary = true,
                State = "MA",
                License = "MA12345"
            },
            new NppesTaxonomy
            {
                Code = "207RC0000X",
                Desc = "Cardiovascular Disease",
                Primary = false,
                State = "MA",
                License = "MA12346"
            }
        ]
    };

    private static readonly NppesResult MockOrganizationNppes = new()
    {
        Number = "9876543213",
        EnumerationType = "NPI-2",
        Basic = new NppesBasicInfo
        {
            OrganizationName = "Boston Medical Center",
            EnumerationDate = "2005-06-01",
            LastUpdated = "2024-02-15",
            Status = "A"
        },
        Addresses =
        [
            new NppesAddress
            {
                AddressPurpose = "LOCATION",
                Address1 = "1 Medical Center Plaza",
                City = "Boston",
                State = "MA",
                PostalCode = "02118",
                CountryCode = "US",
                TelephoneNumber = "6176381000",
                FaxNumber = "6176381001"
            }
        ],
        Taxonomies =
        [
            new NppesTaxonomy
            {
                Code = "282N00000X",
                Desc = "General Acute Care Hospital",
                Primary = true
            }
        ],
        OtherNames =
        [
            new NppesOtherName
            {
                OrganizationName = "BMC",
                Type = "DBA"
            }
        ]
    };

    // ── NPI Validation ───────────────────────────────────────────────────────

    [Fact]
    public void ValidateNpi_AcceptsCorrectNpi()
    {
        ProviderDirectoryMapper.ValidateNpi("1234567893").Should().BeTrue();
    }

    [Theory]
    [InlineData("123456789")]   // 9 digits
    [InlineData("12345678931")] // 11 digits
    public void ValidateNpi_RejectsWrongLength(string npi)
    {
        ProviderDirectoryMapper.ValidateNpi(npi).Should().BeFalse();
    }

    [Theory]
    [InlineData("123456789A")]
    [InlineData("123-456-78")]
    public void ValidateNpi_RejectsNonNumericCharacters(string npi)
    {
        ProviderDirectoryMapper.ValidateNpi(npi).Should().BeFalse();
    }

    [Fact]
    public void ValidateNpi_RejectsNpiFailingLuhnCheck()
    {
        ProviderDirectoryMapper.ValidateNpi("1234567891").Should().BeFalse();
    }

    // ── NPPES → Practitioner Mapping ─────────────────────────────────────────

    [Fact]
    public void MapNppesToPractitioner_CreatesResourceWithCorrectTypeAndId()
    {
        var practitioner = ProviderDirectoryMapper.MapNppesToPractitioner(MockPractitionerNppes);

        practitioner.ResourceType.Should().Be("Practitioner");
        practitioner.Id.Should().Be("1234567893");
        practitioner.Active.Should().BeTrue();
    }

    [Fact]
    public void MapNppesToPractitioner_IncludesUsCoreProfile()
    {
        var practitioner = ProviderDirectoryMapper.MapNppesToPractitioner(MockPractitionerNppes);

        practitioner.Meta!.Profile.Should().Contain(
            "http://hl7.org/fhir/us/core/StructureDefinition/us-core-practitioner");
    }

    [Fact]
    public void MapNppesToPractitioner_MapsNpiIdentifier()
    {
        var practitioner = ProviderDirectoryMapper.MapNppesToPractitioner(MockPractitionerNppes);

        var npiId = practitioner.Identifier!.FirstOrDefault(
            i => i.System == "http://hl7.org/fhir/sid/us-npi");
        npiId.Should().NotBeNull();
        npiId!.Value.Should().Be("1234567893");
    }

    [Fact]
    public void MapNppesToPractitioner_MapsNameWithPrefixAndSuffix()
    {
        var practitioner = ProviderDirectoryMapper.MapNppesToPractitioner(MockPractitionerNppes);

        var name = practitioner.Name![0];
        name.Family.Should().Be("Smith");
        name.Given.Should().Contain("Jane");
        name.Given.Should().Contain("Marie");
        name.Prefix.Should().Contain("Dr.");
        name.Suffix.Should().Contain("MD");
    }

    [Fact]
    public void MapNppesToPractitioner_MapsGenderCorrectly()
    {
        var practitioner = ProviderDirectoryMapper.MapNppesToPractitioner(MockPractitionerNppes);
        practitioner.Gender.Should().Be("female");
    }

    [Fact]
    public void MapNppesToPractitioner_MapsAddresses()
    {
        var practitioner = ProviderDirectoryMapper.MapNppesToPractitioner(MockPractitionerNppes);

        practitioner.Address.Should().HaveCount(2);

        var workAddr = practitioner.Address!.FirstOrDefault(a => a.Use == "work");
        workAddr.Should().NotBeNull();
        workAddr!.Line.Should().Contain("123 Medical Center Drive");
        workAddr.City.Should().Be("Boston");
        workAddr.State.Should().Be("MA");
        workAddr.PostalCode.Should().Be("02101");
    }

    [Fact]
    public void MapNppesToPractitioner_MapsTelecom()
    {
        var practitioner = ProviderDirectoryMapper.MapNppesToPractitioner(MockPractitionerNppes);

        var phone = practitioner.Telecom!.FirstOrDefault(t => t.System == "phone");
        phone.Should().NotBeNull();
        phone!.Value.Should().Contain("617");

        var fax = practitioner.Telecom!.FirstOrDefault(t => t.System == "fax");
        fax.Should().NotBeNull();
    }

    [Fact]
    public void MapNppesToPractitioner_MapsQualificationsFromTaxonomies()
    {
        var practitioner = ProviderDirectoryMapper.MapNppesToPractitioner(MockPractitionerNppes);

        practitioner.Qualification.Should().HaveCount(2);

        var primaryQual = practitioner.Qualification![0];
        primaryQual.Code!.Coding![0].Code.Should().Be("207R00000X");
        primaryQual.Code.Coding[0].Display.Should().Be("Internal Medicine");
    }

    [Fact]
    public void MapNppesToPractitioner_ThrowsForNpi2()
    {
        var act = () => ProviderDirectoryMapper.MapNppesToPractitioner(MockOrganizationNppes);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Cannot map NPI-2*");
    }

    [Fact]
    public void MapNppesToPractitioner_MarksDeactivatedAsInactive()
    {
        var deactivated = MockPractitionerNppes with
        {
            Basic = MockPractitionerNppes.Basic with { DeactivationDate = "2023-01-01" }
        };

        var practitioner = ProviderDirectoryMapper.MapNppesToPractitioner(deactivated);
        practitioner.Active.Should().BeFalse();
    }

    // ── NPPES → Organization Mapping ─────────────────────────────────────────

    [Fact]
    public void MapNppesToOrganization_CreatesResourceWithCorrectTypeAndId()
    {
        var org = ProviderDirectoryMapper.MapNppesToOrganization(MockOrganizationNppes);

        org.ResourceType.Should().Be("Organization");
        org.Id.Should().Be("9876543213");
        org.Active.Should().BeTrue();
    }

    [Fact]
    public void MapNppesToOrganization_IncludesUsCoreProfile()
    {
        var org = ProviderDirectoryMapper.MapNppesToOrganization(MockOrganizationNppes);

        org.Meta!.Profile.Should().Contain(
            "http://hl7.org/fhir/us/core/StructureDefinition/us-core-organization");
    }

    [Fact]
    public void MapNppesToOrganization_MapsOrganizationName()
    {
        var org = ProviderDirectoryMapper.MapNppesToOrganization(MockOrganizationNppes);
        org.Name.Should().Be("Boston Medical Center");
    }

    [Fact]
    public void MapNppesToOrganization_IncludesAlias()
    {
        var org = ProviderDirectoryMapper.MapNppesToOrganization(MockOrganizationNppes);
        org.Alias.Should().Contain("BMC");
    }

    [Fact]
    public void MapNppesToOrganization_MapsTypeFromTaxonomy()
    {
        var org = ProviderDirectoryMapper.MapNppesToOrganization(MockOrganizationNppes);

        org.Type.Should().HaveCount(1);
        org.Type![0].Coding![0].Code.Should().Be("282N00000X");
        org.Type[0].Coding[0].Display.Should().Be("General Acute Care Hospital");
    }

    [Fact]
    public void MapNppesToOrganization_ThrowsForNpi1()
    {
        var act = () => ProviderDirectoryMapper.MapNppesToOrganization(MockPractitionerNppes);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Cannot map NPI-1*");
    }

    // ── NPPES → PractitionerRole Mapping ─────────────────────────────────────
    //
    // The NPPES PractitionerRole mapping is the legacy path replaced by
    // capability 5.8 (provider-service FhirPractitionerRoleProjector).
    // The helper is marked [Obsolete] so callers migrate, but these tests
    // continue to gate the helper's behaviour until capability 5.9
    // retires the NPPES path entirely. Suppress CS0618 for the section.
#pragma warning disable CS0618 // PractitionerRole helper is intentionally Obsolete (capability 5.8)

    [Fact]
    public void MapNppesToPractitionerRole_CreatesResourceWithCorrectIdFormat()
    {
        var role = ProviderDirectoryMapper.MapNppesToPractitionerRole(MockPractitionerNppes);

        role.ResourceType.Should().Be("PractitionerRole");
        role.Id.Should().Be("1234567893-role");
    }

    [Fact]
    public void MapNppesToPractitionerRole_IncludesUsCoreProfile()
    {
        var role = ProviderDirectoryMapper.MapNppesToPractitionerRole(MockPractitionerNppes);

        role.Meta!.Profile.Should().Contain(
            "http://hl7.org/fhir/us/core/StructureDefinition/us-core-practitionerrole");
    }

    [Fact]
    public void MapNppesToPractitionerRole_ReferencesPractitioner()
    {
        var role = ProviderDirectoryMapper.MapNppesToPractitionerRole(MockPractitionerNppes);
        role.Practitioner!.Reference.Should().Be("Practitioner/1234567893");
    }

    [Fact]
    public void MapNppesToPractitionerRole_IncludesOrganizationRefWhenProvided()
    {
        var orgRef = new FhirReference { Reference = "Organization/9876543213" };
        var role = ProviderDirectoryMapper.MapNppesToPractitionerRole(MockPractitionerNppes, orgRef);

        role.Organization.Should().Be(orgRef);
    }

    [Fact]
    public void MapNppesToPractitionerRole_MapsSpecialtiesFromTaxonomies()
    {
        var role = ProviderDirectoryMapper.MapNppesToPractitionerRole(MockPractitionerNppes);

        role.Specialty.Should().HaveCount(2);
        role.Specialty![0].Coding![0].Code.Should().Be("207R00000X");
    }

    [Fact]
    public void MapNppesToPractitionerRole_MapsRoleCodeFromPrimaryTaxonomy()
    {
        var role = ProviderDirectoryMapper.MapNppesToPractitionerRole(MockPractitionerNppes);

        role.Code.Should().HaveCount(1);
        role.Code![0].Coding![0].Display.Should().Be("Internal Medicine");
    }

    [Fact]
    public void MapNppesToPractitionerRole_IncludesLocationReferences()
    {
        var role = ProviderDirectoryMapper.MapNppesToPractitionerRole(MockPractitionerNppes);

        role.Location.Should().HaveCount(1);
        role.Location![0].Reference.Should().Be("Location/1234567893-loc-0");
    }

#pragma warning restore CS0618

    // ── NPPES → Location Mapping ─────────────────────────────────────────────

    [Fact]
    public void MapNppesToLocation_CreatesResourceWithCorrectId()
    {
        var location = ProviderDirectoryMapper.MapNppesToLocation(MockPractitionerNppes, 0);

        location.ResourceType.Should().Be("Location");
        location.Id.Should().Be("1234567893-loc-0");
    }

    [Fact]
    public void MapNppesToLocation_IncludesUsCoreProfile()
    {
        var location = ProviderDirectoryMapper.MapNppesToLocation(MockPractitionerNppes, 0);

        location.Meta!.Profile.Should().Contain(
            "http://hl7.org/fhir/us/core/StructureDefinition/us-core-location");
    }

    [Fact]
    public void MapNppesToLocation_MapsAddress()
    {
        var location = ProviderDirectoryMapper.MapNppesToLocation(MockPractitionerNppes, 0);

        location.Address!.Line.Should().Contain("123 Medical Center Drive");
        location.Address.City.Should().Be("Boston");
        location.Address.State.Should().Be("MA");
    }

    [Fact]
    public void MapNppesToLocation_SetsStatusBasedOnEnumerationStatus()
    {
        var location = ProviderDirectoryMapper.MapNppesToLocation(MockPractitionerNppes, 0);
        location.Status.Should().Be("active");

        var deactivated = MockPractitionerNppes with
        {
            Basic = MockPractitionerNppes.Basic with { DeactivationDate = "2023-01-01" }
        };
        var inactiveLocation = ProviderDirectoryMapper.MapNppesToLocation(deactivated, 0);
        inactiveLocation.Status.Should().Be("inactive");
    }

    [Fact]
    public void MapNppesToLocation_IncludesTelecomFromAddress()
    {
        var location = ProviderDirectoryMapper.MapNppesToLocation(MockPractitionerNppes, 0);

        location.Telecom.Should().NotBeNull();
        location.Telecom!.FirstOrDefault(t => t.System == "phone").Should().NotBeNull();
    }

    [Fact]
    public void MapNppesToLocation_IncludesManagingOrganizationForNpi2()
    {
        var location = ProviderDirectoryMapper.MapNppesToLocation(MockOrganizationNppes, 0);
        location.ManagingOrganization!.Reference.Should().Be("Organization/9876543213");
    }

    [Fact]
    public void MapNppesToLocation_ThrowsForOutOfRangeIndex()
    {
        var nppesMailingOnly = MockPractitionerNppes with
        {
            Addresses =
            [
                new NppesAddress
                {
                    AddressPurpose = "MAILING",
                    Address1 = "PO Box 1234",
                    City = "Boston",
                    State = "MA",
                    PostalCode = "02102",
                    CountryCode = "US"
                }
            ]
        };

        var act = () => ProviderDirectoryMapper.MapNppesToLocation(nppesMailingOnly, 10);
        act.Should().Throw<InvalidOperationException>().WithMessage("*at index 10*");
    }

    // ── Search Bundle Creation ───────────────────────────────────────────────

    [Fact]
    public void CreateSearchBundle_CreatesValidSearchsetBundle()
    {
        var practitioner = ProviderDirectoryMapper.MapNppesToPractitioner(MockPractitionerNppes);
        var bundle = ProviderDirectoryMapper.CreateSearchBundle("Practitioner", [practitioner]);

        bundle.ResourceType.Should().Be("Bundle");
        bundle.Type.Should().Be("searchset");
        bundle.Total.Should().Be(1);
        bundle.Entry.Should().HaveCount(1);
        bundle.Entry![0].Search!.Mode.Should().Be("match");
    }

    [Fact]
    public void CreateSearchBundle_HandlesEmptyResults()
    {
        var bundle = ProviderDirectoryMapper.CreateSearchBundle("Practitioner", []);

        bundle.Total.Should().Be(0);
        bundle.Entry.Should().BeEmpty();
    }
}
