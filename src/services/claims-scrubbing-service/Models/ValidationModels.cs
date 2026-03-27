using System.Text.Json.Serialization;

namespace ClaimsScrubbingService.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ValidationCategory
{
    [JsonPropertyName("data-completeness")]
    DataCompleteness,

    [JsonPropertyName("data-format")]
    DataFormat,

    [JsonPropertyName("code-validity")]
    CodeValidity,

    [JsonPropertyName("code-combination")]
    CodeCombination,

    [JsonPropertyName("date-logic")]
    DateLogic,

    [JsonPropertyName("amount-logic")]
    AmountLogic,

    [JsonPropertyName("provider-validation")]
    ProviderValidation,

    [JsonPropertyName("member-validation")]
    MemberValidation,

    [JsonPropertyName("authorization")]
    Authorization,

    [JsonPropertyName("duplicate-detection")]
    DuplicateDetection,

    [JsonPropertyName("medical-necessity")]
    MedicalNecessity,

    [JsonPropertyName("modifier-validation")]
    ModifierValidation,

    [JsonPropertyName("bundling-unbundling")]
    BundlingUnbundling,

    [JsonPropertyName("payer-specific")]
    PayerSpecific,

    [JsonPropertyName("custom")]
    Custom
}

/// <summary>Validation rule definition.</summary>
public class ValidationRule
{
    [JsonPropertyName("ruleId")]
    public string RuleId { get; set; } = string.Empty;

    [JsonPropertyName("ruleName")]
    public string RuleName { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = string.Empty; // "error" | "warning" | "info"

    [JsonPropertyName("appliesTo")]
    public List<string> AppliesTo { get; set; } = new(); // ["837P","837I","837D"]

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("priority")]
    public int Priority { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "standard"; // "standard" | "custom" | "payer-specific"

    [JsonPropertyName("payerId")]
    public string? PayerId { get; set; }

    [JsonPropertyName("effectiveDateRange")]
    public EffectiveDateRange? EffectiveDateRange { get; set; }

    [JsonPropertyName("config")]
    public Dictionary<string, object>? Config { get; set; }

    [JsonPropertyName("customScript")]
    public string? CustomScript { get; set; }

    [JsonPropertyName("autoCorrect")]
    public bool? AutoCorrect { get; set; }
}

public class EffectiveDateRange
{
    [JsonPropertyName("startDate")]
    public string StartDate { get; set; } = string.Empty;

    [JsonPropertyName("endDate")]
    public string? EndDate { get; set; }
}

/// <summary>Custom rule definition (extends ValidationRule with a script).</summary>
public class CustomRule : ValidationRule
{
    [JsonPropertyName("validationScript")]
    public string ValidationScript { get; set; } = string.Empty;

    [JsonPropertyName("parameters")]
    public Dictionary<string, object>? Parameters { get; set; }

    [JsonPropertyName("dependsOn")]
    public List<string>? DependsOn { get; set; }
}

/// <summary>Validation result for a single rule execution.</summary>
public class ValidationResult
{
    [JsonPropertyName("ruleId")]
    public string RuleId { get; set; } = string.Empty;

    [JsonPropertyName("ruleName")]
    public string RuleName { get; set; } = string.Empty;

    [JsonPropertyName("passed")]
    public bool Passed { get; set; }

    [JsonPropertyName("severity")]
    public string? Severity { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("fields")]
    public List<string>? Fields { get; set; }

    [JsonPropertyName("serviceLines")]
    public List<int>? ServiceLines { get; set; }

    [JsonPropertyName("context")]
    public Dictionary<string, object>? Context { get; set; }

    [JsonPropertyName("editCode")]
    public string? EditCode { get; set; }

    [JsonPropertyName("suggestion")]
    public string? Suggestion { get; set; }

    [JsonPropertyName("autoCorrected")]
    public bool? AutoCorrected { get; set; }

    [JsonPropertyName("executionTimeMs")]
    public long? ExecutionTimeMs { get; set; }
}

/// <summary>Complete claim validation result.</summary>
public class ClaimValidationResult
{
    [JsonPropertyName("claimId")]
    public string ClaimId { get; set; } = string.Empty;

    [JsonPropertyName("claimType")]
    public string ClaimType { get; set; } = string.Empty;

    [JsonPropertyName("patientControlNumber")]
    public string PatientControlNumber { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty; // "clean" | "flagged" | "rejected"

    [JsonPropertyName("rulesExecuted")]
    public int RulesExecuted { get; set; }

    [JsonPropertyName("rulesPassed")]
    public int RulesPassed { get; set; }

    [JsonPropertyName("rulesFailed")]
    public int RulesFailed { get; set; }

    [JsonPropertyName("errorCount")]
    public int ErrorCount { get; set; }

    [JsonPropertyName("warningCount")]
    public int WarningCount { get; set; }

    [JsonPropertyName("infoCount")]
    public int InfoCount { get; set; }

    [JsonPropertyName("results")]
    public List<ValidationResult> Results { get; set; } = new();

    [JsonPropertyName("validatedAt")]
    public string ValidatedAt { get; set; } = string.Empty;

    [JsonPropertyName("totalValidationTimeMs")]
    public long TotalValidationTimeMs { get; set; }

    [JsonPropertyName("routing")]
    public ClaimRoutingDecision Routing { get; set; } = new();

    [JsonPropertyName("firstPassEligible")]
    public bool FirstPassEligible { get; set; }
}

/// <summary>Claim routing decision after validation.</summary>
public class ClaimRoutingDecision
{
    [JsonPropertyName("destination")]
    public string Destination { get; set; } = string.Empty; // "adjudication" | "work-queue" | "reject"

