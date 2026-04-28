using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ProviderService.Models;
using static ProviderService.Services.FhirExtensionBuilder;

namespace ProviderService.Services;

/// <summary>
/// Hand-built FHIR R4 PractitionerRole projector (capability 5.8). Pattern
/// mirrors <see cref="FhirPractitionerProjector"/> (5.7): stateless,
/// deterministic, no Hl7.Fhir.R4 dependency. Source data is the
/// <see cref="NetworkParticipation"/> on an Active individual
/// <see cref="Provider"/>, optionally enriched with the resolved
/// <see cref="Organization"/> head version for the participation's
/// <c>NetworkId</c>.
/// </summary>
public sealed class FhirPractitionerRoleProjector : IFhirPractitionerRoleProjector
{
    /// <summary>
    /// FHIR R4 <c>id</c> grammar is <c>[A-Za-z0-9\-\.]{1,64}</c>. The
    /// composite-id encoding uses dash separators only, so we just need
    /// to enforce the length cap.
    /// </summary>
    private const int FhirIdMaxLength = 64;

    /// <summary>
    /// Composite shape: <c>{npi:10}-{lobInt:1+}-{yyyymmdd:8}-{networkId}</c>.
    /// The trailing capture is the network id (which itself may contain
    /// hyphens — guid-shaped chain keys are the common case), so the
    /// regex is anchored at the start to lock the first three components
    /// to fixed-shape captures.
    /// </summary>
    private static readonly Regex IdPattern = new(
        @"^(?<npi>\d{10})-(?<lob>\d+)-(?<date>\d{8})-(?<network>.+)$",
        RegexOptions.Compiled);

