using System.Text.Json;
using System.Text.Json.Serialization;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// A CDS Hooks service invocation request.
///
/// A plain model of the wire shape rather than a builder that hides it: a
/// scenario that constructs one of these shows exactly what it sent, which is the
/// only way an interoperability failure stays diagnosable. Context and prefetch
/// are untyped JSON because their shape is hook-specific and defined by the
/// service being called — the harness must not impose a shape the external
/// implementation did not advertise.
/// </summary>
public sealed record CdsHooksRequest
{
    [JsonPropertyName("hookInstance")] public string HookInstance { get; init; } = "";
    [JsonPropertyName("hook")] public string Hook { get; init; } = "";

    /// <summary>
    /// The FHIR server the CDS service may call back to for anything not supplied
    /// in prefetch. Never a placeholder: a scenario either supplies every prefetch
    /// key the service needs (so no callback occurs, verified by observation), or
    /// points this at a server the harness actually runs.
    /// </summary>
    [JsonPropertyName("fhirServer")] public string? FhirServer { get; init; }

    [JsonPropertyName("context")] public Dictionary<string, object> Context { get; init; } = new();
    [JsonPropertyName("prefetch")] public Dictionary<string, object> Prefetch { get; init; } = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);
}

/// <summary>
/// A CDS Hooks service response.
///
/// Per the CDS Hooks specification a response carries `cards` (possibly empty)
/// and optionally `systemActions`. Da Vinci CRD conveys its coverage
/// determination through a system action that decorates the ordered resource with
/// the coverage-information extension, so a scenario that looked only at cards
/// would miss the actual decision entirely.
/// </summary>
public sealed record CdsHooksResponse
{
    [JsonPropertyName("cards")] public List<CdsHooksCard>? Cards { get; init; }
    [JsonPropertyName("systemActions")] public List<CdsHooksSystemAction>? SystemActions { get; init; }

    /// <summary>
    /// True when the payload has a `cards` member at all. CDS Hooks requires it to
    /// be present even when empty, so its absence is a protocol violation rather
    /// than "no recommendations".
    /// </summary>
    [JsonIgnore] public bool HasCardsMember => Cards is not null;

    public static CdsHooksResponse? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<CdsHooksResponse>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Structural problems with the response, as CDS Hooks defines them. Empty
    /// means the payload is a well-formed CDS Hooks response — it says nothing
    /// about whether the clinical decision it carries is correct.
    /// </summary>
    public IReadOnlyList<string> ProtocolViolations()
    {
        var problems = new List<string>();
        if (!HasCardsMember)
        {
            problems.Add("response has no 'cards' member; CDS Hooks requires it even when empty");
        }

        foreach (var (card, index) in (Cards ?? new List<CdsHooksCard>()).Select((c, i) => (c, i)))
        {
            problems.AddRange(card.ProtocolViolations().Select(p => $"cards[{index}]: {p}"));
        }

        return problems;
    }
}

/// <summary>One CDS Hooks card.</summary>
public sealed record CdsHooksCard
{
    [JsonPropertyName("summary")] public string? Summary { get; init; }
    [JsonPropertyName("detail")] public string? Detail { get; init; }
    [JsonPropertyName("indicator")] public string? Indicator { get; init; }
    [JsonPropertyName("source")] public CdsHooksCardSource? Source { get; init; }
    [JsonPropertyName("links")] public List<CdsHooksLink>? Links { get; init; }

    /// <summary>Indicators CDS Hooks defines. Anything else is out of specification.</summary>
    private static readonly HashSet<string> ValidIndicators =
        new(StringComparer.Ordinal) { "info", "warning", "critical" };

    public IReadOnlyList<string> ProtocolViolations()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Summary))
        {
            problems.Add("card.summary is required");
        }
        else if (Summary.Length > 140)
        {
            problems.Add($"card.summary exceeds the 140-character limit ({Summary.Length})");
        }

        if (string.IsNullOrWhiteSpace(Indicator))
        {
            problems.Add("card.indicator is required");
        }
        else if (!ValidIndicators.Contains(Indicator))
        {
            problems.Add($"card.indicator '{Indicator}' is not one of info|warning|critical");
        }

        if (Source is null)
        {
            problems.Add("card.source is required");
        }
        else if (string.IsNullOrWhiteSpace(Source.Label))
        {
            problems.Add("card.source.label is required");
        }

        return problems;
    }
}

public sealed record CdsHooksCardSource
{
    [JsonPropertyName("label")] public string? Label { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("topic")] public JsonElement? Topic { get; init; }
}

public sealed record CdsHooksLink
{
    [JsonPropertyName("label")] public string? Label { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
}

/// <summary>
/// A CDS Hooks system action: a proposed change to a resource, applied by the
/// client without user interaction. CRD uses it to attach coverage information to
/// the ordered resource.
/// </summary>
public sealed record CdsHooksSystemAction
{
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("resource")] public JsonElement? Resource { get; init; }

    /// <summary>resourceType of the decorated resource, when present.</summary>
    public string? ResourceType =>
        Resource is { ValueKind: JsonValueKind.Object } element
        && element.TryGetProperty("resourceType", out var type)
        && type.ValueKind == JsonValueKind.String
            ? type.GetString()
            : null;
}
