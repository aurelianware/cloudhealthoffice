namespace ProviderService.Models;

/// <summary>
/// Body shape returned by <c>GET /api/v1/networks/{id}/members/{npi}</c>
/// (capability 5.6 — Network &amp; Credentialing Enforcement).
///
/// <para>
/// 200 with <see cref="IsActiveMember"/> false when the NPI exists in the
/// network's participation history but is not active for the requested
/// <c>asOf</c> date (e.g. terminated participation, future-dated
/// participation). 404 only when no participation row for the NPI exists
/// at all in this network. Body-shaped status keeps the consumer logic
/// uniform for caching — the <see cref="ParticipationStatus"/> string is
/// the audit-grade signal, the boolean is the enforcement-grade signal.
/// </para>
/// </summary>
public sealed class NetworkMembershipResponse
{
    public string NetworkId { get; set; } = string.Empty;
    public string Npi { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>
    /// True iff a participation row exists with
    /// <c>EffectiveDate &lt;= asOf &lt; (TerminationDate ?? +inf)</c>.
    /// Drives the claims-side network-tier match in
    /// <c>NetworkCredentialingStage</c>.
    /// </summary>
    public bool IsActiveMember { get; set; }

    /// <summary>
    /// The <c>asOf</c> date evaluated. Echoed so callers can confirm which
    /// snapshot the result corresponds to (the param defaults to UtcNow
    /// server-side when omitted).
    /// </summary>
    public DateTime AsOfDate { get; set; }

    public DateTime? EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }

    /// <summary>
    /// <c>active | terminated | suspended | pending | future</c>.
    /// </summary>
    public string? ParticipationStatus { get; set; }

    public LineOfBusiness? LineOfBusiness { get; set; }
    public string? NetworkTier { get; set; }
}
