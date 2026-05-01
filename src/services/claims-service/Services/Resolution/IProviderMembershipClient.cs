using System.Text.Json.Serialization;

namespace ClaimsService.Services.Resolution;

/// <summary>
/// Cross-service network-membership lookup against provider-service's
/// <c>GET /api/v1/networks/{id}/members/{npi}</c> (capability 5.6).
/// Consumed by <see cref="Adjudication.Stages.NetworkCredentialingStage"/>;
/// wrapped by <see cref="CachingProviderMembershipClient"/> in production
/// for a 5-minute per-pod TTL.
/// </summary>
public interface IProviderMembershipClient
{
    /// <summary>
    /// Returns the membership snapshot for
    /// (<paramref name="networkId"/>, <paramref name="npi"/>) at
    /// <paramref name="asOf"/>, or <c>null</c> when the lookup degrades
    /// (404, transport failure, deserialization error). Non-throwing —
    /// the enforcement stage applies the configured fail-mode policy
    /// when the result is null.
    /// </summary>
    Task<NetworkMembership?> GetMembershipAsync(
        string tenantId,
        string networkId,
        string npi,
        DateTime asOf,
        bool forceRefresh = false,
        CancellationToken ct = default);
}

/// <summary>
/// Pipeline-local membership snapshot. Mirrors the wire shape of
/// provider-service's <c>NetworkMembershipResponse</c> (capability 5.6)
/// but lives in claims-service to keep the cross-service boundary
/// asymmetric (claims doesn't reference the provider-service project).
/// </summary>
public sealed class NetworkMembership
{
    [JsonPropertyName("networkId")]
    public string NetworkId { get; set; } = string.Empty;

    [JsonPropertyName("npi")]
    public string Npi { get; set; } = string.Empty;

    [JsonPropertyName("providerId")]
    public string ProviderId { get; set; } = string.Empty;

    [JsonPropertyName("isActiveMember")]
    public bool IsActiveMember { get; set; }

    [JsonPropertyName("asOfDate")]
    public DateTime AsOfDate { get; set; }

    [JsonPropertyName("effectiveFrom")]
    public DateTime? EffectiveFrom { get; set; }

    [JsonPropertyName("effectiveTo")]
    public DateTime? EffectiveTo { get; set; }

    [JsonPropertyName("participationStatus")]
    public string? ParticipationStatus { get; set; }
}
