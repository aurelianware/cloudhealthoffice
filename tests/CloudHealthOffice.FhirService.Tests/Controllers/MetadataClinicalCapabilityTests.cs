using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using FhirService.Controllers;
using FhirService.Services.Clinical;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace CloudHealthOffice.FhirService.Tests.Controllers;

/// <summary>
/// The CapabilityStatement is a promise. These tests keep it to exactly what the
/// server does — every clinical resource it advertises has a real read and search
/// path, every implemented one is advertised, and no search parameter is claimed
/// that no query applies.
/// </summary>
public class MetadataClinicalCapabilityTests : IClassFixture<FhirTestWebAppFactory>
{
    private static readonly FhirJsonParser Parser = new(new ParserSettings { PermissiveParsing = true });

    private readonly FhirTestWebAppFactory _factory;

    public MetadataClinicalCapabilityTests(FhirTestWebAppFactory factory) => _factory = factory;

    private async Task<CapabilityStatement> GetCapabilityStatementAsync()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/fhir/r4/metadata");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return Parser.Parse<CapabilityStatement>(await response.Content.ReadAsStringAsync());
    }

    private static IReadOnlyList<CapabilityStatement.ResourceComponent> ClinicalEntries(
        CapabilityStatement statement)
        => [.. statement.Rest[0].Resource
            .Where(r => ClinicalResourceInventory.IsClinical(r.Type))];

    [Fact]
    public async Task EveryRequiredClinicalResourceIsAdvertised()
    {
        var statement = await GetCapabilityStatementAsync();

        ClinicalEntries(statement).Select(r => r.Type)
            .Should().BeEquivalentTo(ClinicalResourceInventory.ResourceTypes);
    }

    [Fact]
    public async Task NoClinicalResourceIsAdvertisedTwice()
    {
        var statement = await GetCapabilityStatementAsync();

        statement.Rest[0].Resource.Select(r => r.Type)
            .Should().OnlyHaveUniqueItems("a duplicated entry makes the statement ambiguous");
    }

    [Fact]
    public async Task EachClinicalResourceAdvertisesReadAndSearchTypeAndNothingElse()
    {
        // Nothing is written through this surface, so no create/update/delete may
        // be claimed.
        var statement = await GetCapabilityStatementAsync();

        foreach (var resource in ClinicalEntries(statement))
        {
            resource.Interaction.Select(i => i.Code).Should().BeEquivalentTo(
            [
                CapabilityStatement.TypeRestfulInteraction.Read,
                CapabilityStatement.TypeRestfulInteraction.SearchType,
            ], $"{resource.Type} is served read-only");
        }
    }

    [Fact]
    public async Task AdvertisedSearchParametersAreExactlyTheImplementedOnes()
    {
        var statement = await GetCapabilityStatementAsync();

        foreach (var resource in ClinicalEntries(statement))
        {
            var entry = ClinicalResourceInventory.Find(resource.Type)!;

            resource.SearchParam.Select(p => p.Name)
                .Should().BeEquivalentTo(entry.SearchParameters,
                    $"{resource.Type} must advertise exactly what its controller honours");
        }
    }

    [Fact]
    public async Task NoUsCoreProfileIsClaimedForClinicalResources()
    {
        // CHO serves imported clinical content as valid FHIR R4 and does not
        // re-shape it to satisfy US Core invariants. A profile URL here would be
        // a label rather than a conformance claim.
        var statement = await GetCapabilityStatementAsync();

        foreach (var resource in ClinicalEntries(statement))
            resource.SupportedProfile.Should().BeEmpty($"{resource.Type} declares no validated profile");
    }

    [Fact]
    public async Task EveryAdvertisedClinicalResourceHasARealRoute()
    {
        // The statement is generated from the inventory and the routes are
        // constrained to the inventory, so this closes the loop: advertised ->
        // routable -> served.
        var statement = await GetCapabilityStatementAsync();
        var routed = RoutedClinicalTypes();

        foreach (var resource in ClinicalEntries(statement))
            routed.Should().Contain(resource.Type, $"{resource.Type} is advertised, so it must be routable");
    }

    [Fact]
    public async Task NoResourceIsAdvertisedThatHasNeitherAControllerNorAProxy()
    {
        // A guard on the WHOLE statement, not only the clinical part: an entry
        // for a type no controller answers for is a promise the server breaks.
        var statement = await GetCapabilityStatementAsync();

        var served = ServedResourceTypes();

        foreach (var resource in statement.Rest[0].Resource)
            served.Should().Contain(resource.Type,
                $"{resource.Type} is advertised but no controller route serves it");
    }

    [Fact]
    public async Task NoResourceIsServedThatTheStatementDoesNotAdvertise()
    {
        // The converse of the test above, and the one that was missing. An
        // advertised type with no route is a promise the server breaks; a served
        // type with no entry is the server disclaiming something it does. The
        // second is the quieter failure and the more damaging one here: every
        // Inferno suite reads $metadata before anything else, and the Provider
        // Access EOB's careTeam.provider is a literal Practitioner/{npi}
        // reference served from this same base. A conformant consumer is
        // entitled not to follow a reference into a type the server says it does
        // not support.
        //
        // Routes under fhir/r4 whose first segment is not a resource type are
        // excluded by name, so a new one has to be considered rather than
        // absorbed.
        var statement = await GetCapabilityStatementAsync();
        var advertised = statement.Rest[0].Resource
            .Select(r => r.Type)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // One assertion over the whole set rather than one per type, so a gap
        // reports every missing entry at once instead of the first.
        FhirR4RouteSegments()
            .Where(t => !NonResourceRouteSegments.Contains(t) && !advertised.Contains(t))
            .Should().BeEmpty(
                "every resource type served under fhir/r4 must be advertised; add the entry, or "
                + "name the route in NonResourceRouteSegments if it is not a FHIR resource");
    }

    /// <summary>
    /// First route segments under <c>fhir/r4</c> that name something other than a
    /// FHIR resource type, so they belong in no CapabilityStatement entry.
    /// </summary>
    private static readonly IReadOnlySet<string> NonResourceRouteSegments =
        new HashSet<string>(
        [
            "metadata",
            // CHO's own operational reports, which happen to live under this
            // base: the adapter-binding report (which resource the mock serves
            // and which a CHO service does) and the CMS-0057-F self-report.
            // Neither is a FHIR resource.
            "adapter-status",
            "compliance-status",
            // SMART App Launch discovery. Required by the specification to sit
            // beside the FHIR base, and named in CapabilityStatement.rest
            // .security rather than as a resource entry.
            ".well-known",
            // Bulk Data `Group/{id}/$export`: an operation on the caller's own
            // panel, with no Group resource read or search behind it.
            "Group",
        ], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// First path segments reachable under the <c>fhir/r4</c> base, read from the
    /// controllers' own route prefixes and action templates.
    ///
    /// <para>
    /// Deliberately narrower than <see cref="ServedResourceTypes"/>, which
    /// ignores the controller prefix and so also picks up routes on other bases
    /// (<c>api/v1/crd/code-classification</c>, for one). A superset is harmless
    /// when asking whether everything advertised is served; it is wrong when
    /// asking the converse, which is what this feeds.
    /// </para>
    /// </summary>
    private static IReadOnlySet<string> FhirR4RouteSegments()
    {
        var segments = new HashSet<string>(RoutedClinicalTypes(), StringComparer.OrdinalIgnoreCase);

        var controllers = typeof(FhirControllerBase).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract);

        foreach (var controller in controllers)
        {
            var prefix = controller.GetCustomAttribute<RouteAttribute>()?.Template?.Trim('/');
            if (!string.Equals(prefix, "fhir/r4", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var route in method.GetCustomAttributes<HttpGetAttribute>())
                {
                    var first = route.Template?.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (string.IsNullOrEmpty(first) || first.StartsWith('{') || first.StartsWith('$')) continue;
                    segments.Add(first);
                }
            }
        }

        return segments;
    }

    /// <summary>Clinical types the clinical controller's route constraint admits.</summary>
    private static IReadOnlySet<string> RoutedClinicalTypes()
    {
        var alternation = (string)typeof(ClinicalResourceController)
            .GetField("ClinicalTypes", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        return new HashSet<string>(alternation.Split('|'), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every resource type reachable through a controller route under
    /// <c>fhir/r4</c>, read from the attribute routes themselves rather than from
    /// a list somebody has to remember to update.
    /// </summary>
    private static IReadOnlySet<string> ServedResourceTypes()
    {
        var types = new HashSet<string>(RoutedClinicalTypes(), StringComparer.OrdinalIgnoreCase);

        var controllers = typeof(FhirControllerBase).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract);

        foreach (var controller in controllers)
        {
            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var route in method.GetCustomAttributes<HttpGetAttribute>())
                {
                    var first = route.Template?.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (string.IsNullOrEmpty(first) || first.StartsWith('{') || first.StartsWith('$')) continue;
                    types.Add(first);
                }
            }
        }

        return types;
    }
}
