namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// HIPAA / X12 transaction sets that a healthcare transaction gateway may
/// carry between Cloud Health Office and an external payer or clearinghouse.
///
/// Eligibility, claim submission, 277CA acknowledgment, 275 attachments,
/// 276/277 claim status, and 835 remittance retrieve are exercised today.
/// Claim intelligence is a read model over those transactions, not a new
/// HIPAA transaction set.
/// </summary>
public enum HealthcareTransactionType
{
    /// <summary>270 request / 271 response — eligibility &amp; benefit inquiry.</summary>
    Eligibility270271,

    /// <summary>837P — professional claim submission.</summary>
    ProfessionalClaim837P,

    /// <summary>837I — institutional claim submission.</summary>
    InstitutionalClaim837I,

    /// <summary>837D — dental claim submission.</summary>
    DentalClaim837D,

    /// <summary>276 request / 277 response — claim status inquiry.</summary>
    ClaimStatus276277,

    /// <summary>277CA — claim acknowledgment.</summary>
    ClaimAcknowledgment277CA,

    /// <summary>275 — additional information / claim attachments.</summary>
    ClaimAttachment275,

    /// <summary>835 — electronic remittance advice.</summary>
    Remittance835
}
