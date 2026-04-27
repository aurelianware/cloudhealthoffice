using System.Text.Json.Serialization;

namespace ProviderService.Models;

/// <summary>
/// Lifecycle state of a single <see cref="Organization"/> version document.
/// Mirrors <see cref="ProviderVersionState"/> (capability 5.1) — same Draft →
/// Active → Suspended → Superseded → Terminated state machine.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OrganizationVersionState
{
    Draft = 0,
    Active = 1,
    Suspended = 2,
    Superseded = 3,
    Terminated = 4
}
