using System.Text.Json;
using CloudHealthOffice.Infrastructure.Models;

namespace CloudHealthOffice.Infrastructure.Tests;

public class StandardErrorResponseTests
{
    [Fact]
    public void Serialization_UsesCamelCase()
    {
        var error = new StandardErrorResponse
        {
            Code = "TEST_ERROR",
            Message = "Test message",
            TraceId = "trace-123"
        };

        var json = JsonSerializer.Serialize(error);

        json.Should().Contain("\"code\":");
        json.Should().Contain("\"message\":");
        json.Should().Contain("\"traceId\":");
    }

    [Fact]
    public void Serialization_OmitsNullDetails()
    {
        var error = new StandardErrorResponse
        {
            Code = "TEST_ERROR",
            Message = "Test message",
            Details = null,
            TraceId = "trace-123"
        };

        var json = JsonSerializer.Serialize(error);

        json.Should().NotContain("details");
    }

    [Fact]
    public void Serialization_IncludesDetailsWhenPresent()
    {
        var error = new StandardErrorResponse
        {
            Code = "TEST_ERROR",
            Message = "Test message",
            Details = "Stack trace here",
            TraceId = "trace-123"
        };

        var json = JsonSerializer.Serialize(error);

        json.Should().Contain("\"details\":");
        json.Should().Contain("Stack trace here");
    }

    [Fact]
    public void Deserialization_RoundTrips()
    {
        var original = new StandardErrorResponse
        {
            Code = "NOT_FOUND",
            Message = "Resource not found",
            Details = "Entity with ID 123 does not exist",
            TraceId = "trace-abc"
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<StandardErrorResponse>(json);

        deserialized.Should().NotBeNull();
        deserialized!.Code.Should().Be(original.Code);
        deserialized.Message.Should().Be(original.Message);
        deserialized.Details.Should().Be(original.Details);
        deserialized.TraceId.Should().Be(original.TraceId);
    }
}
