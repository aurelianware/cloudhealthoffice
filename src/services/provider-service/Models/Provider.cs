using System;
using System.ComponentModel.DataAnnotations;

namespace ProviderService.Models;

/// <summary>
/// Represents a healthcare provider (physician, hospital, facility).
/// Used for network validation, claims adjudication, and provider directory.
///
/// <para>
/// Individual-type rows (<see cref="ProviderType.Individual"/>) are
/// projected to a FHIR R4 Practitioner resource by
/// <see cref="Services.IFhirPractitionerProjector"/> (capability 5.7).
/// Organization-type rows project as FHIR Organization in capability 5.9.
/// </para>
/// </summary>
public class Provider
{
    /// <summary>
    /// Multi-tenant partition key (required for Cosmos DB isolation)
    /// </summary>
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Unique identifier (Cosmos DB document id / Mongo _id). Per-version
    /// — each row in the version chain has its own <c>Id</c>. Existing
    /// callers that historically treated <c>Id</c> as the persistent
    /// provider identifier are still supported because legacy single-row
    /// chains satisfy <c>ProviderId == Id</c> after hydration; on
    /// multi-version chains, the chain key is <see cref="ProviderId"/>.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Stable chain key — the provider entity identifier preserved across
    /// every version row. For genesis / legacy single-row chains
    /// <c>ProviderId == Id</c>; subsequent amend versions share the same
    /// <c>ProviderId</c> with a new per-row <c>Id</c>. Empty on the wire
    /// is the legacy marker; hydration sets <c>ProviderId = Id</c>.
    /// </summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>
    /// National Provider Identifier (10-digit)
    /// Type 1 = Individual, Type 2 = Organization
    /// </summary>
    [Required]
    [StringLength(10, MinimumLength = 10)]
    [RegularExpression(@"^\d{10}$", ErrorMessage = "NPI must be 10 digits")]
    public string NPI { get; set; } = string.Empty;

    /// <summary>
    /// Provider type (Individual or Organization)
    /// </summary>
    [Required]
    public ProviderType ProviderType { get; set; }

    /// <summary>
    /// Tax Identification Number (EIN for organizations, SSN for individuals - encrypted)
    /// </summary>
    [StringLength(20)]
    public string? TaxId { get; set; }

    // Individual provider fields
    /// <summary>
    /// First name (for individual providers)
    /// </summary>
    [StringLength(100)]
    public string? FirstName { get; set; }

    /// <summary>
    /// Last name (for individual providers)
    /// </summary>
    [StringLength(100)]
    public string? LastName { get; set; }

    /// <summary>
    /// Middle name (for individual providers)
    /// </summary>
    [StringLength(100)]
    public string? MiddleName { get; set; }

    /// <summary>
    /// Professional credentials (MD, DO, NP, PA, DDS, etc.)
    /// </summary>
    [StringLength(20)]
    public string? Credentials { get; set; }

    // Organization provider fields
    /// <summary>
    /// Organization legal name (for facility/group providers)
    /// </summary>
    [StringLength(300)]
    public string? OrganizationName { get; set; }

    /// <summary>
    /// Doing Business As (DBA) name
    /// </summary>
    [StringLength(300)]
    public string? DBAName { get; set; }

    /// <summary>
    /// Primary specialty (NUCC taxonomy code)
    /// </summary>
    [Required]
    [StringLength(20)]
    public string PrimarySpecialty { get; set; } = string.Empty;

    /// <summary>
    /// Primary taxonomy code (NUCC Healthcare Provider Taxonomy)
    /// Example: 207R00000X = Internal Medicine
    /// </summary>
    [Required]
    [StringLength(10)]
    public string TaxonomyCode { get; set; } = string.Empty;

    /// <summary>
    /// Secondary specialties (taxonomy codes)
    /// </summary>
    public List<string> SecondarySpecialties { get; set; } = new();

    /// <summary>
    /// Practice address
    /// </summary>
    [StringLength(300)]
    public string? Address { get; set; }

    /// <summary>
    /// City
    /// </summary>
    [StringLength(100)]
    public string? City { get; set; }

    /// <summary>
    /// State code (2 letters)
    /// </summary>
    [StringLength(2)]
    public string? State { get; set; }

    /// <summary>
    /// ZIP code
    /// </summary>
    [StringLength(10)]
    public string? ZipCode { get; set; }

