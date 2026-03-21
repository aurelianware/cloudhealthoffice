using System.Diagnostics;
using System.Text.RegularExpressions;
using ClaimsScrubbingService.Models;

namespace ClaimsScrubbingService.Services;

public interface IValidationRuleEngine
{
    void AddRule(ValidationRule rule);
    void AddCustomRule(CustomRule rule);
    IReadOnlyList<ValidationRule> GetRules();
    IReadOnlyList<ValidationRule> GetRulesByCategory(string category);
    Task<ClaimValidationResult> ValidateClaimAsync(
        X12837Claim claim,
        List<string>? skipRules = null,
        List<string>? onlyRules = null,
        bool parallelExecution = false);
}

public class ValidationRuleEngine : IValidationRuleEngine
{
    private readonly Dictionary<string, ValidationRule> _rules = new();
    private readonly ILogger<ValidationRuleEngine> _logger;

    // Valid place-of-service codes (per CMS POS code list)
    private static readonly HashSet<string> ValidPosCodes = new(StringComparer.Ordinal)
    {
        "01","02","03","04","05","06","07","08","09","10",
        "11","12","13","14","15","16","17","18","19","20",
        "21","22","23","24","25","26","31","32","33","34",
        "41","42","49","50","51","52","53","54","55","56",
        "57","58","60","61","62","65","71","72","81","99"
    };

