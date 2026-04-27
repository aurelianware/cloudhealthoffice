namespace ProviderService.Models;

/// <summary>
/// Vendor-neutral response envelope returned by
/// <see cref="Adapters.IProviderAdapter.GetProviderAsync"/> and
/// <see cref="Adapters.IProviderAdapter.GetProviderByNpiAsync"/>.
///
/// <para>
/// The payload <see cref="AdapterProvider"/> is shaped to project cleanly onto
/// future FHIR <c>Practitioner</c> / <c>Organization</c> resources (Sections
/// 5.7–5.9): individual-name fields map onto <c>Practitioner.name</c>,
/// organization name onto <c>Organization.name</c>, address fields onto
/// <c>Practitioner.address</c> / <c>Organization.address</c>, taxonomy onto
/// <c>Practitioner.qualification</c>, and the integrity fields onto a
/// payer-specific verification extension.
/// </para>
/// </summary>
public class ProviderAdapterResponse
{
    /// <summary>Adapter that produced the response (e.g. "cho", "qnxt").</summary>
    public string Platform { get; set; } = string.Empty;

    /// <summary>Optional raw vendor response retained for audit / debugging.</summary>
    public string? RawResponse { get; set; }

    /// <summary>Provider payload. Null when the requested provider is not found.</summary>
    public AdapterProvider? Provider { get; set; }
}

/// <summary>
/// Vendor-neutral response envelope for collection-returning adapter methods
/// (<see cref="Adapters.IProviderAdapter.SearchProvidersAsync"/> and
/// <see cref="Adapters.IProviderAdapter.GetNetworkRosterAsync"/>).
/// </summary>
public class ProviderRosterAdapterResponse
{
    public string Platform { get; set; } = string.Empty;
    public string? RawResponse { get; set; }

    /// <summary>Page of providers returned by the adapter (never null; may be empty).</summary>
    public IReadOnlyList<AdapterProvider> Providers { get; set; } = Array.Empty<AdapterProvider>();

    /// <summary>Total matching count when the platform reports it; null otherwise.</summary>
    public int? TotalCount { get; set; }
}

/// <summary>
/// Vendor-neutral response envelope for
/// <see cref="Adapters.IProviderAdapter.GetNetworkAsync"/>. The
/// <see cref="AdapterNetwork"/> shape is a deliberate placeholder until
/// capability 5.3 (Network entity) ships; today every adapter throws
/// <see cref="NotImplementedException"/> from this call.
/// </summary>
public class NetworkAdapterResponse
{
    public string Platform { get; set; } = string.Empty;
    public string? RawResponse { get; set; }

    /// <summary>Network payload. Null today; populated once 5.3 lands.</summary>
    public AdapterNetwork? Network { get; set; }
}

/// <summary>
/// Placeholder Network DTO. Field set will be expanded by capability 5.3.
/// Kept minimal today so the interface compiles and future fields can be added
/// without a breaking change to callers that only inspect the envelope.
/// </summary>
public class AdapterNetwork
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Free-form status string ("Active", "Inactive", ...). Strict enum follows in 5.3.</summary>
    public string? Status { get; set; }
}

