using System.Text.Json.Serialization;

namespace BenefitPlanService.Models;

/// <summary>
/// Lifecycle state of a single <see cref="BenefitPlan"/> version document.
///
/// <para>State machine:</para>
/// <list type="bullet">
///   <item><c>Draft</c> — mutable; not visible to default reads.</item>
///   <item><c>Published</c> — read-only at the application layer; serves
///         "latest as-of-today" lookups while in effect.</item>
///   <item><c>Superseded</c> — terminal; replaced by a newer Published
///         version (recorded in <c>SupersededByVersionId</c>) or terminated.</item>
/// </list>
///
/// Documents persisted before this field existed are hydrated as
/// <c>Published</c> for backward-compatibility (see plan-versioning.md).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlanVersionState
{
    Draft = 0,
    Published = 1,
    Superseded = 2
}
