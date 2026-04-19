using System.Text.Json.Nodes;
using MemberService.Controllers;
using MemberService.Models;
using static MemberService.Services.FhirExtensionBuilder;

namespace MemberService.Services;

/// <summary>
/// Hand-built FHIR R4 Patient projector. Populates:
///   - identifier[] (MemberId + typed identifiers; PII values are redacted unless a
///     caller-supplied <see cref="IIdentifierEncryptor"/> decrypts them upstream)
///   - name, telecom, address, gender, birthDate, deceased[x]
///   - communication (preferred language + additional languages)
///   - maritalStatus
///   - US Core extensions: us-core-race, us-core-ethnicity, us-core-birthsex,
///     us-core-genderIdentity
/// </summary>
public sealed class FhirPatientProjector : IFhirPatientProjector
{
    private const string NpiSystem = "http://hl7.org/fhir/sid/us-npi";

    public JsonObject Project(Member member) => Project(member, null);

    public JsonObject Project(Member member, MemberPcpResponse? pcp)
    {
        ArgumentNullException.ThrowIfNull(member);

        var patient = new JsonObject
        {
            ["resourceType"] = "Patient",
            ["id"] = member.Id,
            ["active"] = member.Status == EnrollmentStatus.Active
        };

        // ── Extensions (US Core) ─────────────────────────────────────
        var extensions = new JsonArray();
        var raceExt = BuildRaceExtension(member.Race, member.RaceDetail, member.Race?.Display);
        if (raceExt != null) extensions.Add(raceExt);

        var ethExt = BuildEthnicityExtension(member.Ethnicity, member.EthnicityDetail, member.Ethnicity?.Display);
        if (ethExt != null) extensions.Add(ethExt);

        if (!string.IsNullOrEmpty(member.BirthSex))
        {
            extensions.Add(new JsonObject
            {
                ["url"] = UsCoreBirthSex,
                ["valueCode"] = member.BirthSex
            });
        }

        if (member.GenderIdentity != null)
        {
            extensions.Add(ExtensionCodeableConcept(
                UsCoreGenderIdentity,
                new JsonObject
                {
                    ["coding"] = new JsonArray(Coding(
                        member.GenderIdentity.System,
                        member.GenderIdentity.Code,
                        member.GenderIdentity.Display))
                }));
        }

        if (!string.IsNullOrEmpty(member.Pronouns))
        {
            extensions.Add(ExtensionString(
                "http://hl7.org/fhir/StructureDefinition/individual-pronouns",
                member.Pronouns));
        }

        if (extensions.Count > 0) patient["extension"] = extensions;

        // ── Identifiers ──────────────────────────────────────────────
        var identifiers = new JsonArray
        {
            new JsonObject
            {
                ["use"] = "official",
                ["system"] = FhirIdentifierSystems.MemberId,
                ["value"] = member.MemberId
            }
        };
        foreach (var ident in member.Identifiers)
        {
            if (string.IsNullOrEmpty(ident.Value)) continue;

            // Never emit ciphertext. If the stored value is encrypted and no
            // upstream decryption was done, redact to avoid leaking envelope bytes.
            var value = ident.IsEncrypted ? "[REDACTED]" : ident.Value;

            var node = new JsonObject
            {
                ["use"] = string.IsNullOrEmpty(ident.Use) ? "official" : ident.Use,
                ["system"] = ident.System,
                ["value"] = value
            };
            if (ident.PeriodStart.HasValue || ident.PeriodEnd.HasValue)
            {
                var period = new JsonObject();
                if (ident.PeriodStart.HasValue) period["start"] = ident.PeriodStart.Value.ToString("o");
                if (ident.PeriodEnd.HasValue) period["end"] = ident.PeriodEnd.Value.ToString("o");
                node["period"] = period;
            }
            if (!string.IsNullOrEmpty(ident.Assigner))
            {
                node["assigner"] = new JsonObject { ["display"] = ident.Assigner };
            }
            identifiers.Add(node);
        }
        patient["identifier"] = identifiers;

        // ── Name ─────────────────────────────────────────────────────
        var givenNames = new JsonArray { member.FirstName };
        if (!string.IsNullOrEmpty(member.MiddleName)) givenNames.Add(member.MiddleName);
        patient["name"] = new JsonArray
        {
            new JsonObject
            {
                ["use"] = "official",
                ["family"] = member.LastName,
                ["given"] = givenNames
            }
        };

        // ── Telecom ──────────────────────────────────────────────────
        var telecom = new JsonArray();
        if (!string.IsNullOrEmpty(member.Phone))
            telecom.Add(new JsonObject { ["system"] = "phone", ["value"] = member.Phone, ["use"] = "home" });
        if (!string.IsNullOrEmpty(member.Email))
            telecom.Add(new JsonObject { ["system"] = "email", ["value"] = member.Email });
        if (telecom.Count > 0) patient["telecom"] = telecom;

        // ── Gender / BirthDate ───────────────────────────────────────
        patient["gender"] = MapGender(member.Gender);
        patient["birthDate"] = member.DateOfBirth.ToString("yyyy-MM-dd");

        // ── Deceased ─────────────────────────────────────────────────
        if (member.DeceasedDate.HasValue)
        {
            patient["deceasedDateTime"] = member.DeceasedDate.Value.ToString("o");
        }
        else if (member.Deceased)
        {
            patient["deceasedBoolean"] = true;
        }

        // ── Address ──────────────────────────────────────────────────
        if (HasAddress(member))
        {
            var address = new JsonObject { ["use"] = "home" };
            if (!string.IsNullOrEmpty(member.Address))
                address["line"] = new JsonArray { member.Address };
            if (!string.IsNullOrEmpty(member.City)) address["city"] = member.City;
            if (!string.IsNullOrEmpty(member.State)) address["state"] = member.State;
            if (!string.IsNullOrEmpty(member.ZipCode)) address["postalCode"] = member.ZipCode;
            patient["address"] = new JsonArray { address };
        }

        // ── Marital Status ───────────────────────────────────────────
        if (member.MaritalStatus != null)
        {
            patient["maritalStatus"] = new JsonObject
            {
                ["coding"] = new JsonArray(Coding(
                    member.MaritalStatus.System,
                    member.MaritalStatus.Code,
                    member.MaritalStatus.Display))
            };
        }

        // ── Communication ────────────────────────────────────────────
        var communications = new JsonArray();
        if (!string.IsNullOrEmpty(member.PreferredLanguage))
        {
            communications.Add(new JsonObject
            {
                ["language"] = new JsonObject
                {
                    ["coding"] = new JsonArray(Coding(
                        "urn:ietf:bcp:47", member.PreferredLanguage, member.PreferredLanguage))
                },
                ["preferred"] = true
            });
        }
        foreach (var lang in member.Languages)
        {
            if (string.IsNullOrEmpty(lang)) continue;
            if (string.Equals(lang, member.PreferredLanguage, StringComparison.OrdinalIgnoreCase)) continue;
            communications.Add(new JsonObject
            {
                ["language"] = new JsonObject
                {
                    ["coding"] = new JsonArray(Coding("urn:ietf:bcp:47", lang, lang))
                },
                ["preferred"] = false
            });
        }
        if (communications.Count > 0) patient["communication"] = communications;

        // ── General Practitioner (PCP) ───────────────────────────────
        // Emitted only when caller supplied PCP context — projection should
        // remain pure (no side-effecting fetches). NPI is the stable external
        // id, so the reference uses an identifier-shaped Reference; consumers
        // that have a Practitioner record already can swap it for a logical
        // Practitioner/{id} reference.
        if (pcp != null && !string.IsNullOrEmpty(pcp.NPI))
        {
            patient["generalPractitioner"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "Practitioner",
                    ["identifier"] = new JsonObject
                    {
                        ["system"] = NpiSystem,
                        ["value"] = pcp.NPI
                    },
                    ["display"] = string.IsNullOrEmpty(pcp.ProviderName) ? pcp.NPI : pcp.ProviderName
                }
            };
        }

        // ── Meta ─────────────────────────────────────────────────────
        patient["meta"] = new JsonObject
        {
            ["lastUpdated"] = member.LastUpdatedDate.ToString("o"),
            ["profile"] = new JsonArray("http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient")
        };

        return patient;
    }

    private static string MapGender(string? g) => g?.ToUpperInvariant() switch
    {
        "M" => "male",
        "F" => "female",
        "U" => "unknown",
        _ => "unknown"
    };

    private static bool HasAddress(Member m) =>
        !string.IsNullOrEmpty(m.Address) || !string.IsNullOrEmpty(m.City) ||
        !string.IsNullOrEmpty(m.State) || !string.IsNullOrEmpty(m.ZipCode);
}
