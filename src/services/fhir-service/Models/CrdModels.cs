using System.Text.Json;
using System.Text.Json.Serialization;

namespace FhirService.Models;

// ── CDS Hooks Request Models ─────────────────────────────────────────────────

public class CrdHookRequest
{
    [JsonPropertyName("hookInstance")]
    public string HookInstance { get; set; } = string.Empty;

    [JsonPropertyName("hook")]
    public string Hook { get; set; } = string.Empty;

    [JsonPropertyName("fhirServer")]
    public string? FhirServer { get; set; }

    [JsonPropertyName("context")]
    public CrdHookContext? Context { get; set; }

    [JsonPropertyName("prefetch")]
    public Dictionary<string, System.Text.Json.JsonElement>? Prefetch { get; set; }
}

public class CrdHookContext
{
    [JsonPropertyName("userId")]
    public string? UserId { get; set; }

    [JsonPropertyName("patientId")]
    public string? PatientId { get; set; }

    [JsonPropertyName("draftOrders")]
    public CrdDraftOrders? DraftOrders { get; set; }
}

public class CrdDraftOrders
{
    [JsonPropertyName("resourceType")]
    public string ResourceType { get; set; } = "Bundle";

    [JsonPropertyName("entry")]
    public List<CrdDraftOrderEntry>? Entry { get; set; }
}

public class CrdDraftOrderEntry
{
    private static readonly JsonSerializerOptions SerializerOptions = new();
    private CrdDraftOrderResource? _typed;
    private bool _deserialized;

    /// <summary>
    /// The draft order exactly as the EHR sent it.
    ///
    /// <para>
    /// Kept verbatim because a CDS Hooks system action asks the EHR to
    /// <em>update</em> this resource, and the only honest update is the EHR's own
    /// resource with CHO's extension added. Rebuilding it from
    /// <see cref="CrdDraftOrderResource"/> would silently drop everything that
    /// model does not parse — <c>CodeableConcept.text</c>, the subject, the
    /// requester — and hand the EHR back a lesser version of its own order.
    /// </para>
    /// </summary>
    [JsonPropertyName("resource")]
    public JsonElement? RawResource { get; set; }

    /// <summary>
    /// The parsed view used for evaluation. Setting it also sets
    /// <see cref="RawResource"/>, so a hand-constructed entry still produces a
    /// usable system action.
    /// </summary>
    [JsonIgnore]
    public CrdDraftOrderResource? Resource
    {
        get
        {
            if (_deserialized) return _typed;
            _deserialized = true;
            _typed = RawResource is { ValueKind: JsonValueKind.Object } raw
                ? raw.Deserialize<CrdDraftOrderResource>(SerializerOptions)
                : null;
            return _typed;
        }
        set
        {
            _typed = value;
            _deserialized = true;
            RawResource = value is null
                ? null
                : JsonSerializer.SerializeToElement(value, SerializerOptions);
        }
    }
}

public class CrdDraftOrderResource
{
    [JsonPropertyName("resourceType")]
    public string ResourceType { get; set; } = string.Empty;

    [JsonPropertyName("code")]
    public CrdCodeableConcept? Code { get; set; }

    [JsonPropertyName("medicationCodeableConcept")]
    public CrdCodeableConcept? MedicationCodeableConcept { get; set; }
}

public class CrdCodeableConcept
{
    [JsonPropertyName("coding")]
    public List<CrdCoding>? Coding { get; set; }
}

public class CrdCoding
{
    [JsonPropertyName("system")]
    public string System { get; set; } = string.Empty;

    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("display")]
    public string? Display { get; set; }
}

// ── Terminology Service Models ───────────────────────────────────────────────

public class TranslatedCode
{
    public CrdCoding OriginalCode { get; set; }
    public CrdCoding? TranslatedCoding { get; set; }
    public bool WasTranslated { get; set; }

    public TranslatedCode(CrdCoding original)
    {
        OriginalCode = original;
        WasTranslated = false;
    }

    public TranslatedCode(CrdCoding original, CrdCoding translated)
    {
        OriginalCode = original;
        TranslatedCoding = translated;
        WasTranslated = true;
    }

    /// <summary>Returns the translated code if available, otherwise the original.</summary>
    public string EffectiveCode => TranslatedCoding?.Code ?? OriginalCode.Code;
}

// ── CDS Hooks Response Models ────────────────────────────────────────────────

public class CrdCardResponse
{
    [JsonPropertyName("cards")]
    public List<CrdCard> Cards { get; set; } = new();

    /// <summary>
    /// Changes CHO asks the EHR to apply without a user accepting a suggestion.
    /// This is where the machine-readable determination lives: a card is prose
    /// an EHR can display, a system action is an answer it can branch on.
    ///
    /// <para>
    /// Omitted from the JSON when empty, so a response with nothing to assert is
    /// byte-for-byte what it was before CHO emitted system actions at all.
    /// </para>
    /// </summary>
    [JsonPropertyName("systemActions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CdsHooksSystemAction>? SystemActions { get; set; }
}

public class CrdCard
{
    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    [JsonPropertyName("indicator")]
    public string Indicator { get; set; } = "info";

    [JsonPropertyName("source")]
    public CrdCardSource Source { get; set; } = new();

    [JsonPropertyName("suggestions")]
    public List<CrdCardSuggestion> Suggestions { get; set; } = new();

    [JsonPropertyName("links")]
    public List<CrdCardLink> Links { get; set; } = new();
}

public class CrdCardSource
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "Cloud Health Office";

    [JsonPropertyName("url")]
    public string? Url { get; set; } = "https://cloudhealthoffice.com";

    [JsonPropertyName("topic")]
    public CrdCoding? Topic { get; set; }
}

public class CrdCardSuggestion
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("uuid")]
    public string? Uuid { get; set; }

    [JsonPropertyName("isRecommended")]
    public bool? IsRecommended { get; set; }
}

public class CrdCardLink
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "absolute";
}

// ── Code Classification (dynamic benefit lookup) ────────────────────────────

public class CrdCodeClassification
{
    public HashSet<string> AuthRequiredCodes { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> AutoApprovedCodes { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> DocumentationRequiredCodes { get; set; } = new(StringComparer.Ordinal);
    public DateTimeOffset LoadedAt { get; set; } = DateTimeOffset.UtcNow;
}

// ── Evaluation Result ────────────────────────────────────────────────────────

public class CrdEvaluationResult
{
    public List<CrdCard> Cards { get; set; } = new();

    /// <summary>
    /// The system actions carrying the same determinations the cards describe.
    /// Empty when CHO could not resolve the member's Coverage: the
    /// <c>coverage-information</c> extension requires a Coverage reference, and
    /// an assertion that names no coverage is not a conformant assertion. The
    /// cards still answer.
    /// </summary>
    public List<CdsHooksSystemAction> SystemActions { get; set; } = new();
    public int CodesEvaluated { get; set; }
    public int TranslationsPerformed { get; set; }
    public long ElapsedMs { get; set; }
}

// ── Discovery Models ─────────────────────────────────────────────────────────

public class CrdDiscoveryResponse
{
    [JsonPropertyName("services")]
    public List<CrdServiceDefinition> Services { get; set; } = new();
}

public class CrdServiceDefinition
{
    [JsonPropertyName("hook")]
    public string Hook { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("prefetch")]
    public Dictionary<string, string>? Prefetch { get; set; }
}
