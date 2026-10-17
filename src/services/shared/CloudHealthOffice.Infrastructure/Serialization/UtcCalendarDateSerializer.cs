using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace CloudHealthOffice.Infrastructure.Serialization;

/// <summary>
/// BSON serializer for calendar-date <see cref="DateTime"/> fields such as
/// plan-year boundaries. The driver's default serializer treats
/// <see cref="DateTimeKind.Unspecified"/> as local time on write, so on a
/// non-UTC host <c>new DateTime(2025, 1, 1)</c> is stored as
/// 2025-01-01T07:00Z (UTC-7) or 2024-12-31T22:00Z (UTC+2) and read back
/// shifted — the latter even lands in the previous year.
/// <para>This serializer stores the value's wall-clock date and time as UTC,
/// whatever its <see cref="DateTime.Kind"/> and whatever the host time zone,
/// and reads it back as <see cref="DateTimeKind.Utc"/>. On a UTC host it
/// writes exactly what the default serializer wrote, so documents written by
/// a UTC host read back unchanged.</para>
/// </summary>
public sealed class UtcCalendarDateSerializer : StructSerializerBase<DateTime>
{
    public static readonly UtcCalendarDateSerializer Instance = new();

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, DateTime value) =>
        context.Writer.WriteDateTime(BsonUtils.ToMillisecondsSinceEpoch(DateTime.SpecifyKind(value, DateTimeKind.Utc)));

    public override DateTime Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args) =>
        BsonUtils.ToDateTimeFromMillisecondsSinceEpoch(context.Reader.ReadDateTime());
}

/// <summary>Nullable form of <see cref="UtcCalendarDateSerializer"/>.</summary>
public sealed class NullableUtcCalendarDateSerializer : SerializerBase<DateTime?>
{
    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, DateTime? value)
    {
        if (value is { } date)
            UtcCalendarDateSerializer.Instance.Serialize(context, args, date);
        else
            context.Writer.WriteNull();
    }

    public override DateTime? Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        if (context.Reader.GetCurrentBsonType() == BsonType.Null)
        {
            context.Reader.ReadNull();
            return null;
        }
        return UtcCalendarDateSerializer.Instance.Deserialize(context, args);
    }
}