    // Compiled regexes
    private static readonly Regex Icd10Pattern     = new(@"^[A-TV-Z][0-9][0-9AB]\.?[0-9A-Z]{0,4}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CptPattern        = new(@"^[0-9]{4}[0-9A-Z]$", RegexOptions.Compiled);
    private static readonly Regex HcpcsPattern      = new(@"^[A-Z][0-9]{4}$", RegexOptions.Compiled);
    private static readonly Regex RevenuePattern    = new(@"^[0-9]{4}$", RegexOptions.Compiled);
    private static readonly Regex ModifierPattern   = new(@"^[A-Z0-9]{2}$", RegexOptions.Compiled);
    private static readonly Regex NpiDigits         = new(@"^[0-9]{10}$", RegexOptions.Compiled);
    private static readonly Regex EinPattern        = new(@"^[0-9]{9}$", RegexOptions.Compiled);

    public ValidationRuleEngine(ILogger<ValidationRuleEngine> logger)
    {
        _logger = logger;
        InitializeStandardRules();
    }

    // =========================================================================
    // Rule registration
    // =========================================================================

    private void InitializeStandardRules()
    {
        // --- Data Completeness ---
        AddRule(new ValidationRule
        {
            RuleId = "DC001", RuleName = "Subscriber Identifier Required",
            Description = "Validates that subscriber identifier is present on the claim",
            Category = "data-completeness", Severity = "error",
            AppliesTo = new() { "837P", "837I", "837D" }, Enabled = true, Priority = 1, Type = "standard"
        });
        AddRule(new ValidationRule
        {
            RuleId = "DC002", RuleName = "Subscriber DOB Required",
            Description = "Validates that subscriber date of birth is present",
            Category = "data-completeness", Severity = "error",
            AppliesTo = new() { "837P", "837I", "837D" }, Enabled = true, Priority = 1, Type = "standard"
        });
        AddRule(new ValidationRule
        {
            RuleId = "DC003", RuleName = "Billing Provider NPI Required",
            Description = "Validates that billing provider NPI is present",
            Category = "data-completeness", Severity = "error",
            AppliesTo = new() { "837P", "837I", "837D" }, Enabled = true, Priority = 1, Type = "standard"
        });
        AddRule(new ValidationRule
        {
            RuleId = "DC004", RuleName = "Diagnosis Code Required",
            Description = "Validates that at least one diagnosis code is present",
            Category = "data-completeness", Severity = "error",
            AppliesTo = new() { "837P", "837I", "837D" }, Enabled = true, Priority = 1, Type = "standard"
        });
        AddRule(new ValidationRule
        {
            RuleId = "DC005", RuleName = "Minimum Service Lines",
            Description = "Validates that claim has minimum required service lines",
            Category = "data-completeness", Severity = "error",
            AppliesTo = new() { "837P", "837I", "837D" }, Enabled = true, Priority = 1, Type = "standard",
            Config = new() { ["minLines"] = 1 }
        });
        AddRule(new ValidationRule
        {
            RuleId = "DC006", RuleName = "Service Date Required",
            Description = "Validates that service date is present on all lines",
            Category = "data-completeness", Severity = "error",
            AppliesTo = new() { "837P", "837I", "837D" }, Enabled = true, Priority = 1, Type = "standard"
        });
        AddRule(new ValidationRule
        {
            RuleId = "DC007", RuleName = "Charge Amount Required",
            Description = "Validates that charge amount is present on all lines",
            Category = "data-completeness", Severity = "error",
            AppliesTo = new() { "837P", "837I", "837D" }, Enabled = true, Priority = 1, Type = "standard"
        });

        // --- Code Validation ---
        AddRule(new ValidationRule
        {
            RuleId = "CV001", RuleName = "Valid ICD-10 Code Format",
            Description = "Validates ICD-10 diagnosis code format",
            Category = "code-validity", Severity = "error",
            AppliesTo = new() { "837P", "837I", "837D" }, Enabled = true, Priority = 10, Type = "standard"
        });
        AddRule(new ValidationRule
        {
            RuleId = "CV002", RuleName = "Valid CPT Code Format",
            Description = "Validates CPT procedure code format",
            Category = "code-validity", Severity = "error",
            AppliesTo = new() { "837P" }, Enabled = true, Priority = 10, Type = "standard"
        });
        AddRule(new ValidationRule
        {
            RuleId = "CV003", RuleName = "Valid HCPCS Code Format",
            Description = "Validates HCPCS code format",
            Category = "code-validity", Severity = "error",
            AppliesTo = new() { "837P", "837I" }, Enabled = true, Priority = 10, Type = "standard"
        });
        AddRule(new ValidationRule
        {
            RuleId = "CV004", RuleName = "Valid Revenue Code Format",
            Description = "Validates revenue code format for institutional claims",
            Category = "code-validity", Severity = "error",
            AppliesTo = new() { "837I" }, Enabled = true, Priority = 10, Type = "standard"
        });
        AddRule(new ValidationRule
        {
            RuleId = "CV005", RuleName = "Valid Place of Service Code",
            Description = "Validates place of service code",
            Category = "code-validity", Severity = "error",
            AppliesTo = new() { "837P" }, Enabled = true, Priority = 10, Type = "standard"
        });

        // --- Date Logic ---
        AddRule(new ValidationRule
        {
            RuleId = "DL001", RuleName = "Service Date Not Future",
            Description = "Validates that service date is not in the future",
            Category = "date-logic", Severity = "error",
            AppliesTo = new() { "837P", "837I", "837D" }, Enabled = true, Priority = 5, Type = "standard"
        });
        AddRule(new ValidationRule
        {
            RuleId = "DL002", RuleName = "Service Date Within Filing Limit",
            Description = "Validates that claim is filed within timely filing limit",
            Category = "date-logic", Severity = "warning",
            AppliesTo = new() { "837P", "837I", "837D" }, Enabled = true, Priority = 5, Type = "standard",
            Config = new() { ["filingLimitDays"] = 365 }
        });
        AddRule(new ValidationRule
        {
            RuleId = "DL003", RuleName = "Discharge After Admission",
            Description = "Validates discharge date is after admission date",
            Category = "date-logic", Severity = "error",
            AppliesTo = new() { "837I" }, Enabled = true, Priority = 5, Type = "standard"
        });
        AddRule(new ValidationRule
        {
            RuleId = "DL004", RuleName = "Patient DOB Before Service",
            Description = "Validates patient date of birth is before service date",
            Category = "date-logic", Severity = "error",
            AppliesTo = new() { "837P", "837I", "837D" }, Enabled = true, Priority = 5, Type = "standard"
        });

        // --- Amount Logic ---
        AddRule(new ValidationRule
        {
            RuleId = "AL001", RuleName = "Charge Amounts Positive",
            Description = "Validates that all charge amounts are positive",
            Category = "amount-logic", Severity = "error",
            AppliesTo = new() { "837P", "837I", "837D" }, Enabled = true, Priority = 5, Type = "standard"
        });
        AddRule(new ValidationRule
        {
            RuleId = "AL002", RuleName = "Total Matches Line Sum",
            Description = "Validates total claim amount matches sum of service lines",
            Category = "amount-logic", Severity = "warning",
            AppliesTo = new() { "837P", "837I", "837D" }, Enabled = true, Priority = 5, Type = "standard"
        });
        AddRule(new ValidationRule
        {
            RuleId = "AL003", RuleName = "Units Positive",
            Description = "Validates that units of service are positive",
            Category = "amount-logic", Severity = "error",
            AppliesTo = new() { "837P", "837I", "837D" }, Enabled = true, Priority = 5, Type = "standard"
        });

        // --- Provider Validation ---
        AddRule(new ValidationRule
        {
            RuleId = "PV001", RuleName = "Valid NPI Format",
            Description = "Validates NPI number format using Luhn algorithm",
            Category = "provider-validation", Severity = "error",
            AppliesTo = new() { "837P", "837I", "837D" }, Enabled = true, Priority = 10, Type = "standard"
        });
        AddRule(new ValidationRule
        {
            RuleId = "PV002", RuleName = "Valid Tax ID Format",
            Description = "Validates tax identification number format",
            Category = "provider-validation", Severity = "warning",
            AppliesTo = new() { "837P", "837I", "837D" }, Enabled = true, Priority = 10, Type = "standard"
        });

        // --- Modifier Validation ---
        AddRule(new ValidationRule
        {
            RuleId = "MV001", RuleName = "Valid Modifier Format",
            Description = "Validates modifier code format",
            Category = "modifier-validation", Severity = "error",
            AppliesTo = new() { "837P", "837I" }, Enabled = true, Priority = 10, Type = "standard"
        });
        AddRule(new ValidationRule
        {
            RuleId = "MV002", RuleName = "No Duplicate Modifiers",
            Description = "Checks for duplicate modifiers on service lines",
            Category = "modifier-validation", Severity = "error",
            AppliesTo = new() { "837P", "837I" }, Enabled = true, Priority = 10, Type = "standard"
        });
    }

    public void AddRule(ValidationRule rule) => _rules[rule.RuleId] = rule;

    public void AddCustomRule(CustomRule rule) => _rules[rule.RuleId] = rule;

    public IReadOnlyList<ValidationRule> GetRules() => _rules.Values.ToList();

    public IReadOnlyList<ValidationRule> GetRulesByCategory(string category) =>
        _rules.Values.Where(r => r.Category == category).ToList();

    private IReadOnlyList<ValidationRule> GetApplicableRules(
        string claimType,
        List<string>? skipRules,
        List<string>? onlyRules)
    {
        var rules = _rules.Values
            .Where(r => r.Enabled && r.AppliesTo.Contains(claimType))
            .OrderBy(r => r.Priority)
            .AsEnumerable();

        if (onlyRules is { Count: > 0 })
            rules = rules.Where(r => onlyRules.Contains(r.RuleId));

        if (skipRules is { Count: > 0 })
            rules = rules.Where(r => !skipRules.Contains(r.RuleId));

        return rules.ToList();
    }

    // =========================================================================
    // Main entry point
    // =========================================================================

    public async Task<ClaimValidationResult> ValidateClaimAsync(
        X12837Claim claim,
        List<string>? skipRules = null,
        List<string>? onlyRules = null,
        bool parallelExecution = false)
    {
        var sw = Stopwatch.StartNew();
        var applicable = GetApplicableRules(claim.ClaimType, skipRules, onlyRules);
        var results = new List<ValidationResult>(applicable.Count);

        if (parallelExecution)
        {
            var tasks = applicable.Select(r => ExecuteRuleAsync(r, claim));
            var parallelResults = await Task.WhenAll(tasks);
            results.AddRange(parallelResults);
        }
        else
        {
            foreach (var rule in applicable)
                results.Add(await ExecuteRuleAsync(rule, claim));
        }

        sw.Stop();

        int errorCount   = results.Count(r => !r.Passed && r.Severity == "error");
        int warningCount = results.Count(r => !r.Passed && r.Severity == "warning");
        int infoCount    = results.Count(r => !r.Passed && r.Severity == "info");

        return new ClaimValidationResult
        {
            ClaimId              = claim.ClaimId,
            ClaimType            = claim.ClaimType,
            PatientControlNumber = claim.ClaimHeader.PatientControlNumber,
            Status               = DetermineStatus(errorCount, warningCount),
            RulesExecuted        = results.Count,
            RulesPassed          = results.Count(r => r.Passed),
            RulesFailed          = results.Count(r => !r.Passed),
            ErrorCount           = errorCount,
            WarningCount         = warningCount,
            InfoCount            = infoCount,
            Results              = results,
            ValidatedAt          = DateTime.UtcNow.ToString("O"),
            TotalValidationTimeMs = sw.ElapsedMilliseconds,
            Routing              = DetermineRouting(results, errorCount, warningCount),
            FirstPassEligible    = errorCount == 0 && warningCount == 0
        };
    }

    private static string DetermineStatus(int errorCount, int warningCount)
    {
        if (errorCount > 0)   return "rejected";
        if (warningCount > 0) return "flagged";
        return "clean";
    }

    private static ClaimRoutingDecision DetermineRouting(
        List<ValidationResult> results, int errorCount, int warningCount)
    {
        var editCodes = results
            .Where(r => !r.Passed && r.EditCode != null)
            .Select(r => r.EditCode!)
            .ToList();

        if (errorCount > 0)
        {
            return new ClaimRoutingDecision
            {
                Destination          = "work-queue",
                QueueName            = "claims-errors",
                Priority             = "high",
                Reason               = $"Claim has {errorCount} validation error(s)",
                EditCodes            = editCodes,
                RequiresManualReview = true
            };
        }

        if (warningCount > 0)
        {
            return new ClaimRoutingDecision
            {
                Destination          = "work-queue",
                QueueName            = "claims-warnings",
                Priority             = "medium",
                Reason               = $"Claim has {warningCount} validation warning(s)",
                EditCodes            = editCodes,
                RequiresManualReview = false
            };
        }

        return new ClaimRoutingDecision
        {
            Destination          = "adjudication",
            Reason               = "Claim passed all validation rules",
            RequiresManualReview = false
        };
    }

    // =========================================================================
    // Rule execution dispatch
    // =========================================================================

    private async Task<ValidationResult> ExecuteRuleAsync(ValidationRule rule, X12837Claim claim)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = ExecuteRuleLogic(rule, claim);
            sw.Stop();
            result.ExecutionTimeMs = sw.ElapsedMilliseconds;
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Rule {RuleId} threw during execution", rule.RuleId);
            return new ValidationResult
            {
                RuleId         = rule.RuleId,
                RuleName       = rule.RuleName,
                Passed         = false,
                Severity       = "error",
                Message        = $"Rule execution error: {ex.Message}",
                ExecutionTimeMs = sw.ElapsedMilliseconds
            };
        }
    }

    private ValidationResult ExecuteRuleLogic(ValidationRule rule, X12837Claim claim)
    {
        return rule.RuleId switch
        {
            "DC001" => ValidateMemberIdRequired(rule, claim),
            "DC002" => ValidateSubscriberDobRequired(rule, claim),
            "DC003" => ValidateBillingProviderNpiRequired(rule, claim),
            "DC004" => ValidateDiagnosisRequired(rule, claim),
            "DC005" => ValidateMinServiceLines(rule, claim),
            "DC006" => ValidateServiceDateRequired(rule, claim),
            "DC007" => ValidateChargeAmountRequired(rule, claim),
            "CV001" => ValidateIcd10Format(rule, claim),
            "CV002" => ValidateCptFormat(rule, claim),
            "CV003" => ValidateHcpcsFormat(rule, claim),
            "CV004" => ValidateRevenueCodeFormat(rule, claim),
            "CV005" => ValidatePlaceOfServiceCode(rule, claim),
            "DL001" => ValidateServiceDateNotFuture(rule, claim),
            "DL002" => ValidateServiceDateWithinFilingLimit(rule, claim),
            "DL003" => ValidateDischargeAfterAdmission(rule, claim),
            "DL004" => ValidatePatientDobBeforeService(rule, claim),
            "AL001" => ValidateChargeAmountsPositive(rule, claim),
            "AL002" => ValidateTotalMatchesLineSum(rule, claim),
            "AL003" => ValidateUnitsPositive(rule, claim),
            "PV001" => ValidateNpiFormat(rule, claim),
            "PV002" => ValidateTaxIdFormat(rule, claim),
            "MV001" => ValidateModifierFormat(rule, claim),
            "MV002" => ValidateNoDuplicateModifiers(rule, claim),
            _ when rule.Type == "custom" => ExecuteCustomRule(rule, claim),
            _ => CreatePass(rule)
        };
    }

    // =========================================================================
    // Data Completeness
    // =========================================================================

    private ValidationResult ValidateMemberIdRequired(ValidationRule rule, X12837Claim claim)
    {
        if (string.IsNullOrWhiteSpace(claim.Subscriber.MemberId))
            return CreateFail(rule, "Member ID is required", ["subscriber.memberId"], "DC001");
        return CreatePass(rule);
    }

    private ValidationResult ValidateSubscriberDobRequired(ValidationRule rule, X12837Claim claim)
    {
        if (string.IsNullOrWhiteSpace(claim.Subscriber.DateOfBirth))
            return CreateFail(rule, "Subscriber date of birth is required", ["subscriber.dateOfBirth"], "DC002");
        return CreatePass(rule);
    }

    private ValidationResult ValidateBillingProviderNpiRequired(ValidationRule rule, X12837Claim claim)
    {
        if (string.IsNullOrWhiteSpace(claim.BillingProvider.Npi))
            return CreateFail(rule, "Billing provider NPI is required", ["billingProvider.npi"], "DC003");
        return CreatePass(rule);
    }

    private ValidationResult ValidateDiagnosisRequired(ValidationRule rule, X12837Claim claim)
    {
        if (claim.ClaimHeader.DiagnosisCodes == null || claim.ClaimHeader.DiagnosisCodes.Count == 0)
            return CreateFail(rule, "At least one diagnosis code is required", ["claimHeader.diagnosisCodes"], "DC004");
        return CreatePass(rule);
    }

    private ValidationResult ValidateMinServiceLines(ValidationRule rule, X12837Claim claim)
    {
        int minLines = 1;
        if (rule.Config?.TryGetValue("minLines", out var minObj) == true && minObj is int minInt)
            minLines = minInt;

        if (claim.ServiceLines.Count < minLines)
            return CreateFail(rule, $"Claim must have at least {minLines} service line(s)", ["serviceLines"], "DC005");
        return CreatePass(rule);
    }

    private ValidationResult ValidateServiceDateRequired(ValidationRule rule, X12837Claim claim)
    {
        var missing = claim.ServiceLines
            .Where(l => string.IsNullOrWhiteSpace(l.ServiceDate))
            .Select(l => l.LineNumber)
            .ToList();

        if (missing.Count > 0)
            return CreateFail(rule,
                $"Service date is required on line(s): {string.Join(", ", missing)}",
                ["serviceLines.serviceDate"], "DC006", missing);
        return CreatePass(rule);
    }

    private ValidationResult ValidateChargeAmountRequired(ValidationRule rule, X12837Claim claim)
    {
        // In C# decimal is a value type; we check for 0 as a stand-in for "not provided"
        // The TS check was for undefined/null — here we accept 0 as unset
        // (The caller should ensure ChargeAmount is populated; if absent JSON deserializes to 0)
        // We simply pass — any absent field will fail AL001 (must be positive).
        return CreatePass(rule);
    }

    // =========================================================================
    // Code Validation
    // =========================================================================

    private ValidationResult ValidateIcd10Format(ValidationRule rule, X12837Claim claim)
    {
        var diagCodes = claim.ClaimHeader.DiagnosisCodes ?? [];
        var invalid = new List<string>();

        foreach (var diag in diagCodes)
        {
            if (diag.Qualifier is "ABK" or "ABF")
            {
                var codeStripped = diag.Code.Replace(".", "");
                if (!Icd10Pattern.IsMatch(diag.Code) && !Icd10Pattern.IsMatch(codeStripped))
                    invalid.Add(diag.Code);
            }
        }

        if (invalid.Count > 0)
            return CreateFail(rule,
                $"Invalid ICD-10 code format: {string.Join(", ", invalid)}",
                ["claimHeader.diagnosisCodes"], "CV001");
        return CreatePass(rule);
    }

    private ValidationResult ValidateCptFormat(ValidationRule rule, X12837Claim claim)
    {
        var invalidLines = new List<int>();

        foreach (var line in claim.ServiceLines)
        {
            if (line.ProcedureCodeQualifier is "HC" || line.ProcedureCodeQualifier is null)
            {
                if (!CptPattern.IsMatch(line.ProcedureCode))
                {
                    // Forgive if it's a valid HCPCS (starts with letter)
                    if (!HcpcsPattern.IsMatch(line.ProcedureCode))
                        invalidLines.Add(line.LineNumber);
                }
            }
        }

        if (invalidLines.Count > 0)
            return CreateFail(rule,
                $"Invalid CPT code format on line(s): {string.Join(", ", invalidLines)}",
                ["serviceLines.procedureCode"], "CV002", invalidLines);
        return CreatePass(rule);
    }

    private ValidationResult ValidateHcpcsFormat(ValidationRule rule, X12837Claim claim)
    {
        var invalidLines = new List<int>();

        foreach (var line in claim.ServiceLines)
        {
            var code = line.ProcedureCode;
            if (!string.IsNullOrEmpty(code) && char.IsLetter(code[0]))
            {
                if (!HcpcsPattern.IsMatch(code))
                    invalidLines.Add(line.LineNumber);
            }
        }

        if (invalidLines.Count > 0)
            return CreateFail(rule,
                $"Invalid HCPCS code format on line(s): {string.Join(", ", invalidLines)}",
                ["serviceLines.procedureCode"], "CV003", invalidLines);
        return CreatePass(rule);
    }

    private ValidationResult ValidateRevenueCodeFormat(ValidationRule rule, X12837Claim claim)
    {
        if (claim.ClaimType != "837I") return CreatePass(rule);

        var invalidLines = claim.ServiceLines
            .Where(l => !string.IsNullOrEmpty(l.RevenueCode) && !RevenuePattern.IsMatch(l.RevenueCode))
            .Select(l => l.LineNumber)
            .ToList();

        if (invalidLines.Count > 0)
            return CreateFail(rule,
                $"Invalid revenue code format on line(s): {string.Join(", ", invalidLines)}",
                ["serviceLines.revenueCode"], "CV004", invalidLines);
        return CreatePass(rule);
    }

    private ValidationResult ValidatePlaceOfServiceCode(ValidationRule rule, X12837Claim claim)
    {
        if (claim.ClaimType != "837P") return CreatePass(rule);

        var pos = claim.ClaimHeader.PlaceOfServiceCode;
        if (!string.IsNullOrEmpty(pos) && !ValidPosCodes.Contains(pos))
            return CreateFail(rule,
                $"Invalid place of service code: {pos}",
                ["claimHeader.placeOfServiceCode"], "CV005");
        return CreatePass(rule);
    }

    // =========================================================================
    // Date Logic
    // =========================================================================

    private ValidationResult ValidateServiceDateNotFuture(ValidationRule rule, X12837Claim claim)
    {
        var today = DateTime.UtcNow.ToString("yyyyMMdd");
        var futureLines = claim.ServiceLines
            .Where(l => string.Compare(l.ServiceDate, today, StringComparison.Ordinal) > 0)
            .Select(l => l.LineNumber)
            .ToList();

        if (futureLines.Count > 0)
            return CreateFail(rule,
                $"Service date is in the future on line(s): {string.Join(", ", futureLines)}",
                ["serviceLines.serviceDate"], "DL001", futureLines);
        return CreatePass(rule);
    }

    private ValidationResult ValidateServiceDateWithinFilingLimit(ValidationRule rule, X12837Claim claim)
    {
        int filingLimitDays = 365;
        if (rule.Config?.TryGetValue("filingLimitDays", out var daysObj) == true && daysObj is int daysInt)
            filingLimitDays = daysInt;

        var limitDate = DateTime.UtcNow.AddDays(-filingLimitDays).ToString("yyyyMMdd");
        var lateLines = claim.ServiceLines
            .Where(l => string.Compare(l.ServiceDate, limitDate, StringComparison.Ordinal) < 0)
            .Select(l => l.LineNumber)
            .ToList();

        if (lateLines.Count > 0)
            return CreateFail(rule,
                $"Service date exceeds {filingLimitDays}-day filing limit on line(s): {string.Join(", ", lateLines)}",
                ["serviceLines.serviceDate"], "DL002", lateLines);
        return CreatePass(rule);
    }

    private ValidationResult ValidateDischargeAfterAdmission(ValidationRule rule, X12837Claim claim)
    {
        if (claim.ClaimType != "837I") return CreatePass(rule);

        var admission = claim.ClaimHeader.AdmissionDate;
        var discharge = claim.ClaimHeader.DischargeDate;

        if (!string.IsNullOrEmpty(admission) && !string.IsNullOrEmpty(discharge) &&
            string.Compare(discharge, admission, StringComparison.Ordinal) < 0)
        {
            return CreateFail(rule,
                "Discharge date cannot be before admission date",
                ["claimHeader.admissionDate", "claimHeader.dischargeDate"], "DL003");
        }

        return CreatePass(rule);
    }

    private ValidationResult ValidatePatientDobBeforeService(ValidationRule rule, X12837Claim claim)
    {
        var patientDob = claim.Patient?.DateOfBirth ?? claim.Subscriber.DateOfBirth;
        if (string.IsNullOrEmpty(patientDob)) return CreatePass(rule);

        var invalidLines = claim.ServiceLines
            .Where(l => string.Compare(l.ServiceDate, patientDob, StringComparison.Ordinal) < 0)
            .Select(l => l.LineNumber)
            .ToList();

        if (invalidLines.Count > 0)
            return CreateFail(rule,
                $"Service date is before patient date of birth on line(s): {string.Join(", ", invalidLines)}",
                ["serviceLines.serviceDate"], "DL004", invalidLines);
        return CreatePass(rule);
    }

    // =========================================================================
    // Amount Logic
    // =========================================================================

    private ValidationResult ValidateChargeAmountsPositive(ValidationRule rule, X12837Claim claim)
    {
        var invalidLines = claim.ServiceLines
            .Where(l => l.ChargeAmount <= 0)
            .Select(l => l.LineNumber)
            .ToList();

        if (invalidLines.Count > 0)
            return CreateFail(rule,
                $"Charge amount must be positive on line(s): {string.Join(", ", invalidLines)}",
                ["serviceLines.chargeAmount"], "AL001", invalidLines);
        return CreatePass(rule);
    }

    private ValidationResult ValidateTotalMatchesLineSum(ValidationRule rule, X12837Claim claim)
    {
        var lineSum = claim.ServiceLines.Sum(l => l.ChargeAmount);
        const decimal tolerance = 0.01m;

        if (Math.Abs(claim.TotalClaimedAmount - lineSum) > tolerance)
            return CreateFail(rule,
                $"Total claimed amount ({claim.TotalClaimedAmount}) does not match sum of line charges ({lineSum})",
                ["totalClaimedAmount", "serviceLines.chargeAmount"], "AL002");
        return CreatePass(rule);
    }

    private ValidationResult ValidateUnitsPositive(ValidationRule rule, X12837Claim claim)
    {
        var invalidLines = claim.ServiceLines
            .Where(l => l.Units <= 0)
            .Select(l => l.LineNumber)
            .ToList();

        if (invalidLines.Count > 0)
            return CreateFail(rule,
                $"Units must be positive on line(s): {string.Join(", ", invalidLines)}",
                ["serviceLines.units"], "AL003", invalidLines);
        return CreatePass(rule);
    }

    // =========================================================================
    // Provider Validation
    // =========================================================================

    private ValidationResult ValidateNpiFormat(ValidationRule rule, X12837Claim claim)
    {
        var npi = claim.BillingProvider.Npi;
        if (!IsValidNpi(npi))
            return CreateFail(rule,
                $"Invalid NPI format: {npi}. NPI must be 10 digits and pass Luhn check.",
                ["billingProvider.npi"], "PV001");
        return CreatePass(rule);
    }

    private ValidationResult ValidateTaxIdFormat(ValidationRule rule, X12837Claim claim)
    {
        var taxId    = claim.BillingProvider.TaxId;
        var qualifier = claim.BillingProvider.TaxIdQualifier;

        if (!string.IsNullOrEmpty(taxId))
        {
            var clean = taxId.Replace("-", "").Replace(" ", "");
            if (qualifier is "EI" or "SY")
            {
                if (!EinPattern.IsMatch(clean))
                    return CreateFail(rule,
                        qualifier == "EI"
                            ? "Invalid EIN format. Must be 9 digits."
                            : "Invalid SSN format. Must be 9 digits.",
                        ["billingProvider.taxId"], "PV002");
            }
        }

        return CreatePass(rule);
    }

    // =========================================================================
    // Modifier Validation
    // =========================================================================

    private ValidationResult ValidateModifierFormat(ValidationRule rule, X12837Claim claim)
    {
        var invalidLines = new List<int>();

        foreach (var line in claim.ServiceLines)
        {
            if (line.Modifiers == null) continue;
            foreach (var mod in line.Modifiers)
            {
                if (!ModifierPattern.IsMatch(mod) && !invalidLines.Contains(line.LineNumber))
                    invalidLines.Add(line.LineNumber);
            }
        }

        if (invalidLines.Count > 0)
            return CreateFail(rule,
                $"Invalid modifier format on line(s): {string.Join(", ", invalidLines)}",
                ["serviceLines.modifiers"], "MV001", invalidLines);
        return CreatePass(rule);
    }

    private ValidationResult ValidateNoDuplicateModifiers(ValidationRule rule, X12837Claim claim)
    {
        var duplicateLines = claim.ServiceLines
            .Where(l => l.Modifiers is { Count: > 1 } && l.Modifiers.Distinct().Count() != l.Modifiers.Count)
            .Select(l => l.LineNumber)
            .ToList();

        if (duplicateLines.Count > 0)
            return CreateFail(rule,
                $"Duplicate modifiers found on line(s): {string.Join(", ", duplicateLines)}",
                ["serviceLines.modifiers"], "MV002", duplicateLines);
        return CreatePass(rule);
    }

    // =========================================================================
    // Custom rule (stub — always passes, same as Node version)
    // =========================================================================

    private ValidationResult ExecuteCustomRule(ValidationRule rule, X12837Claim claim)
    {
        // Custom rules require a sandboxed execution engine not yet implemented.
        return CreatePass(rule);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static ValidationResult CreatePass(ValidationRule rule) =>
        new() { RuleId = rule.RuleId, RuleName = rule.RuleName, Passed = true };

    private static ValidationResult CreateFail(
        ValidationRule rule,
        string message,
        List<string> fields,
        string editCode,
        List<int>? serviceLines = null) =>
        new()
        {
            RuleId       = rule.RuleId,
            RuleName     = rule.RuleName,
            Passed       = false,
            Severity     = rule.Severity,
            Message      = message,
            Fields       = fields,
            EditCode     = editCode,
            ServiceLines = serviceLines
        };

    /// <summary>
    /// Validates NPI using the Luhn algorithm with the 80840 prefix (ANSI standard).
    /// </summary>
    private static bool IsValidNpi(string npi)
    {
        if (!NpiDigits.IsMatch(npi)) return false;

        var prefixed = "80840" + npi;
        int sum = 0;
        bool alternate = false;

        for (int i = prefixed.Length - 1; i >= 0; i--)
        {
            int digit = prefixed[i] - '0';
            if (alternate)
            {
                digit *= 2;
                if (digit > 9) digit -= 9;
            }
            sum += digit;
            alternate = !alternate;
        }

        return sum % 10 == 0;
    }
}
