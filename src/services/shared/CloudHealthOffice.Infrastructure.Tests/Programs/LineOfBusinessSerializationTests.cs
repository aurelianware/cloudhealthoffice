using System.Text.Json;
using CloudHealthOffice.Infrastructure.Programs;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace CloudHealthOffice.Infrastructure.Tests.Programs;

public class LineOfBusinessSerializationTests
{
    private sealed class Holder
    {
        [BsonSerializer(typeof(LineOfBusinessBsonSerializer))]
        public LineOfBusiness LineOfBusiness { get; set; }
    }

    private static JsonSerializerOptions Json(bool allowNumbers = false) =>
        new() { Converters = { new LineOfBusinessJsonConverter(allowNumbers) } };

    [Theory]
    [InlineData("\"Exchange\"", LineOfBusiness.Exchange)]
    [InlineData("\"Marketplace\"", LineOfBusiness.Exchange)]
    [InlineData("\"medicaid\"", LineOfBusiness.Medicaid)]
    [InlineData("\"TRICARE\"", LineOfBusiness.TRICARE)]
    public void Json_reads_canonical_and_legacy_names(string json, LineOfBusiness expected)
        => JsonSerializer.Deserialize<LineOfBusiness>(json, Json()).Should().Be(expected);

    [Theory]
    [InlineData("\"Dental\"")]
    [InlineData("\"\"")]
    [InlineData("4")] // numbers differed between services: refused unless allowed
    [InlineData("null")]
    public void Json_refuses_anything_else(string json)
    {
        var act = () => JsonSerializer.Deserialize<LineOfBusiness>(json, Json());
        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Json_reads_numbers_when_allowed_and_always_writes_the_name()
    {
        JsonSerializer.Deserialize<LineOfBusiness>("4", Json(allowNumbers: true)).Should().Be(LineOfBusiness.Exchange);
        JsonSerializer.Serialize(LineOfBusiness.Exchange, Json()).Should().Be("\"Exchange\"");
    }

    [Theory]
    [InlineData("Marketplace", LineOfBusiness.Exchange)]
    [InlineData("MedicarePartD", LineOfBusiness.Medicare)]
    [InlineData("Exchange", LineOfBusiness.Exchange)]
    [InlineData("Dental", LineOfBusiness.Unknown)] // one bad document never fails a query
    public void Bson_reads_stored_names(string stored, LineOfBusiness expected)
        => BsonSerializer.Deserialize<Holder>(new BsonDocument("LineOfBusiness", stored)).LineOfBusiness.Should().Be(expected);

    [Theory]
    [InlineData(4, LineOfBusiness.Exchange)]
    [InlineData(0, LineOfBusiness.Unknown)]
    [InlineData(9, LineOfBusiness.Unknown)]
    public void Bson_reads_stored_numbers(int stored, LineOfBusiness expected)
        => BsonSerializer.Deserialize<Holder>(new BsonDocument("LineOfBusiness", stored)).LineOfBusiness.Should().Be(expected);

    [Fact]
    public void Bson_writes_the_name_or_the_number_as_configured()
    {
        new Holder { LineOfBusiness = LineOfBusiness.Exchange }.ToBsonDocument()["LineOfBusiness"]
            .Should().Be(new BsonString("Exchange"));

        var asInt = new LineOfBusinessBsonSerializer().WithRepresentation(BsonType.Int32);
        var doc = new BsonDocument();
        using (var writer = new MongoDB.Bson.IO.BsonDocumentWriter(doc))
        {
            writer.WriteStartDocument();
            writer.WriteName("v");
            asInt.Serialize(BsonSerializationContext.CreateRoot(writer), LineOfBusiness.VA);
            writer.WriteEndDocument();
        }
        doc["v"].Should().Be(new BsonInt32(6));
    }
}
