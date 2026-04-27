using System.Text.Json.Serialization;

namespace ProviderService.Models;

/// <summary>
/// Payer-defined network classification for an <see cref="Organization"/>.
///
/// <para>
/// Follows the PR #705 enum-handling pattern: every value is explicitly
/// numbered, <c>Unknown = 0</c> is the safe default for documents written
/// before this enum existed, and the converter is locked to string form
/// (no integer parsing) so on-the-wire payloads stay self-describing.
/// </para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NetworkType
{
    /// <summary>Default for hydrated documents that predate the field.</summary>
    Unknown = 0,

    /// <summary>Preferred Provider Organization.</summary>
    PPO = 1,

    /// <summary>Health Maintenance Organization.</summary>
    HMO = 2,

    /// <summary>Exclusive Provider Organization.</summary>
    EPO = 3,

    /// <summary>Point of Service.</summary>
    POS = 4,

    /// <summary>Indemnity / fee-for-service network.</summary>
    Indemnity = 5,

    /// <summary>Custom payer-defined classification not covered by the canonical list.</summary>
    Custom = 99
}
