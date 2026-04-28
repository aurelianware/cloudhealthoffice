using System.Text.Json.Nodes;
using ProviderService.Models;
using static ProviderService.Services.FhirExtensionBuilder;

namespace ProviderService.Services;

/// <summary>
/// Hand-built FHIR R4 Organization projector (capability 5.9). Pattern
/// mirrors <see cref="FhirPractitionerProjector"/> (5.7) and
/// <see cref="FhirPractitionerRoleProjector"/> (5.8): stateless,
/// deterministic, no Hl7.Fhir.R4 dependency. Two source entities project
/// to a single FHIR Organization resource type, discriminated by FHIR
/// <c>type</c>:
/// <list type="bullet">
///   <item><see cref="Organization"/> network → <c>type=ins</c></item>
///   <item><see cref="Provider"/> with <see cref="ProviderType.Organization"/>
///   → <c>type=prov</c></item>
/// </list>
/// </summary>
public sealed class FhirOrganizationProjector : IFhirOrganizationProjector
{
    // ── Organization network → type=ins ──────────────────────────────────

    /// <inheritdoc/>
    public JsonObject? Project(Organization network)
    {
        ArgumentNullException.ThrowIfNull(network);

        // Only project the head Active version. Draft / Suspended /
        // Superseded / Terminated are not directory-eligible.
        if (network.VersionState != OrganizationVersionState.Active) return null;

        // Name is required in US Core 6.1.0 Organization.
        if (string.IsNullOrWhiteSpace(network.Name)) return null;

        var org = new JsonObject
        {
            ["resourceType"] = "Organization",
            ["id"] = network.OrganizationId,
            ["active"] = true,
        };

        // ── type (ins = insurance / network) ────────────────────────────
        org["type"] = new JsonArray
        {
            CodeableConcept(
                Coding(ChoProviderFhirUrls.OrganizationTypeCodeSystem, "ins", "Insurance Company"),
                text: "Insurance Company")
        };

        // ── name ────────────────────────────────────────────────────────
        org["name"] = network.Name;

        // ── identifier ──────────────────────────────────────────────────
        // US Core Organization requires identifier (1..*). If no valid
        // identifier can be projected, the resource would be non-conformant;
        // return null so callers map this to a 404 OperationOutcome (read)
        // or skip the row (search).
        var identifiers = new JsonArray();
        foreach (var id in network.Identifiers)
        {
            if (string.IsNullOrEmpty(id.System) || string.IsNullOrEmpty(id.Value)) continue;
            var idNode = new JsonObject
            {
                ["system"] = id.System,
                ["value"] = id.Value,
            };
            if (!string.IsNullOrEmpty(id.Use)) idNode["use"] = id.Use;
            if (!string.IsNullOrEmpty(id.Type))
            {
                idNode["type"] = CodeableConcept(coding: null, text: id.Type);
            }
            identifiers.Add(idNode);
        }
        if (identifiers.Count == 0) return null;
        org["identifier"] = identifiers;

        // ── telecom ─────────────────────────────────────────────────────
        if (network.ContactInfo != null)
        {
            var telecom = BuildTelecom(
                network.ContactInfo.Phone,
                network.ContactInfo.Email,
                network.ContactInfo.Fax);
            if (telecom.Count > 0) org["telecom"] = telecom;

            // ── address ─────────────────────────────────────────────────
            if (HasAddress(network.ContactInfo))
            {
                org["address"] = new JsonArray { BuildAddress(network.ContactInfo) };
            }

            // ── contact ─────────────────────────────────────────────────
            var contact = BuildOrgContact(network.ContactInfo);
            if (contact != null)
            {
                org["contact"] = new JsonArray { contact };
            }
        }

        // ── partOf ──────────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(network.ParentOrganizationId))
        {
            org["partOf"] = new JsonObject
            {
                ["reference"] = $"Organization/{network.ParentOrganizationId}",
            };
        }

        // ── meta ────────────────────────────────────────────────────────
        org["meta"] = new JsonObject
        {
            ["lastUpdated"] = DateTime.SpecifyKind(network.LastUpdatedDate, DateTimeKind.Utc).ToString("o"),
            ["profile"] = new JsonArray(
                ChoProviderFhirUrls.UsCoreOrganizationProfile,
                ChoProviderFhirUrls.PlanNetOrganizationProfile),
        };

        return org;
    }

    // ── Provider with ProviderType=Organization → type=prov ──────────────

