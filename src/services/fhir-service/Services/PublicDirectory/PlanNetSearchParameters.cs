using System.Text.Json.Nodes;

namespace FhirService.Services.PublicDirectory;

public enum PlanNetSearchParamType
{
    String,
    Token,
    Reference,
    Date,
}

/// <summary>
/// One search parameter: where its values live in a resource, and for a
/// reference, which resource types it may point at.
/// </summary>
public sealed record PlanNetSearchParameter(
    string Name,
    PlanNetSearchParamType Type,
    IReadOnlyList<NodePath> Paths,
    IReadOnlyList<string> Targets)
{
    public IEnumerable<JsonNode> Values(JsonObject resource) => Paths.SelectMany(p => p.Select(resource));
}

/// <summary>
/// A path into a resource, FHIRPath-style but only as far as the directory's
/// search parameters need: property steps, flattening arrays, and filtering
/// <c>extension</c> by url.
/// </summary>
public sealed class NodePath
{
    private readonly (string Property, string? ExtensionUrl)[] _steps;

    private NodePath((string, string?)[] steps) => _steps = steps;

    public static NodePath Of(params string[] properties)
        => new(properties.Select(p => (p, (string?)null)).ToArray());

    /// <summary>The path continued into <c>extension</c> entries with <paramref name="url"/>.</summary>
    public NodePath Extension(string url) => new([.. _steps, ("extension", url)]);

    public NodePath Then(params string[] properties)
        => new([.. _steps, .. properties.Select(p => (p, (string?)null))]);

    public IEnumerable<JsonNode> Select(JsonObject root)
    {
        IEnumerable<JsonNode> current = [root];
        foreach (var (property, url) in _steps)
        {
            current = current.SelectMany(node => Children(node, property))
                .Where(child => url is null || child["url"]?.GetValue<string>() == url);
        }
        return current;
    }

    private static IEnumerable<JsonNode> Children(JsonNode node, string property)
    {
        if (node is not JsonObject obj || obj[property] is not { } child) return [];
        return child is JsonArray array ? array.OfType<JsonNode>() : [child];
    }
}

/// <summary>
/// The search parameters the public directory supports: every parameter the
/// Plan-Net 1.2.0 CapabilityStatement marks SHALL, with the expressions from
/// the IG's SearchParameter resources, plus <c>identifier</c> on every type
/// (to look a provider up by NPI) and Endpoint <c>connection-type</c>.
/// <c>_id</c> and <c>_lastUpdated</c> apply to every type and are handled by
/// <see cref="PlanNetSearchEngine"/>.
/// </summary>
public static class PlanNetSearchParameters
{
    public static readonly IReadOnlyList<string> ResourceTypes =
    [
        "Endpoint", "HealthcareService", "InsurancePlan", "Location", "Organization",
        "OrganizationAffiliation", "Practitioner", "PractitionerRole",
    ];

    private static readonly NodePath Identifier = NodePath.Of("identifier");

    private static readonly Dictionary<string, IReadOnlyDictionary<string, PlanNetSearchParameter>> ByType =
        new(StringComparer.Ordinal)
        {
            ["Endpoint"] = Table(
                Ref("organization", NodePath.Of("managingOrganization"), "Organization"),
                Token("connection-type", NodePath.Of("connectionType")),
                Token("identifier", Identifier)),
            ["HealthcareService"] = Table(
                Ref("coverage-area", NodePath.Of("coverageArea"), "Location"),
                Token("delivery-method", NodePath.Of().Extension(PlanNetCanonicals.DeliveryMethodExtension)
                    .Extension("type").Then("valueCodeableConcept")),
                Ref("endpoint", NodePath.Of("endpoint"), "Endpoint"),
                Ref("location", NodePath.Of("location"), "Location"),
                Str("name", NodePath.Of("name")),
                Ref("organization", NodePath.Of("providedBy"), "Organization"),
                Token("service-category", NodePath.Of("category")),
                Token("service-type", NodePath.Of("type")),
                Token("specialty", NodePath.Of("specialty")),
                Token("identifier", Identifier)),
            ["InsurancePlan"] = Table(
                Ref("administered-by", NodePath.Of("administeredBy"), "Organization"),
                Ref("coverage-area", NodePath.Of("coverageArea"), "Location"),
                Token("identifier", Identifier),
                Str("name", NodePath.Of("name"), NodePath.Of("alias")),
                Ref("network", NodePath.Of("network"), "Organization"),
                Ref("owned-by", NodePath.Of("ownedBy"), "Organization"),
                Token("plan-type", NodePath.Of("plan", "type")),
                Token("type", NodePath.Of("type"))),
            ["Location"] = Table(
                Str("address", NodePath.Of("address")),
                Str("address-city", NodePath.Of("address", "city")),
                Str("address-postalcode", NodePath.Of("address", "postalCode")),
                Str("address-state", NodePath.Of("address", "state")),
                Ref("endpoint", NodePath.Of("endpoint"), "Endpoint"),
                Ref("organization", NodePath.Of("managingOrganization"), "Organization"),
                Ref("partof", NodePath.Of("partOf"), "Location"),
                Token("type", NodePath.Of("type")),
                Token("identifier", Identifier)),
            ["Organization"] = Table(
                Str("address", NodePath.Of("address")),
                Ref("coverage-area", NodePath.Of().Extension(PlanNetCanonicals.LocationReferenceExtension).Then("valueReference"), "Location"),
                Ref("endpoint", NodePath.Of("endpoint"), "Endpoint"),
                Str("name", NodePath.Of("name"), NodePath.Of("alias")),
                Ref("partof", NodePath.Of("partOf"), "Organization"),
                Date("period", NodePath.Of().Extension(PlanNetCanonicals.OrganizationPeriodExtension).Then("valuePeriod")),
                Token("type", NodePath.Of("type")),
                Token("identifier", Identifier)),
            ["OrganizationAffiliation"] = Table(
                Ref("endpoint", NodePath.Of("endpoint"), "Endpoint"),
                Ref("location", NodePath.Of("location"), "Location"),
                Ref("network", NodePath.Of("network"), "Organization"),
                Ref("participating-organization", NodePath.Of("participatingOrganization"), "Organization"),
                Date("period", NodePath.Of("period")),
                Ref("primary-organization", NodePath.Of("organization"), "Organization"),
                Token("role", NodePath.Of("code")),
                Ref("service", NodePath.Of("healthcareService"), "HealthcareService"),
                Token("specialty", NodePath.Of("specialty")),
                Token("identifier", Identifier)),
            ["Practitioner"] = Table(
                Str("family", NodePath.Of("name", "family")),
                Str("given", NodePath.Of("name", "given")),
                Str("name", NodePath.Of("name")),
                Date("period", NodePath.Of().Extension(PlanNetCanonicals.PractitionerPeriodExtension).Then("valuePeriod")),
                Token("identifier", Identifier)),
            ["PractitionerRole"] = Table(
                Ref("endpoint", NodePath.Of("endpoint"), "Endpoint"),
                Ref("location", NodePath.Of("location"), "Location"),
                Ref("network", NodePath.Of().Extension(PlanNetCanonicals.NetworkReferenceExtension).Then("valueReference"), "Organization"),
                Ref("organization", NodePath.Of("organization"), "Organization"),
                Date("period", NodePath.Of("period")),
                Ref("practitioner", NodePath.Of("practitioner"), "Practitioner"),
                Token("role", NodePath.Of("code")),
                Ref("service", NodePath.Of("healthcareService"), "HealthcareService"),
                Token("specialty", NodePath.Of("specialty")),
                Token("identifier", Identifier)),
        };

