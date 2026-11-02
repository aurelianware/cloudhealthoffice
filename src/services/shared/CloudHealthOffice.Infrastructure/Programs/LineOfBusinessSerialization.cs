using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace CloudHealthOffice.Infrastructure.Programs;

/// <summary>
/// JSON for <see cref="LineOfBusiness"/> as its canonical name. Reads canonical names and
/// the legacy spellings <see cref="LineOfBusinessNames"/> knows (Marketplace → Exchange).
/// Numbers are refused unless <c>allowNumbers</c>, because services disagreed on them.
/// A service registers it ahead of its generic enum converter; it is deliberately not an
/// attribute on the enum, so services that send numbers keep doing so.
/// </summary>
public class LineOfBusinessJsonConverter(bool allowNumbers = false) : JsonConverter<LineOfBusiness>
{
    public override LineOfBusiness Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? text = reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number when allowNumbers && reader.TryGetInt32(out var n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new JsonException($"A line of business is one of {string.Join(", ", LineOfBusinessNames.AllNames)}."),
        };
        Reject(text);
        return LineOfBusinessNames.TryParse(text, out var lob)
            ? lob
            : throw new JsonException($"'{text}' is not a line of business. Use one of {string.Join(", ", LineOfBusinessNames.AllNames)}.");
    }

    public override void Write(Utf8JsonWriter writer, LineOfBusiness value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());

    /// <summary>Lets a service refuse a spelling the shared parser would accept (appeals refuses MedicarePartD).</summary>
    protected virtual void Reject(string? text) { }
}

/// <summary>
/// BSON for <see cref="LineOfBusiness"/>. Reads a canonical or legacy name (appeals stored
/// Marketplace and MedicarePartD) or a stored number, and writes in the representation
/// given: <see cref="BsonType.String"/> (canonical name) or <see cref="BsonType.Int32"/>.
/// A value it cannot read is <see cref="LineOfBusiness.Unknown"/>, never an exception, so
/// one bad document does not fail a whole query.
/// </summary>
public sealed class LineOfBusinessBsonSerializer(BsonType representation = BsonType.String)
    : SerializerBase<LineOfBusiness>, IRepresentationConfigurable<LineOfBusinessBsonSerializer>
{
    /// <summary>Stored as the canonical name; usable from <c>[BsonSerializer(typeof(LineOfBusinessBsonSerializer))]</c>.</summary>
    public LineOfBusinessBsonSerializer() : this(BsonType.String) { }

    public BsonType Representation { get; } = representation is BsonType.String or BsonType.Int32
        ? representation
        : throw new ArgumentOutOfRangeException(nameof(representation), "String or Int32.");

    public LineOfBusinessBsonSerializer WithRepresentation(BsonType representation) => new(representation);

    IBsonSerializer IRepresentationConfigurable.WithRepresentation(BsonType representation) => WithRepresentation(representation);

    public override LineOfBusiness Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        switch (reader.GetCurrentBsonType())
        {
            case BsonType.String:
                return LineOfBusinessNames.Parse(reader.ReadString());
            case BsonType.Int32:
                return FromNumber(reader.ReadInt32());
            case BsonType.Int64:
                return FromNumber(reader.ReadInt64());
            case BsonType.Null:
                reader.ReadNull();
                return LineOfBusiness.Unknown;
            default:
                reader.SkipValue();
                return LineOfBusiness.Unknown;
        }
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, LineOfBusiness value)
    {
        if (Representation == BsonType.Int32) context.Writer.WriteInt32((int)value);
        else context.Writer.WriteString(value.ToString());
    }

    private static LineOfBusiness FromNumber(long n) =>
        n >= 1 && n <= LineOfBusinessNames.All.Count ? (LineOfBusiness)n : LineOfBusiness.Unknown;
}
