using System.ComponentModel.DataAnnotations;

namespace MemberService.Models;

/// <summary>
/// Typed identifier attached to a <see cref="Member"/>.
/// Projects to a FHIR R4 Identifier with <c>use</c> + <c>type</c> + <c>system</c> + <c>value</c> + <c>period</c>.
/// </summary>
public class MemberIdentifier
{
    [Required]
    public MemberIdentifierType Type { get; set; }

    /// <summary>
    /// Canonical identifier system URI. For <see cref="MemberIdentifierType.Legacy"/>
    /// this is the tenant-config slug-scoped URI (<c>urn:cho:legacy:{slug}</c>).
    /// </summary>
    [Required]
    [StringLength(256)]
    public string System { get; set; } = string.Empty;

    /// <summary>
    /// The identifier value as stored. PII identifiers (SSN, MBI, Medicaid) are
    /// expected to be encrypted-at-rest; <see cref="IsEncrypted"/> marks them.
    /// </summary>
    [Required]
    [StringLength(512)]
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// FHIR Identifier.use: usual | official | temp | secondary | old
    /// </summary>
    [StringLength(16)]
    public string Use { get; set; } = "official";

    public DateTime? PeriodStart { get; set; }
    public DateTime? PeriodEnd { get; set; }

    [StringLength(200)]
    public string? Assigner { get; set; }

    /// <summary>
    /// True when <see cref="Value"/> is ciphertext from <c>IIdentifierEncryptor</c>.
    /// </summary>
    public bool IsEncrypted { get; set; }

    /// <summary>
    /// Keyed HMAC fingerprint of the NORMALIZED plaintext (dashes/spaces/case
    /// stripped). Populated for PII identifier types (SSN, MBI, Medicaid) to
    /// enable dedupe without storing plaintext. Null for non-PII types where
    /// <see cref="Value"/> itself is sufficient for equality comparison.
    /// </summary>
    [StringLength(128)]
    public string? ValueFingerprint { get; set; }
}

/// <summary>
/// Known identifier types. Legacy is the escape hatch for external/tenant-specific systems.
/// </summary>
public enum MemberIdentifierType
{
    MemberId = 1,
    SSN = 2,
    MedicareMbi = 3,
    Medicaid = 4,
    Exchange = 5,
    Portal = 6,
    Legacy = 7
}