    /// <summary>
    /// Phone number
    /// </summary>
    [Phone]
    [StringLength(20)]
    public string? Phone { get; set; }

    /// <summary>
    /// Fax number
    /// </summary>
    [StringLength(20)]
    public string? Fax { get; set; }

    /// <summary>
    /// Email address
    /// </summary>
    [EmailAddress]
    [StringLength(200)]
    public string? Email { get; set; }

    /// <summary>
    /// Network participations (in-network for specific plans/LOBs)
    /// </summary>
    public List<NetworkParticipation> NetworkParticipations { get; set; } = new();

    /// <summary>
    /// Credentialing status. Projection of the credentialing event chain
    /// (capability 5.6) — written via
    /// <see cref="Repositories.IProviderRepository.UpdateCredentialingProjectionAsync"/>,
    /// NOT via <see cref="Repositories.IProviderRepository.UpdateAsync"/>.
    /// New providers default to <see cref="CredentialingStatus.Unknown"/>
    /// until an <see cref="CredentialingEventType.ApplicationSubmitted"/>
    /// event opens the first chain.
    /// </summary>
    [Required]
    public CredentialingStatus CredentialingStatus { get; set; } = CredentialingStatus.Unknown;

    /// <summary>
    /// Most recent approval date. Projection of the credentialing event
    /// chain — set when a
    /// <see cref="CredentialingEventType.DecisionRecorded"/> event with
    /// <see cref="CredentialingDecision.Approved"/> lands.
    /// </summary>
    public DateTime? CredentialingDate { get; set; }

    /// <summary>
    /// Next re-credentialing due date (typically every 2-3 years).
    /// Projection of the credentialing event chain — set on the most
    /// recent approval. When elapsed the projector reports
    /// <see cref="CredentialingStatus.Expired"/> at read time even if the
    /// stored value is still <see cref="CredentialingStatus.Approved"/>.
    /// </summary>
    public DateTime? RecredentialingDueDate { get; set; }

    /// <summary>
    /// CAQH ProView ID (for credentialing data exchange)
    /// </summary>
    [StringLength(20)]
    public string? CAQHProviderId { get; set; }

    /// <summary>
    /// Last CAQH sync date
    /// </summary>
    public DateTime? LastCAQHSyncDate { get; set; }

    /// <summary>
    /// Board certifications
    /// </summary>
    public List<BoardCertification> BoardCertifications { get; set; } = new();

    /// <summary>
    /// Hospital affiliations (for admitting privileges)
    /// </summary>
    public List<HospitalAffiliation> HospitalAffiliations { get; set; } = new();

    /// <summary>
    /// Accepting new patients?
    /// </summary>
    public bool AcceptingNewPatients { get; set; } = true;

    /// <summary>
    /// Handicap accessible?
    /// </summary>
    public bool HandicapAccessible { get; set; }

    /// <summary>
    /// Languages spoken (ISO 639-1 codes: en, es, zh, etc.)
    /// </summary>
    public List<string> LanguagesSpoken { get; set; } = new() { "en" };

    /// <summary>
    /// Provider status
    /// </summary>
    [Required]
    public ProviderStatus Status { get; set; } = ProviderStatus.Active;

    /// <summary>
    /// Termination date (if deactivated)
    /// </summary>
    public DateTime? TerminationDate { get; set; }

    /// <summary>
    /// Termination reason
    /// </summary>
    [StringLength(500)]
    public string? TerminationReason { get; set; }

    /// <summary>
    /// Bank account / EFT disbursement information for capitation payments
    /// </summary>
    public ProviderBankAccount? BankAccount { get; set; }

    /// <summary>
    /// Audit: Record creation timestamp
    /// </summary>
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Audit: Last modification timestamp
    /// </summary>
    public DateTime LastUpdatedDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Audit: Created by user/system
    /// </summary>
    [StringLength(200)]
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Audit: Last updated by user/system
    /// </summary>
    [StringLength(200)]
    public string? LastUpdatedBy { get; set; }

