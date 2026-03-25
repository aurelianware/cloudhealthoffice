using System.Text;
using System.Text.Json;
using CapitationService.Middleware;

namespace CapitationService.Tests.Middleware;

public class CosmosSerializerTests
{
    private readonly CosmosSystemTextJsonSerializer _serializer = new();

    private class TestDocument
    {
        public string Id { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public TestStatus Status { get; set; }
        public decimal Amount { get; set; }
        public string? NullableField { get; set; }
    }

    private enum TestStatus
    {
        Active,
        Inactive
    }

    #region ToStream

    [Fact]
    public void ToStream_SerializesWithCamelCase()
    {
        var doc = new TestDocument { Id = "123", TenantId = "t-1", Amount = 99.50m };

        var stream = _serializer.ToStream(doc);
        var json = new StreamReader(stream).ReadToEnd();

        json.Should().Contain("\"id\":");     // camelCase
        json.Should().Contain("\"tenantId\":"); // camelCase
        json.Should().Contain("\"amount\":");
        json.Should().NotContain("\"Id\":");   // NOT PascalCase
    }

    [Fact]
    public void ToStream_OmitsNullFields()
    {
        var doc = new TestDocument { Id = "123", NullableField = null };

        var stream = _serializer.ToStream(doc);
        var json = new StreamReader(stream).ReadToEnd();

        json.Should().NotContain("nullableField");
    }

    [Fact]
    public void ToStream_SerializesEnumsAsStrings()
    {
        var doc = new TestDocument { Id = "123", Status = TestStatus.Active };

        var stream = _serializer.ToStream(doc);
        var json = new StreamReader(stream).ReadToEnd();

        json.Should().Contain("\"active\""); // camelCase enum string
        json.Should().NotContain("\"0\"");   // NOT numeric
    }

    [Fact]
    public void ToStream_StreamPositionIsZero()
    {
        var doc = new TestDocument { Id = "123" };

        var stream = _serializer.ToStream(doc);

        stream.Position.Should().Be(0);
    }

    #endregion

    #region FromStream

    [Fact]
    public void FromStream_DeserializesCamelCase()
    {
        var json = """{"id":"abc","tenantId":"t-1","amount":42.50,"status":"active"}""";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var result = _serializer.FromStream<TestDocument>(stream);

        result.Should().NotBeNull();
        result.Id.Should().Be("abc");
        result.TenantId.Should().Be("t-1");
        result.Amount.Should().Be(42.50m);
        result.Status.Should().Be(TestStatus.Active);
    }

    [Fact]
    public void FromStream_EmptyStream_ReturnsDefault()
    {
        var stream = new MemoryStream(Array.Empty<byte>());

        var result = _serializer.FromStream<TestDocument>(stream);

        result.Should().BeNull();
    }

    [Fact]
    public void FromStream_RoundTrip_PreservesData()
    {
        var original = new TestDocument
        {
            Id = "round-trip",
            TenantId = "t-99",
            Status = TestStatus.Inactive,
            Amount = 1234.56m,
            NullableField = "has-value"
        };

        var stream = _serializer.ToStream(original);
        var deserialized = _serializer.FromStream<TestDocument>(stream);

        deserialized.Should().NotBeNull();
        deserialized.Id.Should().Be("round-trip");
        deserialized.TenantId.Should().Be("t-99");
        deserialized.Status.Should().Be(TestStatus.Inactive);
        deserialized.Amount.Should().Be(1234.56m);
        deserialized.NullableField.Should().Be("has-value");
    }

    #endregion
}
