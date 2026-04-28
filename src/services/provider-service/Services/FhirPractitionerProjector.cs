using System.Text.Json.Nodes;
using ProviderService.Models;
using static ProviderService.Services.FhirExtensionBuilder;

namespace ProviderService.Services;

/// <summary>
/// Hand-built FHIR R4 Practitioner projector (capability 5.7). Pattern
/// mirrors member-service's <c>FhirPatientProjector</c>: stateless,
/// deterministic, no Hl7.Fhir.R4 dependency.
/// </summary>
public sealed class FhirPractitionerProjector : IFhirPractitionerProjector
{
    public JsonObject? Project(Provider provider) => Project(provider, null);

    public JsonObject? Project(Provider provider, ProviderIntegrityProjection? integrity)
    {
        ArgumentNullException.ThrowIfNull(provider);

        // Organization-type providers project as FHIR Organization
        // (capability 5.8), not Practitioner. Caller maps null to 404.
        if (provider.ProviderType == ProviderType.Organization) return null;

        var practitioner = new JsonObject
        {
            ["resourceType"] = "Practitioner",
            ["id"] = provider.NPI,
            // Active when the head version is Active *and* the legacy
            // Status flag is Active. Hydration normalises Status from
            // VersionState, so this is effectively single-state, but
            // checking both shields against any future divergence.
            ["active"] = provider.VersionState == ProviderVersionState.Active
                         && provider.Status == ProviderStatus.Active
        };

        // ── Identifier (NPI) ─────────────────────────────────────────
        practitioner["identifier"] = new JsonArray
        {
            new JsonObject
            {
                ["use"] = "official",
                ["system"] = ChoProviderFhirUrls.NpiSystem,
                ["value"] = provider.NPI
            }
        };

        // ── Name ─────────────────────────────────────────────────────
        var nameNode = new JsonObject { ["use"] = "official" };
        if (!string.IsNullOrEmpty(provider.LastName))
        {
            nameNode["family"] = provider.LastName;
        }

        var givenNames = new JsonArray();
        if (!string.IsNullOrEmpty(provider.FirstName)) givenNames.Add(provider.FirstName);
        if (!string.IsNullOrEmpty(provider.MiddleName)) givenNames.Add(provider.MiddleName);
        if (givenNames.Count > 0) nameNode["given"] = givenNames;

        var suffixes = ParseCredentialsToSuffixes(provider.Credentials);
        if (suffixes.Count > 0)
        {
            var suffixArray = new JsonArray();
            foreach (var s in suffixes) suffixArray.Add(s);
            nameNode["suffix"] = suffixArray;
        }
        practitioner["name"] = new JsonArray { nameNode };

        // ── gender ───────────────────────────────────────────────────
        // Intentionally omitted. Provider has no Gender field today;
        // capability 5.17 adds Plan-Net demographics. US Core 6.1.0
        // Practitioner.gender is Must Support 0..1 — omission is conformant.

        // ── Telecom ──────────────────────────────────────────────────
        var telecom = new JsonArray();
        if (!string.IsNullOrEmpty(provider.Phone))
            telecom.Add(new JsonObject { ["system"] = "phone", ["value"] = provider.Phone, ["use"] = "work" });
        if (!string.IsNullOrEmpty(provider.Fax))
            telecom.Add(new JsonObject { ["system"] = "fax", ["value"] = provider.Fax, ["use"] = "work" });
        if (!string.IsNullOrEmpty(provider.Email))
            telecom.Add(new JsonObject { ["system"] = "email", ["value"] = provider.Email, ["use"] = "work" });
        if (telecom.Count > 0) practitioner["telecom"] = telecom;

        // ── Address ──────────────────────────────────────────────────
        if (HasAddress(provider))
        {
            var address = new JsonObject { ["use"] = "work" };
            if (!string.IsNullOrEmpty(provider.Address))
                address["line"] = new JsonArray { provider.Address };
            if (!string.IsNullOrEmpty(provider.City)) address["city"] = provider.City;
            if (!string.IsNullOrEmpty(provider.State)) address["state"] = provider.State;
            if (!string.IsNullOrEmpty(provider.ZipCode)) address["postalCode"] = provider.ZipCode;
            practitioner["address"] = new JsonArray { address };
        }

        // ── Communication (LanguagesSpoken → BCP-47) ────────────────
        if (provider.LanguagesSpoken.Count > 0)
        {
            var communication = new JsonArray();
            foreach (var lang in provider.LanguagesSpoken)
            {
                if (string.IsNullOrEmpty(lang)) continue;
                var display = LanguageDisplayName(lang) ?? lang;
                communication.Add(CodeableConcept(
                    Coding(ChoProviderFhirUrls.Bcp47LanguageSystem, lang, display),
                    text: display));
            }
            if (communication.Count > 0) practitioner["communication"] = communication;
        }

        // ── Qualification (specialties + board certifications) ──────
        var qualifications = BuildQualifications(provider);
        if (qualifications.Count > 0) practitioner["qualification"] = qualifications;

        // ── Extension: integrity score (capability 5.4.5) ───────────
        if (integrity != null && integrity.Score.HasValue)
        {
            var extInner = new JsonArray
            {
                ExtensionInteger("score", integrity.Score.Value)
            };
            if (!string.IsNullOrEmpty(integrity.Rating))
            {
                extInner.Add(ExtensionString("rating", integrity.Rating));
            }
            if (integrity.LastVerifiedAt.HasValue)
            {
                extInner.Add(ExtensionDateTime("lastVerifiedAt", integrity.LastVerifiedAt.Value));
            }

            practitioner["extension"] = new JsonArray
            {
                new JsonObject
                {
                    ["url"] = ChoProviderFhirUrls.ProviderIntegrityScoreExt,
                    ["extension"] = extInner
                }
            };
        }

        // ── Meta ────────────────────────────────────────────────────
        practitioner["meta"] = new JsonObject
        {
            ["lastUpdated"] = DateTime.SpecifyKind(provider.LastUpdatedDate, DateTimeKind.Utc).ToString("o"),
            ["profile"] = new JsonArray(
                ChoProviderFhirUrls.UsCorePractitionerProfile,
                ChoProviderFhirUrls.PlanNetPractitionerProfile)
        };

        return practitioner;
    }

