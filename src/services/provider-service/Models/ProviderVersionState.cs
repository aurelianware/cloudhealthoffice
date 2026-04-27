using System.Text.Json.Serialization;

namespace ProviderService.Models;

/// <summary>
/// Lifecycle state of a single <see cref="Provider"/> version document.
///
/// <para>State machine:</para>
/// <list type="bullet">
///   <item><c>Draft</c> — mutable; not visible to default reads.</item>
///   <item><c>Active</c> — read-only at the application layer; serves
///         "latest as-of-today" lookups while in effect.</item>
///   <item><c>Suspended</c> — non-terminal pause; reactivation creates
///         a new <c>Active</c> version that supersedes the suspended one.</item>
///   <item><c>Superseded</c> — displaced by a newer <c>Active</c> version
///         (or by reactivation). Still queryable; <c>SupersededByVersionId</c>
///         points at the replacement.</item>
///   <item><c>Terminated</c> — permanently ended with no successor.
///         Distinct from <c>Superseded</c>: terminated versions require
///         an explicit <c>ReactivateProvider</c> to resume the chain.</item>
/// </list>
///
/// Documents persisted before this field existed are hydrated as
/// <c>Active</c> for backward compatibility (see provider-versioning.md).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProviderVersionState
{
    Draft = 0,
    Active = 1,
    Suspended = 2,
    Superseded = 3,
    Terminated = 4
}
