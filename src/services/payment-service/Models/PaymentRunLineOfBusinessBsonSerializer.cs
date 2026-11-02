using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace PaymentService.Models;

/// <summary>
/// BSON for a payment run's <see cref="LineOfBusiness"/>. Writes the shared name. Reads a
/// name (the shared ones and the old "Marketplace"), and reads a stored number as
/// payment-service's old unnumbered enum (0 Commercial, 1 Medicare, 2 Medicaid,
/// 3 Marketplace = Exchange), because payment-service never stored any other numbers:
/// a run is read correctly before <c>PaymentRunLineOfBusinessMigrationHostedService</c>
/// has rewritten it, or if that rewrite could not run.
/// </summary>
public sealed class PaymentRunLineOfBusinessBsonSerializer : SerializerBase<LineOfBusiness>
{
    /// <summary>payment-service's old enum, in its stored order.</summary>
    public static IReadOnlyList<LineOfBusiness> LegacyOrder { get; } =
        [LineOfBusiness.Commercial, LineOfBusiness.Medicare, LineOfBusiness.Medicaid, LineOfBusiness.Exchange];

    private static readonly LineOfBusinessBsonSerializer Shared = new(BsonType.String);

    public override LineOfBusiness Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        return reader.GetCurrentBsonType() switch
        {
            BsonType.Int32 => FromLegacy(reader.ReadInt32()),
            BsonType.Int64 => FromLegacy(reader.ReadInt64()),
            _ => Shared.Deserialize(context, args),
        };
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, LineOfBusiness value)
        => Shared.Serialize(context, args, value);

    /// <summary>The line of business payment-service's old number <paramref name="n"/> meant; Unknown when none.</summary>
    public static LineOfBusiness FromLegacy(long n) => n >= 0 && n < LegacyOrder.Count ? LegacyOrder[(int)n] : LineOfBusiness.Unknown;
}
