namespace CloudHealthOffice.ProviderEnrollmentService.Models;

// ─────────────────────────────────────────────────────────────────
// Enumerations
// ─────────────────────────────────────────────────────────────────

public enum EnrollmentStatus
{
    Unknown,
    Pending,
    Active,
    Suspended,          // payment hold / prepayment review
    Terminated,
    Denied,
    RevalidationRequired
}

public enum ApplicationStatus
{
    Draft,
    Submitted,
    PendingDocuments,
    UnderReview,
    PendingApproval,
    Approved,
    Denied,
    Withdrawn
}

public enum ProviderTypeClassification
{
    Unknown,
    PhysicianMD,
    PhysicianDO,
    NursePractitioner,
    PhysicianAssistant,
    BehavioralHealth,
    Dental,
    Vision,
    Pharmacy,
    Laboratory,
    DME,
    HomeHealth,
    Facility,
    FederallyQualifiedHealthCenter,
    RuralHealthClinic,
    Other
}

[Flags]
public enum LineOfBusiness
{
    None             = 0,
    Medicaid         = 1 << 0,
    CHIP             = 1 << 1,
    LTSS             = 1 << 2,
    BehavioralHealth = 1 << 3,
    STAR             = 1 << 4,   // TX-specific
    STARPlus         = 1 << 5,   // TX LTSS
    STARKids         = 1 << 6,   // TX children's
    Marketplace      = 1 << 7,
    Medicare         = 1 << 8,
    All              = ~0
}

public enum EnrollmentGapType
{
    ActiveContractNoEnrollment,   // CHO ProviderContract exists — state shows not enrolled
    ActiveEnrollmentNoContract,   // Enrolled in state — no CHO contract found
    TaxonomyMismatch,             // Enrolled taxonomy != contract taxonomy
    CountyServiceAreaMismatch,    // Enrolled counties != contracted service areas
    McoPanelMismatch,             // State MCO participation flag differs from CHO panel
    RevalidationOverdue           // Revalidation date passed but status still Active
}

public enum RestrictionType
{
    PaymentHold,
    PrepaymentReview,
    SiteVisitRequired,
    OwnershipDisclosure,
    CriminalBackgroundCheck
}

// ─────────────────────────────────────────────────────────────────
// Core domain records
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// Universal enrollment record — one per (NPI, StateCode) pair.
/// Produced by every IStateEnrollmentSource implementation.
/// </summary>
public record StateEnrollmentRecord
{
    public required string Npi                              { get; init; }
    public required string StateCode                        { get; init; }
    public required string SourceSystem                     { get; init; }  // "PEMS", "PAVE", "FMMIS", "eMedNY"
    public required EnrollmentStatus Status                 { get; init; }
    public required DateOnly EffectiveDate                  { get; init; }
    public DateOnly? TerminationDate                        { get; init; }
    public DateOnly? RevalidationDueDate                    { get; init; }
    public DateOnly? LastVerifiedDate                       { get; init; }
    public required ProviderTypeClassification ProviderType { get; init; }
    public LineOfBusiness SupportedLobs                     { get; init; }
    public IReadOnlyList<string> EnrolledTaxonomies         { get; init; } = [];
    public IReadOnlyList<string> EnrolledCounties           { get; init; } = [];
    public IReadOnlyList<string> EnrolledZipCodes           { get; init; } = [];
    public IReadOnlyList<string> McoParticipation           { get; init; } = [];
    public IReadOnlyList<EnrollmentRestriction> Restrictions { get; init; } = [];
    public DateTime CachedAt                                { get; init; } = DateTime.UtcNow;
    public bool IsFromCache                                 { get; init; }
    public string? RawSourcePayload                         { get; init; }  // audit trail
}

public record EnrollmentRestriction
{
    public required RestrictionType Type    { get; init; }
    public required string Description      { get; init; }
    public DateOnly? EffectiveDate          { get; init; }
    public DateOnly? LiftDate               { get; init; }
}

/// <summary>
/// Cross-state provider enrollment summary — aggregated from all active sources.
/// </summary>
public record ProviderEnrollmentSummary
{
    public required string Npi                                  { get; init; }
    public IReadOnlyList<string> ActiveStates                   { get; init; } = [];
    public IReadOnlyList<string> PendingStates                  { get; init; } = [];
    public IReadOnlyList<string> TerminatedStates               { get; init; } = [];
    public IReadOnlyList<StateEnrollmentRecord> AllRecords      { get; init; } = [];
    public IReadOnlyList<EnrollmentGap> EnrollmentGaps          { get; init; } = [];
    public IReadOnlyList<RevalidationRisk> RevalidationRisks    { get; init; } = [];
    public DateTime GeneratedAt                                 { get; init; } = DateTime.UtcNow;
}

