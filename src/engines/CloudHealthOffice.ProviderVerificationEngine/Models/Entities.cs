namespace CloudHealthOffice.ProviderVerificationEngine.Models;

/// <summary>
/// Composite provider verification result aggregating all public data sources.
/// Stored as the canonical verified-provider record in Cloud Health Office.
/// </summary>
public class ProviderVerificationRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Npi { get; set; } = string.Empty;
    public string? Ein { get; set; }

    // --- NPPES-sourced fields ---
    public NppesProviderData? NppesData { get; set; }

    // --- OIG/LEIE exclusion screening ---
    public ExclusionScreeningResult? ExclusionScreening { get; set; }

    // --- PECOS Medicare enrollment status ---
    public PecosEnrollmentStatus? PecosStatus { get; set; }

    // --- CMS Open Payments summary ---
    public OpenPaymentsSummary? OpenPaymentsSummary { get; set; }

    // --- Medicare utilization profile ---
    public MedicareUtilizationProfile? UtilizationProfile { get; set; }

    // --- Optional: FSMB license verification (paid tier) ---
    public FsmbLicenseVerification? FsmbVerification { get; set; }

    // --- Composite integrity score ---
    public ProviderIntegrityScore IntegrityScore { get; set; } = new();

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastVerifiedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? NextScheduledVerification { get; set; }
    public VerificationStatus Status { get; set; } = VerificationStatus.Pending;
}

public enum VerificationStatus
{
    Pending,
    Verified,
    VerifiedWithWarnings,
    Failed,
    Excluded,
    Expired,
    ManualReviewRequired
}

// -----------------------------------------------------------------
// NPPES
// -----------------------------------------------------------------

public class NppesProviderData
{
    public string Npi { get; set; } = string.Empty;
    public NppesEnumerationType EnumerationType { get; set; }
    public string? ProviderFirstName { get; set; }
    public string? ProviderLastName { get; set; }
    public string? ProviderMiddleName { get; set; }
    public string? ProviderCredential { get; set; }
    public string? OrganizationName { get; set; }
    public string? OrganizationSubpart { get; set; }
    public string? AuthorizedOfficialFirstName { get; set; }
    public string? AuthorizedOfficialLastName { get; set; }
    public string? AuthorizedOfficialTitle { get; set; }

    public List<NppesAddress> Addresses { get; set; } = [];
    public List<NppesTaxonomy> Taxonomies { get; set; } = [];
    public List<NppesIdentifier> OtherIdentifiers { get; set; } = [];
    public List<NppesEndpoint> Endpoints { get; set; } = [];

    public DateTimeOffset? EnumerationDate { get; set; }
    public DateTimeOffset? LastUpdated { get; set; }
    public DateTimeOffset? DeactivationDate { get; set; }
    public DateTimeOffset? ReactivationDate { get; set; }
    public NppesNpiStatus NpiStatus { get; set; } = NppesNpiStatus.Active;

    public DateTimeOffset RetrievedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum NppesEnumerationType
{
    Individual,   // Type 1 (NPI-1)
    Organization  // Type 2 (NPI-2)
}

public enum NppesNpiStatus
{
    Active,
    Deactivated
}

public class NppesAddress
{
    public string AddressPurpose { get; set; } = string.Empty; // LOCATION, MAILING
    public string AddressLine1 { get; set; } = string.Empty;
    public string? AddressLine2 { get; set; }
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public string CountryCode { get; set; } = "US";
    public string? TelephoneNumber { get; set; }
    public string? FaxNumber { get; set; }
}

public class NppesTaxonomy
{
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? License { get; set; }
    public string? State { get; set; }
    public bool IsPrimary { get; set; }