    // ── Cached integrity projection (capability 5.4.5) ──────────────
    //
    // Projection metadata maintained by ProviderIntegrityProjectionService.
    // The hosted IntegrityProjectionWorker calls provider-verification-service
    // on a schedule and writes the four fields below back onto the head
    // Active version via IProviderRepository.UpdateIntegrityProjectionAsync —
    // a dedicated patch path that bypasses the version-state guard on
    // UpdateAsync. Active rows otherwise remain read-only at the
    // application layer (PR 7.2 / 5.1 — provider versioning).
    //
    // These fields are *projection metadata*, NOT version-identity fields:
    // updating them does NOT create a new VersionNumber. See
    // docs/architecture/provider-versioning.md "Projection metadata —
    // exempt from versioning".
    //
    // Read consumers: roster (capability 5.4) sorts on IntegrityScore;
    // adjudication, FHIR projections, and the provider-profile portal
    // card surface it. Live HTTP fetch via HttpProviderIntegrityGate
    // remains available for fresh-or-cached decisions per consumer.

    /// <summary>
    /// Composite integrity score (0–100) from the most recent
    /// verification. Null until the projection worker runs the first
    /// time for this provider.
    /// </summary>
    public int? IntegrityScore { get; set; }

    /// <summary>
    /// Rating bucket — Clear / Advisory / Caution / Alert / Blocked /
    /// Unknown. Mirrors <c>IntegrityRating</c> in the verification engine.
    /// </summary>
    [StringLength(50)]
    public string? IntegrityRating { get; set; }

    /// <summary>
    /// When the score was produced by provider-verification-service.
    /// </summary>
    public DateTimeOffset? LastVerifiedAt { get; set; }

    /// <summary>
    /// When the projection worker should re-verify this provider next.
    /// Computed from <c>LastVerifiedAt + ShortestActiveWindow</c>; null
    /// for never-verified rows (sweep filter picks them up too).
    /// </summary>
    public DateTimeOffset? NextVerificationDue { get; set; }

    // ---------------------------------------------------------------------
    // Version identity (5.1 — Provider Identity & Versioning)
    //
    // A provider is an append-only chain of immutable Active versions. Each
    // row in the Providers collection is one version; the chain is keyed on
    // (TenantId, ProviderId) — ProviderId is the persistent provider identifier,
    // while Id (VersionId) is the per-version ULID row key. Documents written
    // before these fields existed hydrate as VersionState=Active, VersionNumber=1,
    // ProviderId=Id (see docs/architecture/provider-versioning.md).
    //
    // The legacy ProviderStatus enum is preserved as the back-compat signal:
    // hydration normalizes Status from VersionState (Active↔Active,
    // Suspended→Inactive, Terminated→Terminated) so existing consumers
    // (search filter `status = 'Active'`, PcpAssignmentService) continue to
    // work without changes.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Stable per-version identifier (ULID, Crockford base-32). Set
    /// explicitly by the service layer when a draft or legacy v1 is
    /// created. Empty on the wire ⇒ legacy row (predates this feature)
    /// and is hydrated as Active v1 on read.
    /// </summary>
    public string VersionId { get; set; } = string.Empty;

    /// <summary>
    /// 1-based monotonic sequence within <c>(TenantId, ProviderId)</c>. Populated
    /// by the service when creating new versions; left at the default for
    /// legacy documents so hydration can fix it up on read.
    /// </summary>
    public int VersionNumber { get; set; }

    /// <summary>
    /// Lifecycle state. Populated by the service when creating new
    /// versions; legacy documents missing this field deserialize to the
    /// default and are normalized to <see cref="ProviderVersionState.Active"/>
    /// during hydration.
    /// </summary>
    public ProviderVersionState VersionState { get; set; }

    /// <summary>
    /// <see cref="VersionId"/> of the version this draft amends, if any.
    /// Null for the genesis version.
    /// </summary>
    public string? PredecessorVersionId { get; set; }

    public DateTime? ActivatedAt { get; set; }

    [StringLength(200)]
    public string? ActivatedBy { get; set; }

    public DateTime? SuspendedAt { get; set; }

    [StringLength(500)]
    public string? SuspensionReason { get; set; }

    public DateTime? SupersededAt { get; set; }

    [StringLength(64)]
    public string? SupersededByVersionId { get; set; }

    /// <summary>
    /// Full name helper property
    /// </summary>
    public string FullName => ProviderType == ProviderType.Individual
        ? $"{FirstName} {MiddleName} {LastName} {Credentials}".Replace("  ", " ").Trim()
        : OrganizationName ?? "Unknown Organization";
}