    /// <summary>
    /// Parse the comma-separated <c>Credentials</c> string ("MD",
    /// "MD, FACP", "DO,NP") into a trimmed list suitable for FHIR
    /// HumanName.suffix. Empty / whitespace-only entries are dropped;
    /// duplicates are preserved (caller-supplied order is honored).
    /// </summary>
    private static IReadOnlyList<string> ParseCredentialsToSuffixes(string? credentials)
    {
        if (string.IsNullOrWhiteSpace(credentials)) return Array.Empty<string>();
        return credentials
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    private static bool HasAddress(Provider p) =>
        !string.IsNullOrEmpty(p.Address) || !string.IsNullOrEmpty(p.City) ||
        !string.IsNullOrEmpty(p.State) || !string.IsNullOrEmpty(p.ZipCode);

    /// <summary>
    /// Build the <c>qualification</c> array. Three sources:
    /// <list type="number">
    /// <item>Primary specialty: full NUCC coding from
    /// <see cref="Provider.TaxonomyCode"/> + display from
    /// <see cref="Provider.PrimarySpecialty"/>.</item>
    /// <item>Secondary specialties: text-only CodeableConcept entries.
    /// Provider-service does not store NUCC codes for secondaries
    /// today; emitting text-only is conformant FHIR. Capability 5.17 or
    /// later adds taxonomy resolution.</item>
    /// <item>Board certifications: each cert becomes a qualification
    /// entry with the v2-0360 BC code, period, and issuer display.</item>
    /// </list>
    /// </summary>
    private static JsonArray BuildQualifications(Provider provider)
    {
        var qualifications = new JsonArray();

        if (!string.IsNullOrEmpty(provider.PrimarySpecialty)
            || !string.IsNullOrEmpty(provider.TaxonomyCode))
        {
            JsonObject codeNode;
            if (!string.IsNullOrEmpty(provider.TaxonomyCode))
            {
                var coding = Coding(
                    ChoProviderFhirUrls.NuccTaxonomySystem,
                    provider.TaxonomyCode,
                    string.IsNullOrEmpty(provider.PrimarySpecialty) ? null : provider.PrimarySpecialty);
                codeNode = CodeableConcept(coding,
                    text: string.IsNullOrEmpty(provider.PrimarySpecialty) ? provider.TaxonomyCode : provider.PrimarySpecialty);
            }
            else
            {
                codeNode = CodeableConcept(coding: null, text: provider.PrimarySpecialty);
            }
            qualifications.Add(new JsonObject { ["code"] = codeNode });
        }

        foreach (var secondary in provider.SecondarySpecialties)
        {
            if (string.IsNullOrWhiteSpace(secondary)) continue;
            qualifications.Add(new JsonObject
            {
                ["code"] = CodeableConcept(coding: null, text: secondary)
            });
        }

        foreach (var cert in provider.BoardCertifications)
        {
            if (string.IsNullOrEmpty(cert.Specialty) && string.IsNullOrEmpty(cert.Board)) continue;

            var certText = string.IsNullOrEmpty(cert.Board)
                ? cert.Specialty
                : (string.IsNullOrEmpty(cert.Specialty) ? cert.Board : $"{cert.Specialty} ({cert.Board})");

            var qualification = new JsonObject
            {
                ["code"] = CodeableConcept(
                    Coding(ChoProviderFhirUrls.Hl70360CodeSystem, "BC", "Board Certified"),
                    text: certText)
            };

            if (cert.CertificationDate != default)
            {
                var period = new JsonObject
                {
                    ["start"] = DateTime.SpecifyKind(cert.CertificationDate, DateTimeKind.Utc).ToString("yyyy-MM-dd")
                };
                if (cert.ExpirationDate.HasValue)
                {
                    period["end"] = DateTime.SpecifyKind(cert.ExpirationDate.Value, DateTimeKind.Utc).ToString("yyyy-MM-dd");
                }
                qualification["period"] = period;
            }

            if (!string.IsNullOrEmpty(cert.Board))
            {
                qualification["issuer"] = new JsonObject { ["display"] = cert.Board };
            }

            qualifications.Add(qualification);
        }

        return qualifications;
    }

    /// <summary>
    /// Display name for the most common ISO 639-1 language codes that
    /// providers actually carry. Falls through to the code itself for
    /// anything not in the table; FHIR consumers should resolve via
    /// terminology service for full coverage. The handful of mappings
    /// here mirrors the LanguagesSpoken comment on Provider.cs.
    /// </summary>
    private static string? LanguageDisplayName(string code) => code.ToLowerInvariant() switch
    {
        "en" => "English",
        "es" => "Spanish",
        "zh" => "Chinese",
        "vi" => "Vietnamese",
        "tl" => "Tagalog",
        "ko" => "Korean",
        "ru" => "Russian",
        "ar" => "Arabic",
        "fr" => "French",
        "de" => "German",
        "hi" => "Hindi",
        "ja" => "Japanese",
        "pt" => "Portuguese",
        "it" => "Italian",
        "pl" => "Polish",
        _ => null
    };
}
