// TODO: extract to a shared FHIR-infrastructure project. Mirrors
// member-service's MemberService.Services.FhirExtensionBuilder verbatim
// (same helpers, same US Core URL constants). Deliberate replication
// rather than a project reference because cross-service projects in CHO
// today don't share infrastructure and adding one is its own
// architectural decision (Phase 2 cleanup PR).

using System.Text.Json.Nodes;

namespace ProviderService.Services;

/// <summary>
/// Small helper for assembling FHIR extension nodes. Keeps US Core
/// extension wiring DRY across resource projectors.
/// </summary>
internal static class FhirExtensionBuilder
{
    public const string UsCoreRace           = "http://hl7.org/fhir/us/core/StructureDefinition/us-core-race";
    public const string UsCoreEthnicity      = "http://hl7.org/fhir/us/core/StructureDefinition/us-core-ethnicity";
    public const string UsCoreBirthSex       = "http://hl7.org/fhir/us/core/StructureDefinition/us-core-birthsex";
    public const string UsCoreGenderIdentity = "http://hl7.org/fhir/us/core/StructureDefinition/us-core-genderIdentity";

    public static JsonObject Coding(string system, string code, string? display = null)
    {
        var coding = new JsonObject
        {
            ["system"] = system,
            ["code"] = code
        };
        if (!string.IsNullOrEmpty(display)) coding["display"] = display;
        return coding;
    }

    public static JsonObject ExtensionString(string url, string value) => new()
    {
        ["url"] = url,
        ["valueString"] = value
    };

    public static JsonObject ExtensionInteger(string url, int value) => new()
    {
        ["url"] = url,
        ["valueInteger"] = value
    };

    public static JsonObject ExtensionDateTime(string url, DateTimeOffset value) => new()
    {
        ["url"] = url,
        ["valueDateTime"] = value.ToString("o")
    };

    public static JsonObject ExtensionCoding(string url, JsonObject coding) => new()
    {
        ["url"] = url,
        ["valueCoding"] = coding
    };

    public static JsonObject ExtensionCodeableConcept(string url, JsonObject codeableConcept) => new()
    {
        ["url"] = url,
        ["valueCodeableConcept"] = codeableConcept
    };

    /// <summary>
    /// Build a CodeableConcept JsonObject. Either <paramref name="coding"/>
    /// or <paramref name="text"/> (or both) must be non-empty; a fully
    /// empty CodeableConcept is not valid FHIR.
    /// </summary>
    public static JsonObject CodeableConcept(JsonObject? coding = null, string? text = null)
    {
        var concept = new JsonObject();
        if (coding != null)
        {
            concept["coding"] = new JsonArray(coding);
        }
        if (!string.IsNullOrEmpty(text))
        {
            concept["text"] = text;
        }
        return concept;
    }

    /// <summary>
    /// Build a CodeableConcept JsonObject with multiple coding entries.
    /// </summary>
    public static JsonObject CodeableConcept(IEnumerable<JsonObject> codings, string? text = null)
    {
        var concept = new JsonObject();
        var array = new JsonArray();
        foreach (var c in codings) array.Add(c);
        if (array.Count > 0) concept["coding"] = array;
        if (!string.IsNullOrEmpty(text)) concept["text"] = text;
        return concept;
    }
}