    public JsonObject? Project(NetworkParticipation participation, Provider provider, Organization? network)
    {
        ArgumentNullException.ThrowIfNull(participation);
        ArgumentNullException.ThrowIfNull(provider);

        // Decision 5.8/2 (premise gate): legacy participations without a
        // NetworkId are invisible to FHIR — same posture as the 5.4
        // roster API. Caller maps this to a 404 OperationOutcome on the
        // read path or skips the row on search.
        if (string.IsNullOrEmpty(participation.NetworkId)) return null;

        // Organization-type providers project as FHIR Organization
        // (capability 5.9), not PractitionerRole. Caller maps to 404.
        if (provider.ProviderType != ProviderType.Individual) return null;

        // Only project from the head Active version. Suspended /
        // Terminated / Superseded versions are not directory-eligible.
        if (provider.VersionState != ProviderVersionState.Active) return null;
        if (provider.Status != ProviderStatus.Active) return null;

        var encodedId = EncodeId(participation, provider);
        if (encodedId is null)
        {
            // Composite would exceed FHIR R4 id 64-char grammar (a
            // long-form NetworkId stretches the encoding past the cap).
            // Emitting an invalid id would silently break consumers, so
            // the row is treated as non-projectable: search omits the
            // row, and the read path falls through to the same null-
            // handling shape as the other cases above (404
            // OperationOutcome).
            return null;
        }

        var role = new JsonObject
        {
            ["resourceType"] = "PractitionerRole",
            ["id"] = encodedId,
            ["active"] = ComputeActive(participation, provider, DateTime.UtcNow),
        };

        // ── practitioner reference ──────────────────────────────────
        role["practitioner"] = new JsonObject
        {
            ["reference"] = $"Practitioner/{provider.NPI}",
            ["display"] = BuildPractitionerDisplay(provider),
        };

        // ── organization reference ──────────────────────────────────
        // Always emit when NetworkId is set; FHIR does not require the
        // reference target to be resolvable. Display name is filled in
        // when the caller supplied a resolved Organization head.
        var orgRef = new JsonObject
        {
            ["reference"] = $"Organization/{participation.NetworkId}",
        };
        if (network != null && !string.IsNullOrEmpty(network.Name))
        {
            orgRef["display"] = network.Name;
        }
        role["organization"] = orgRef;

        // ── code (network tier) ─────────────────────────────────────
        // CHO does not bind NetworkTier to a canonical CodeSystem; emit
        // a text-only CodeableConcept. Plan-Net IG accepts text-only
        // under the "extensible" binding strength.
        if (!string.IsNullOrEmpty(participation.NetworkTier))
        {
            role["code"] = new JsonArray
            {
                CodeableConcept(coding: null, text: participation.NetworkTier)
            };
        }

        // ── specialty (NUCC from Provider) ──────────────────────────
        // No per-participation Specialty field exists on
        // NetworkParticipation today (premise correction 1a). Specialty
        // is derived from the linked Provider — primary specialty as
        // NUCC-coded CodeableConcept when TaxonomyCode is set, plus
        // text-only entries for SecondarySpecialties (mirrors 5.7
        // qualification handling).
        var specialties = BuildSpecialties(provider);
        if (specialties.Count > 0) role["specialty"] = specialties;

        // ── period ──────────────────────────────────────────────────
        var period = new JsonObject
        {
            ["start"] = FormatFhirDate(participation.EffectiveDate),
        };
        if (participation.TerminationDate.HasValue)
        {
            period["end"] = FormatFhirDate(participation.TerminationDate.Value);
        }
        role["period"] = period;

        // ── telecom (from Provider) ─────────────────────────────────
        var telecom = new JsonArray();
        if (!string.IsNullOrEmpty(provider.Phone))
            telecom.Add(new JsonObject { ["system"] = "phone", ["value"] = provider.Phone, ["use"] = "work" });
        if (!string.IsNullOrEmpty(provider.Fax))
            telecom.Add(new JsonObject { ["system"] = "fax", ["value"] = provider.Fax, ["use"] = "work" });
        if (!string.IsNullOrEmpty(provider.Email))
            telecom.Add(new JsonObject { ["system"] = "email", ["value"] = provider.Email, ["use"] = "work" });
        if (telecom.Count > 0) role["telecom"] = telecom;

        // ── extension: panel-gating (Decision 9) ────────────────────
        var panelGating = BuildPanelGatingExtension(participation);
        if (panelGating != null)
        {
            role["extension"] = new JsonArray { panelGating };
        }

        // ── meta ─────────────────────────────────────────────────────
        role["meta"] = new JsonObject
        {
            ["lastUpdated"] = DateTime.SpecifyKind(provider.LastUpdatedDate, DateTimeKind.Utc).ToString("o"),
            ["profile"] = new JsonArray(
                ChoProviderFhirUrls.UsCorePractitionerRoleProfile,
                ChoProviderFhirUrls.PlanNetPractitionerRoleProfile),
        };

        return role;
    }

    public string? EncodeId(NetworkParticipation participation, Provider provider)
    {
        ArgumentNullException.ThrowIfNull(participation);
        ArgumentNullException.ThrowIfNull(provider);

        if (string.IsNullOrEmpty(provider.NPI)) return null;
        if (string.IsNullOrEmpty(participation.NetworkId)) return null;

        var lobInt = ((int)participation.LineOfBusiness).ToString(CultureInfo.InvariantCulture);
        // SpecifyKind preserves the calendar value; ToUniversalTime
        // would shift Kind=Unspecified rows by the local TZ offset.
        // Persistence layers round-trip dates as Kind=Unspecified, so
        // SpecifyKind is the safer normalisation.
        var date = DateTime.SpecifyKind(participation.EffectiveDate, DateTimeKind.Utc)
            .ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        var composite = $"{provider.NPI}-{lobInt}-{date}-{participation.NetworkId}";
        if (composite.Length > FhirIdMaxLength) return null;
        return composite;
    }

