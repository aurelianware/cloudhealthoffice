using System.Text.Json.Nodes;

namespace FhirService.Services.PublicDirectory;

/// <summary>
/// The links between directory resources that only the whole directory can
/// make, applied once every upstream has been read and before the snapshot is
/// built:
/// <list type="bullet">
///   <item>An OrganizationAffiliation's <c>organization</c> (Plan-Net's
///   primary organization, must-support) is the organization that manages the
///   network it is in: the network's <c>partOf</c>. provider-service knows the
///   network but not who manages it.</item>
///   <item>The payer Organization lists the Endpoints it manages, so a client
///   reading the payer finds its plan documents (Organization.endpoint,
///   must-support).</item>
///   <item>A PractitionerRole's <c>organization</c>, <c>healthcareService</c>
///   and <c>location</c> that name something the directory does not publish
///   are removed. provider-service names a practitioner's affiliated hospital
///   by NPI whether or not that hospital is in the directory; a reference a
///   client cannot resolve is worse than none.</item>
/// </list>
/// </summary>
public static class PlanNetLinking
{
    public static void Link(IReadOnlyList<JsonObject> resources, string payerOrganizationId)
    {
        var published = new HashSet<string>(StringComparer.Ordinal);
        var byKey = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            if (Key(resource) is not { } key) continue;
            published.Add(key);
            byKey[key] = resource;
        }

        var payerKey = $"Organization/{payerOrganizationId}";
        var payerEndpoints = new JsonArray();

        foreach (var resource in resources)
        {
            switch (resource["resourceType"]?.GetValue<string>())
            {
                case "OrganizationAffiliation":
                    LinkPrimaryOrganization(resource, byKey);
                    break;
                case "PractitionerRole":
                    PruneReference(resource, "organization", published);
                    PruneReferences(resource, "healthcareService", published);
                    PruneReferences(resource, "location", published);
                    break;
                case "Endpoint" when ReferenceOf(resource["managingOrganization"]) == payerKey && Key(resource) is { } endpointKey:
                    payerEndpoints.Add(new JsonObject { ["reference"] = endpointKey });
                    break;
            }
        }

        if (payerEndpoints.Count > 0 && byKey.TryGetValue(payerKey, out var payer))
            payer["endpoint"] = payerEndpoints;
    }

    private static void LinkPrimaryOrganization(JsonObject affiliation, Dictionary<string, JsonObject> byKey)
    {
        if (affiliation["organization"] is not null) return;
        foreach (var network in (affiliation["network"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            if (ReferenceOf(network) is not { } networkKey || !byKey.TryGetValue(networkKey, out var resolved)) continue;
            if (ReferenceOf(resolved["partOf"]) is not { } managerKey || !byKey.ContainsKey(managerKey)) continue;

            var organization = new JsonObject { ["reference"] = managerKey };
            if (byKey[managerKey]["name"]?.GetValue<string>() is { Length: > 0 } name) organization["display"] = name;
            affiliation["organization"] = organization;
            return;
        }
    }

    private static void PruneReference(JsonObject resource, string element, HashSet<string> published)
    {
        if (resource[element] is JsonObject reference && ReferenceOf(reference) is { } key && !published.Contains(key))
            resource.Remove(element);
    }

    private static void PruneReferences(JsonObject resource, string element, HashSet<string> published)
    {
        if (resource[element] is not JsonArray references) return;
        var dangling = references
            .OfType<JsonObject>()
            .Where(r => ReferenceOf(r) is { } key && !published.Contains(key))
            .ToList();
        foreach (var reference in dangling) references.Remove(reference);
        if (references.Count == 0) resource.Remove(element);
    }

    private static string? ReferenceOf(JsonNode? reference)
        => (reference as JsonObject)?["reference"]?.GetValue<string>();

    private static string? Key(JsonObject resource)
        => resource["resourceType"]?.GetValue<string>() is { Length: > 0 } type
           && resource["id"]?.GetValue<string>() is { Length: > 0 } id
            ? $"{type}/{id}"
            : null;
}