/// <summary>
/// Network participation record (links provider to specific plan/LOB/network tier)
/// </summary>
public class NetworkParticipation
{
    /// <summary>
    /// Plan ID (optional - can participate at LOB level)
    /// </summary>
    [StringLength(50)]
    public string? PlanId { get; set; }

    /// <summary>
    /// Stable chain key of the <see cref="Organization"/> network this
    /// participation is contracted under (capability 5.3 / 5.4). References
    /// <see cref="Organization.OrganizationId"/> — never a per-version <c>Id</c>.
    ///
    /// <para>
    /// Nullable for backward compatibility with participations written
    /// before capability 5.4. Legacy participations without
    /// <c>NetworkId</c> are <b>invisible</b> to <c>GET /api/v1/networks/{id}/roster</c>
    /// by design; the migration path is per-tenant backfill as
    /// <see cref="Organization"/> rows are authored. Plan-level lookups
    /// (filter on <see cref="PlanId"/> / <see cref="LineOfBusiness"/>)
    /// continue to work unchanged.
    /// </para>
    /// </summary>
    [StringLength(64)]
    public string? NetworkId { get; set; }

    /// <summary>
    /// Line of Business
    /// </summary>
    [Required]
    public LineOfBusiness LineOfBusiness { get; set; }

    /// <summary>
    /// Network tier (Tier 1 = lowest cost-sharing, Tier 2 = medium, Tier 3 = highest)
    /// </summary>
    [StringLength(20)]
    public string NetworkTier { get; set; } = "Tier1";

    /// <summary>
    /// Participation effective date
    /// </summary>
    [Required]
    public DateTime EffectiveDate { get; set; }

    /// <summary>
    /// Participation termination date
    /// </summary>
    public DateTime? TerminationDate { get; set; }

    /// <summary>
    /// Is provider accepting new patients in this network?
    /// </summary>
    public bool AcceptingNewPatients { get; set; } = true;

    /// <summary>
    /// Contracted rates (optional - for fee schedule reference)
    /// </summary>
    public ContractedRates? Rates { get; set; }

    // ── PCP panel controls (capabilities 5.5 / 5.7) ─────────────────────
    //
    // These gate PCP assignment in coverage-service. Null = legacy /
    // unconstrained: a participation that has not been touched by
    // panel-gating-aware code defaults to "panel open, any LOB covered
    // by the participation, no age limits."
    //
    // Going-forward writes through ProvidersController.CreateProvider /
    // UpdateProvider / AddNetworkParticipation are expected to populate
    // these fields. Producers that elide them surface a structured
    // soft-validation warning + Prometheus counter
    // (provider_service_panel_gating_missing_writes_total) so the
    // follow-up hard-validation cutover can flip on telemetry-driven
    // evidence. Capability 5.5 closes the legacy data gap with a
    // one-time admin-triggered backfill that writes legacy-unconstrained
    // defaults via UpdatePanelGatingDefaultsAsync (bypasses the
    // version-immutability guard for these fields only — see
    // docs/architecture/provider-versioning.md "Operational backfill —
    // one-time exemption").
    //
    // See docs/architecture/network-participation-backfill.md for the
    // backfill operational contract and
    // docs/architecture/pcp-assignment.md for the consumer-side
    // semantics in coverage-service.

    /// <summary>
    /// Maximum number of members that may be assigned to this provider under this
    /// participation. Null = unlimited / not yet backfilled.
    /// </summary>
    public int? PanelLimit { get; set; }

    /// <summary>
    /// Whether this provider accepts new PCP assignments for this participation.
    /// Distinct from <see cref="AcceptingNewPatients"/> (any new patient) — a panel
    /// may be closed to new PCP members while still seeing referrals.
    /// Null = treated as <see cref="AcceptingNewPatients"/>.
    /// </summary>
    public bool? PanelAccepted { get; set; }

    /// <summary>
    /// LOBs this participation will accept as a PCP (subset of / equal to
    /// <see cref="LineOfBusiness"/>). Empty = accept any LOB covered by this
    /// participation. Used by coverage-service to enforce Medicaid/Medicare/etc.
    /// PCP rules without proliferating separate participations per LOB.
    /// </summary>
    public List<LineOfBusiness> AcceptedLobs { get; set; } = new();

    /// <summary>
    /// Minimum member age (years) accepted on this panel. Null = no floor.
    /// Example: Internal Medicine participation with MinAcceptedAgeYears=18.
    /// </summary>
    public int? MinAcceptedAgeYears { get; set; }

