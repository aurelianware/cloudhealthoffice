using CloudHealthOffice.Infrastructure.Programs;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using Xunit;

namespace CloudHealthOffice.Infrastructure.Tests.Programs;

public class LegacyLineOfBusinessStorageTests
{
    // eligibility-service's old order: 0 Commercial … 5 VA.
    private static readonly LegacyLineOfBusinessBsonSerializer ZeroBased = new(LineOfBusinessNames.All);

    private static LineOfBusiness Read(BsonValue stored)
    {
        using var reader = new BsonDocumentReader(new BsonDocument("v", stored));
        reader.ReadStartDocument();
        reader.ReadName("v");
        return ZeroBased.Deserialize(BsonDeserializationContext.CreateRoot(reader));
    }

    [Theory]
    [InlineData(0, LineOfBusiness.Commercial)]
    [InlineData(3, LineOfBusiness.Exchange)]
    [InlineData(5, LineOfBusiness.VA)]
    [InlineData(6, LineOfBusiness.Unknown)]
    [InlineData(-1, LineOfBusiness.Unknown)]
    public void A_stored_number_reads_through_the_old_order(int stored, LineOfBusiness expected)
    {
        Read(stored).Should().Be(expected);
        Read(new BsonInt64(stored)).Should().Be(expected);
    }

    [Theory]
    [InlineData("Medicare", LineOfBusiness.Medicare)]
    [InlineData("Marketplace", LineOfBusiness.Exchange)]
    [InlineData("nonsense", LineOfBusiness.Unknown)]
    public void A_stored_name_reads_as_the_shared_enum(string stored, LineOfBusiness expected)
        => Read(stored).Should().Be(expected);

    [Fact]
    public void It_writes_the_shared_name()
    {
        var doc = new BsonDocument();
        using (var writer = new BsonDocumentWriter(doc))
        {
            writer.WriteStartDocument();
            writer.WriteName("v");
            ZeroBased.Serialize(BsonSerializationContext.CreateRoot(writer), LineOfBusiness.TRICARE);
            writer.WriteEndDocument();
        }
        doc["v"].Should().Be(new BsonString("TRICARE"));
    }

    [Theory]
    [InlineData("LineOfBusiness", new[] { "LineOfBusiness", "lineOfBusiness" })]
    [InlineData("Criteria.LineOfBusiness", new[] { "Criteria.LineOfBusiness", "criteria.lineOfBusiness" })]
    [InlineData("criteria.lineOfBusiness", new[] { "criteria.lineOfBusiness" })]
    public void Field_paths_include_their_camelCase_spelling(string path, string[] expected)
        => LegacyLineOfBusinessMigrationHostedService.WithCamelCase(path).Should().Equal(expected);
}
