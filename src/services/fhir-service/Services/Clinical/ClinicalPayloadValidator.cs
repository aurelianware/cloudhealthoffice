using System.Text.Json;
using Hl7.Fhir.Model;

namespace FhirService.Services.Clinical;

/// <summary>Why a clinical resource was not accepted into the store.</summary>
public enum ClinicalPayloadRejection
{
    None = 0,

    /// <summary>The resource carried no id, so it has no source identity to key on.</summary>
    MissingSourceId = 1,

    /// <summary>The source id is longer than a FHIR id may be.</summary>
    OversizedSourceId = 2,

    /// <summary>The serialized resource exceeds the configured per-resource limit.</summary>
    Oversized = 3,

    /// <summary>The resource nests deeper than the configured limit.</summary>
    TooDeeplyNested = 4,

    /// <summary>The resource type is not one CHO serves as clinical data.</summary>
    UnsupportedType = 5,

    /// <summary>The resource could not be read as FHIR at all.</summary>
    Unreadable = 6,
}

/// <summary>Limits applied to an imported clinical resource before it is stored.</summary>
public sealed class ClinicalPayloadLimits
{
    public const string SectionName = "Clinical:PayloadLimits";

    /// <summary>
    /// Largest serialized clinical resource CHO will store, in bytes. One MiB is
    /// generous for a Condition or an Observation and small enough that a peer
    /// cannot use the clinical store as a blob dump. Documents belong in
    /// DocumentReference, which has its own attachment handling.
    /// </summary>
    public int MaxResourceBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Deepest element nesting accepted. FHIR resources are a few levels deep;
    /// a deeply recursive payload is an attempt to make the parser or a reader do
    /// exponential work, not clinical data.
    /// </summary>
    public int MaxDepth { get; set; } = 40;

    /// <summary>FHIR's own id length limit — a source id longer than this is malformed.</summary>
    public const int MaxSourceIdLength = 64;
}

/// <summary>
/// Gate every clinical resource passes before it becomes durable, member-visible
/// data.
///
/// It deliberately does NOT re-parse anything. The Payer-to-Payer pipeline has
/// already read the package with the Firely parser (<c>FhirJsonParser</c>, the
/// one parser this service owns), so by the time a resource reaches here it is a
/// validated FHIR POCO; building a second parser to check the first one's work
/// would be two sources of truth about what FHIR is. What is left, and what this
/// class does, is the part parsing does not answer:
///
///   * is this a resource type CHO actually serves clinically?
///   * does it carry a source identity CHO can key on, of a legal length?
///   * is it small enough, and shallow enough, to store and serve safely?
///
/// A rejected resource is COUNTED AND NAMED on the exchange, never silently
/// dropped, and the verbatim package stays archived — so a payer can be told
/// precisely what CHO did not take, and nothing is lost while it is sorted out.
/// </summary>
public sealed class ClinicalPayloadValidator
{
    private readonly ClinicalPayloadLimits _limits;

    public ClinicalPayloadValidator(ClinicalPayloadLimits? limits = null)
        => _limits = limits ?? new ClinicalPayloadLimits();

    /// <summary>
    /// Checks a parsed clinical resource and its serialized form.
    /// <paramref name="resourceJson"/> is the exact text that would be stored,
    /// so the size limit is measured on what CHO would actually keep rather than
    /// on an estimate.
    /// </summary>
    public ClinicalPayloadRejection Validate(Resource resource, string resourceJson)
    {
        if (!ClinicalResourceInventory.IsClinical(resource.TypeName))
            return ClinicalPayloadRejection.UnsupportedType;

        if (string.IsNullOrWhiteSpace(resource.Id))
            return ClinicalPayloadRejection.MissingSourceId;

        if (resource.Id.Length > ClinicalPayloadLimits.MaxSourceIdLength)
            return ClinicalPayloadRejection.OversizedSourceId;

        if (System.Text.Encoding.UTF8.GetByteCount(resourceJson) > _limits.MaxResourceBytes)
            return ClinicalPayloadRejection.Oversized;

        if (!DepthWithinLimit(resourceJson, _limits.MaxDepth))
            return ClinicalPayloadRejection.TooDeeplyNested;

        return ClinicalPayloadRejection.None;
    }

    /// <summary>
    /// Walks the JSON with a streaming reader — no document tree is built, so a
    /// payload designed to blow up an object graph is rejected without ever
    /// being materialized as one.
    /// </summary>
    private static bool DepthWithinLimit(string json, int maxDepth)
    {
        try
        {
            var reader = new Utf8JsonReader(
                System.Text.Encoding.UTF8.GetBytes(json),
                new JsonReaderOptions { MaxDepth = Math.Max(1, maxDepth) });

            while (reader.Read()) { }
            return true;
        }
        catch (JsonException)
        {
            // Either deeper than the limit or not readable as JSON. Both are
            // refusals, and the caller records the category rather than the
            // message, which could carry payload content.
            return false;
        }
    }
}