    /// <summary>
    /// Enriched via NLM Clinical Tables API crosswalk:
    /// NUCC taxonomy -> Medicare provider type + specialty.
    /// </summary>
    public string? MedicareProviderType { get; set; }
    public string? MedicareSpecialtyCode { get; set; }
}

public class NppesIdentifier
{
    public string Identifier { get; set; } = string.Empty;
    public string? Type { get; set; }
    public string? State { get; set; }
    public string? Issuer { get; set; }
}

public class NppesEndpoint
{
    public string EndpointType { get; set; } = string.Empty;
    public string? EndpointDescription { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string? Affiliation { get; set; }
    public string? ContentType { get; set; }
}

// -----------------------------------------------------------------
// OIG / LEIE / SAM Exclusion Screening
// -----------------------------------------------------------------

public class ExclusionScreeningResult
{
    public bool IsExcluded { get; set; }
    public List<ExclusionMatch> Matches { get; set; } = [];
    public DateTimeOffset ScreenedAt { get; set; } = DateTimeOffset.UtcNow;
    public ExclusionScreeningSource Source { get; set; }
}

public enum ExclusionScreeningSource
{
    OigLeie,
    SamGov,
    CmsPreclusion,
    StateMedicaid
}

public class ExclusionMatch
{
    public ExclusionScreeningSource Source { get; set; }
    public string? ExcludedName { get; set; }
    public string? Npi { get; set; }
    public string? ExclusionType { get; set; }
    public string? ExclusionReason { get; set; }
    public DateTimeOffset? ExclusionDate { get; set; }
    public DateTimeOffset? ReinstatementDate { get; set; }
    public string? WaiverState { get; set; }
    public float MatchConfidence { get; set; }
}

// -----------------------------------------------------------------
// PECOS Medicare Enrollment
// -----------------------------------------------------------------

public class PecosEnrollmentStatus
{
    public bool IsEnrolledInMedicare { get; set; }
    public string? EnrollmentType { get; set; }
    public string? ProviderTypeCode { get; set; }
    public string? ProviderTypeDescription { get; set; }
    public string? SpecialtyCode { get; set; }
    public string? SpecialtyDescription { get; set; }
    public string? EnrollmentState { get; set; }
    public string? EnrollmentCity { get; set; }
    public string? EnrollmentZip { get; set; }
    public bool? AcceptsMedicareAssignment { get; set; }
    public DateTimeOffset? EnrollmentDate { get; set; }
    public DateTimeOffset RetrievedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<PecosReassignment> Reassignments { get; set; } = [];
}

public class PecosReassignment
{
    public string OrganizationNpi { get; set; } = string.Empty;
    public string? OrganizationName { get; set; }
    public string? AssociationType { get; set; }
}

// -----------------------------------------------------------------
// CMS Open Payments
// -----------------------------------------------------------------

public class OpenPaymentsSummary
{
    public int ProgramYear { get; set; }
    public decimal TotalGeneralPayments { get; set; }
    public int GeneralPaymentCount { get; set; }
    public decimal TotalResearchPayments { get; set; }
    public int ResearchPaymentCount { get; set; }
    public bool HasOwnershipInterest { get; set; }
    public List<OpenPaymentsTopPayer> TopPayers { get; set; } = [];
    public DateTimeOffset RetrievedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class OpenPaymentsTopPayer
{
    public string PayerName { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public int PaymentCount { get; set; }
    public string? NatureOfPayment { get; set; }
}

// -----------------------------------------------------------------
// Medicare Utilization (Provider & Service Level)
// -----------------------------------------------------------------

public class MedicareUtilizationProfile
{
    public int CalendarYear { get; set; }
    public int TotalBeneficiaries { get; set; }
    public int TotalServices { get; set; }
    public decimal TotalSubmittedCharges { get; set; }
    public decimal TotalMedicareAllowed { get; set; }
    public decimal TotalMedicarePayment { get; set; }

    public List<UtilizationByService> TopServices { get; set; } = [];

    public PartDPrescribingSummary? PartDSummary { get; set; }

    public DateTimeOffset RetrievedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class UtilizationByService
{
    public string HcpcsCode { get; set; } = string.Empty;
    public string? HcpcsDescription { get; set; }
    public int ServiceCount { get; set; }
    public int BeneficiaryCount { get; set; }
    public decimal AverageSubmittedCharge { get; set; }
    public decimal AverageMedicarePayment { get; set; }
}

public class PartDPrescribingSummary
{
    public int TotalClaimCount { get; set; }
    public int TotalDrugCount { get; set; }
    public decimal TotalDrugCost { get; set; }
    public int BeneficiaryCount { get; set; }
    public decimal? OpioidPrescribingRate { get; set; }
    public bool? IsOpioidPrescriber { get; set; }
    public List<TopPrescribedDrug> TopDrugs { get; set; } = [];
}

public class TopPrescribedDrug
{
    public string DrugName { get; set; } = string.Empty;
    public string? GenericName { get; set; }
    public int ClaimCount { get; set; }
    public decimal TotalCost { get; set; }
    public int BeneficiaryCount { get; set; }
}

// -----------------------------------------------------------------
// FSMB License Verification (Premium Tier)
// -----------------------------------------------------------------

public class FsmbLicenseVerification
{
    public List<StateLicense> Licenses { get; set; } = [];
    public List<DisciplinaryAction> DisciplinaryActions { get; set; } = [];
    public string? DeaRegistrationNumber { get; set; }
    public DeaRegistrationStatus? DeaStatus { get; set; }
    public List<BoardCertification> BoardCertifications { get; set; } = [];
    public DateTimeOffset RetrievedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class StateLicense
{
    public string State { get; set; } = string.Empty;
    public string LicenseNumber { get; set; } = string.Empty;
    public string LicenseType { get; set; } = string.Empty;
    public LicenseStatus Status { get; set; }
    public DateTimeOffset? IssueDate { get; set; }
    public DateTimeOffset? ExpirationDate { get; set; }
    public bool IsPrimarySource { get; set; }
}

public enum LicenseStatus
{
    Active,
    Inactive,
    Expired,
    Revoked,
    Suspended,
    Probation,
    Surrendered,
    Pending,
    Unknown
}

public class DisciplinaryAction
{
    public string State { get; set; } = string.Empty;
    public string ActionType { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset? ActionDate { get; set; }
    public DateTimeOffset? EndDate { get; set; }
    public string? Basis { get; set; }
}

public enum DeaRegistrationStatus
{
    Active,
    Inactive,
    Revoked,
    Surrendered,
    Expired,
    Unknown
}

public class BoardCertification
{
    public string BoardName { get; set; } = string.Empty;
    public string SpecialtyName { get; set; } = string.Empty;
    public bool IsCertified { get; set; }
    public DateTimeOffset? CertificationDate { get; set; }
    public DateTimeOffset? ExpirationDate { get; set; }
}

// -----------------------------------------------------------------
// Composite Integrity Score
// -----------------------------------------------------------------

/// <summary>
/// Normalized 0-100 composite score with per-dimension breakdowns.
/// Designed to surface in the Blazor portal provider profile card
/// and as a claims adjudication pre-check signal.
/// </summary>
public class ProviderIntegrityScore
{
    public int CompositeScore { get; set; }
    public IntegrityRating Rating { get; set; } = IntegrityRating.Unknown;

    public ScoreDimension NpiValidation { get; set; } = new();
    public ScoreDimension ExclusionScreening { get; set; } = new();
    public ScoreDimension MedicareEnrollment { get; set; } = new();
    public ScoreDimension LicenseVerification { get; set; } = new();
    public ScoreDimension ConflictOfInterest { get; set; } = new();

    public List<IntegrityFlag> Flags { get; set; } = [];
    public DateTimeOffset CalculatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ScoreDimension
{
    public string Dimension { get; set; } = string.Empty;
    public int Score { get; set; }
    public int Weight { get; set; }
    public string? Detail { get; set; }
    public bool WasEvaluated { get; set; }
}

public enum IntegrityRating
{
    Unknown,
    Clear,         // 80-100: All checks passed
    Advisory,      // 60-79:  Minor warnings
    Caution,       // 40-59:  Significant concerns
    Alert,         // 20-39:  Critical issues
    Blocked        //  0-19:  Hard stop
}

public class IntegrityFlag
{
    public IntegrityFlagSeverity Severity { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum IntegrityFlagSeverity
{
    Info,
    Warning,
    Critical,
    Blocking
}
