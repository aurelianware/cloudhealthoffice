namespace MigrationWizard.Models;

/// <summary>
/// Member record from claims backend via Open Access
/// </summary>
public record BackendMember
{
    public string MemberId { get; init; } = string.Empty;
    public string SubscriberId { get; init; } = string.Empty;
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public DateTime DateOfBirth { get; init; }
    public string Gender { get; init; } = string.Empty;
    public string PlanCode { get; init; } = string.Empty;
    public string GroupNumber { get; init; } = string.Empty;
    public DateTime EffectiveDate { get; init; }
    public DateTime? TerminationDate { get; init; }
    public string RelationshipCode { get; init; } = string.Empty;
    public AddressInfo? Address { get; init; }
    public string PhoneNumber { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
}

/// <summary>
/// Provider record from claims backend via Open Access
/// </summary>
public record BackendProvider
{
    public string ProviderId { get; init; } = string.Empty;
    public string Npi { get; init; } = string.Empty;
    public string TaxId { get; init; } = string.Empty;
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string OrganizationName { get; init; } = string.Empty;
    public string ProviderType { get; init; } = string.Empty;
    public string Specialty { get; init; } = string.Empty;
    public string TaxonomyCode { get; init; } = string.Empty;
    public bool IsParticipating { get; init; }
    public AddressInfo? PracticeAddress { get; init; }
    public string Phone { get; init; } = string.Empty;
    public DateTime? ContractEffectiveDate { get; init; }
    public DateTime? ContractTerminationDate { get; init; }
}

/// <summary>
/// Benefit plan record from claims backend via Open Access
/// </summary>
public record BackendBenefitPlan
{
    public string PlanId { get; init; } = string.Empty;
    public string PlanCode { get; init; } = string.Empty;
    public string PlanName { get; init; } = string.Empty;
    public string PlanType { get; init; } = string.Empty;
    public string ProductType { get; init; } = string.Empty;
    public string LineOfBusiness { get; init; } = string.Empty;
    public DateTime EffectiveDate { get; init; }
    public DateTime? TerminationDate { get; init; }
    public List<BenefitInfo> Benefits { get; init; } = new();
    public CostShareInfo? CostShare { get; init; }
}

public record AddressInfo
{
    public string Line1 { get; init; } = string.Empty;
    public string Line2 { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string ZipCode { get; init; } = string.Empty;
}

public record BenefitInfo
{
    public string ServiceTypeCode { get; init; } = string.Empty;
    public string ServiceTypeName { get; init; } = string.Empty;
    public bool IsCovered { get; init; }
    public bool RequiresPriorAuth { get; init; }
    public decimal? Copay { get; init; }
    public decimal? Coinsurance { get; init; }
    public decimal? DeductibleAmount { get; init; }
}

public record CostShareInfo
{
    public decimal IndividualDeductible { get; init; }
    public decimal FamilyDeductible { get; init; }
    public decimal IndividualOutOfPocketMax { get; init; }
    public decimal FamilyOutOfPocketMax { get; init; }
}

/// <summary>
/// Cosmos DB member document format (Cloud Health Office schema)
/// </summary>
public class CosmosDbMember
{
    public string Id { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string SubscriberId { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string DateOfBirth { get; set; } = string.Empty;
    public string Gender { get; set; } = string.Empty;
    public string PlanCode { get; set; } = string.Empty;
    public string GroupNumber { get; set; } = string.Empty;
    public string EffectiveDate { get; set; } = string.Empty;
    public string? TerminationDate { get; set; }
    public string RelationshipCode { get; set; } = string.Empty;
    public CosmosDbAddress? Address { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Source { get; set; } = "Backend-Migration";
    public string MigratedAt { get; set; } = string.Empty;
}

public class CosmosDbAddress
{
    public string Line1 { get; set; } = string.Empty;
    public string Line2 { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
}

/// <summary>
/// Cosmos DB provider document format
/// </summary>
public class CosmosDbProvider
{
    public string Id { get; set; } = string.Empty;
    public string Npi { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string TaxId { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string OrganizationName { get; set; } = string.Empty;
    public string ProviderType { get; set; } = string.Empty;
    public string Specialty { get; set; } = string.Empty;
    public string TaxonomyCode { get; set; } = string.Empty;
    public bool IsParticipating { get; set; }
    public CosmosDbAddress? PracticeAddress { get; set; }
    public string Phone { get; set; } = string.Empty;
    public string? ContractEffectiveDate { get; set; }
    public string? ContractTerminationDate { get; set; }
    public string Source { get; set; } = "Backend-Migration";
    public string MigratedAt { get; set; } = string.Empty;
}

/// <summary>
/// Cosmos DB benefit plan document format
/// </summary>
public class CosmosDbBenefitPlan
{
    public string Id { get; set; } = string.Empty;
    public string PlanId { get; set; } = string.Empty;
    public string PlanCode { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string PlanType { get; set; } = string.Empty;
    public string ProductType { get; set; } = string.Empty;
    public string LineOfBusiness { get; set; } = string.Empty;
    public string EffectiveDate { get; set; } = string.Empty;
    public string? TerminationDate { get; set; }
    public List<CosmosDbBenefit> Benefits { get; set; } = new();
    public CosmosDbCostShare? CostShare { get; set; }
    public string Source { get; set; } = "Backend-Migration";
    public string MigratedAt { get; set; } = string.Empty;
}

public class CosmosDbBenefit
{
    public string ServiceTypeCode { get; set; } = string.Empty;
    public string ServiceTypeName { get; set; } = string.Empty;
    public bool IsCovered { get; set; }
    public bool RequiresPriorAuth { get; set; }
    public decimal? Copay { get; set; }
    public decimal? Coinsurance { get; set; }
    public decimal? DeductibleAmount { get; set; }
}

public class CosmosDbCostShare
{
    public decimal IndividualDeductible { get; set; }
    public decimal FamilyDeductible { get; set; }
    public decimal IndividualOutOfPocketMax { get; set; }
    public decimal FamilyOutOfPocketMax { get; set; }
}

/// <summary>
/// Migration status tracking
/// </summary>
public class MigrationStatus
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public MigrationPhase CurrentPhase { get; set; } = MigrationPhase.NotStarted;
    public int TotalMembers { get; set; }
    public int MigratedMembers { get; set; }
    public int TotalProviders { get; set; }
    public int MigratedProviders { get; set; }
    public int TotalBenefitPlans { get; set; }
    public int MigratedBenefitPlans { get; set; }
    public int AutoMatchedRecords { get; set; }
    public int ManualReviewRequired { get; set; }
    public double MatchPercentage => TotalRecords > 0 ? (AutoMatchedRecords * 100.0 / TotalRecords) : 0;
    public int TotalRecords => TotalMembers + TotalProviders + TotalBenefitPlans;
    public int MigratedRecords => MigratedMembers + MigratedProviders + MigratedBenefitPlans;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public List<MigrationError> Errors { get; set; } = new();
    public string? LastError { get; set; }
    public bool IsCutoverComplete { get; set; }
}

public enum MigrationPhase
{
    NotStarted,
    Connecting,
    ExportingMembers,
    ExportingProviders,
    ExportingBenefitPlans,
    GeneratingMappingReport,
    ReadyForCutover,
    CutoverInProgress,
    Completed,
    Failed
}

public class MigrationError
{
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Mapping report for data reconciliation
/// </summary>
public class MappingReport
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public MappingSummary Summary { get; set; } = new();
    public List<MappingResult> MemberMappings { get; set; } = new();
    public List<MappingResult> ProviderMappings { get; set; } = new();
    public List<MappingResult> BenefitPlanMappings { get; set; } = new();
}

public class MappingSummary
{
    public int TotalRecords { get; set; }
    public int AutoMatched { get; set; }
    public int PartialMatch { get; set; }
    public int NoMatch { get; set; }
    public double AutoMatchPercentage => TotalRecords > 0 ? (AutoMatched * 100.0 / TotalRecords) : 0;
}

public class MappingResult
{
    public string SourceId { get; set; } = string.Empty;
    public string? TargetId { get; set; }
    public MappingConfidence Confidence { get; set; }
    public List<FieldMapping> FieldMappings { get; set; } = new();
    public string? ReviewNote { get; set; }
}

public enum MappingConfidence
{
    Exact,
    High,
    Medium,
    Low,
    NoMatch
}

public class FieldMapping
{
    public string SourceField { get; set; } = string.Empty;
    public string TargetField { get; set; } = string.Empty;
    public string? SourceValue { get; set; }
    public string? TargetValue { get; set; }
    public bool IsMatched { get; set; }
    public string? TransformationApplied { get; set; }
}

/// <summary>
/// backend system Open Access APIs SOAP API configuration
/// </summary>
public class TriZettoOpenAccessConfig
{
    /// <summary>
    /// SOAP endpoint URL for backend system Open Access APIs
    /// Example: https://your-backend-server.com/OpenAccess/Services/MemberService.svc
    /// </summary>
    public string EndpointUrl { get; set; } = "https://backend-server.example.com/OpenAccess/Services";
    
    /// <summary>
    /// Username for SOAP authentication
    /// </summary>
    public string Username { get; set; } = "your-username";
    
    /// <summary>
    /// Password for SOAP authentication (should be stored in Key Vault in production)
    /// </summary>
    public string Password { get; set; } = "your-password";
    
    /// <summary>
    /// Client ID for OAuth (if using OAuth-based authentication)
    /// </summary>
    public string? ClientId { get; set; }
    
    /// <summary>
    /// Client secret for OAuth (should be stored in Key Vault in production)
    /// </summary>
    public string? ClientSecret { get; set; }
    
    /// <summary>
    /// Tenant/organization identifier
    /// </summary>
    public string TenantId { get; set; } = "default-tenant";
    
    /// <summary>
    /// Request timeout in seconds
    /// </summary>
    public int TimeoutSeconds { get; set; } = 120;
    
    /// <summary>
    /// Bypass SSL certificate validation. WARNING: Only use in development environments.
    /// In production, proper SSL certificates should be configured.
    /// </summary>
    public bool BypassCertificateValidation { get; set; } = false;
}

/// <summary>
/// Cosmos DB configuration for Cloud Health Office
/// </summary>
public class CosmosDbConfig
{
    public string Endpoint { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = "cloudhealthoffice";
    public string MembersContainer { get; set; } = "Members";
    public string ProvidersContainer { get; set; } = "ProviderDirectory";
    public string BenefitPlansContainer { get; set; } = "BenefitPlans";
    
    /// <summary>
    /// Default throughput in RU/s for new containers. Set to 0 to use serverless.
    /// </summary>
    public int DefaultThroughput { get; set; } = 400;
}

/// <summary>
/// API Management configuration for cutover
/// </summary>
public class ApiManagementConfig
{
    public string ServiceName { get; set; } = string.Empty;
    public string ResourceGroup { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string RoutingKeyName { get; set; } = "backend-routing";
    public string LegacyBackendId { get; set; } = "legacy-backend";
    public string CloudHealthOfficeBackendId { get; set; } = "cloudhealthoffice-backend";
}
