using System.Globalization;
using System.Text.Json.Nodes;

namespace FhirService.Services.PlanOrganization;

/// <summary>
/// The health plan's own Organization (<c>Fhir:PlanOrganization</c>): the
/// insurer an ExplanationOfBenefit names and the payor a Coverage names, served
/// at <c>Organization/{Id}</c> as a CARIN BB Organization. claims-service
/// (<c>Fhir:Insurer:Reference</c>) and coverage-service
/// (<c>Fhir:Coverage:PayorReference</c>) point at it by the same id. Nothing is
/// served until <see cref="Id"/> and <see cref="Name"/> are set.
/// </summary>
public sealed class PlanOrganizationOptions
{
    public const string SectionName = "Fhir:PlanOrganization";

    /// <summary>The Organization id. Must not be ten digits, which provider-service reads as an NPI.</summary>
    public string? Id { get; set; }

    public string? Name { get; set; }

    /// <summary>The plan's payer id (the id claims are sent to), published as CARIN BB's identifier:payerid.</summary>
    public string? PayerId { get; set; }

    /// <summary>The plan's NAIC company code, published as CARIN BB's identifier:naiccode.</summary>
    public string? NaicCode { get; set; }

    public string? Phone { get; set; }

    public string? Website { get; set; }

    public string? AddressLine { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public string? PostalCode { get; set; }

    public string Country { get; set; } = "US";

    /// <summary>
    /// When this configuration last changed. Without it, the time the service
    /// started, which is when the configuration was last read.
    /// </summary>
    public DateTimeOffset? LastUpdated { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(Name);
}

/// <summary>The <see cref="PlanOrganizationOptions"/> as a FHIR Organization.</summary>
public static class PlanOrganizationResource
{
    public const string CarinBbOrganizationProfile = "http://hl7.org/fhir/us/carin-bb/StructureDefinition/C4BB-Organization";

    /// <summary>CARIN BB 2.0.0's meta-profile-version invariant wants the canonical followed by <c>|2.0.</c>.</summary>
    public const string CarinBbProfileVersion = "2.0.0";

    public const string C4BbIdentifierTypeSystem = "http://hl7.org/fhir/us/carin-bb/CodeSystem/C4BBIdentifierType";
    public const string NaicSystem = "urn:oid:2.16.840.1.113883.6.300";
    public const string PayerIdSystem = "urn:cho:payer-id";
    public const string OrganizationTypeSystem = "http://terminology.hl7.org/CodeSystem/organization-type";

    // Millisecond precision, so the instant served is exactly the one a
    // _lastUpdated search with it matches.
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>
    /// Whether <paramref name="reference"/> names the configured plan
    /// Organization, relative (<c>Organization/{Id}</c>) or absolute.
    /// </summary>
    public static bool IsReferenceTo(PlanOrganizationOptions? options, string? reference)
    {
        if (options is not { IsConfigured: true } || string.IsNullOrWhiteSpace(reference)) return false;
        var relative = $"Organization/{options.Id!.Trim()}";
        return string.Equals(reference, relative, StringComparison.Ordinal)
               || reference.EndsWith("/" + relative, StringComparison.Ordinal);
    }

    public static DateTime LastUpdated(PlanOrganizationOptions options)
        => (options.LastUpdated ?? StartedAt).UtcDateTime;

    public static JsonObject Project(PlanOrganizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var organization = new JsonObject
        {
            ["resourceType"] = "Organization",
            ["id"] = options.Id!.Trim(),
            ["meta"] = new JsonObject
            {
                ["lastUpdated"] = LastUpdated(options).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                ["profile"] = new JsonArray(CarinBbOrganizationProfile, $"{CarinBbOrganizationProfile}|{CarinBbProfileVersion}"),
            },
        };

        var identifiers = new JsonArray();
        if (!string.IsNullOrWhiteSpace(options.PayerId))
            identifiers.Add(Identifier("payerid", PayerIdSystem, options.PayerId));
        if (!string.IsNullOrWhiteSpace(options.NaicCode))
            identifiers.Add(Identifier("naiccode", NaicSystem, options.NaicCode));
        if (identifiers.Count > 0) organization["identifier"] = identifiers;

        organization["active"] = true;
        organization["type"] = new JsonArray(new JsonObject
        {
            ["coding"] = new JsonArray(new JsonObject
            {
                ["system"] = OrganizationTypeSystem,
                ["code"] = "pay",
                ["display"] = "Payer",
            }),
        });
        organization["name"] = options.Name!.Trim();

        var telecom = new JsonArray();
        if (!string.IsNullOrWhiteSpace(options.Phone))
            telecom.Add(new JsonObject { ["system"] = "phone", ["value"] = options.Phone.Trim(), ["use"] = "work" });
        if (!string.IsNullOrWhiteSpace(options.Website))
            telecom.Add(new JsonObject { ["system"] = "url", ["value"] = options.Website.Trim(), ["use"] = "work" });
        if (telecom.Count > 0) organization["telecom"] = telecom;

        if (!string.IsNullOrWhiteSpace(options.AddressLine) || !string.IsNullOrWhiteSpace(options.City)
            || !string.IsNullOrWhiteSpace(options.State) || !string.IsNullOrWhiteSpace(options.PostalCode))
        {
            var address = new JsonObject();
            if (!string.IsNullOrWhiteSpace(options.AddressLine)) address["line"] = new JsonArray(options.AddressLine.Trim());
            if (!string.IsNullOrWhiteSpace(options.City)) address["city"] = options.City.Trim();
            if (!string.IsNullOrWhiteSpace(options.State)) address["state"] = options.State.Trim();
            if (!string.IsNullOrWhiteSpace(options.PostalCode)) address["postalCode"] = options.PostalCode.Trim();
            if (!string.IsNullOrWhiteSpace(options.Country)) address["country"] = options.Country.Trim();
            organization["address"] = new JsonArray(address);
        }

        return organization;
    }

    // No display on the type: a display the validator disagrees with fails
    // the instance, and none does not.
    private static JsonObject Identifier(string code, string system, string value) => new()
    {
        ["type"] = new JsonObject
        {
            ["coding"] = new JsonArray(new JsonObject
            {
                ["system"] = C4BbIdentifierTypeSystem,
                ["code"] = code,
            }),
        },
        ["system"] = system,
        ["value"] = value.Trim(),
    };
}
