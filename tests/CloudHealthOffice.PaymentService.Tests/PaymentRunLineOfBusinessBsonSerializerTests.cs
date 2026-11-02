using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using PaymentService.Models;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests;

/// <summary>A payment run's line of business in BSON, without a database.</summary>
public class PaymentRunLineOfBusinessBsonSerializerTests
{
    private static readonly LegacyLineOfBusinessBsonSerializer Serializer = global::PaymentService.HostedServices.PaymentRunLineOfBusinessMigrationHostedService.Serializer;

    private static LineOfBusiness Read(BsonValue stored)
    {
        var doc = new BsonDocument("v", stored);
        using var reader = new BsonDocumentReader(doc);
        reader.ReadStartDocument();
        reader.ReadName("v");
        return Serializer.Deserialize(BsonDeserializationContext.CreateRoot(reader));
    }

    private static BsonValue Write(LineOfBusiness value)
    {
        var doc = new BsonDocument();
        using var writer = new BsonDocumentWriter(doc);
        writer.WriteStartDocument();
        writer.WriteName("v");
        Serializer.Serialize(BsonSerializationContext.CreateRoot(writer), value);
        writer.WriteEndDocument();
        return doc["v"];
    }

    [Theory]
    [InlineData(0, LineOfBusiness.Commercial)]
    [InlineData(1, LineOfBusiness.Medicare)]
    [InlineData(2, LineOfBusiness.Medicaid)]
    [InlineData(3, LineOfBusiness.Exchange)]
    [InlineData(4, LineOfBusiness.Unknown)]
    [InlineData(-1, LineOfBusiness.Unknown)]
    public void A_stored_number_is_read_as_payment_services_old_enum(int stored, LineOfBusiness expected)
    {
        Assert.Equal(expected, Read(stored));
        Assert.Equal(expected, Read(new BsonInt64(stored)));
    }

    [Theory]
    [InlineData("Exchange", LineOfBusiness.Exchange)]
    [InlineData("Marketplace", LineOfBusiness.Exchange)]
    [InlineData("TRICARE", LineOfBusiness.TRICARE)]
    [InlineData("nonsense", LineOfBusiness.Unknown)]
    public void A_stored_name_is_read_as_the_shared_enum(string stored, LineOfBusiness expected)
        => Assert.Equal(expected, Read(stored));

    [Fact]
    public void It_writes_the_shared_name()
        => Assert.Equal(new BsonString("Exchange"), Write(LineOfBusiness.Exchange));
}
