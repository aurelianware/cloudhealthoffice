using System.Globalization;
using System.Text.Json.Nodes;
using ProviderService.Models;
using static ProviderService.Services.FhirExtensionBuilder;

namespace ProviderService.Services;

/// <summary>
/// Da Vinci PDex Plan-Net 1.2.0 projection for the public Provider Directory
/// (G-36). See <see cref="IPlanNetDirectoryProjector"/>.
///
/// Where Plan-Net and the US Core projections differ:
/// <list type="bullet">
///   <item>A network is a plannet-Network: type <c>OrgTypeCS#ntwk</c>,
///   partOf the payer, and no telecom (0..0 in the profile).</item>
///   <item>A PractitionerRole names its network in the <c>network-reference</c>
///   extension, not in <c>organization</c> (which Plan-Net reserves for the
///   organization the practitioner works for, and CHO does not record).</item>
///   <item>Plan-Net binds <c>specialty</c> and PractitionerRole <c>code</c> as
///   required, so only NUCC codes in the Plan-Net value sets are emitted
///   (<see cref="PlanNetSpecialties"/>), and the role code comes from
///   <see cref="PlanNetTaxonomy"/>. The network tier, which has no Plan-Net
///   code, is not emitted as <c>code</c>.</item>
///   <item>Plan-Net fixes <c>active</c> to true, so a role or affiliation
///   outside its period is not published at all.</item>
/// </list>
/// Location, HealthcareService and OrganizationAffiliation exist only here:
/// CHO records one practice address per provider, which becomes that
/// provider's Location (<c>{npi}-loc</c>); an organization's services become
/// one HealthcareService (<c>{npi}-svc</c>) categorised from its taxonomy; and
/// each of an organization's network participations becomes an
/// OrganizationAffiliation, keyed like a PractitionerRole.
/// </summary>
public sealed class PlanNetDirectoryProjector : IPlanNetDirectoryProjector
{
    private const string LocationIdSuffix = "-loc";
    private const string HealthcareServiceIdSuffix = "-svc";

    private readonly IFhirPractitionerProjector _practitioners;
    private readonly IFhirPractitionerRoleProjector _roles;
    private readonly IFhirOrganizationProjector _organizations;

    public PlanNetDirectoryProjector(
        IFhirPractitionerProjector practitioners,
        IFhirPractitionerRoleProjector roles,
        IFhirOrganizationProjector organizations)
    {
        _practitioners = practitioners;
        _roles = roles;
        _organizations = organizations;
    }

    public static string LocationId(Provider provider) => provider.NPI + LocationIdSuffix;

    public static string HealthcareServiceId(Provider provider) => provider.NPI + HealthcareServiceIdSuffix;

    // ── Network ───────────────────────────────────────────────────────────

    public JsonObject? ProjectNetwork(Organization network, string payerOrganizationId)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentException.ThrowIfNullOrEmpty(payerOrganizationId);

        if (network.Status != OrganizationStatus.Active) return null;
        var resource = _organizations.Project(network);
        if (resource is null) return null;

        resource["type"] = new JsonArray
        {
            CodeableConcept(Coding(ChoProviderFhirUrls.PlanNetOrgTypeSystem, "ntwk", "Network"), text: "Network"),
        };

        // Plan-Net Network.telecom is 0..0; the same numbers stay reachable
        // through contact, which the US Core projection already fills.
        resource.Remove("telecom");

        // partOf names the organization that manages the network. A parent
        // network is itself a network, not a Plan-Net Organization, so the
        // payer is named whatever the internal hierarchy says.
        resource["partOf"] = Reference("Organization", payerOrganizationId);

