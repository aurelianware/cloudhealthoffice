using CloudHealthOffice.Infrastructure.ReferenceData;
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
    private const string V2IdentifierTypeSystem = "http://terminology.hl7.org/CodeSystem/v2-0203";
    private const string C4BbIdentifierTypeSystem = "http://hl7.org/fhir/us/carin-bb/CodeSystem/C4BBIdentifierType";
    public const string UsCorePatientProfile = "http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient";
    public const string CarinBbPatientProfile = "http://hl7.org/fhir/us/carin-bb/StructureDefinition/C4BB-Patient|2.0.0";

    public JsonObject Project(Member member) => Project(member, null);

    public JsonObject Project(Member member, MemberPcpResponse? pcp)
    {
        ArgumentNullException.ThrowIfNull(member);

        var patient = new JsonObject
        {
            ["resourceType"] = "Patient",
            // The member id, not the internal document id: every CHO resource
            // references a patient as Patient/{memberId} (ExplanationOfBenefit,
            // Coverage, the SMART launch context), so this is the id a client
            // following one of those references must land on (ADR 018).
            ["id"] = member.MemberId,
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
                // MB marks the member id, which CARIN BB's identifier:memberid
                // slice requires.
                ["type"] = new JsonObject
                {
                    ["coding"] = new JsonArray(Coding(V2IdentifierTypeSystem, "MB", "Member Number"))
                },
                ["system"] = FhirIdentifierSystems.MemberId,
                ["value"] = member.MemberId
            },
            // The member id is also the payer's unique identifier for the
            // person: it stays the same across coverages, which is what
            // CARIN BB's identifier:uniquememberid ("um") slice holds. A
            // second identifier rather than a second coding, because one
            // identifier matching two slices is invalid.
            new JsonObject
            {
                ["use"] = "usual",
                ["type"] = new JsonObject
                {
                    ["coding"] = new JsonArray(Coding(C4BbIdentifierTypeSystem, "um"))
                },
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
        else
        {
            // The record says whether the member has died, so false is a
            // fact the plan holds, not an absence.
            patient["deceasedBoolean"] = member.Deceased;
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
                        Bcp47Language.System, member.PreferredLanguage, Bcp47Language.DisplayName(member.PreferredLanguage)))
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
                    ["coding"] = new JsonArray(Coding(Bcp47Language.System, lang, Bcp47Language.DisplayName(lang)))
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
            // CARIN BB's patient-meta-profile-version invariant requires its
            // profile with a major.minor version; US Core sets no such rule.
            ["profile"] = new JsonArray(UsCorePatientProfile, CarinBbPatientProfile)
        };

        return patient;
    }

    /// <summary>
    /// X12 834 DMG03 to FHIR administrative-gender.
    ///
    /// <para>
    /// The fallthrough is not a mapping. US Core requires <c>Patient.gender</c>, so
    /// the element cannot be omitted to say "CHO holds a value it does not
    /// recognise", and <c>unknown</c> is the only conformant code left — which is
    /// why <see cref="StoredGenderCodes"/> refuses an unrecognised code at the
    /// request instead. Records written before that validation existed still reach
    /// here, and still have to project to something.
    /// </para>
    /// </summary>
    private static string MapGender(string? g) => g?.ToUpperInvariant() switch
    {
        StoredGenderCodes.Male => "male",
        StoredGenderCodes.Female => "female",
        _ => "unknown"
    };

    private static bool HasAddress(Member m) =>
        !string.IsNullOrEmpty(m.Address) || !string.IsNullOrEmpty(m.City) ||
        !string.IsNullOrEmpty(m.State) || !string.IsNullOrEmpty(m.ZipCode);
}
