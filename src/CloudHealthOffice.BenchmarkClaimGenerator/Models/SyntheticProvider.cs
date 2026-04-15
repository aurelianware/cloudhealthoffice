namespace CloudHealthOffice.BenchmarkClaimGenerator.Models;

/// <summary>
/// Represents a synthetic rendering or billing provider for benchmark claim generation.
/// Structurally compatible with the production Provider entity in provider-service.
/// </summary>
public class SyntheticProvider
{
    /// <summary>National Provider Identifier (10-digit, Luhn-10 valid).</summary>
    public string Npi { get; set; } = string.Empty;

    /// <summary>Tax identification number (EIN format: XX-XXXXXXX).</summary>
    public string TaxId { get; set; } = string.Empty;

    /// <summary>Provider type: Individual (Type 1 NPI) or Organization (Type 2 NPI).</summary>
    public string ProviderType { get; set; } = "Individual";

    /// <summary>Provider first name (for individual providers).</summary>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>Provider last name (for individual providers).</summary>
    public string LastName { get; set; } = string.Empty;

    /// <summary>Organization name (for organizational providers).</summary>
    public string? OrganizationName { get; set; }

    /// <summary>Provider credentials (MD, DO, NP, PA, DDS, etc.).</summary>
    public string? Credentials { get; set; }

    /// <summary>Provider specialty code (NUCC taxonomy code, e.g., 207Q00000X).</summary>
    public string SpecialtyCode { get; set; } = string.Empty;

    /// <summary>Human-readable specialty description.</summary>
    public string SpecialtyDescription { get; set; } = string.Empty;

    /// <summary>NUCC taxonomy code (same as SpecialtyCode for primary).</summary>
    public string TaxonomyCode { get; set; } = string.Empty;

    /// <summary>Whether this is a participating (in-network) provider.</summary>
    public bool IsParticipating { get; set; }

    /// <summary>Network status: InNetwork, OutOfNetwork, Terminated.</summary>
    public string NetworkStatus { get; set; } = "InNetwork";

    /// <summary>Credentialing status: Active, Provisional, Expired.</summary>
    public string CredentialingStatus { get; set; } = "Active";

    /// <summary>Street address.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>City.</summary>
    public string City { get; set; } = string.Empty;

    /// <summary>Provider state (two-letter code).</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Provider ZIP code.</summary>
    public string ZipCode { get; set; } = string.Empty;

    /// <summary>Phone number.</summary>
    public string? Phone { get; set; }

    /// <summary>Fax number.</summary>
    public string? Fax { get; set; }

    /// <summary>Email address.</summary>
    public string? Email { get; set; }

    /// <summary>Contract identifier linking to provider contract.</summary>
    public string? ContractId { get; set; }

    /// <summary>Fee schedule identifier for rate lookup.</summary>
    public string? FeeScheduleId { get; set; }

    /// <summary>Contract type: FeeForService, Capitation, PerDiem.</summary>
    public string ContractType { get; set; } = "FeeForService";

    /// <summary>Facility type for organizational providers: Hospital, Clinic, SNF, BehavioralHealth.</summary>
    public string? FacilityType { get; set; }

    /// <summary>Effective date of the provider's network participation.</summary>
    public DateTime EffectiveDate { get; set; }

    /// <summary>Termination date of the provider (null if active).</summary>
    public DateTime? TermDate { get; set; }

    /// <summary>Whether provider is accepting new patients.</summary>
    public bool AcceptingNewPatients { get; set; } = true;

    /// <summary>Tenant identifier for multi-tenant Cosmos DB.</summary>
    public string TenantId { get; set; } = "mcc-benchmark";

    /// <summary>Full display name (individual or organization).</summary>
    public string FullName => ProviderType == "Organization"
        ? OrganizationName ?? LastName
        : $"{FirstName} {LastName}".Trim();
}
