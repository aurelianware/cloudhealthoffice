namespace BenefitPlanService.Models;

/// <summary>
/// Configuration for <see cref="Services.IAcaLimitsProvider"/> (capability
/// BP 5.7 — Embedded vs Aggregate Family OOP Rules + ACA cap enforcement).
///
/// <para>
/// The ACA 45 CFR §156.130 individual / family out-of-pocket caps live
/// in a versioned seed file (see <c>schemas/aca-oop-limits/limits.json</c>),
/// loaded once at service startup by <see cref="Services.AcaLimitsProvider"/>.
/// Operators bump the file when CMS publishes the annual NBPP final rule;
/// the provider rejects unknown plan years at write time so a missing-year
/// regression is impossible to ignore.
/// </para>
///
/// <para>
/// See <c>docs/architecture/family-accumulator-models.md</c> for the
/// canonical resolution flow and <c>schemas/aca-oop-limits/README.md</c>
/// for source attribution and update cadence.
/// </para>
/// </summary>
public sealed class AcaOopLimitsOptions
{
    public const string SectionName = "AcaOopLimits";

    /// <summary>
    /// Filesystem path to the JSON limits file relative to the service
    /// content root. Default <c>schemas/aca-oop-limits/limits.json</c>.
    /// Override in tests to point at a fixture.
    /// </summary>
    public string LimitsFilePath { get; set; }
        = "schemas/aca-oop-limits/limits.json";
}