        SetProfile(resource, ChoProviderFhirUrls.PlanNetNetworkProfile);
        return resource;
    }

    // ── Providers ─────────────────────────────────────────────────────────

    public IReadOnlyList<JsonObject> ProjectProvider(
        Provider provider, IReadOnlyDictionary<string, Organization> networks, DateTime asOf)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(networks);

        if (provider.VersionState != ProviderVersionState.Active || provider.Status != ProviderStatus.Active)
            return Array.Empty<JsonObject>();

        return provider.ProviderType == ProviderType.Individual
            ? ProjectIndividual(provider, networks, asOf)
            : ProjectOrganization(provider, networks, asOf);
    }

    private List<JsonObject> ProjectIndividual(
        Provider provider, IReadOnlyDictionary<string, Organization> networks, DateTime asOf)
    {
        var resources = new List<JsonObject>();

        var practitioner = _practitioners.Project(provider, integrity: null);
        if (practitioner is null) return resources;
        SetProfile(practitioner, ChoProviderFhirUrls.PlanNetPractitionerProfile);
        resources.Add(practitioner);

        var location = ProjectLocation(provider, managingOrganizationId: null);
        if (location is not null) resources.Add(location);

        foreach (var (participation, network) in ActiveParticipations(provider, networks, asOf))
        {
            var role = _roles.Project(participation, provider, network);
            if (role is null) continue;

            // The US Core projection puts the network in organization; Plan-Net
            // names it in network-reference instead.
            role.Remove("organization");
            role.Remove("code");

            var roleCode = PlanNetTaxonomy.PractitionerRole(provider.TaxonomyCode);
            if (roleCode is { } rc)
            {
                role["code"] = new JsonArray
                {
                    CodeableConcept(Coding(ChoProviderFhirUrls.PlanNetProviderRoleSystem, rc.Code, rc.Display), text: rc.Display),
                };
            }

            ReplaceSpecialty(role, provider.TaxonomyCode, PlanNetSpecialties.IndividualAndGroup);

            if (location is not null)
                role["location"] = new JsonArray { Reference("Location", location["id"]!.GetValue<string>()) };

            var extensions = role["extension"] as JsonArray ?? new JsonArray();
            extensions.Add(NewPatients(participation.PanelAccepted ?? participation.AcceptingNewPatients, network.OrganizationId));
            extensions.Add(new JsonObject
            {
                ["url"] = ChoProviderFhirUrls.PlanNetNetworkReferenceExt,
                ["valueReference"] = Reference("Organization", network.OrganizationId),
            });
            role["extension"] = extensions;

            SetProfile(role, ChoProviderFhirUrls.PlanNetPractitionerRoleProfile);
            resources.Add(role);
        }

        return resources;
    }

    private List<JsonObject> ProjectOrganization(
        Provider provider, IReadOnlyDictionary<string, Organization> networks, DateTime asOf)
    {
        var resources = new List<JsonObject>();

        var organization = _organizations.Project(provider);
        if (organization is null) return resources;

        // Keep the HL7 "prov" coding the US Core projection emits and add the
        // Plan-Net classification beside it, in the same concept.
        var orgType = PlanNetTaxonomy.OrganizationType(provider.TaxonomyCode);
        organization["type"] = new JsonArray
        {
            CodeableConcept(
                new[]
                {
                    Coding(ChoProviderFhirUrls.PlanNetOrgTypeSystem, orgType.Code, orgType.Display),
                    Coding(ChoProviderFhirUrls.OrganizationTypeCodeSystem, "prov", "Healthcare Provider"),
                },
                text: orgType.Display),
        };
        SetProfile(organization, ChoProviderFhirUrls.PlanNetOrganizationProfile);
        resources.Add(organization);

        var location = ProjectLocation(provider, managingOrganizationId: provider.NPI);
        if (location is not null) resources.Add(location);

        var category = PlanNetTaxonomy.ServiceCategory(provider.TaxonomyCode);
        var service = new JsonObject
        {
            ["resourceType"] = "HealthcareService",
            ["id"] = HealthcareServiceId(provider),
            ["meta"] = Meta(provider.LastUpdatedDate, ChoProviderFhirUrls.PlanNetHealthcareServiceProfile),
            ["extension"] = new JsonArray { NewPatients(provider.AcceptingNewPatients, fromNetworkId: null) },
            ["active"] = true,
            ["providedBy"] = Reference("Organization", provider.NPI, provider.OrganizationName),
            ["category"] = new JsonArray
            {
                CodeableConcept(
                    Coding(ChoProviderFhirUrls.PlanNetHealthcareServiceCategorySystem, category.Code, category.Display),
                    text: category.Display),
            },
            ["name"] = provider.DBAName is { Length: > 0 } dba ? dba : provider.OrganizationName,
        };
        var serviceSpecialty = SpecialtyConcept(provider.TaxonomyCode, PlanNetSpecialties.NonIndividual)
                               ?? SpecialtyConcept(provider.TaxonomyCode, PlanNetSpecialties.IndividualAndGroup);
        if (serviceSpecialty is not null) service["specialty"] = new JsonArray { serviceSpecialty };
        if (location is not null)
            service["location"] = new JsonArray { Reference("Location", location["id"]!.GetValue<string>()) };
        var serviceTelecom = Telecom(provider);
        if (serviceTelecom.Count > 0) service["telecom"] = serviceTelecom;
        resources.Add(service);

        var role = PlanNetTaxonomy.AffiliationRole(provider.TaxonomyCode);
        foreach (var (participation, network) in ActiveParticipations(provider, networks, asOf))
        {
            var id = _roles.EncodeId(participation, provider);
            if (id is null) continue;

            var affiliation = new JsonObject
            {
                ["resourceType"] = "OrganizationAffiliation",
                ["id"] = id,
                ["meta"] = Meta(provider.LastUpdatedDate, ChoProviderFhirUrls.PlanNetOrganizationAffiliationProfile),
                ["active"] = true,
                ["period"] = Period(participation),
                ["participatingOrganization"] = Reference("Organization", provider.NPI, provider.OrganizationName),
                ["network"] = new JsonArray { Reference("Organization", network.OrganizationId, network.Name) },
                ["code"] = new JsonArray
                {
                    CodeableConcept(
                        Coding(ChoProviderFhirUrls.PlanNetOrganizationAffiliationRoleSystem, role.Code, role.Display),
                        text: role.Display),
                },
                ["healthcareService"] = new JsonArray { Reference("HealthcareService", HealthcareServiceId(provider)) },
            };
            if (serviceSpecialty is not null) affiliation["specialty"] = new JsonArray { serviceSpecialty.DeepClone() };
            if (location is not null)
                affiliation["location"] = new JsonArray { Reference("Location", location["id"]!.GetValue<string>()) };
            if (serviceTelecom.Count > 0) affiliation["telecom"] = serviceTelecom.DeepClone();
            resources.Add(affiliation);
        }

        return resources;
    }

    // ── Location ──────────────────────────────────────────────────────────

    /// <summary>
    /// The provider's practice address as a plannet-Location, or null when no
    /// address is recorded. US Core requires a name; CHO keeps none for an
    /// address, so the provider's own name labels it.
    /// </summary>
    private static JsonObject? ProjectLocation(Provider provider, string? managingOrganizationId)
    {
        if (string.IsNullOrEmpty(provider.Address) && string.IsNullOrEmpty(provider.City)
            && string.IsNullOrEmpty(provider.State) && string.IsNullOrEmpty(provider.ZipCode))
            return null;

        var address = new JsonObject { ["use"] = "work", ["type"] = "physical" };
        if (!string.IsNullOrEmpty(provider.Address)) address["line"] = new JsonArray { provider.Address };
        if (!string.IsNullOrEmpty(provider.City)) address["city"] = provider.City;
        if (!string.IsNullOrEmpty(provider.State)) address["state"] = provider.State;
        if (!string.IsNullOrEmpty(provider.ZipCode)) address["postalCode"] = provider.ZipCode;

        var location = new JsonObject
        {
            ["resourceType"] = "Location",
            ["id"] = LocationId(provider),
            ["meta"] = Meta(provider.LastUpdatedDate, ChoProviderFhirUrls.PlanNetLocationProfile),
            ["status"] = "active",
            ["name"] = ProviderName(provider),
            ["address"] = address,
        };

        if (provider.HandicapAccessible)
        {
            location["extension"] = new JsonArray
            {
                ExtensionCodeableConcept(
                    ChoProviderFhirUrls.PlanNetAccessibilityExt,
                    CodeableConcept(
                        Coding(ChoProviderFhirUrls.PlanNetAccessibilitySystem, "handiaccess", "handicap accessible"),
                        text: "handicap accessible")),
            };
        }

        var telecom = Telecom(provider);
        if (telecom.Count > 0) location["telecom"] = telecom;

        if (managingOrganizationId is not null)
            location["managingOrganization"] = Reference("Organization", managingOrganizationId, provider.OrganizationName);

        return location;
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static IEnumerable<(NetworkParticipation Participation, Organization Network)> ActiveParticipations(
        Provider provider, IReadOnlyDictionary<string, Organization> networks, DateTime asOf)
    {
        foreach (var participation in provider.NetworkParticipations)
        {
            if (string.IsNullOrEmpty(participation.NetworkId)) continue;
            if (!networks.TryGetValue(participation.NetworkId, out var network)) continue;
            if (!FhirPractitionerRoleProjector.ComputeActive(participation, provider, asOf)) continue;
            yield return (participation, network);
        }
    }

    private static void ReplaceSpecialty(
        JsonObject resource, string? taxonomy, IReadOnlyDictionary<string, string> valueSet)
    {
        resource.Remove("specialty");
        var specialty = SpecialtyConcept(taxonomy, valueSet);
        if (specialty is not null) resource["specialty"] = new JsonArray { specialty };
    }

    private static JsonObject? SpecialtyConcept(string? taxonomy, IReadOnlyDictionary<string, string> valueSet)
    {
        if (string.IsNullOrEmpty(taxonomy) || !valueSet.TryGetValue(taxonomy, out var display)) return null;
        return CodeableConcept(Coding(ChoProviderFhirUrls.NuccTaxonomySystem, taxonomy, display), text: display);
    }

    private static JsonObject NewPatients(bool accepting, string? fromNetworkId)
    {
        var inner = new JsonArray
        {
            new JsonObject
            {
                ["url"] = "acceptingPatients",
                ["valueCodeableConcept"] = accepting
                    ? CodeableConcept(Coding(ChoProviderFhirUrls.PlanNetAcceptingPatientsSystem, "newpt", "Accepting"), text: "Accepting")
                    : CodeableConcept(Coding(ChoProviderFhirUrls.PlanNetAcceptingPatientsSystem, "nopt", "Not Accepting"), text: "Not Accepting"),
            },
        };
        if (fromNetworkId is not null)
        {
            inner.Add(new JsonObject
            {
                ["url"] = "fromNetwork",
                ["valueReference"] = Reference("Organization", fromNetworkId),
            });
        }
        return new JsonObject { ["url"] = ChoProviderFhirUrls.PlanNetNewPatientsExt, ["extension"] = inner };
    }

    private static JsonArray Telecom(Provider provider)
    {
        var telecom = new JsonArray();
        if (!string.IsNullOrEmpty(provider.Phone))
            telecom.Add(new JsonObject { ["system"] = "phone", ["value"] = provider.Phone, ["use"] = "work" });
        if (!string.IsNullOrEmpty(provider.Fax))
            telecom.Add(new JsonObject { ["system"] = "fax", ["value"] = provider.Fax, ["use"] = "work" });
        return telecom;
    }

    private static string ProviderName(Provider provider)
    {
        if (provider.ProviderType != ProviderType.Individual)
            return provider.DBAName is { Length: > 0 } dba ? dba : provider.OrganizationName ?? provider.NPI;
        var parts = new[] { provider.FirstName, provider.LastName, provider.Credentials }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        var name = string.Join(' ', parts).Trim();
        return name.Length > 0 ? name : provider.NPI;
    }

    private static JsonObject Period(NetworkParticipation participation)
    {
        var period = new JsonObject { ["start"] = FhirDate(participation.EffectiveDate) };
        if (participation.TerminationDate.HasValue) period["end"] = FhirDate(participation.TerminationDate.Value);
        return period;
    }

    private static JsonObject Reference(string type, string id, string? display = null)
    {
        var reference = new JsonObject { ["reference"] = $"{type}/{id}" };
        if (!string.IsNullOrEmpty(display)) reference["display"] = display;
        return reference;
    }

    private static JsonObject Meta(DateTime lastUpdated, params string[] profiles)
    {
        var profileArray = new JsonArray();
        foreach (var profile in profiles) profileArray.Add(profile);
        return new JsonObject
        {
            ["lastUpdated"] = DateTime.SpecifyKind(lastUpdated, DateTimeKind.Utc).ToString("o"),
            ["profile"] = profileArray,
        };
    }

    /// <summary>
    /// Declares the Plan-Net profile alone. Each Plan-Net profile derives from
    /// the US Core one the projection declared, so the claim is not weakened,
    /// and a client matching on the Plan-Net canonical finds exactly one.
    /// </summary>
    private static void SetProfile(JsonObject resource, string planNetProfile)
    {
        var meta = resource["meta"] as JsonObject ?? new JsonObject();
        meta["profile"] = new JsonArray { planNetProfile };
        resource["meta"] = meta;
    }

    private static string FhirDate(DateTime value)
        => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
