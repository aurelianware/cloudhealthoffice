using MemberService.Models;

namespace MemberService.Tests.Models;

public class MemberIdentifierTests
{
    [Fact]
    public void FromType_MapsStandardTypes_ToCanonicalSystemUris()
    {
        FhirIdentifierSystems.FromType(MemberIdentifierType.SSN).Should().Be("http://hl7.org/fhir/sid/us-ssn");
        FhirIdentifierSystems.FromType(MemberIdentifierType.MedicareMbi).Should().Be("http://hl7.org/fhir/sid/us-mbi");
        FhirIdentifierSystems.FromType(MemberIdentifierType.Medicaid).Should().Be("http://hl7.org/fhir/sid/us-medicaid");
        FhirIdentifierSystems.FromType(MemberIdentifierType.MemberId).Should().Be("urn:cho:member-id");
        FhirIdentifierSystems.FromType(MemberIdentifierType.Portal).Should().Be("urn:cho:portal-id");
        FhirIdentifierSystems.FromType(MemberIdentifierType.Exchange).Should().Be("urn:cho:exchange-id");
    }

    [Fact]
    public void LegacyForSystem_BuildsSlugScopedUri()
    {
        FhirIdentifierSystems.LegacyForSystem("acme-enroll-v1")
            .Should().Be("urn:cho:legacy:acme-enroll-v1");
    }

    [Fact]
    public void LegacyForSystem_EmptySlug_Throws()
    {
        var act = () => FhirIdentifierSystems.LegacyForSystem("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Identifier_Defaults_ToOfficialUse_AndNotEncrypted()
    {
        var id = new MemberIdentifier
        {
            Type = MemberIdentifierType.MemberId,
            System = FhirIdentifierSystems.MemberId,
            Value = "M-0001"
        };
        id.Use.Should().Be("official");
        id.IsEncrypted.Should().BeFalse();
    }
}
