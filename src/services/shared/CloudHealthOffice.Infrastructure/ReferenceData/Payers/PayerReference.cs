using CloudHealthOffice.Infrastructure.Gateways;

namespace CloudHealthOffice.Infrastructure.ReferenceData.Payers;

/// <summary>
/// Vendor-neutral Cloud Health Office payer identity. Clearinghouse / network
/// identifiers live in <see cref="ExternalIdentifiers"/> rather than as
/// vendor-named properties on this type.
/// </summary>
public sealed class PayerReference
{
    /// <summary>Stable Cloud Health Office canonical payer identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Primary display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Alternate names and payer IDs that resolve to this record.</summary>
    public List<string> Aliases { get; set; } = new();

    /// <summary>
    /// Generic external identifiers (clearinghouse, CMS, direct X12, etc.).
    /// Example: System = "stedi", Type = "tradingPartnerServiceId", Value = "...".
    /// </summary>
    public List<PayerExternalIdentifier> ExternalIdentifiers { get; set; } = new();

    /// <summary>
    /// Per-transaction support as advertised by the source directory for this
    /// payer/network — independent of whether a CHO gateway implements the
    /// transaction.
    /// </summary>
    public List<PayerTransactionCapability> SupportedTransactions { get; set; } = new();

    /// <summary>
    /// Enrollment requirements captured from the source directory. Presence
    /// here does not enroll anyone; it only records that enrollment may be
    /// required before a transaction is ready.
    /// </summary>
    public List<PayerEnrollmentRequirement> EnrollmentRequirements { get; set; } = new();

    /// <summary>True when this payer is currently active in the local directory.</summary>
    public bool Active { get; set; } = true;

    /// <summary>Directory provenance (source system, timestamps).</summary>
    public PayerReferenceProvenance Provenance { get; set; } = new();

    /// <summary>Non-identifying extra attributes (coverage types, parent group, website, ...).</summary>
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// An identifier assigned by an external system. Systems are opaque strings so
/// future clearinghouses can be represented without a model change.
/// Well-known system values include <c>stedi</c>, <c>availity</c>,
/// <c>change-healthcare</c>, <c>cms</c>, and <c>direct-x12</c>.
/// </summary>
public sealed class PayerExternalIdentifier
{
    /// <summary>External system name, e.g. <c>stedi</c>.</summary>
    public string System { get; set; } = string.Empty;

    /// <summary>
    /// Identifier kind within that system, e.g. <c>tradingPartnerServiceId</c>
    /// or <c>id</c>.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>The identifier value.</summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Whether a specific HIPAA/X12 transaction is available for this payer, and
/// whether enrollment is required. Distinct from gateway implementation
/// capability: a gateway may implement eligibility even when this payer does
/// not support it, and vice versa.
/// </summary>
public sealed class PayerTransactionCapability
{
    public HealthcareTransactionType Transaction { get; set; }

    public PayerTransactionSupport Support { get; set; }
}

/// <summary>Directory-level support for a transaction at a given payer.</summary>
public enum PayerTransactionSupport
{
    NotSupported = 0,
    Supported = 1,
    EnrollmentRequired = 2
}

/// <summary>
/// Enrollment metadata for a transaction. This is awareness only — enrollment
/// submission is out of scope.
/// </summary>
public sealed class PayerEnrollmentRequirement
{
    public HealthcareTransactionType Transaction { get; set; }

    /// <summary>True when the source directory says enrollment is required.</summary>
    public bool Required { get; set; }

    /// <summary>Source-reported enrollment process type, when available (e.g. ONE_CLICK).</summary>
    public string? ProcessType { get; set; }

    /// <summary>Source-reported expected timeframe, when available (e.g. DAYS).</summary>
    public string? Timeframe { get; set; }
}

/// <summary>Where a payer record came from and when it was last refreshed.</summary>
public sealed class PayerReferenceProvenance
{
    /// <summary>Source system, e.g. <c>stedi</c> or <c>seed</c>.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>When the source record was last observed during a directory sync.</summary>
    public DateTimeOffset? SourceUpdatedAt { get; set; }

    /// <summary>When Cloud Health Office last wrote this record.</summary>
    public DateTimeOffset LastSyncedAt { get; set; }
}
