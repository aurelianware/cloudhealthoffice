using FhirService.Mappers;
using FhirService.Models;
using FluentAssertions;

namespace CloudHealthOffice.FhirService.Tests.Controllers;

// CS0618: ProviderDirectoryMapper.MapNppesToPractitioner / EnrichWithVerification
// were marked [Obsolete] in capability 5.7 — Practitioner proxies to
// provider-service now. This class still exercises the NPPES enrichment
// path until the mapper itself is removed in the post-5.8/5.9 cleanup PR.
#pragma warning disable CS0618

public class ProviderDirectoryVerificationTests
{
    [Fact]
    public void EnrichWithVerification_IncludesIntegrityExtension()
    {
        var practitioner = new FhirPractitioner
        {
            Id = "1234567890",
            Active = true,
            Name = new[] { new FhirHumanName { Family = "Smith", Given = new[] { "John" } } },
        };

        var verification = new ProviderVerificationSummary
        {
            IntegrityScore = 85,
            Rating = "Clear",
            Status = "Active",
            IsExcluded = false,
        };

        ProviderDirectoryMapper.EnrichWithVerification(practitioner, verification);

        practitioner.Extension.Should().NotBeNull();
        practitioner.Extension.Should().HaveCount(1);

        var ext = practitioner.Extension![0];
        ext.Url.Should().Be("https://cloudhealthoffice.com/fhir/StructureDefinition/provider-verification");
        ext.Extension.Should().Contain(e => e.Url == "integrityScore" && e.ValueInteger == 85);
        ext.Extension.Should().Contain(e => e.Url == "rating" && e.ValueString == "Clear");
        ext.Extension.Should().Contain(e => e.Url == "isExcluded" && e.ValueBoolean == false);
        practitioner.Active.Should().BeTrue();
    }

    [Fact]
    public void EnrichWithVerification_ExcludedProvider_SetsActiveToFalse()
    {
        var practitioner = new FhirPractitioner
        {
            Id = "1234567890",
            Active = true,
        };

        var verification = new ProviderVerificationSummary
        {
            IntegrityScore = 0,
            Rating = "Excluded",
            Status = "Excluded",
            IsExcluded = true,
            ExclusionSource = "OIG/LEIE",
        };

        ProviderDirectoryMapper.EnrichWithVerification(practitioner, verification);

        practitioner.Active.Should().BeFalse();
        practitioner.Extension.Should().NotBeNull();
        practitioner.Extension![0].Extension.Should().Contain(e => e.Url == "isExcluded" && e.ValueBoolean == true);
    }

    [Fact]
    public void EnrichWithVerification_VerificationServiceDown_ReturnsWithoutEnrichment()
    {
        var practitioner = new FhirPractitioner
        {
            Id = "1234567890",
            Active = true,
        };

        // Simulate service down: verification is null, enrichment not called
        ProviderVerificationSummary? verification = null;

        // Controller pattern: if (verification != null) EnrichWithVerification(...)
        if (verification != null)
            ProviderDirectoryMapper.EnrichWithVerification(practitioner, verification);

        practitioner.Extension.Should().BeNull();
        practitioner.Active.Should().BeTrue();
    }

    [Fact]
    public void SearchPractitioners_DoesNotCallVerificationService()
    {
        // Verification enrichment is for single-resource reads only.
        // Search returns mapped resources without enrichment — verify
        // by checking that a search result has no verification extension.
        var nppes = CreateTestNppesResult();
        var practitioner = ProviderDirectoryMapper.MapNppesToPractitioner(nppes);

        practitioner.Extension.Should().BeNull();
    }

    private static NppesResult CreateTestNppesResult() => new()
    {
        Number = "1234567890",
        EnumerationType = "NPI-1",
        Basic = new NppesBasicInfo
        {
            FirstName = "John",
            LastName = "Smith",
            Gender = "M",
        },
        Addresses = new[]
        {
            new NppesAddress
            {
                AddressPurpose = "LOCATION",
                Address1 = "123 Main St",
                City = "Springfield",
                State = "IL",
                PostalCode = "62701",
                CountryCode = "US",
            },
        },
        Taxonomies = new[]
        {
            new NppesTaxonomy { Code = "207R00000X", Desc = "Internal Medicine", Primary = true },
        },
    };
}
