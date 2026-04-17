using System.Text.Json.Nodes;

namespace MemberService.Services;

/// <summary>
/// Small helper for assembling FHIR extension nodes. Keeps US Core
/// race/ethnicity/genderIdentity/birthSex wiring DRY.
/// </summary>
internal static class FhirExtensionBuilder
{
    public const string UsCoreRace = "http://hl7.org/fhir/us/core/StructureDefinition/us-core-race";
    public const string UsCoreEthnicity = "http://hl7.org/fhir/us/core/StructureDefinition/us-core-ethnicity";
    public const string UsCoreBirthSex = "http://hl7.org/fhir/us/core/StructureDefinition/us-core-birthsex";
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
    /// Build a US Core race/ethnicity extension composite:
    /// ombCategory (0..1 for ethnicity, 0..5 for race) + detailed (0..*) + text (1..1).
    /// </summary>
    public static JsonObject? BuildRaceExtension(
        Models.CodedConcept? omb,
        IReadOnlyList<Models.CodedConcept> detail,
        string? text)
    {
        if (omb == null && detail.Count == 0 && string.IsNullOrEmpty(text)) return null;

        var extensions = new JsonArray();
        if (omb != null)
        {
            extensions.Add(ExtensionCoding(
                "ombCategory",
                Coding(omb.System, omb.Code, omb.Display)));
        }
        foreach (var d in detail)
        {
            extensions.Add(ExtensionCoding(
                "detailed",
                Coding(d.System, d.Code, d.Display)));
        }
        extensions.Add(ExtensionString("text", text ?? omb?.Display ?? "Unknown"));

        return new JsonObject
        {
            ["url"] = UsCoreRace,
            ["extension"] = extensions
        };
    }

    public static JsonObject? BuildEthnicityExtension(
        Models.CodedConcept? omb,
        IReadOnlyList<Models.CodedConcept> detail,
        string? text)
    {
        var ext = BuildRaceExtension(omb, detail, text);
        if (ext == null) return null;
        ext["url"] = UsCoreEthnicity;
        return ext;
    }
}
