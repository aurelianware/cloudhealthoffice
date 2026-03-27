using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Serialization;

namespace CloudHealthOffice.Infrastructure.Tests;

public class CosmosSystemTextJsonSerializerTests
{
    private readonly CosmosSystemTextJsonSerializer _serializer = new();

    private enum TestStatus { Active, Inactive, Pending }

    private class TestDocument
    {
        public string? Id { get; set; }
        public string? FullName { get; set; }
        public int Age { get; set; }
        public TestStatus Status { get; set; }
        public string? NullableField { get; set; }
    }

    [Fact]
    public void ToStream_SerializesWithCamelCase()
    {
        var doc = new TestDocument { Id = "123", FullName = "John Doe", Age = 30 };

        using var stream = _serializer.ToStream(doc);
        var json = new StreamReader(stream).ReadToEnd();

        json.Should().Contain("\"id\":");
        json.Should().Contain("\"fullName\":");
        json.Should().Contain("\"age\":");
        json.Should().NotContain("\"Id\":");
        json.Should().NotContain("\"FullName\":");
    }

    [Fact]
    public void ToStream_SerializesEnumsAsStrings()
    {
        var doc = new TestDocument { Id = "1", Status = TestStatus.Active };

        using var stream = _serializer.ToStream(doc);
        var json = new StreamReader(stream).ReadToEnd();

        json.Should().Contain("\"status\":\"active\"");
        json.Should().NotContain("\"status\":0");
    }

    [Fact]
    public void ToStream_OmitsNullProperties()
    {
        var doc = new TestDocument { Id = "1", NullableField = null };

        using var stream = _serializer.ToStream(doc);
        var json = new StreamReader(stream).ReadToEnd();

        json.Should().NotContain("nullableField");
    }

    [Fact]
    public void ToStream_IncludesNonNullProperties()
    {
        var doc = new TestDocument { Id = "1", NullableField = "present" };

        using var stream = _serializer.ToStream(doc);
        var json = new StreamReader(stream).ReadToEnd();

        json.Should().Contain("\"nullableField\":\"present\"");
    }

    [Fact]
    public void FromStream_DeserializesFromCamelCase()
    {
        var json = """{"id":"456","fullName":"Jane Doe","age":25,"status":"inactive"}""";
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));

        var result = _serializer.FromStream<TestDocument>(stream);

        result.Id.Should().Be("456");
        result.FullName.Should().Be("Jane Doe");
        result.Age.Should().Be(25);
        result.Status.Should().Be(TestStatus.Inactive);
    }

    [Fact]
    public void FromStream_EmptyStream_ReturnsDefault()
    {
        var stream = new MemoryStream();

        var result = _serializer.FromStream<TestDocument>(stream);

        result.Should().BeNull();
    }

    [Fact]
    public void RoundTrip_PreservesAllProperties()
    {
        var original = new TestDocument
        {
            Id = "rt-1",
            FullName = "Round Trip",
            Age = 42,
            Status = TestStatus.Pending,
            NullableField = "exists"
        };

        using var stream = _serializer.ToStream(original);
        var deserialized = _serializer.FromStream<TestDocument>(stream);

        deserialized.Id.Should().Be(original.Id);
        deserialized.FullName.Should().Be(original.FullName);
        deserialized.Age.Should().Be(original.Age);
        deserialized.Status.Should().Be(original.Status);
        deserialized.NullableField.Should().Be(original.NullableField);
    }

    [Fact]
    public void ToStream_StreamPositionIsZero()
    {
        var doc = new TestDocument { Id = "1" };

        using var stream = _serializer.ToStream(doc);

        stream.Position.Should().Be(0);
    }

    [Fact]
    public void Constructor_WithCustomOptions_UsesProvidedOptions()
    {
        var customOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null // PascalCase
        };
        var customSerializer = new CosmosSystemTextJsonSerializer(customOptions);

        var doc = new TestDocument { Id = "1", FullName = "Test" };
        using var stream = customSerializer.ToStream(doc);
        var json = new StreamReader(stream).ReadToEnd();

        // With null naming policy, property names should be PascalCase
        json.Should().Contain("\"Id\":");
        json.Should().Contain("\"FullName\":");
    }
}
