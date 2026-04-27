namespace ProviderService.Models;

/// <summary>
/// Payer-defined network classification for an <see cref="Organization"/>.
///
/// <para>
/// Follows the PR #705 enum-handling pattern: every value is explicitly
/// numbered and <c>Unknown = 0</c> is the safe default for documents
/// written before this enum existed. The string-only / no-integer
/// enforcement is delegated to the shared MVC JSON options registered by
/// <c>AddCloudHealthOfficeJsonOptions</c> (which constructs a
/// <c>JsonStringEnumConverter(allowIntegerValues: false)</c>) — declaring
/// a type-level converter here would override that with the lax default.
/// </para>
/// </summary>
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