    /// <summary>
    /// Maximum member age (years) accepted on this panel. Null = no ceiling.
    /// Example: Pediatrics participation with MaxAcceptedAgeYears=21.
    /// </summary>
    public int? MaxAcceptedAgeYears { get; set; }
}

/// <summary>
/// Contracted payment rates
/// </summary>
public class ContractedRates
{
    /// <summary>
    /// Fee schedule name
    /// </summary>
    [StringLength(100)]
    public string? FeeScheduleName { get; set; }

    /// <summary>
    /// Percentage of Medicare (e.g., 1.15 = 115% of Medicare)
    /// </summary>
    public decimal? PercentOfMedicare { get; set; }

    /// <summary>
    /// Flat per-member-per-month capitation
    /// </summary>
    public decimal? PMPM { get; set; }

    /// <summary>
    /// Case rate (e.g., per pregnancy, per surgery)
    /// </summary>
    public decimal? CaseRate { get; set; }
}

/// <summary>
/// Board certification record
/// </summary>
public class BoardCertification
{
    /// <summary>
    /// Specialty (e.g., "Internal Medicine", "Cardiology")
    /// </summary>
    [Required]
    [StringLength(200)]
    public string Specialty { get; set; } = string.Empty;

    /// <summary>
    /// Certifying board (e.g., "American Board of Internal Medicine")
    /// </summary>
    [Required]
    [StringLength(200)]
    public string Board { get; set; } = string.Empty;

    /// <summary>
    /// Certification date
    /// </summary>
    public DateTime CertificationDate { get; set; }

    /// <summary>
    /// Expiration date (typically 10 years)
    /// </summary>
    public DateTime? ExpirationDate { get; set; }
}

/// <summary>
/// Hospital affiliation (admitting privileges)
/// </summary>
public class HospitalAffiliation
{
    /// <summary>
    /// Hospital NPI
    /// </summary>
    [Required]
    [StringLength(10)]
    public string HospitalNPI { get; set; } = string.Empty;

    /// <summary>
    /// Hospital name
    /// </summary>
    [Required]
    [StringLength(300)]
    public string HospitalName { get; set; } = string.Empty;

    /// <summary>
    /// Has admitting privileges?
    /// </summary>
    public bool AdmittingPrivileges { get; set; }

    /// <summary>
    /// Effective date
    /// </summary>
    public DateTime EffectiveDate { get; set; }

    /// <summary>
    /// Termination date
    /// </summary>
    public DateTime? TerminationDate { get; set; }
}

/// <summary>
/// Provider type (NPI Type 1 vs Type 2)
/// </summary>
public enum ProviderType
{
    /// <summary>
    /// Individual provider (physician, NP, PA, etc.)
    /// </summary>
    Individual = 1,

    /// <summary>
    /// Organization (hospital, clinic, group practice, DME supplier)
    /// </summary>
    Organization = 2
}

/// <summary>
/// Credentialing status — read-side projection of the credentialing event
/// chain (capability 5.6). The credentialing event chain is the
/// system-of-record; this enum is the collapsed status flag consumed by
/// downstream services (coverage-service PCP gating, benefit-plan-service
/// adjudication checks).
///
/// <para>
/// Per PR #705 enum convention: <see cref="Unknown"/> = 0 first, explicit
/// integer values, string serialization with
/// <c>JsonStringEnumConverter(allowIntegerValues: false)</c>. Adding
/// <see cref="Unknown"/>=0 in front of <see cref="Pending"/>=1 is
/// backward-compatible: existing stored integer values map by position
/// (Mongo's default reflection serializer reads integer 1 → Pending),
/// and existing string values ("Pending", "Approved", etc.) continue to
/// deserialize unchanged.
/// </para>
/// </summary>
public enum CredentialingStatus
{
    /// <summary>
    /// No credentialing chain has ever been opened for this provider.
    /// Default for newly-created providers. The projector returns this
    /// value when the event chain is empty.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Application submitted or re-credentialing triggered, under review.
    /// </summary>
    Pending = 1,

    /// <summary>
    /// Credentialing approved, can participate.
    /// </summary>
    Approved = 2,

    /// <summary>
    /// Credentialing denied.
    /// </summary>
    Denied = 3,

