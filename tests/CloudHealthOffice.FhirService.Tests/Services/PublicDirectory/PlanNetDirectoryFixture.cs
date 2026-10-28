using System.Text.Json.Nodes;
using FhirService.Services.PublicDirectory;

namespace CloudHealthOffice.FhirService.Tests.Services.PublicDirectory;

/// <summary>
/// A small directory in the shape provider-service and benefit-plan-service
/// export: a payer, one network, a hospital with its service, location and
/// network affiliation, and two practitioners with roles in the network.
/// </summary>
internal static class PlanNetDirectoryFixture
{
    public const string Network = "net-a";
    public const string Hospital = "1999999992";
    public const string Smith = "1234567893";
    public const string Muller = "1111111111";

    public static IReadOnlyList<JsonObject> Resources() =>
    [
        Parse($$$"""
            {"resourceType":"Organization","id":"payer","meta":{"lastUpdated":"2026-01-01T00:00:00Z"},
             "active":true,"name":"Acme Health Plan",
             "type":[{"coding":[{"system":"{{{PlanNetCanonicals.OrgTypeSystem}}}","code":"payer"}]}]}
            """),
        Parse($$$"""
            {"resourceType":"Organization","id":"{{{Network}}}","meta":{"lastUpdated":"2026-01-01T00:00:00Z"},
             "active":true,"name":"Acme Gold Network","partOf":{"reference":"Organization/payer"},
             "type":[{"coding":[{"system":"{{{PlanNetCanonicals.OrgTypeSystem}}}","code":"ntwk"}]}]}
            """),
        Parse($$$"""
            {"resourceType":"Organization","id":"{{{Hospital}}}","meta":{"lastUpdated":"2026-02-01T00:00:00Z"},
             "active":true,"name":"Hartford General Hospital",
             "identifier":[{"system":"http://hl7.org/fhir/sid/us-npi","value":"{{{Hospital}}}"}],
             "address":[{"line":["1 Main St"],"city":"Hartford","state":"CT","postalCode":"06101"}],
             "type":[{"coding":[{"system":"{{{PlanNetCanonicals.OrgTypeSystem}}}","code":"fac"}]}]}
            """),
        Parse($$$"""
            {"resourceType":"Practitioner","id":"{{{Smith}}}","meta":{"lastUpdated":"2026-03-01T00:00:00Z"},
             "active":true,"identifier":[{"system":"http://hl7.org/fhir/sid/us-npi","value":"{{{Smith}}}"}],
             "name":[{"family":"Smith","given":["John"]}]}
            """),
        Parse($$$"""
            {"resourceType":"Practitioner","id":"{{{Muller}}}","meta":{"lastUpdated":"2025-03-01T00:00:00Z"},
             "active":true,"name":[{"family":"Müller","given":["Anna"]}]}
            """),
        Parse($$$"""
            {"resourceType":"Location","id":"{{{Smith}}}-loc","meta":{"lastUpdated":"2026-03-01T00:00:00Z"},
             "status":"active","name":"John Smith",
             "address":{"line":["5 Elm St"],"city":"Hartford","state":"CT","postalCode":"06103"}}
            """),
        Parse($$$"""
            {"resourceType":"Location","id":"{{{Hospital}}}-loc","meta":{"lastUpdated":"2026-02-01T00:00:00Z"},
             "status":"active","name":"Hartford General Hospital",
             "address":{"line":["1 Main St"],"city":"New Haven","state":"CT","postalCode":"06510"},
             "managingOrganization":{"reference":"Organization/{{{Hospital}}}"}}
            """),
        Parse($$$"""
            {"resourceType":"PractitionerRole","id":"r1","meta":{"lastUpdated":"2026-04-27T10:00:00Z"},
             "active":true,"practitioner":{"reference":"Practitioner/{{{Smith}}}"},
             "location":[{"reference":"Location/{{{Smith}}}-loc"}],
             "extension":[{"url":"{{{PlanNetCanonicals.NetworkReferenceExtension}}}","valueReference":{"reference":"Organization/{{{Network}}}"}}],
             "code":[{"coding":[{"system":"http://hl7.org/fhir/us/davinci-pdex-plan-net/CodeSystem/ProviderRoleCS","code":"ph","display":"Physician"}]}],
             "specialty":[{"coding":[{"system":"http://nucc.org/provider-taxonomy","code":"207R00000X","display":"Internal Medicine Physician"}],"text":"Internal Medicine Physician"}],
             "period":{"start":"2024-01-01"}}
            """),
        Parse($$$"""
            {"resourceType":"PractitionerRole","id":"r2","meta":{"lastUpdated":"2025-01-01T00:00:00Z"},
             "active":true,"practitioner":{"reference":"Practitioner/{{{Muller}}}"},
             "extension":[{"url":"{{{PlanNetCanonicals.NetworkReferenceExtension}}}","valueReference":{"reference":"Organization/{{{Network}}}"}}],
             "specialty":[{"coding":[{"system":"http://nucc.org/provider-taxonomy","code":"208D00000X","display":"General Practice Physician"}]}],
             "period":{"start":"2020-06-01","end":"2030-01-01"}}
            """),
        Parse($$$"""
            {"resourceType":"HealthcareService","id":"{{{Hospital}}}-svc","meta":{"lastUpdated":"2026-02-01T00:00:00Z"},
             "active":true,"name":"Hartford General Hospital",
             "providedBy":{"reference":"Organization/{{{Hospital}}}"},
             "location":[{"reference":"Location/{{{Hospital}}}-loc"}],
             "category":[{"coding":[{"system":"http://hl7.org/fhir/us/davinci-pdex-plan-net/CodeSystem/HealthcareServiceCategoryCS","code":"hosp","display":"Hospital"}]}]}
            """),
        Parse($$$"""
            {"resourceType":"OrganizationAffiliation","id":"a1","meta":{"lastUpdated":"2026-02-01T00:00:00Z"},
             "active":true,"participatingOrganization":{"reference":"Organization/{{{Hospital}}}"},
             "network":[{"reference":"Organization/{{{Network}}}"}],
             "healthcareService":[{"reference":"HealthcareService/{{{Hospital}}}-svc"}],
             "location":[{"reference":"Location/{{{Hospital}}}-loc"}],
             "code":[{"coding":[{"system":"http://hl7.org/fhir/us/davinci-pdex-plan-net/CodeSystem/OrganizationAffiliationRoleCS","code":"hospital"}]}]}
            """),
        Parse($$$"""
            {"resourceType":"InsurancePlan","id":"plan-1","meta":{"lastUpdated":"2026-01-15T00:00:00Z"},
             "status":"active","name":"Acme Gold HMO",
             "type":[{"coding":[{"system":"{{{PlanNetCanonicals.InsuranceProductTypeSystem}}}","code":"commhmo"}]}],
             "ownedBy":{"reference":"Organization/payer"},"administeredBy":{"reference":"Organization/payer"},
             "network":[{"reference":"Organization/{{{Network}}}"}]}
            """),
    ];

    public static PlanNetDirectorySnapshot Snapshot()
        => PlanNetDirectorySnapshot.Create("acme-health", Resources(), DateTimeOffset.Parse("2026-10-28T00:00:00Z"));

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();
}
