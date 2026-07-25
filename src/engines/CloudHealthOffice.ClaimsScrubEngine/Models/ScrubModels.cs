namespace CloudHealthOffice.ClaimsScrubEngine.Models;

// ============================================================================
// X12 837 Claim Types
// ============================================================================

public enum ClaimType
{
    Professional,
    Institutional,
    Dental
}

public static class ClaimTypeExtensions
{
    public static string ToCode(this ClaimType ct) => ct switch
    {
        ClaimType.Professional => "837P",
        ClaimType.Institutional => "837I",
        ClaimType.Dental => "837D",
        _ => throw new ArgumentOutOfRangeException(nameof(ct))
    };

    public static ClaimType FromCode(string code) => code switch
    {
        "837P" => ClaimType.Professional,
        "837I" => ClaimType.Institutional,
        "837D" => ClaimType.Dental,
        _ => throw new ArgumentOutOfRangeException(nameof(code), $"Unknown claim type: {code}")
    };
}

public record X12837Claim
{
    public string ClaimId { get; init; } = default!;
    public ClaimType ClaimType { get; init; }
    public string TransactionControlNumber { get; init; } = default!;
    public string InterchangeControlNumber { get; init; } = default!;
    public string TransactionDate { get; init; } = default!;
    public ClaimSubmitter Submitter { get; init; } = default!;
    public ClaimReceiver Receiver { get; init; } = default!;
    public BillingProvider BillingProvider { get; init; } = default!;
    public ClaimSubscriber Subscriber { get; init; } = default!;
    public ClaimPatient? Patient { get; init; }
    public ClaimHeader ClaimHeader { get; init; } = default!;
    public List<ServiceLine> ServiceLines { get; init; } = [];
    public decimal TotalClaimedAmount { get; init; }
    public string? RawEdi { get; init; }
    public string ParsedAt { get; init; } = default!;
}

public record ClaimSubmitter
{
    public string Name { get; init; } = default!;
    public string IdentificationCode { get; init; } = default!;
    public string IdentificationQualifier { get; init; } = default!;
}

public record ClaimReceiver
{
    public string Name { get; init; } = default!;
    public string IdentificationCode { get; init; } = default!;
    public string IdentificationQualifier { get; init; } = default!;
}

public record ProviderAddress
{
    public string Line1 { get; init; } = default!;
    public string? Line2 { get; init; }
    public string City { get; init; } = default!;
    public string State { get; init; } = default!;
    public string PostalCode { get; init; } = default!;
    public string? CountryCode { get; init; }
}

public record BillingProvider
{
    public string Npi { get; init; } = default!;
    public string Name { get; init; } = default!;
    public string EntityType { get; init; } = default!;
    public string? TaxId { get; init; }
    public string? TaxIdQualifier { get; init; }
    public ProviderAddress Address { get; init; } = default!;
    public string? TaxonomyCode { get; init; }
}

public record ClaimSubscriber
{
    public string MemberId { get; init; } = default!;
    public string FirstName { get; init; } = default!;
    public string LastName { get; init; } = default!;
    public string? MiddleName { get; init; }
    public string DateOfBirth { get; init; } = default!;
    public string? Gender { get; init; }
    public string? GroupNumber { get; init; }
}

public record ClaimPatient
{
    /// <summary>
    /// The dependent's own member id (2010CA NM109), distinct from the
    /// subscriber's. Null when the source 837 doesn't carry one — some
    /// payers expect demographic matching instead, which this DTO does
    /// not attempt to resolve.
    /// </summary>
    public string? MemberId { get; init; }
    public string FirstName { get; init; } = default!;
    public string LastName { get; init; } = default!;
    public string? MiddleName { get; init; }
    public string DateOfBirth { get; init; } = default!;
    public string? Gender { get; init; }
    public string RelationshipCode { get; init; } = default!;
}

public record DiagnosisCode
{
    public string Code { get; init; } = default!;
    public string Qualifier { get; init; } = default!;
    public int? Pointer { get; init; }
    public string? PresentOnAdmission { get; init; }
}

public record ClaimHeader
{
    public string PatientControlNumber { get; init; } = default!;
    public decimal TotalChargeAmount { get; init; }
    public string? PlaceOfServiceCode { get; init; }
    public string? FacilityTypeCode { get; init; }
    public string? FrequencyCode { get; init; }
    public string? PrincipalDiagnosisCode { get; init; }
    public string? AdmittingDiagnosisCode { get; init; }
    public List<DiagnosisCode>? DiagnosisCodes { get; init; }
    public string? AdmissionDate { get; init; }
    public string? DischargeDate { get; init; }
    public string? AdmissionTypeCode { get; init; }
    public string? PriorAuthorizationNumber { get; init; }
    public RenderingProviderInfo? RenderingProvider { get; init; }
}

