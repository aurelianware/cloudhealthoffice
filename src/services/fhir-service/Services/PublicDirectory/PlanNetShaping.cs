using System.Text.Json.Nodes;

namespace FhirService.Services.PublicDirectory;

/// <summary>
/// The Plan-Net 1.2.0 adjustments the public directory makes to
/// benefit-plan-service's InsurancePlan and Endpoint projections, and the
/// plan's own Organization it publishes from configuration. provider-service
/// shapes its resources itself (<c>PlanNetDirectoryProjector</c>); these two
/// need the payer, which only the directory knows.
/// </summary>
public static class PlanNetShaping
{
    private const string NpiSystem = "http://hl7.org/fhir/sid/us-npi";
    private const string DirectoryIdSystem = "http://fhir.cloudhealthoffice.com/sid/public-directory";

    /// <summary>The plan's own Organization (plannet-Organization, OrgTypeCS#payer).</summary>
    public static JsonObject PayerOrganization(PayerOrganization payer, string directoryId)
    {
        var identifier = !string.IsNullOrWhiteSpace(payer.IdentifierSystem) && !string.IsNullOrWhiteSpace(payer.IdentifierValue)
            ? new JsonObject { ["system"] = payer.IdentifierSystem, ["value"] = payer.IdentifierValue }
            : new JsonObject { ["system"] = DirectoryIdSystem, ["value"] = directoryId };

        var organization = new JsonObject
        {
            ["resourceType"] = "Organization",
            ["id"] = PublicDirectory.PayerOrganization.ResourceId,
            ["meta"] = new JsonObject
            {
                ["lastUpdated"] = DateTimeOffset.UtcNow.ToString("o"),
                ["profile"] = new JsonArray(PlanNetCanonicals.OrganizationProfile),
            },
            ["identifier"] = new JsonArray { identifier },
            ["active"] = true,
            ["type"] = new JsonArray
            {
                new JsonObject
                {
                    ["coding"] = new JsonArray
                    {
                        new JsonObject { ["system"] = PlanNetCanonicals.OrgTypeSystem, ["code"] = "payer", ["display"] = "Payer" },
                    },
                    ["text"] = "Payer",
                },
            },
            ["name"] = payer.Name,
        };

        var telecom = new JsonArray();
        if (!string.IsNullOrWhiteSpace(payer.Phone))
            telecom.Add(new JsonObject { ["system"] = "phone", ["value"] = payer.Phone, ["use"] = "work" });
        if (!string.IsNullOrWhiteSpace(payer.Website))
            telecom.Add(new JsonObject { ["system"] = "url", ["value"] = payer.Website, ["use"] = "work" });
        if (telecom.Count > 0) organization["telecom"] = telecom;

        if (!string.IsNullOrWhiteSpace(payer.AddressLine) || !string.IsNullOrWhiteSpace(payer.City)
            || !string.IsNullOrWhiteSpace(payer.State) || !string.IsNullOrWhiteSpace(payer.PostalCode))
        {
            var address = new JsonObject { ["use"] = "work" };
            if (!string.IsNullOrWhiteSpace(payer.AddressLine)) address["line"] = new JsonArray(payer.AddressLine);
            if (!string.IsNullOrWhiteSpace(payer.City)) address["city"] = payer.City;
            if (!string.IsNullOrWhiteSpace(payer.State)) address["state"] = payer.State;
            if (!string.IsNullOrWhiteSpace(payer.PostalCode)) address["postalCode"] = payer.PostalCode;
            organization["address"] = new JsonArray { address };
        }

        return organization;
    }

    /// <summary>
    /// InsurancePlan: owned and administered by the payer (both 1..1 in
    /// Plan-Net), with the Plan-Net product type alongside CHO's codings.
    /// </summary>
    public static JsonObject InsurancePlan(JsonObject plan, PayerOrganization payer)
    {
        var payerReference = new JsonObject
        {
            ["reference"] = $"Organization/{PublicDirectory.PayerOrganization.ResourceId}",
            ["display"] = payer.Name,
        };
        plan["ownedBy"] = payerReference;
        plan["administeredBy"] = payerReference.DeepClone();

        if (plan["type"] is JsonArray { Count: > 0 } types && types[0] is JsonObject type)
        {
            var codings = type["coding"] as JsonArray ?? new JsonArray();
            var productShape = codings
                .Select(c => c?["system"]?.GetValue<string>()?.EndsWith("/plan-product-shape", StringComparison.Ordinal) == true
                    ? c["code"]?.GetValue<string>()
                    : null)
                .FirstOrDefault(c => c is not null);
            if (ProductType(productShape) is { } product
                && !codings.Any(c => c?["system"]?.GetValue<string>() == PlanNetCanonicals.InsuranceProductTypeSystem))
            {
                codings.Insert(0, new JsonObject
                {
                    ["system"] = PlanNetCanonicals.InsuranceProductTypeSystem,
                    ["code"] = product.Code,
                    ["display"] = product.Display,
                });
                if (codings.Parent is null) type["coding"] = codings;
            }
        }

        SetProfile(plan, PlanNetCanonicals.InsurancePlanProfile);
        return plan;
    }

    /// <summary>
    /// Endpoint: Plan-Net fixes status to active, so anything else is not
    /// published (null). A plan document is served over plain HTTPS, which
    /// Plan-Net names <c>rest-non-fhir</c>; its payload type is <c>NA</c>, the
    /// only code Plan-Net defines, with CHO's document type kept beside it.
    /// </summary>
    public static JsonObject? Endpoint(JsonObject endpoint)
    {
        if (endpoint["status"]?.GetValue<string>() != "active") return null;

        endpoint["connectionType"] = new JsonObject
        {
            ["system"] = PlanNetCanonicals.EndpointConnectionTypeSystem,
            ["code"] = "rest-non-fhir",
            ["display"] = "REST (not FHIR)",
        };

        var original = (endpoint["payloadType"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var codings = new JsonArray
        {
            new JsonObject { ["system"] = PlanNetCanonicals.EndpointPayloadTypeSystem, ["code"] = "NA", ["display"] = "Not Applicable" },
        };
        if (original?["coding"] is JsonArray originalCodings)
        {
            foreach (var coding in originalCodings)
                if (coding is not null) codings.Add(coding.DeepClone());
        }
        var payload = new JsonObject { ["coding"] = codings };
        if (original?["text"] is JsonNode text) payload["text"] = text.DeepClone();
        endpoint["payloadType"] = new JsonArray { payload };

        endpoint["managingOrganization"] = new JsonObject
        {
            ["reference"] = $"Organization/{PublicDirectory.PayerOrganization.ResourceId}",
        };

        SetProfile(endpoint, PlanNetCanonicals.EndpointProfile);
        return endpoint;
    }

    private static (string Code, string Display)? ProductType(string? planType) => planType switch
    {
        "HMO" => ("commhmo", "Commercial HMO"),
        "PPO" => ("commppo", "Commercial PPO"),
        "EPO" => ("commepo", "Commercial EPO"),
        "POS" => ("commpos", "Commercial POS"),
        "HDHP" => ("commhdhp", "Commercial HDHP"),
        "Medicaid" => ("medi", "Medicaid"),
        "Medicare" => ("mediadv", "Medicare Advantage"),
        _ => null,
    };

    private static void SetProfile(JsonObject resource, string profile)
    {
        if (resource["meta"] is not JsonObject meta)
        {
            meta = new JsonObject();
            resource["meta"] = meta;
        }
        meta["profile"] = new JsonArray(profile);
    }
}
