using CloudHealthOffice.Infrastructure.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace CloudHealthOffice.Infrastructure.Tests.Serialization;

/// <summary>
/// A calendar date is stored as midnight UTC of that date whatever the
/// value's Kind and the host's time zone. With the driver's default
/// serializer an Unspecified 2025-01-01 was stored as 2025-01-01T07:00Z on a
/// UTC-7 host (CI's TZ=America/Phoenix leg runs these on such a host).
/// </summary>
public class UtcCalendarDateSerializerTests
{
    private sealed class Doc
    {
        [BsonSerializer(typeof(UtcCalendarDateSerializer))]
        public DateTime Date { get; set; }

        [BsonSerializer(typeof(NullableUtcCalendarDateSerializer))]
        public DateTime? MaybeDate { get; set; }
    }

    private static readonly DateTime MidnightUtc = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    public void StoresTheWallClockDate_AsUtc_WhateverTheKind(DateTimeKind kind)
    {
        var date = DateTime.SpecifyKind(new DateTime(2025, 1, 1), kind);

        var bson = new Doc { Date = date, MaybeDate = date }.ToBsonDocument();

        bson["Date"].ToUniversalTime().Should().Be(MidnightUtc);
        bson["MaybeDate"].ToUniversalTime().Should().Be(MidnightUtc);
    }

    [Fact]
    public void RoundTrips_AsTheSameDate_WithUtcKind()
    {
        var bson = new Doc { Date = new DateTime(2025, 1, 1), MaybeDate = new DateTime(2025, 12, 31) }.ToBsonDocument();

        var back = BsonSerializer.Deserialize<Doc>(bson);

        back.Date.Should().Be(new DateTime(2025, 1, 1));
        back.Date.Kind.Should().Be(DateTimeKind.Utc);
        back.MaybeDate.Should().Be(new DateTime(2025, 12, 31));
        back.MaybeDate!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Null_RoundTrips()
    {
        var bson = new Doc { Date = MidnightUtc, MaybeDate = null }.ToBsonDocument();

        bson["MaybeDate"].IsBsonNull.Should().BeTrue();
        BsonSerializer.Deserialize<Doc>(bson).MaybeDate.Should().BeNull();
    }

    [Fact]
    public void ReadsADateWrittenByTheDefaultSerializerOnAUtcHost_Unchanged()
    {
        // What existing documents (written by UTC pods) hold: midnight UTC.
        var legacy = new BsonDocument
        {
            { "Date", new BsonDateTime(MidnightUtc) },
            { "MaybeDate", new BsonDateTime(MidnightUtc) },
        };

        var back = BsonSerializer.Deserialize<Doc>(legacy);

        back.Date.Should().Be(new DateTime(2025, 1, 1));
        back.MaybeDate.Should().Be(new DateTime(2025, 1, 1));
    }
}