    /// <summary>
    /// Most recent approval has lapsed; re-credentialing required.
    /// Computed at projection time from
    /// <see cref="Provider.RecredentialingDueDate"/>.
    /// </summary>
    Expired = 4,

    /// <summary>
    /// Suspended (quality issues, fraud, etc.). Phase 1 has no event-driven
    /// write path for this state — it remains for read-side compatibility.
    /// A future <c>SuspensionRecorded</c> event lands in Phase 2 alongside
    /// appeals and peer review.
    /// </summary>
    Suspended = 5
}

/// <summary>
/// Provider status
/// </summary>
public enum ProviderStatus
{
    /// <summary>
    /// Active and participating
    /// </summary>
    Active = 1,

    /// <summary>
    /// Temporarily inactive (leave of absence)
    /// </summary>
    Inactive = 2,

    /// <summary>
    /// Terminated from network
    /// </summary>
    Terminated = 3,

    /// <summary>
    /// Pending activation
    /// </summary>
    Pending = 4
}

/// <summary>
/// Line of Business enum (matches other services)
/// </summary>
public enum LineOfBusiness
{
    Commercial = 1,
    Medicare = 2,
    Medicaid = 3,
    Exchange = 4,
    TRICARE = 5,
    VA = 6
}

/// <summary>
/// Provider bank account / EFT disbursement information.
/// Mirrors SponsorBankAccount from premium-billing-service but for the credit (payment) side.
/// Used by capitation-service to disburse NACHA credits or Stripe Connect payouts.
/// </summary>
public class ProviderBankAccount
{
    /// <summary>
    /// Whether EFT disbursement is enabled for this provider
    /// </summary>
    public bool EftEnabled { get; set; }

    /// <summary>
    /// Preferred disbursement method
    /// </summary>
    public DisbursementMethod PreferredDisbursementMethod { get; set; } = DisbursementMethod.Check;

    /// <summary>
    /// Bank routing number (9-digit ABA — stored in vault, passed through for NACHA generation)
    /// </summary>
    [StringLength(9)]
    public string? RoutingNumber { get; set; }

    /// <summary>
    /// Bank account number (stored in vault)
    /// </summary>
    [StringLength(34)]
    public string? AccountNumber { get; set; }

    /// <summary>
    /// Account type
    /// </summary>
    public BankAccountType AccountType { get; set; } = BankAccountType.Checking;

    /// <summary>
    /// Name on the bank account
    /// </summary>
    [StringLength(200)]
    public string? AccountHolderName { get; set; }

    /// <summary>
    /// Stripe Connect account ID (acct_xxx) for Stripe payouts
    /// </summary>
    [StringLength(100)]
    public string? StripeConnectedAccountId { get; set; }

    /// <summary>
    /// Last 4 digits of routing number (for display)
    /// </summary>
    [StringLength(4)]
    public string? RoutingNumberLast4 { get; set; }

    /// <summary>
    /// Last 4 digits of account number (for display)
    /// </summary>
    [StringLength(4)]
    public string? AccountNumberLast4 { get; set; }

    /// <summary>
    /// Whether a W-9 is on file (required for 1099 compliance)
    /// </summary>
    public bool W9OnFile { get; set; }

    /// <summary>
    /// Tax ID for 1099 reporting (EIN or SSN)
    /// </summary>
    [StringLength(20)]
    public string? TaxId { get; set; }

    /// <summary>
    /// Type of Tax ID on file
    /// </summary>
    public TaxIdType? TaxIdType { get; set; }
}

/// <summary>
/// Disbursement method for provider payments
/// </summary>
public enum DisbursementMethod
{
    /// <summary>
    /// NACHA ACH credit (bank file submission)
    /// </summary>
    NachaCredit = 1,

    /// <summary>
    /// Stripe Connect payout
    /// </summary>
    StripeConnect = 2,

    /// <summary>
    /// Paper check
    /// </summary>
    Check = 3
}

/// <summary>
/// Bank account type
/// </summary>
public enum BankAccountType
{
    Checking = 1,
    Savings = 2
}

/// <summary>
/// Tax identification number type for 1099 reporting
/// </summary>
public enum TaxIdType
{
    /// <summary>
    /// Employer Identification Number (organizations)
    /// </summary>
    EIN = 1,

    /// <summary>
    /// Social Security Number (individuals)
    /// </summary>
    SSN = 2
}