/// <summary>
/// Normalized provider DTO. Field shape mirrors <see cref="Provider"/> so the
/// CHO pass-through is lossless; round-trip mappers <see cref="From"/> and
/// <see cref="ToProvider"/> let the controller return the existing wire format
/// to current consumers without any contract change.
/// </summary>
public class AdapterProvider
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;

    public string Npi { get; set; } = string.Empty;
    public ProviderType ProviderType { get; set; }
    public string? TaxId { get; set; }

    // Individual name parts
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? MiddleName { get; set; }
    public string? Credentials { get; set; }

    // Organization name parts
    public string? OrganizationName { get; set; }
    public string? DBAName { get; set; }

    public string PrimarySpecialty { get; set; } = string.Empty;
    public string TaxonomyCode { get; set; } = string.Empty;
    public List<string> SecondarySpecialties { get; set; } = new();

    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? ZipCode { get; set; }
    public string? Phone { get; set; }
    public string? Fax { get; set; }
    public string? Email { get; set; }

    public List<NetworkParticipation> NetworkParticipations { get; set; } = new();

    public CredentialingStatus CredentialingStatus { get; set; }
    public DateTime? CredentialingDate { get; set; }
    public DateTime? RecredentialingDueDate { get; set; }
    public string? CAQHProviderId { get; set; }
    public DateTime? LastCAQHSyncDate { get; set; }

    public List<BoardCertification> BoardCertifications { get; set; } = new();
    public List<HospitalAffiliation> HospitalAffiliations { get; set; } = new();

    public bool AcceptingNewPatients { get; set; } = true;
    public bool HandicapAccessible { get; set; }
    public List<string> LanguagesSpoken { get; set; } = new() { "en" };

    public ProviderStatus Status { get; set; } = ProviderStatus.Active;
    public DateTime? TerminationDate { get; set; }
    public string? TerminationReason { get; set; }

    public ProviderBankAccount? BankAccount { get; set; }

    public DateTime CreatedDate { get; set; }
    public DateTime LastUpdatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public string? LastUpdatedBy { get; set; }

    /// <summary>
    /// Cached integrity score from the verification engine. Carried through so
    /// FHIR projections (5.7–5.9) can surface verification metadata without a
    /// second round-trip; capability 5.10 owns the read-side decoration that
    /// keeps this fresh independently of the adapter.
    /// </summary>
    public int? IntegrityScore { get; set; }
    public string? IntegrityRating { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public DateTimeOffset? NextVerificationDue { get; set; }

    // Version-chain identity (5.1)
    public string VersionId { get; set; } = string.Empty;
    public int VersionNumber { get; set; }
    public ProviderVersionState VersionState { get; set; }
    public string? PredecessorVersionId { get; set; }
    public DateTime? ActivatedAt { get; set; }
    public string? ActivatedBy { get; set; }
    public DateTime? SuspendedAt { get; set; }
    public string? SuspensionReason { get; set; }
    public DateTime? SupersededAt { get; set; }
    public string? SupersededByVersionId { get; set; }

    public static AdapterProvider From(Provider src) => new()
    {
        Id = src.Id,
        TenantId = src.TenantId,
        ProviderId = src.ProviderId,
        Npi = src.NPI,
        ProviderType = src.ProviderType,
        TaxId = src.TaxId,
        FirstName = src.FirstName,
        LastName = src.LastName,
        MiddleName = src.MiddleName,
        Credentials = src.Credentials,
        OrganizationName = src.OrganizationName,
        DBAName = src.DBAName,
        PrimarySpecialty = src.PrimarySpecialty,
        TaxonomyCode = src.TaxonomyCode,
        SecondarySpecialties = src.SecondarySpecialties.ToList(),
        Address = src.Address,
        City = src.City,
        State = src.State,
        ZipCode = src.ZipCode,
        Phone = src.Phone,
        Fax = src.Fax,
        Email = src.Email,
        NetworkParticipations = src.NetworkParticipations.ToList(),
        CredentialingStatus = src.CredentialingStatus,
        CredentialingDate = src.CredentialingDate,
        RecredentialingDueDate = src.RecredentialingDueDate,
        CAQHProviderId = src.CAQHProviderId,
        LastCAQHSyncDate = src.LastCAQHSyncDate,
        BoardCertifications = src.BoardCertifications.ToList(),
        HospitalAffiliations = src.HospitalAffiliations.ToList(),
        AcceptingNewPatients = src.AcceptingNewPatients,
        HandicapAccessible = src.HandicapAccessible,
        LanguagesSpoken = src.LanguagesSpoken.ToList(),
        Status = src.Status,
        TerminationDate = src.TerminationDate,
        TerminationReason = src.TerminationReason,
        BankAccount = src.BankAccount,
        CreatedDate = src.CreatedDate,
        LastUpdatedDate = src.LastUpdatedDate,
        CreatedBy = src.CreatedBy,
        LastUpdatedBy = src.LastUpdatedBy,
        IntegrityScore = src.IntegrityScore,
        IntegrityRating = src.IntegrityRating,
        LastVerifiedAt = src.LastVerifiedAt,
        NextVerificationDue = src.NextVerificationDue,
        VersionId = src.VersionId,
        VersionNumber = src.VersionNumber,
        VersionState = src.VersionState,
        PredecessorVersionId = src.PredecessorVersionId,
        ActivatedAt = src.ActivatedAt,
        ActivatedBy = src.ActivatedBy,
        SuspendedAt = src.SuspendedAt,
        SuspensionReason = src.SuspensionReason,
        SupersededAt = src.SupersededAt,
        SupersededByVersionId = src.SupersededByVersionId,
    };

    public Provider ToProvider() => new()
    {
        Id = Id,
        TenantId = TenantId,
        ProviderId = ProviderId,
        NPI = Npi,
        ProviderType = ProviderType,
        TaxId = TaxId,
        FirstName = FirstName,
        LastName = LastName,
        MiddleName = MiddleName,
        Credentials = Credentials,
        OrganizationName = OrganizationName,
        DBAName = DBAName,
        PrimarySpecialty = PrimarySpecialty,
        TaxonomyCode = TaxonomyCode,
        SecondarySpecialties = SecondarySpecialties.ToList(),
        Address = Address,
        City = City,
        State = State,
        ZipCode = ZipCode,
        Phone = Phone,
        Fax = Fax,
        Email = Email,
        NetworkParticipations = NetworkParticipations.ToList(),
        CredentialingStatus = CredentialingStatus,
        CredentialingDate = CredentialingDate,
        RecredentialingDueDate = RecredentialingDueDate,
        CAQHProviderId = CAQHProviderId,
        LastCAQHSyncDate = LastCAQHSyncDate,
        BoardCertifications = BoardCertifications.ToList(),
        HospitalAffiliations = HospitalAffiliations.ToList(),
        AcceptingNewPatients = AcceptingNewPatients,
        HandicapAccessible = HandicapAccessible,
        LanguagesSpoken = LanguagesSpoken.ToList(),
        Status = Status,
        TerminationDate = TerminationDate,
        TerminationReason = TerminationReason,
        BankAccount = BankAccount,
        CreatedDate = CreatedDate,
        LastUpdatedDate = LastUpdatedDate,
        CreatedBy = CreatedBy,
        LastUpdatedBy = LastUpdatedBy,
        IntegrityScore = IntegrityScore,
        IntegrityRating = IntegrityRating,
        LastVerifiedAt = LastVerifiedAt,
        NextVerificationDue = NextVerificationDue,
        VersionId = VersionId,
        VersionNumber = VersionNumber,
        VersionState = VersionState,
        PredecessorVersionId = PredecessorVersionId,
        ActivatedAt = ActivatedAt,
        ActivatedBy = ActivatedBy,
        SuspendedAt = SuspendedAt,
        SuspensionReason = SuspensionReason,
        SupersededAt = SupersededAt,
        SupersededByVersionId = SupersededByVersionId,
    };
}
