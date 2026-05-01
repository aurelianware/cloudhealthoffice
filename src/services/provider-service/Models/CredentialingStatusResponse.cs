namespace ProviderService.Models;

/// <summary>
/// Body shape returned by
/// <c>GET /api/v1/providers/{id}/credentialing/status-as-of</c>
/// (capability 5.6). Projects <see cref="ProviderService.Services.CredentialingProjectionResult"/>
/// down to the caller-relevant fields and adds the echoed
/// <see cref="AsOfDate"/> the projection was evaluated against.
///
/// <para>
/// Internal projection internals (<c>CurrentApplicationEventId</c>,
/// <c>ApplicationSubmittedAt</c>, <c>EventCount</c>, <c>LatestVersion</c>)
/// are deliberately omitted — they are operational fields belonging to the
/// admin <c>GET /status</c> + <c>GET /history</c> surface, not to
/// cross-service enforcement consumers.
/// </para>
/// </summary>
public sealed class CredentialingStatusResponse
{
    public string ProviderId { get; set; } = string.Empty;
    public DateTime AsOfDate { get; set; }

    /// <summary>
    /// String name of <see cref="CredentialingStatus"/> — serialized as
    /// the enum name to match the existing
    /// <see cref="ProviderService.Services.CredentialingProjectionResult"/>
    /// surface and stay stable across consumer schema migrations.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    public DateTime? CredentialingDate { get; set; }
    public DateTime? RecredentialingDueDate { get; set; }

    public string? LastDecisionAuthorityId { get; set; }
    public string? LastDecisionAuthorityType { get; set; }
    public DateTimeOffset? LastDecidedAt { get; set; }
}