public record RenderingProviderInfo
{
    public string Npi { get; init; } = default!;
    public string? Name { get; init; }
    public string? TaxonomyCode { get; init; }
}

public record ServiceLine
{
    public int LineNumber { get; init; }
    public string ProcedureCode { get; init; } = default!;
    public string? ProcedureCodeQualifier { get; init; }
    public List<string>? Modifiers { get; init; }
    public string? Description { get; init; }
    public string ServiceDate { get; init; } = default!;
    public string? ServiceDateEnd { get; init; }
    public decimal ChargeAmount { get; init; }
    public decimal Units { get; init; }
    public string? UnitType { get; init; }
    public string? PlaceOfService { get; init; }
    public string? RevenueCode { get; init; }
    public List<int>? DiagnosisPointers { get; init; }
    public string? PriorAuthorizationNumber { get; init; }
}

// ============================================================================
// Validation Rule Types
// ============================================================================

public enum ValidationSeverity
{
    Error,
    Warning,
    Info
}

public enum ValidationCategory
{
    DataCompleteness,
    DataFormat,
    CodeValidity,
    CodeCombination,
    DateLogic,
    AmountLogic,
    ProviderValidation,
    MemberValidation,
    Authorization,
    DuplicateDetection,
    MedicalNecessity,
    ModifierValidation,
    BundlingUnbundling,
    PayerSpecific,
    Custom
}

public static class ValidationCategoryNames
{
    public static string ToSlug(ValidationCategory cat) => cat switch
    {
        ValidationCategory.DataCompleteness => "data-completeness",
        ValidationCategory.CodeValidity => "code-validity",
        ValidationCategory.DateLogic => "date-logic",
        ValidationCategory.AmountLogic => "amount-logic",
        ValidationCategory.ProviderValidation => "provider-validation",
        ValidationCategory.ModifierValidation => "modifier-validation",
        ValidationCategory.Custom => "custom",
        _ => cat.ToString().ToLowerInvariant()
    };

    public static ValidationCategory FromSlug(string slug) => slug switch
    {
        "data-completeness" => ValidationCategory.DataCompleteness,
        "code-validity" => ValidationCategory.CodeValidity,
        "date-logic" => ValidationCategory.DateLogic,
        "amount-logic" => ValidationCategory.AmountLogic,
        "provider-validation" => ValidationCategory.ProviderValidation,
        "modifier-validation" => ValidationCategory.ModifierValidation,
        "custom" => ValidationCategory.Custom,
        _ => Enum.Parse<ValidationCategory>(slug, ignoreCase: true)
    };
}

public enum RuleType
{
    Standard,
    Custom,
    PayerSpecific
}

public record ValidationRule
{
    public string RuleId { get; init; } = default!;
    public string RuleName { get; init; } = default!;
    public string Description { get; init; } = default!;
    public ValidationCategory Category { get; init; }
    public ValidationSeverity Severity { get; init; }
    public List<ClaimType> AppliesTo { get; init; } = [];
    public bool Enabled { get; init; }
    public int Priority { get; init; }
    public RuleType Type { get; init; }
    public string? PayerId { get; init; }
    public Dictionary<string, object>? Config { get; init; }
    public bool AutoCorrect { get; init; }
}

public record ValidationResult
{
    public string RuleId { get; init; } = default!;
    public string RuleName { get; init; } = default!;
    public bool Passed { get; init; }
    public ValidationSeverity? Severity { get; init; }
    public string? Message { get; init; }
    public List<string>? Fields { get; init; }
    public List<int>? ServiceLines { get; init; }
    public string? EditCode { get; init; }
    public string? Suggestion { get; init; }
    public bool? AutoCorrected { get; init; }
    public long? ExecutionTimeMs { get; init; }
}

