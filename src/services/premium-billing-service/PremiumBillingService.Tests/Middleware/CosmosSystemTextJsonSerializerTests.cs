using PremiumBillingService.Middleware;

namespace PremiumBillingService.Tests.Middleware;

public class CosmosSystemTextJsonSerializerTests
{
    private readonly CosmosSystemTextJsonSerializer _serializer;

    public CosmosSystemTextJsonSerializerTests()
    {
        _serializer = new CosmosSystemTextJsonSerializer();
    }

    [Fact]
    public void ToStream_SerializesObjectToCamelCase()
    {
        var obj = new TestModel { Name = "Test", Value = 42 };

        var stream = _serializer.ToStream(obj);

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        json.Should().Contain("\"name\"");
        json.Should().Contain("\"value\"");
        json.Should().Contain("\"Test\"");
        json.Should().Contain("42");
    }

    [Fact]
    public void FromStream_DeserializesFromCamelCase()
    {
        var json = """{"name":"Hello","value":99}""";
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));

        var result = _serializer.FromStream<TestModel>(stream);

        result.Name.Should().Be("Hello");
        result.Value.Should().Be(99);
    }

    [Fact]
    public void FromStream_EmptyStream_ReturnsDefault()
    {
        var stream = new MemoryStream();

        var result = _serializer.FromStream<TestModel>(stream);

        result.Should().BeNull();
    }

    [Fact]
    public void ToStream_NullProperties_OmitsFromJson()
    {
        var obj = new TestModel { Name = null!, Value = 10 };

        var stream = _serializer.ToStream(obj);

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        json.Should().NotContain("\"name\"");
        json.Should().Contain("\"value\"");
    }

    [Fact]
    public void ToStream_StreamPositionIsZero()
    {
        var obj = new TestModel { Name = "Test", Value = 1 };

        var stream = _serializer.ToStream(obj);

        stream.Position.Should().Be(0);
    }

    [Fact]
    public void ToStream_EnumSerialization_UsesCamelCase()
    {
        var obj = new TestModelWithEnum { Status = TestStatus.Active };

        var stream = _serializer.ToStream(obj);

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        json.Should().Contain("\"active\"");
    }

    [Fact]
    public void RoundTrip_PreservesData()
    {
        var original = new TestModel { Name = "RoundTrip", Value = 123 };

        var stream = _serializer.ToStream(original);
        var deserialized = _serializer.FromStream<TestModel>(stream);

        deserialized.Name.Should().Be("RoundTrip");
        deserialized.Value.Should().Be(123);
    }

    private class TestModel
    {
        public string? Name { get; set; }
        public int Value { get; set; }
    }

    private class TestModelWithEnum
    {
        public TestStatus Status { get; set; }
    }

    private enum TestStatus
    {
        Active,
        Inactive
    }
}