    /// <inheritdoc/>
    public JsonObject? Project(Provider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        // Individual providers project as FHIR Practitioner (5.7), not
        // Organization. Caller maps null to 404 OperationOutcome.
        if (provider.ProviderType != ProviderType.Organization) return null;

        // Only project the head Active version.
        if (provider.VersionState != ProviderVersionState.Active) return null;
        if (provider.Status != ProviderStatus.Active) return null;

        // OrganizationName is required for FHIR Organization.name.
        if (string.IsNullOrWhiteSpace(provider.OrganizationName)) return null;

        var org = new JsonObject
        {
            ["resourceType"] = "Organization",
            // FHIR id is the NPI — matches the read-path shape-detection
            // logic in FhirOrganizationController (Decision 6).
            ["id"] = provider.NPI,
            ["active"] = true,
        };

        // ── type (prov = provider-organization) ─────────────────────────
        org["type"] = new JsonArray
        {
            CodeableConcept(
                Coding(ChoProviderFhirUrls.OrganizationTypeCodeSystem, "prov", "Healthcare Provider"),
                text: "Healthcare Provider")
        };

        // ── name ────────────────────────────────────────────────────────
        org["name"] = provider.OrganizationName;

        // ── alias (DBA name → alias[0]) ──────────────────────────────────
        // FHIR Organization.alias is a list; DBAName is the one alternative
        // name Provider carries (Decision 11).
        if (!string.IsNullOrWhiteSpace(provider.DBAName))
        {
            org["alias"] = new JsonArray { provider.DBAName };
        }

        // ── identifier (NPI + TaxId/EIN) ────────────────────────────────
        var identifiers = new JsonArray();

        // NPI is always present (validated by the data model).
        identifiers.Add(new JsonObject
        {
            ["use"] = "official",
            ["system"] = ChoProviderFhirUrls.NpiSystem,
            ["value"] = provider.NPI,
        });

        // TaxId (EIN) — optional on Provider; system per Decision 8.
        if (!string.IsNullOrEmpty(provider.TaxId))
        {
            identifiers.Add(new JsonObject
            {
                ["system"] = ChoProviderFhirUrls.EinSystem,
                ["value"] = provider.TaxId,
            });
        }

        org["identifier"] = identifiers;

        // ── telecom ─────────────────────────────────────────────────────
        var telecom = BuildTelecom(provider.Phone, provider.Email, provider.Fax);
        if (telecom.Count > 0) org["telecom"] = telecom;

        // ── address ─────────────────────────────────────────────────────
        if (HasProviderAddress(provider))
        {
            var address = new JsonObject { ["use"] = "work" };
            if (!string.IsNullOrEmpty(provider.Address))
                address["line"] = new JsonArray { provider.Address };
            if (!string.IsNullOrEmpty(provider.City)) address["city"] = provider.City;
            if (!string.IsNullOrEmpty(provider.State)) address["state"] = provider.State;
            if (!string.IsNullOrEmpty(provider.ZipCode)) address["postalCode"] = provider.ZipCode;
            org["address"] = new JsonArray { address };
        }

        // ── meta ────────────────────────────────────────────────────────
        org["meta"] = new JsonObject
        {
            ["lastUpdated"] = DateTime.SpecifyKind(provider.LastUpdatedDate, DateTimeKind.Utc).ToString("o"),
            ["profile"] = new JsonArray(
                ChoProviderFhirUrls.UsCoreOrganizationProfile,
                ChoProviderFhirUrls.PlanNetOrganizationProfile),
        };

        return org;
    }

    // ── shared helpers ─────────────────────────────────────────────────────

    private static JsonArray BuildTelecom(string? phone, string? email, string? fax)
    {
        var telecom = new JsonArray();
        if (!string.IsNullOrEmpty(phone))
            telecom.Add(new JsonObject { ["system"] = "phone", ["value"] = phone, ["use"] = "work" });
        if (!string.IsNullOrEmpty(fax))
            telecom.Add(new JsonObject { ["system"] = "fax", ["value"] = fax, ["use"] = "work" });
        if (!string.IsNullOrEmpty(email))
            telecom.Add(new JsonObject { ["system"] = "email", ["value"] = email, ["use"] = "work" });
        return telecom;
    }

    private static bool HasAddress(OrganizationContactInfo c) =>
        !string.IsNullOrEmpty(c.Address) || !string.IsNullOrEmpty(c.City) ||
        !string.IsNullOrEmpty(c.State) || !string.IsNullOrEmpty(c.ZipCode);

    private static JsonObject BuildAddress(OrganizationContactInfo c)
    {
        var address = new JsonObject { ["use"] = "work" };
        if (!string.IsNullOrEmpty(c.Address))
            address["line"] = new JsonArray { c.Address };
        if (!string.IsNullOrEmpty(c.City)) address["city"] = c.City;
        if (!string.IsNullOrEmpty(c.State)) address["state"] = c.State;
        if (!string.IsNullOrEmpty(c.ZipCode)) address["postalCode"] = c.ZipCode;
        return address;
    }

    /// <summary>
    /// Build the FHIR Organization.contact node from
    /// <see cref="OrganizationContactInfo"/>. Returns null when neither a
    /// name nor a telecom contact is available.
    /// </summary>
    private static JsonObject? BuildOrgContact(OrganizationContactInfo c)
    {
        var contact = new JsonObject();
        var hasContent = false;

        if (!string.IsNullOrEmpty(c.PrimaryContactName))
        {
            contact["name"] = new JsonObject
            {
                ["use"] = "official",
                ["text"] = c.PrimaryContactName,
            };
            hasContent = true;
        }

        var telecom = BuildTelecom(c.Phone, c.Email, c.Fax);
        if (telecom.Count > 0)
        {
            contact["telecom"] = telecom;
            hasContent = true;
        }

        if (!string.IsNullOrEmpty(c.Address) || !string.IsNullOrEmpty(c.City) ||
            !string.IsNullOrEmpty(c.State) || !string.IsNullOrEmpty(c.ZipCode))
        {
            contact["address"] = BuildAddress(c);
            hasContent = true;
        }

        return hasContent ? contact : null;
    }

    private static bool HasProviderAddress(Provider p) =>
        !string.IsNullOrEmpty(p.Address) || !string.IsNullOrEmpty(p.City) ||
        !string.IsNullOrEmpty(p.State) || !string.IsNullOrEmpty(p.ZipCode);
}
