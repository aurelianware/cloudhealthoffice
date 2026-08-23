using CloudHealthOffice.Infrastructure.Gateways;

namespace CloudHealthOffice.Infrastructure.ReferenceData.Payers;

/// <summary>
/// Tenant-scoped overlay on a global <see cref="PayerReference"/>. Global
/// directory data is never copied per tenant; only the fields that actually
/// differ are stored here. One tenant cannot read or write another tenant's
/// overrides — stores key exclusively by the requesting tenant id.
/// </summary>
public sealed class PayerTenantOverride
{
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Canonical payer id this overlay applies to.</summary>
    public string PayerId { get; set; } = string.Empty;

    /// <summary>
    /// When false, this tenant must not route transactions to the payer even
    /// if the global record is active.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Optional tenant-preferred display alias.</summary>
    public string? PreferredAlias { get; set; }

    /// <summary>
    /// Optional additional or replacement external identifiers for this tenant
    /// (for example a tenant-specific trading-partner id).
    /// </summary>
    public List<PayerExternalIdentifier> ExternalIdentifiers { get; set; } = new();

    /// <summary>
    /// Transactions for which this tenant has completed enrollment. Used to
    /// distinguish "payer requires enrollment" from "this tenant is enrolled".
    /// </summary>
    public List<HealthcareTransactionType> EnrolledTransactions { get; set; } = new();
}
