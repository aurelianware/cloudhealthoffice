// TODO: extract to a shared FHIR-infrastructure project. Mirrors
// provider-service's ProviderService.Services.FhirExtensionBuilder
// verbatim, which itself mirrors member-service's. Deliberate
// replication rather than a project reference because cross-service
// projects in CHO today don't share infrastructure and adding one is
// its own architectural decision (Phase 2 cleanup PR). Capability BP 5.8
// — first benefit-plan-domain projector to need these helpers.

using System.Text.Json.Nodes;

namespace BenefitPlanService.Services;

/// <summary>
/// Small helper for assembling FHIR extension nodes. Keeps US Core /
/// Plan-Net extension wiring DRY across resource projectors.
/// </summary>
internal static class FhirExtensionBuilder
{
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

    public static JsonObject ExtensionBoolean(string url, bool value) => new()
    {
        ["url"] = url,
        ["valueBoolean"] = value
    };

    public static JsonObject ExtensionCode(string url, string value) => new()
    {
        ["url"] = url,
        ["valueCode"] = value
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

    /// <summary>
    /// Build a FHIR Money node. Currency defaults to USD because CHO is
    /// US-only today; emitting under a CHO consumer that ever served
    /// non-USD plans would need a per-plan currency field on
    /// BenefitPlan, which doesn't exist.
    /// </summary>
    public static JsonObject Money(decimal amount, string currency = "USD") => new()
    {
        ["value"] = (double)amount,
        ["currency"] = currency
    };

    /// <summary>
    /// Build a FHIR Quantity node. Used for visit limits ("12 visits per
    /// year") and coinsurance percentages ("20 %").
    /// </summary>
    public static JsonObject Quantity(decimal value, string unit, string? system = null, string? code = null)
    {
        var q = new JsonObject
        {
            ["value"] = (double)value,
            ["unit"] = unit
        };
        if (!string.IsNullOrEmpty(system)) q["system"] = system;
        if (!string.IsNullOrEmpty(code)) q["code"] = code;
        return q;
    }
}
