using System.Text.Json.Nodes;

namespace FhirService.Services.PublicDirectory;

/// <summary>
/// The CapabilityStatement a published directory serves at its own
/// <c>/metadata</c>: a Plan-Net 1.2.0 server, read-only and unauthenticated,
/// with exactly the search parameters, includes and revincludes
/// <see cref="PlanNetSearchEngine"/> implements.
/// </summary>
public static class PlanNetCapabilityStatement
{
    private const string PlanNetSearchParameterBase = PlanNetCanonicals.Base + "SearchParameter/";
    private const string BaseSearchParameterBase = "http://hl7.org/fhir/SearchParameter/";

    private static readonly Dictionary<string, string[]> Profiles = new(StringComparer.Ordinal)
    {
        ["Endpoint"] = [PlanNetCanonicals.EndpointProfile],
        ["HealthcareService"] = [PlanNetCanonicals.HealthcareServiceProfile],
        ["InsurancePlan"] = [PlanNetCanonicals.InsurancePlanProfile],
        ["Location"] = [PlanNetCanonicals.LocationProfile],
        ["Organization"] = [PlanNetCanonicals.NetworkProfile, PlanNetCanonicals.OrganizationProfile],
        ["OrganizationAffiliation"] = [PlanNetCanonicals.OrganizationAffiliationProfile],
        ["Practitioner"] = [PlanNetCanonicals.PractitionerProfile],
        ["PractitionerRole"] = [PlanNetCanonicals.PractitionerRoleProfile],
    };

    public static JsonObject Build(string baseUrl, string directoryId, DateTimeOffset date)
    {
        var resources = new JsonArray();
        foreach (var type in PlanNetSearchParameters.ResourceTypes)
        {
            var searchParams = new JsonArray
            {
                SearchParam("_id", BaseSearchParameterBase + "Resource-id", "token"),
                SearchParam("_lastUpdated", BaseSearchParameterBase + "Resource-lastUpdated", "date"),
            };
            foreach (var parameter in PlanNetSearchParameters.For(type).Values.OrderBy(p => p.Name, StringComparer.Ordinal))
                searchParams.Add(SearchParam(parameter.Name, Definition(type, parameter.Name), TypeCode(parameter.Type)));

            var profiles = new JsonArray();
            foreach (var profile in Profiles[type]) profiles.Add(profile);

            var resource = new JsonObject
            {
                ["type"] = type,
                ["supportedProfile"] = profiles,
                ["interaction"] = new JsonArray
                {
                    new JsonObject { ["code"] = "read" },
                    new JsonObject { ["code"] = "vread" },
                    new JsonObject { ["code"] = "search-type" },
                },
                ["versioning"] = "versioned",
                ["readHistory"] = false,
                ["searchInclude"] = Strings(PlanNetSearchParameters.Includes[type]),
                ["searchRevInclude"] = Strings(PlanNetSearchParameters.RevIncludes[type]),
                ["searchParam"] = searchParams,
            };
            resources.Add(resource);
        }

        return new JsonObject
        {
            ["resourceType"] = "CapabilityStatement",
            ["id"] = $"plan-net-directory-{directoryId}",
            ["url"] = $"{baseUrl}/metadata",
            ["name"] = "CloudHealthOfficePublicProviderDirectory",
            ["title"] = "Cloud Health Office public Provider Directory (Da Vinci PDex Plan-Net)",
            ["status"] = "active",
            ["date"] = date.ToString("yyyy-MM-dd"),
            ["publisher"] = "Cloud Health Office",
            ["kind"] = "instance",
            // Unversioned: clients (the Inferno Plan-Net kit among them) match
            // this canonical exactly. The version is stated by implementationGuide.
            ["instantiates"] = new JsonArray(PlanNetCanonicals.CapabilityStatement),
            ["software"] = new JsonObject { ["name"] = "Cloud Health Office" },
            ["implementation"] = new JsonObject
            {
                ["description"] = $"Public Provider Directory '{directoryId}'",
                ["url"] = baseUrl,
            },
            ["fhirVersion"] = "4.0.1",
            ["format"] = new JsonArray("json", "application/fhir+json"),
            ["implementationGuide"] = new JsonArray(
                "http://hl7.org/fhir/us/davinci-pdex-plan-net/ImplementationGuide/hl7.fhir.us.davinci-pdex-plan-net|" + PlanNetCanonicals.Version),
            ["rest"] = new JsonArray
            {
                new JsonObject
                {
                    ["mode"] = "server",
                    ["documentation"] = "Read-only Provider Directory. No authentication is required or used " +
                                        "(CMS-9115-F; Plan-Net: a directory SHALL NOT require consumer-identifying information).",
                    ["resource"] = resources,
                },
            },
        };
    }

    /// <summary>The canonical of a parameter: Plan-Net's own where it defines one, otherwise the base FHIR one.</summary>
    public static string Definition(string resourceType, string name)
    {
        var lower = resourceType.ToLowerInvariant();
        return (resourceType, name) switch
        {
            ("Practitioner", "family") => PlanNetSearchParameterBase + "practitioner-family-name",
            ("Practitioner", "given") => PlanNetSearchParameterBase + "practitioner-given-name",
            ("Endpoint", "connection-type") or (_, "identifier") when resourceType != "InsurancePlan"
                => BaseSearchParameterBase + $"{resourceType}-{name}",
            _ => PlanNetSearchParameterBase + $"{lower}-{name}",
        };
    }

    private static JsonObject SearchParam(string name, string definition, string type)
        => new() { ["name"] = name, ["definition"] = definition, ["type"] = type };

    private static string TypeCode(PlanNetSearchParamType type) => type switch
    {
        PlanNetSearchParamType.String => "string",
        PlanNetSearchParamType.Token => "token",
        PlanNetSearchParamType.Reference => "reference",
        PlanNetSearchParamType.Date => "date",
        _ => "special",
    };

    private static JsonArray Strings(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }
}
