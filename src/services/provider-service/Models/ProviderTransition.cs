using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace ProviderService.Models;

/// <summary>
/// Audit record of a single state transition in a provider's version chain.
/// Append-only; one row per <see cref="ProviderTransitionType"/> event.
/// Partitioned on <c>TenantId</c> to match the rest of the service.
/// </summary>
[BsonIgnoreExtraElements]
public class ProviderTransition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Provider chain key — the persistent provider identifier
    /// (<see cref="Provider.ProviderId"/>), not the per-version <c>VersionId</c>.
    /// </summary>
    [Required]
    [JsonPropertyName("providerId")]
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>
    /// Predecessor <c>VersionId</c>. Null for the genesis <c>Activate</c>
    /// of a brand-new provider.
    /// </summary>
    [JsonPropertyName("fromVersionId")]
    public string? FromVersionId { get; set; }

    /// <summary>
    /// Successor <c>VersionId</c>. Null for terminal <c>Terminate</c>
    /// transitions and for <c>Suspend</c> (which keeps the same VersionId).
    /// </summary>
    [JsonPropertyName("toVersionId")]
    public string? ToVersionId { get; set; }

    [Required]
    [JsonPropertyName("transitionType")]
    public ProviderTransitionType TransitionType { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("effectiveDate")]
    public DateTime? EffectiveDate { get; set; }

    [JsonPropertyName("occurredAt")]
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("actorId")]
    public string? ActorId { get; set; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProviderTransitionType
{
    /// <summary>Genesis Activate — no predecessor.</summary>
    Activate = 1,
    /// <summary>Draft created from an existing Active version.</summary>
    Amend = 2,
    /// <summary>Predecessor moved to <c>Superseded</c> by a new Active version.</summary>
    Supersede = 3,
    /// <summary>Active version paused (no successor; same VersionId remains addressable).</summary>
    Suspend = 4,
    /// <summary>A new Active version replaces a Suspended or Terminated head.</summary>
    Reactivate = 5,
    /// <summary>Active version permanently terminated; no successor.</summary>
    Terminate = 6
}
