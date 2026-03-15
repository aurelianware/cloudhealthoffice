using System.Text.Json.Serialization;

namespace CloudHealthOffice.Infrastructure.Models;

/// <summary>
/// Standard error response model used across all CHO microservices.
/// </summary>
public class StandardErrorResponse
{
    /// <summary>
    /// Machine-readable error code (e.g., "BAD_REQUEST", "NOT_FOUND", "INTERNAL_ERROR").
    /// </summary>
    [JsonPropertyName("code")]
    public required string Code { get; set; }

    /// <summary>
    /// Human-readable error message. In development, contains exception details.
    /// In production, contains a generic message.
    /// </summary>
    [JsonPropertyName("message")]
    public required string Message { get; set; }

    /// <summary>
    /// Additional error details. Only populated in development environments.
    /// </summary>
    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Details { get; set; }

    /// <summary>
    /// Distributed trace ID for correlating logs and requests.
    /// </summary>
    [JsonPropertyName("traceId")]
    public required string TraceId { get; set; }
}
