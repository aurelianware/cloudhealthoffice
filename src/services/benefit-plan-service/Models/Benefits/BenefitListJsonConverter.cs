using System.Text.Json;
using System.Text.Json.Serialization;

namespace BenefitPlanService.Models.Benefits;

/// <summary>
/// Applies <see cref="BenefitJsonConverter"/> to each element of
/// <see cref="BenefitPlan.Benefits"/>, whatever serializer options are in
/// use. Put on the property (not on <see cref="Benefit"/>, which would be
/// inherited by every subclass and recurse), it keeps the typed benefits'
/// <c>benefitType</c> discriminator and type-specific facets on any
/// System.Text.Json path that does not register
/// <see cref="BenefitJsonConverter"/> itself.
/// </summary>
public sealed class BenefitListJsonConverter : JsonConverter<List<Benefit>>
{
    private static readonly BenefitJsonConverter Element = new();

    public override List<Benefit> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException($"Expected an array of benefits, got {reader.TokenType}.");

        var benefits = new List<Benefit>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            benefits.Add(Element.Read(ref reader, typeof(Benefit), options));
        return benefits;
    }

    public override void Write(Utf8JsonWriter writer, List<Benefit> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var benefit in value)
            Element.Write(writer, benefit, options);
        writer.WriteEndArray();
    }
}
