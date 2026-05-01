using System.Text.Json.Serialization;

namespace ClaimsService.Services.Resolution;

/// <summary>
/// Cross-service credentialing-status lookup against provider-service's
/// <c>GET /api/v1/providers/{id}/credentialing/status-as-of</c>
/// (capability 5.6). Consumed by
/// <see cref="Adjudication.Stages.NetworkCredentialingStage"/>; wrapped
/// by <see cref="CachingCredentialingStatusClient"/> in production for a
/// 1-hour per-pod TTL.
///
/// <para>
/// The longer TTL (vs. 5-minute membership) reflects that credentialing
/// transitions are explicit, audit-trailed events on the
/// <see cref="ProviderService.Services.CredentialingProjector"/> chain;
/// staleness within an hour is operationally acceptable and the read
/// volume per provider is low enough that a 1-hour cache is safe.
/// </para>
/// </summary>
public interface ICredentialingStatusClient
{
    /// <summary>
    /// Returns the credentialing-status snapshot for
    /// <paramref name="providerId"/> as of <paramref name="asOfDate"/>,
    /// or <c>null</c> when the lookup degrades. Consumer NPI is mapped
    /// to providerId by the caller; this client takes the upstream
    /// resource id directly.
    /// </summary>
    Task<CredentialingStatusSnapshot?> GetStatusAsOfAsync(
        string tenantId,
        string providerId,
        DateTime asOfDate,
        bool forceRefresh = false,
        CancellationToken ct = default);
}

/// <summary>
/// Pipeline-local credentialing status. Mirrors the wire shape of
/// provider-service's <c>CredentialingStatusResponse</c>.
/// </summary>
public sealed class CredentialingStatusSnapshot
{
    [JsonPropertyName("providerId")]
    public string ProviderId { get; set; } = string.Empty;

    [JsonPropertyName("asOfDate")]
    public DateTime AsOfDate { get; set; }

    /// <summary>
    /// Stringly-typed projection of provider-service's
    /// <c>CredentialingStatus</c> enum: <c>Unknown | Pending | Approved
    /// | Denied | Expired | Suspended</c>. Stays a string so wire-shape
    /// drift in the upstream enum doesn't break the cross-service
    /// boundary.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "Unknown";

    [JsonPropertyName("credentialingDate")]
    public DateTime? CredentialingDate { get; set; }

    [JsonPropertyName("recredentialingDueDate")]
    public DateTime? RecredentialingDueDate { get; set; }

    [JsonPropertyName("lastDecisionAuthorityId")]
    public string? LastDecisionAuthorityId { get; set; }

    [JsonPropertyName("lastDecisionAuthorityType")]
    public string? LastDecisionAuthorityType { get; set; }

    [JsonPropertyName("lastDecidedAt")]
    public DateTimeOffset? LastDecidedAt { get; set; }

    /// <summary>
    /// True when <see cref="Status"/> is the only value that admits
    /// adjudication on a service-dated claim. Approved claims pay;
    /// Expired claims pay if the recredentialing-due gap is within
    /// payer policy (decided by the enforcement stage's mode).
    /// </summary>
    public bool IsApprovedAtAsOf =>
        string.Equals(Status, "Approved", StringComparison.OrdinalIgnoreCase);
}
