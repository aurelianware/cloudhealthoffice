namespace ClaimsService.Models.Adjudication;

/// <summary>
/// Service-wide configuration for the adjudication pipeline (capability 5.5).
/// Bound from configuration section <c>Adjudication:Pipeline</c>.
///
/// <para>
/// Stage enablement is service-wide in Phase 1 — every tenant runs the
/// same set of enabled stages. Per-tenant overrides are deferred to
/// Phase 2 multi-tenant config work; nothing in 5.5 leaks decisions on
/// that surface.
/// </para>
///
/// <para>
/// <see cref="Services.Adjudication.Stages.PersistenceStage"/> is
/// always enabled regardless of this setting — its
/// <see cref="Services.Adjudication.IClaimAdjudicationStage.IsRequired"/>
/// flag forces the orchestrator to bypass the enablement check.
/// </para>
/// </summary>
public class AdjudicationPipelineOptions
{
    public const string SectionName = "Adjudication:Pipeline";

    /// <summary>
    /// Map of stage <see cref="Services.Adjudication.IClaimAdjudicationStage.Name"/>
    /// → enabled. A missing key is treated as enabled. Replace-mode for
    /// 5.4-5.9 keeps the same name keys, so disabling a stub also
    /// disables its real replacement after the capability ships.
    /// </summary>
    public Dictionary<string, bool> EnabledStages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
