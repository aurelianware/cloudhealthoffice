namespace ProviderService.Models;

/// <summary>
/// Single row in the <c>GET /api/v1/networks/{id}/roster</c> response.
/// Composed from the matching <see cref="Provider"/> and the specific
/// <see cref="NetworkParticipation"/> that linked the provider to the
/// roster's network.
/// </summary>
public sealed class NetworkRosterEntry
{
    /// <summary>Provider chain key.</summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Per-version document id of the head Active version.</summary>
    public string VersionId { get; set; } = string.Empty;

    /// <summary>Provider summary (NPI, name, specialty, address).</summary>
    public RosterProviderSummary Provider { get; set; } = new();

    /// <summary>The participation that matched the roster filter.</summary>
    public RosterParticipationSummary Participation { get; set; } = new();

    /// <summary>
    /// Cached integrity score copied from <see cref="Models.Provider.IntegrityScore"/>.
    /// Null when the provider has not yet been verified (or when
    /// verification write-back has not run yet — see network-roster-api.md
    /// "Known gap").
    /// </summary>
    public RosterIntegrityScore? IntegrityScore { get; set; }
}

/// <summary>
/// Provider identity + display fields surfaced on a roster row.
/// </summary>
public sealed class RosterProviderSummary
{
    public string Npi { get; set; } = string.Empty;
    public ProviderType ProviderType { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string PrimarySpecialty { get; set; } = string.Empty;
    public string TaxonomyCode { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? ZipCode { get; set; }
    public bool AcceptingNewPatients { get; set; }
}

/// <summary>
/// Subset of <see cref="NetworkParticipation"/> safe to expose on the
/// roster. Includes the panel-gating summary (PCP-assignment fields
/// from capability 5.7 / pcp-assignment.md) when populated.
/// </summary>
public sealed class RosterParticipationSummary
{
    public string? PlanId { get; set; }
    public LineOfBusiness LineOfBusiness { get; set; }
    public string NetworkTier { get; set; } = string.Empty;
    public bool AcceptingNewPatients { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public RosterPanelGating? PanelGating { get; set; }
}

/// <summary>
/// PCP-assignment panel-gating summary. Mirrors the nullable fields on
/// <see cref="NetworkParticipation"/>. Null on the wire when every
/// underlying field is null (fully unconstrained / not backfilled).
/// </summary>
public sealed class RosterPanelGating
{
    public int? PanelLimit { get; set; }
    public bool? PanelAccepted { get; set; }
    public List<LineOfBusiness>? AcceptedLobs { get; set; }
    public int? MinAcceptedAgeYears { get; set; }
    public int? MaxAcceptedAgeYears { get; set; }
}

/// <summary>
/// Cached integrity-score envelope. Read directly from the Provider
/// row — the roster path does not call <c>ProviderVerificationOrchestrator</c>.
/// </summary>
public sealed class RosterIntegrityScore
{
    public int? Score { get; set; }
    public string? Rating { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
}

/// <summary>Page envelope for the roster endpoint. Cursor-paginated.</summary>
public sealed class NetworkRosterResponse
{
    public IReadOnlyList<NetworkRosterEntry> Items { get; set; } = Array.Empty<NetworkRosterEntry>();

    /// <summary>
    /// Opaque token to fetch the next page. Null when this page is the last.
    /// Bound to the originating filter set via an internal hash; reusing
    /// a cursor with different filters produces a 400.
    /// </summary>
    public string? NextCursor { get; set; }

    public int PageSize { get; set; }
}