    /// <summary>The <c>_include</c> values the Plan-Net CapabilityStatement lists, per type.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Includes =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["Endpoint"] = ["Endpoint:organization"],
            ["HealthcareService"] = ["HealthcareService:location", "HealthcareService:coverage-area", "HealthcareService:organization", "HealthcareService:endpoint"],
            ["InsurancePlan"] = ["InsurancePlan:administered-by", "InsurancePlan:owned-by", "InsurancePlan:coverage-area"],
            ["Location"] = ["Location:endpoint", "Location:organization", "Location:partof"],
            ["Organization"] = ["Organization:partof", "Organization:endpoint", "Organization:coverage-area"],
            ["OrganizationAffiliation"] = ["OrganizationAffiliation:primary-organization", "OrganizationAffiliation:participating-organization", "OrganizationAffiliation:location", "OrganizationAffiliation:service", "OrganizationAffiliation:endpoint", "OrganizationAffiliation:network"],
            ["Practitioner"] = [],
            ["PractitionerRole"] = ["PractitionerRole:practitioner", "PractitionerRole:organization", "PractitionerRole:location", "PractitionerRole:service", "PractitionerRole:network", "PractitionerRole:endpoint"],
        };

    /// <summary>The <c>_revinclude</c> values the Plan-Net CapabilityStatement lists, per type.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> RevIncludes =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["Endpoint"] = [],
            ["HealthcareService"] = ["PractitionerRole:service", "OrganizationAffiliation:service"],
            ["InsurancePlan"] = [],
            ["Location"] = ["HealthcareService:location", "InsurancePlan:coverage-area", "OrganizationAffiliation:location", "PractitionerRole:location"],
            ["Organization"] = ["Endpoint:organization", "HealthcareService:organization", "InsurancePlan:administered-by", "InsurancePlan:owned-by", "OrganizationAffiliation:primary-organization", "PractitionerRole:organization", "PractitionerRole:network", "OrganizationAffiliation:participating-organization"],
            ["OrganizationAffiliation"] = [],
            ["Practitioner"] = ["PractitionerRole:practitioner"],
            ["PractitionerRole"] = [],
        };

    public static bool IsDirectoryType(string resourceType) => ByType.ContainsKey(resourceType);

    public static IReadOnlyDictionary<string, PlanNetSearchParameter> For(string resourceType)
        => ByType.TryGetValue(resourceType, out var table)
            ? table
            : new Dictionary<string, PlanNetSearchParameter>();

    public static PlanNetSearchParameter? Find(string resourceType, string name)
        => ByType.TryGetValue(resourceType, out var table) && table.TryGetValue(name, out var parameter) ? parameter : null;

    private static IReadOnlyDictionary<string, PlanNetSearchParameter> Table(params PlanNetSearchParameter[] parameters)
        => parameters.ToDictionary(p => p.Name, StringComparer.Ordinal);

    private static PlanNetSearchParameter Str(string name, params NodePath[] paths)
        => new(name, PlanNetSearchParamType.String, paths, []);

    private static PlanNetSearchParameter Token(string name, params NodePath[] paths)
        => new(name, PlanNetSearchParamType.Token, paths, []);

    private static PlanNetSearchParameter Date(string name, params NodePath[] paths)
        => new(name, PlanNetSearchParamType.Date, paths, []);

    private static PlanNetSearchParameter Ref(string name, NodePath path, params string[] targets)
        => new(name, PlanNetSearchParamType.Reference, [path], targets);
}
