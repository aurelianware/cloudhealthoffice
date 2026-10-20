using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace FhirService.Models;

/// <summary>
/// A FHIR resource exactly as a CHO service projected it, carried in a
/// lightweight bundle (ADR 020): the Payer-to-Payer export sends the Patient,
/// Coverage and ExplanationOfBenefit resources member-service, coverage-service
/// and claims-service return, unchanged, instead of re-mapping them.
/// </summary>
[JsonConverter(typeof(FhirJsonResourceConverter))]
public sealed record FhirJsonResource : FhirResource
{
    public FhirJsonResource(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);
        Json = json;
        Id = json["id"]?.GetValue<string>();
    }

    /// <summary>The resource JSON as received.</summary>
    public JsonObject Json { get; }

    public override string ResourceType =>
        Json["resourceType"]?.GetValue<string>() ?? throw new InvalidOperationException("A FHIR resource has no resourceType.");
}

/// <summary>Writes <see cref="FhirJsonResource.Json"/> as the resource itself.</summary>
public sealed class FhirJsonResourceConverter : JsonConverter<FhirJsonResource>
{
    public override FhirJsonResource? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonNode.Parse(ref reader) is JsonObject json ? new FhirJsonResource(json) : null;

    public override void Write(Utf8JsonWriter writer, FhirJsonResource value, JsonSerializerOptions options) =>
        value.Json.WriteTo(writer);
}
