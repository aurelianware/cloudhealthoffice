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
    [JsonPropertyName("resource")]
    public CrdDraftOrderResource? Resource { get; set; }
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