public record EnrollmentGap
{
    public required string StateCode        { get; init; }
    public required EnrollmentGapType Type  { get; init; }
    public required string Description      { get; init; }
    public decimal? EstimatedRevenueAtRisk  { get; init; }
}

public record RevalidationRisk
{
    public required string StateCode                { get; init; }
    public required string SourceSystem             { get; init; }
    public required DateOnly RevalidationDueDate    { get; init; }
    public required int DaysRemaining               { get; init; }
    public decimal? EstimatedMonthlyRevenue         { get; init; }
}

/// <summary>
/// Tracks a provider's in-flight enrollment application through a state system.
/// </summary>
public record EnrollmentApplication
{
    public required string ApplicationId        { get; init; }
    public required string Npi                  { get; init; }
    public required string StateCode            { get; init; }
    public required string SourceSystem         { get; init; }
    public required ApplicationStatus Status    { get; init; }
    public DateOnly? SubmittedDate              { get; init; }
    public DateOnly? ExpectedDecisionDate       { get; init; }
    public DateOnly? DecisionDate               { get; init; }
    public string? DenialReason                 { get; init; }
    public IReadOnlyList<ApplicationWorkflowStep> WorkflowHistory { get; init; } = [];
    public IReadOnlyList<DeficiencyNotice> OpenDeficiencies       { get; init; } = [];
}

public record ApplicationWorkflowStep
{
    public required string StepName     { get; init; }
    public required DateTime Timestamp  { get; init; }
    public string? Notes                { get; init; }
}

public record DeficiencyNotice
{
    public required string DeficiencyCode   { get; init; }
    public required string Description      { get; init; }
    public DateOnly? DueDate                { get; init; }
    public bool IsResolved                  { get; init; }
}

/// <summary>
/// Result produced by all bulk batch sync workers.
/// </summary>
public record BatchSyncResult
{
    public required string StateCode        { get; init; }
    public required string SourceSystem     { get; init; }
    public required DateTime SyncStarted    { get; init; }
    public required DateTime SyncCompleted  { get; init; }
    public int RecordsProcessed             { get; init; }
    public int RecordsUpserted              { get; init; }
    public int RecordsSkipped               { get; init; }
    public int Errors                       { get; init; }
    public IReadOnlyList<string> ErrorDetails { get; init; } = [];
}

/// <summary>
/// Gate decision — used by PemsEnrollmentGate and any future state-specific gate.
/// </summary>
public record GateResult
{
    public bool Passed          { get; private init; }
    public string? DenialCode   { get; private init; }
    public string? DenialReason { get; private init; }

    public static GateResult Pass() => new() { Passed = true };

    public static GateResult Deny(string code, string reason) => new()
    {
        Passed      = false,
        DenialCode  = code,
        DenialReason = reason
    };
}

// ─────────────────────────────────────────────────────────────────
// Configuration options
// ─────────────────────────────────────────────────────────────────

public class ProviderEnrollmentOptions
{
    public const string SectionName = "ProviderEnrollmentService";

    /// <summary>Cache TTL for real-time enrollment lookups (default: 4 hours).</summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromHours(4);

    /// <summary>
    /// Redis TTL for tenant enrollment config documents (default: 5 minutes).
    /// Lower during rollout (e.g. 1 minute) so gate mode flips propagate quickly.
    /// </summary>
    public TimeSpan TenantConfigCacheTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Days before revalidation to begin alerting (default: 90).</summary>
    public int RevalidationWarningDays { get; set; } = 90;

    /// <summary>Which state adapters to activate. Empty = all registered adapters.</summary>
    public IList<string> EnabledStateCodes { get; set; } = [];

    public TmhpPemsOptions Tmhp { get; set; } = new();
    public CaqhOptions Caqh     { get; set; } = new();
}

public class TmhpPemsOptions
{
    public string BaseUrl           { get; set; } = "https://www.tmhp.com/api/provider";
    public string ApiKey            { get; set; } = string.Empty;
    public string SftpHost          { get; set; } = "sftp.tmhp.com";
    public string SftpUsername      { get; set; } = string.Empty;
    public string SftpPrivateKeyPath { get; set; } = string.Empty;
    public string BatchDropPath     { get; set; } = "/pems/exports/";
}

public class CaqhOptions
{
    public string BaseUrl           { get; set; } = "https://proview.caqh.org/api";
    public string Username          { get; set; } = string.Empty;
    public string Password          { get; set; } = string.Empty;
    public string OrganizationId    { get; set; } = string.Empty;
}