    public PractitionerRoleId? DecodeId(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;

        var match = IdPattern.Match(id);
        if (!match.Success) return null;

        var npi = match.Groups["npi"].Value;
        var lobRaw = match.Groups["lob"].Value;
        var dateRaw = match.Groups["date"].Value;
        var networkId = match.Groups["network"].Value;

        if (!int.TryParse(lobRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lobInt))
            return null;
        if (!Enum.IsDefined(typeof(LineOfBusiness), lobInt)) return null;

        if (!DateTime.TryParseExact(
                dateRaw, "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var effective))
        {
            return null;
        }

        return new PractitionerRoleId(npi, (LineOfBusiness)lobInt, effective, networkId);
    }

    /// <summary>
    /// Active flag derivation per Decision 7 of the 5.8 plan. Both the
    /// participation period and the provider's version / status must be
    /// active at <paramref name="asOf"/>. Caller passes <c>UtcNow</c> in
    /// production; tests inject a fixed instant for determinism.
    /// </summary>
    internal static bool ComputeActive(NetworkParticipation participation, Provider provider, DateTime asOf)
    {
        // SpecifyKind, not ToUniversalTime — see EncodeId for the
        // rationale on persistence-roundtrip Kind=Unspecified rows.
        var asOfUtc = DateTime.SpecifyKind(asOf, DateTimeKind.Utc);
        if (provider.VersionState != ProviderVersionState.Active) return false;
        if (provider.Status != ProviderStatus.Active) return false;
        if (DateTime.SpecifyKind(participation.EffectiveDate, DateTimeKind.Utc) > asOfUtc) return false;
        if (participation.TerminationDate.HasValue
            && DateTime.SpecifyKind(participation.TerminationDate.Value, DateTimeKind.Utc) < asOfUtc) return false;
        return true;
    }

    private static string BuildPractitionerDisplay(Provider provider)
    {
        var parts = new[] { provider.FirstName, provider.MiddleName, provider.LastName, provider.Credentials }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        return string.Join(' ', parts).Trim();
    }

    private static JsonArray BuildSpecialties(Provider provider)
    {
        var specialties = new JsonArray();

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
                    text: string.IsNullOrEmpty(provider.PrimarySpecialty)
                        ? provider.TaxonomyCode
                        : provider.PrimarySpecialty);
            }
            else
            {
                codeNode = CodeableConcept(coding: null, text: provider.PrimarySpecialty);
            }
            specialties.Add(codeNode);
        }

        foreach (var secondary in provider.SecondarySpecialties)
        {
            if (string.IsNullOrWhiteSpace(secondary)) continue;
            specialties.Add(CodeableConcept(coding: null, text: secondary));
        }

        return specialties;
    }

    /// <summary>
    /// Build the grouped panel-gating extension (capability 5.8 Decision
    /// 9). Returns null when none of the five panel-gating fields are
    /// populated, so the caller can skip emitting the parent extension
    /// entirely on legacy / unconstrained participations.
    /// </summary>
    private static JsonObject? BuildPanelGatingExtension(NetworkParticipation participation)
    {
        var inner = new JsonArray();

        if (participation.PanelLimit.HasValue)
        {
            inner.Add(ExtensionInteger("panel-limit", participation.PanelLimit.Value));
        }
        if (participation.PanelAccepted.HasValue)
        {
            inner.Add(new JsonObject
            {
                ["url"] = "panel-accepted",
                ["valueBoolean"] = participation.PanelAccepted.Value,
            });
        }
        if (participation.AcceptedLobs.Count > 0)
        {
            foreach (var lob in participation.AcceptedLobs)
            {
                inner.Add(ExtensionCoding(
                    "accepted-lobs",
                    Coding(ChoProviderFhirUrls.LineOfBusinessSystem, lob.ToString(), lob.ToString())));
            }
        }
        if (participation.MinAcceptedAgeYears.HasValue)
        {
            inner.Add(ExtensionInteger("min-accepted-age-years", participation.MinAcceptedAgeYears.Value));
        }
        if (participation.MaxAcceptedAgeYears.HasValue)
        {
            inner.Add(ExtensionInteger("max-accepted-age-years", participation.MaxAcceptedAgeYears.Value));
        }

        if (inner.Count == 0) return null;

        return new JsonObject
        {
            ["url"] = ChoProviderFhirUrls.PractitionerRolePanelGatingExt,
            ["extension"] = inner,
        };
    }

    private static string FormatFhirDate(DateTime value)
        => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
