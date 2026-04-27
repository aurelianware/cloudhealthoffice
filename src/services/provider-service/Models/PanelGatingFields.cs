namespace ProviderService.Models;

/// <summary>
/// Value object carrying the five PCP panel-gating fields that
/// <see cref="NetworkParticipation"/> exposes (capability 5.5). Used as
/// the parameter shape for
/// <see cref="ProviderService.Repositories.IProviderRepository.UpdatePanelGatingDefaultsAsync"/>
/// so the backfill path can apply the canonical legacy-unconstrained
/// defaults without proliferating positional parameters.
///
/// <para>
/// All five properties carry their type-default values out of the box —
/// constructing a default <see cref="PanelGatingFields"/> instance and
/// handing it to the repository writes the legacy-unconstrained shape
/// (<c>PanelLimit=null</c>, <c>PanelAccepted=null</c>,
/// <c>AcceptedLobs=empty</c>, <c>MinAcceptedAgeYears=null</c>,
/// <c>MaxAcceptedAgeYears=null</c>). The same shape can also be
/// constructed by hand for a producer that wants to set explicit values.
/// </para>
/// </summary>
public sealed class PanelGatingFields
{
    /// <summary>
    /// Maximum number of members that may be assigned to this provider
    /// under this participation. Null = unlimited / not yet backfilled.
    /// </summary>
    public int? PanelLimit { get; init; }

    /// <summary>
    /// Whether this provider accepts new PCP assignments for this
    /// participation. Null = treated as
    /// <see cref="NetworkParticipation.AcceptingNewPatients"/>.
    /// </summary>
    public bool? PanelAccepted { get; init; }

    /// <summary>
    /// LOBs this participation will accept as a PCP. Empty = accept any
    /// LOB covered by this participation.
    /// </summary>
    public IReadOnlyList<LineOfBusiness> AcceptedLobs { get; init; } = Array.Empty<LineOfBusiness>();

    /// <summary>Minimum member age (years). Null = no floor.</summary>
    public int? MinAcceptedAgeYears { get; init; }

    /// <summary>Maximum member age (years). Null = no ceiling.</summary>
    public int? MaxAcceptedAgeYears { get; init; }

    /// <summary>
    /// Returns the canonical legacy-unconstrained shape
    /// (all five fields at their type defaults). Used by the backfill
    /// service when it patches a participation that no panel-gating-aware
    /// producer has touched.
    /// </summary>
    public static PanelGatingFields LegacyUnconstrained() => new();

    /// <summary>
    /// True when every field is at its type default. Used by the
    /// backfill eligibility check — a participation in this shape has
    /// not been touched by panel-gating-aware code yet, so the backfill
    /// is safe to apply (and idempotent on rerun).
    /// </summary>
    public static bool IsAtTypeDefaults(NetworkParticipation participation) =>
        participation.PanelLimit is null
        && participation.PanelAccepted is null
        && (participation.AcceptedLobs is null || participation.AcceptedLobs.Count == 0)
        && participation.MinAcceptedAgeYears is null
        && participation.MaxAcceptedAgeYears is null;
}