public record ClaimValidationResult
{
    public string ClaimId { get; init; } = default!;
    public ClaimType ClaimType { get; init; }
    public string PatientControlNumber { get; init; } = default!;
    public string Status { get; init; } = default!; // "clean", "flagged", "rejected"
    public int RulesExecuted { get; init; }
    public int RulesPassed { get; init; }
    public int RulesFailed { get; init; }
    public int ErrorCount { get; init; }
    public int WarningCount { get; init; }
    public int InfoCount { get; init; }
    public List<ValidationResult> Results { get; init; } = [];
    public string ValidatedAt { get; init; } = default!;
    public long TotalValidationTimeMs { get; init; }
    public ClaimRoutingDecision Routing { get; init; } = default!;
    public bool FirstPassEligible { get; init; }
}

public record ClaimRoutingDecision
{
    public string Destination { get; init; } = default!; // "adjudication", "work-queue", "reject"
    public string? QueueName { get; init; }
    public string? Priority { get; init; } // "high", "medium", "low"
    public string Reason { get; init; } = default!;
    public List<string> EditCodes { get; init; } = [];
    public bool RequiresManualReview { get; init; }
    public string? AssignedTo { get; init; }
    public string? DueDate { get; init; }
}

// ============================================================================
// Standard Rule Set Configuration
// ============================================================================

public record StandardRuleSet
{
    public StandardDataCompletenessRules DataCompleteness { get; init; } = new();
    public StandardCodeValidationRules CodeValidation { get; init; } = new();
    public StandardDateLogicRules DateLogic { get; init; } = new();
    public StandardAmountLogicRules AmountLogic { get; init; } = new();
    public StandardProviderValidationRules ProviderValidation { get; init; } = new();
    public StandardModifierValidationRules ModifierValidation { get; init; } = new();
}

public record StandardDataCompletenessRules
{
    public bool MemberIdRequired { get; init; } = true;
    public bool SubscriberDobRequired { get; init; } = true;
    public bool BillingProviderNpiRequired { get; init; } = true;
    public bool DiagnosisRequired { get; init; } = true;
    public int MinServiceLines { get; init; } = 1;
    public bool ServiceDateRequired { get; init; } = true;
    public bool ChargeAmountRequired { get; init; } = true;
}

public record StandardCodeValidationRules
{
    public bool ValidateIcd10 { get; init; } = true;
    public bool ValidateCpt { get; init; } = true;
    public bool ValidateHcpcs { get; init; } = true;
    public bool ValidateRevenueCodes { get; init; } = true;
    public bool ValidatePlaceOfService { get; init; } = true;
    public bool CheckObsoleteCodes { get; init; } = true;
    public bool CheckGenderSpecificCodes { get; init; } = true;
    public bool CheckAgeSpecificCodes { get; init; } = true;
}

public record StandardDateLogicRules
{
    public bool ServiceDateNotFuture { get; init; } = true;
    public bool ServiceDateWithinFilingLimit { get; init; } = true;
    public int FilingLimitDays { get; init; } = 365;
    public bool DischargeDateAfterAdmission { get; init; } = true;
    public bool PatientDobBeforeService { get; init; } = true;
    public bool ServiceDatesInSequence { get; init; } = true;
}

public record StandardAmountLogicRules
{
    public bool ChargeAmountsPositive { get; init; } = true;
    public bool TotalMatchesLineSum { get; init; } = true;
    public decimal MaxSingleLineAmount { get; init; } = 1_000_000m;
    public decimal MaxClaimTotal { get; init; } = 10_000_000m;
    public bool UnitsPositive { get; init; } = true;
    public int MaxUnitsPerLine { get; init; } = 9999;
}

public record StandardProviderValidationRules
{
    public bool ValidateNpiFormat { get; init; } = true;
    public bool ValidateNpiRegistry { get; init; }
    public bool ValidateTaxonomyFormat { get; init; } = true;
    public bool ValidateTaxIdFormat { get; init; } = true;
    public bool RenderingProviderRequired { get; init; }
}

public record StandardModifierValidationRules
{
    public bool ValidateModifierFormat { get; init; } = true;
    public bool CheckDuplicateModifiers { get; init; } = true;
    public bool ValidateModifierOrder { get; init; } = true;
    public bool CheckMutuallyExclusiveModifiers { get; init; } = true;
}

// ============================================================================
// Scrub Request / Response (public API contract)
// ============================================================================

public record ClaimsScrubRequest
{
    public X12837Claim Claim { get; init; } = default!;
    public List<string>? SkipRules { get; init; }
    public List<string>? OnlyRules { get; init; }
}

public record ClaimsScrubResponse
{
    public ClaimValidationResult Result { get; init; } = default!;
    public string Timestamp { get; init; } = DateTime.UtcNow.ToString("o");
}