    [JsonPropertyName("queueName")]
    public string? QueueName { get; set; }

    [JsonPropertyName("priority")]
    public string? Priority { get; set; } // "high" | "medium" | "low"

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("editCodes")]
    public List<string>? EditCodes { get; set; }

    [JsonPropertyName("requiresManualReview")]
    public bool RequiresManualReview { get; set; }

    [JsonPropertyName("assignedTo")]
    public string? AssignedTo { get; set; }

    [JsonPropertyName("dueDate")]
    public string? DueDate { get; set; }
}

/// <summary>API request to validate a single claim.</summary>
public class ValidateClaimRequest
{
    [JsonPropertyName("claim")]
    public X12837Claim Claim { get; set; } = new();

    [JsonPropertyName("ruleSetId")]
    public string? RuleSetId { get; set; }

    [JsonPropertyName("skipRules")]
    public List<string>? SkipRules { get; set; }

    [JsonPropertyName("onlyRules")]
    public List<string>? OnlyRules { get; set; }

    [JsonPropertyName("autoCorrect")]
    public bool? AutoCorrect { get; set; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }
}

/// <summary>API response for single claim validation.</summary>
public class ValidateClaimResponse
{
    [JsonPropertyName("result")]
    public ClaimValidationResult Result { get; set; } = new();

    [JsonPropertyName("correctedClaim")]
    public X12837Claim? CorrectedClaim { get; set; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;
}

/// <summary>Batch validation request.</summary>
public class BatchValidateRequest
{
    [JsonPropertyName("claims")]
    public List<X12837Claim> Claims { get; set; } = new();

    [JsonPropertyName("ruleSetId")]
    public string? RuleSetId { get; set; }

    [JsonPropertyName("skipRules")]
    public List<string>? SkipRules { get; set; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }
}

/// <summary>Batch validation response.</summary>
public class BatchValidateResponse
{
    [JsonPropertyName("totalClaims")]
    public int TotalClaims { get; set; }

    [JsonPropertyName("cleanClaims")]
    public int CleanClaims { get; set; }

    [JsonPropertyName("flaggedClaims")]
    public int FlaggedClaims { get; set; }

    [JsonPropertyName("rejectedClaims")]
    public int RejectedClaims { get; set; }

    [JsonPropertyName("results")]
    public List<ClaimValidationResult> Results { get; set; } = new();

    [JsonPropertyName("firstPassRate")]
    public double FirstPassRate { get; set; }

    [JsonPropertyName("totalProcessingTimeMs")]
    public long TotalProcessingTimeMs { get; set; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }
}

/// <summary>Event published when a claim is validated.</summary>
public class ClaimValidatedEvent
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = "ClaimValidated";

    [JsonPropertyName("subject")]
    public string Subject { get; set; } = string.Empty;

    [JsonPropertyName("eventTime")]
    public string EventTime { get; set; } = string.Empty;

    [JsonPropertyName("dataVersion")]
    public string DataVersion { get; set; } = "1.0";

    [JsonPropertyName("data")]
    public ClaimValidatedEventData Data { get; set; } = new();
}

public class ClaimValidatedEventData
{
    [JsonPropertyName("claimId")]
    public string ClaimId { get; set; } = string.Empty;

    [JsonPropertyName("claimType")]
    public string ClaimType { get; set; } = string.Empty;

    [JsonPropertyName("patientControlNumber")]
    public string PatientControlNumber { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("errorCount")]
    public int ErrorCount { get; set; }

    [JsonPropertyName("warningCount")]
    public int WarningCount { get; set; }

    [JsonPropertyName("routingDestination")]
    public string RoutingDestination { get; set; } = string.Empty;

    [JsonPropertyName("totalClaimedAmount")]
    public decimal TotalClaimedAmount { get; set; }

    [JsonPropertyName("billingProviderNpi")]
    public string BillingProviderNpi { get; set; } = string.Empty;

    [JsonPropertyName("memberId")]
    public string MemberId { get; set; } = string.Empty;

    [JsonPropertyName("validationTimeMs")]
    public long ValidationTimeMs { get; set; }

    [JsonPropertyName("firstPassEligible")]
    public bool FirstPassEligible { get; set; }

    [JsonPropertyName("editCodes")]
    public List<string>? EditCodes { get; set; }
}
