using FhirService.Controllers;
using FhirService.Middleware;
using FhirService.Services.Clinical;
using FhirService.Services.ProviderAccess;
using FluentAssertions;
using Hl7.Fhir.Model;

namespace CloudHealthOffice.FhirService.Tests.Services;

/// <summary>
/// The PAT-02 inventory is load-bearing in four places at once — routing, SMART
/// scope, Provider Access consent, and the CapabilityStatement. These tests are
/// the guard that keeps those four honest about the SAME set, and that keeps the
/// set itself honest about FHIR R4.
/// </summary>
public class ClinicalResourceInventoryTests
{
    [Fact]
    public void EveryClinicalTypeIsARealFhirR4ResourceType()
    {
        // A typo in the table would produce a route and a CapabilityStatement
        // entry for a resource type that does not exist.
        foreach (var entry in ClinicalResourceInventory.All)
        {
            ModelInfo.IsKnownResource(entry.ResourceType)
                .Should().BeTrue($"{entry.ResourceType} must be a FHIR R4 resource type");
        }
    }

    [Fact]
    public void EveryClinicalTypeDeclaresTheSubjectElementFhirR4ActuallyGivesIt()
    {
        // The subject element is what the served resource's member binding is
        // written to. Naming an element the type does not have would either throw
        // at serve time or, worse, bind nothing and leave the payer's subject in
        // place.
        foreach (var entry in ClinicalResourceInventory.All)
        {
            var mapping = ModelInfo.ModelInspector.FindClassMapping(entry.ResourceType);
            mapping.Should().NotBeNull($"{entry.ResourceType} must be a mapped FHIR R4 type");

            mapping!.FindMappedElementByName(entry.SubjectElement)
                .Should().NotBeNull(
                    $"{entry.ResourceType}.{entry.SubjectElement} must exist in FHIR R4");
        }
    }

    [Fact]
    public void SubjectSearchIsAdvertisedOnlyWhereFhirR4DefinesIt()
    {
        // AllergyIntolerance, Device and Immunization have `patient` and no
        // `subject` in R4. Advertising one would be inventing a search parameter.
        foreach (var entry in ClinicalResourceInventory.All)
        {
            var defined = ModelInfo.SearchParameters
                .Any(p => string.Equals(p.Resource, entry.ResourceType, StringComparison.Ordinal)
                       && string.Equals(p.Name, "subject", StringComparison.Ordinal));

            entry.SupportsSubjectSearch.Should().Be(defined,
                $"{entry.ResourceType} must advertise 'subject' exactly when FHIR R4 defines it");
        }
    }

    [Fact]
    public void EveryAdvertisedSearchParameterIsOneFhirR4Defines()
    {
        foreach (var entry in ClinicalResourceInventory.All)
        {
            foreach (var parameter in entry.SearchParameters)
            {
                if (parameter.StartsWith('_')) continue; // _id is a common parameter

                ModelInfo.SearchParameters
                    .Any(p => string.Equals(p.Resource, entry.ResourceType, StringComparison.Ordinal)
                           && string.Equals(p.Name, parameter, StringComparison.Ordinal))
                    .Should().BeTrue($"{entry.ResourceType}?{parameter} must be a real R4 search parameter");
            }
        }
    }

    [Fact]
    public void BindingTheSubjectActuallyWritesToTheResource()
    {
        // Exercises the delegate pair on every entry rather than trusting the
        // table: a copy-paste that bound Condition's subject on a CarePlan entry
        // would be caught here, not in production.
        foreach (var entry in ClinicalResourceInventory.All)
        {
            var resource = (Resource)Activator.CreateInstance(
                ModelInfo.GetTypeForFhirType(entry.ResourceType)!)!;

            entry.BindSubject(resource, new ResourceReference("Patient/pat-001"));

            entry.ReadSubject(resource).Should().Be("Patient/pat-001",
                $"{entry.ResourceType} must bind and read back its own subject element");
        }
    }

    [Fact]
    public void TheSmartScopeLayerKnowsEveryClinicalType()
    {
        // A clinical type the SMART middleware did not recognise would fall into
        // its "unknown path" branch and be served with NO scope check.
        var known = (HashSet<string>)typeof(SmartScopeEnforcementMiddleware)
            .GetField("KnownResources",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;

        known.Should().Contain(ClinicalResourceInventory.ResourceTypes);
    }

    [Fact]
    public void ProviderAccessGovernsEveryClinicalType()
    {
        ProviderAccessAuthorizationFilter.GovernedResources
            .Should().Contain(ClinicalResourceInventory.ResourceTypes);
    }

    [Fact]
    public void TheControllerRoutesExactlyTheInventory_NoMoreAndNoFewer()
    {
        // The route constraint has to be a compile-time literal, so this is what
        // ties it back to the table. A type added to the inventory without being
        // added to the route would be advertised and unreachable; one added to the
        // route without the inventory would be reachable and ungoverned.
        var routed = (string)typeof(ClinicalResourceController)
            .GetField("ClinicalTypes",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;

        routed.Should().Be(ClinicalResourceInventory.RouteAlternation);
    }

    [Fact]
    public void EveryMemberSearchParameterIsOneTheProviderAccessFilterResolvesAMemberFrom()
    {
        // A member-naming parameter the filter does not read makes a legitimate
        // provider search look member-less, and it is refused. Fail-closed, but
        // wrong — so the two lists have to agree.
        var filterParameters = (string[])typeof(ProviderAccessAuthorizationFilter)
            .GetField("MemberSearchParameters",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;

        filterParameters.Should().Contain(ClinicalResourceInventory.MemberSearchParameters);
    }

    [Fact]
    public void TheInventoryCoversTheUscdiClinicalClassesTheRepositoryDocuments()
    {
        // The classes named in docs/features/CMS-0057-F-COMPLIANCE.md that CHO did
        // NOT already serve elsewhere. Patient Demographics, Clinical Notes,
        // Health Insurance Info, Coverage and Provenance are excluded because
        // Patient, DocumentReference, Coverage, ExplanationOfBenefit and resource
        // metadata already discharge them.
        ClinicalResourceInventory.UscdiDataClasses.Should().Contain(
        [
            "Allergies and Intolerances",
            "Assessment and Plan of Treatment",
            "Care Team Members",
            "Goals",
            "Health Concerns",
            "Immunizations",
            "Laboratory",
            "Medications",
            "Problems",
            "Procedures",
            "Smoking Status",
            "Unique Device Identifiers",
            "Vital Signs",
        ]);
    }

    [Fact]
    public void TypeLookupIsCaseInsensitive()
    {
        // ASP.NET route matching is case-insensitive, so an ordinal inventory
        // would let /fhir/r4/observation/... reach a controller the authorization
        // layers did not recognise as clinical.
        ClinicalResourceInventory.IsClinical("observation").Should().BeTrue();
        ClinicalResourceInventory.Canonicalize("OBSERVATION").Should().Be("Observation");
    }

    [Fact]
    public void ATypeChoDoesNotServeIsNotClinical()
    {
        ClinicalResourceInventory.IsClinical("RiskAssessment").Should().BeFalse();
        ClinicalResourceInventory.IsClinical("Patient").Should().BeFalse();
        ClinicalResourceInventory.IsClinical(null).Should().BeFalse();
        ClinicalResourceInventory.Canonicalize("NutritionOrder").Should().BeNull();
    }
}
