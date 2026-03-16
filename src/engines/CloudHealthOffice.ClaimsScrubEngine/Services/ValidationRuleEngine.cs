using System.Diagnostics;
using System.Text.RegularExpressions;
using CloudHealthOffice.ClaimsScrubEngine.Models;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.ClaimsScrubEngine.Services;

/// <summary>
/// Port of the TypeScript ValidationRuleEngine.
/// Executes 20+ standard validation rules across 6 categories against X12 837 claims.
/// </summary>
public sealed partial class ValidationRuleEngine : IValidationRuleEngine
{
    private readonly Dictionary<string, ValidationRule> _rules = new();
    private readonly StandardRuleSet _standardRules;
    private readonly ILogger<ValidationRuleEngine> _logger;

    public ValidationRuleEngine(
        StandardRuleSet standardRules,
        ILogger<ValidationRuleEngine> logger)
    {
        _standardRules = standardRules;
        _logger = logger;
        InitializeStandardRules();
    }

    // ========================================================================
    // Public API
    // ========================================================================

    public async Task<ClaimValidationResult> ValidateClaimAsync(
        X12837Claim claim,
        ClaimValidationOptions? options = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var applicableRules = GetApplicableRules(claim.ClaimType, options);
        var results = new List<ValidationResult>(applicableRules.Count);

        foreach (var rule in applicableRules)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await ExecuteRuleAsync(rule, claim));
        }

        var errorCount = results.Count(r => !r.Passed && r.Severity == ValidationSeverity.Error);
        var warningCount = results.Count(r => !r.Passed && r.Severity == ValidationSeverity.Warning);
        var infoCount = results.Count(r => !r.Passed && r.Severity == ValidationSeverity.Info);

        var routing = DetermineRouting(results, errorCount, warningCount);

        sw.Stop();
        return new ClaimValidationResult
        {
            ClaimId = claim.ClaimId,
            ClaimType = claim.ClaimType,
            PatientControlNumber = claim.ClaimHeader.PatientControlNumber,
            Status = DetermineStatus(errorCount, warningCount),
            RulesExecuted = results.Count,
            RulesPassed = results.Count(r => r.Passed),
            RulesFailed = results.Count(r => !r.Passed),
            ErrorCount = errorCount,
            WarningCount = warningCount,
            InfoCount = infoCount,
            Results = results,
            ValidatedAt = DateTime.UtcNow.ToString("o"),
            TotalValidationTimeMs = sw.ElapsedMilliseconds,
            Routing = routing,
            FirstPassEligible = errorCount == 0 && warningCount == 0,
        };
    }

    public IReadOnlyList<ValidationRule> GetRules() =>
        _rules.Values.ToList();

    public IReadOnlyList<ValidationRule> GetRulesByCategory(string categorySlug)
    {
        var category = ValidationCategoryNames.FromSlug(categorySlug);
        return _rules.Values.Where(r => r.Category == category).ToList();
    }

    public IReadOnlyList<ValidationRule> GetEnabledRulesForClaimType(ClaimType claimType) =>
        _rules.Values
            .Where(r => r.Enabled && r.AppliesTo.Contains(claimType))
            .OrderBy(r => r.Priority)
            .ToList();

    public void AddRule(ValidationRule rule) =>
        _rules[rule.RuleId] = rule;

    // ========================================================================
    // Rule Initialization
    // ========================================================================

    private static readonly ClaimType[] AllTypes =
        [ClaimType.Professional, ClaimType.Institutional, ClaimType.Dental];

    private void InitializeStandardRules()
    {
        var dc = _standardRules.DataCompleteness;
        Add("DC001", "Subscriber Identifier Required",
            "Validates that subscriber identifier is present on the claim",
            ValidationCategory.DataCompleteness, ValidationSeverity.Error, AllTypes, dc.MemberIdRequired, 1);
        Add("DC002", "Subscriber DOB Required",
            "Validates that subscriber date of birth is present",
            ValidationCategory.DataCompleteness, ValidationSeverity.Error, AllTypes, dc.SubscriberDobRequired, 1);
        Add("DC003", "Billing Provider NPI Required",
            "Validates that billing provider NPI is present",
            ValidationCategory.DataCompleteness, ValidationSeverity.Error, AllTypes, dc.BillingProviderNpiRequired, 1);
        Add("DC004", "Diagnosis Code Required",
            "Validates that at least one diagnosis code is present",
            ValidationCategory.DataCompleteness, ValidationSeverity.Error, AllTypes, dc.DiagnosisRequired, 1);
        Add("DC005", "Minimum Service Lines",
            "Validates that claim has minimum required service lines",
            ValidationCategory.DataCompleteness, ValidationSeverity.Error, AllTypes, true, 1,
            new() { ["minLines"] = dc.MinServiceLines });
        Add("DC006", "Service Date Required",
            "Validates that service date is present on all lines",
            ValidationCategory.DataCompleteness, ValidationSeverity.Error, AllTypes, dc.ServiceDateRequired, 1);
        // DC007 is intentionally not registered as a runtime rule.
        // ChargeAmount is a non-nullable decimal (default 0), so a
        // presence check cannot meaningfully fail at the engine layer.
        // The field is structurally guaranteed by the model.

        var cv = _standardRules.CodeValidation;
        Add("CV001", "Valid ICD-10 Code Format",
            "Validates ICD-10 diagnosis code format",
            ValidationCategory.CodeValidity, ValidationSeverity.Error, AllTypes, cv.ValidateIcd10, 10);
        Add("CV002", "Valid CPT Code Format",
            "Validates CPT procedure code format",
            ValidationCategory.CodeValidity, ValidationSeverity.Error,
            [ClaimType.Professional], cv.ValidateCpt, 10);
        Add("CV003", "Valid HCPCS Code Format",
            "Validates HCPCS code format",
            ValidationCategory.CodeValidity, ValidationSeverity.Error,
            [ClaimType.Professional, ClaimType.Institutional], cv.ValidateHcpcs, 10);
        Add("CV004", "Valid Revenue Code Format",
            "Validates revenue code format for institutional claims",
            ValidationCategory.CodeValidity, ValidationSeverity.Error,
            [ClaimType.Institutional], cv.ValidateRevenueCodes, 10);
        Add("CV005", "Valid Place of Service Code",
            "Validates place of service code",
            ValidationCategory.CodeValidity, ValidationSeverity.Error,
            [ClaimType.Professional], cv.ValidatePlaceOfService, 10);

        var dl = _standardRules.DateLogic;
        Add("DL001", "Service Date Not Future",
            "Validates that service date is not in the future",
            ValidationCategory.DateLogic, ValidationSeverity.Error, AllTypes, dl.ServiceDateNotFuture, 5);
        Add("DL002", "Service Date Within Filing Limit",
            "Validates that claim is filed within timely filing limit",
            ValidationCategory.DateLogic, ValidationSeverity.Warning, AllTypes, dl.ServiceDateWithinFilingLimit, 5,
            new() { ["filingLimitDays"] = dl.FilingLimitDays });
        Add("DL003", "Discharge After Admission",
            "Validates discharge date is after admission date",
            ValidationCategory.DateLogic, ValidationSeverity.Error,
            [ClaimType.Institutional], dl.DischargeDateAfterAdmission, 5);
        Add("DL004", "Patient DOB Before Service",
            "Validates patient date of birth is before service date",
            ValidationCategory.DateLogic, ValidationSeverity.Error, AllTypes, dl.PatientDobBeforeService, 5);

        var al = _standardRules.AmountLogic;
        Add("AL001", "Charge Amounts Positive",
            "Validates that all charge amounts are positive",
            ValidationCategory.AmountLogic, ValidationSeverity.Error, AllTypes, al.ChargeAmountsPositive, 5);
        Add("AL002", "Total Matches Line Sum",
            "Validates total claim amount matches sum of service lines",
            ValidationCategory.AmountLogic, ValidationSeverity.Warning, AllTypes, al.TotalMatchesLineSum, 5);
        Add("AL003", "Units Positive",
            "Validates that units of service are positive",
            ValidationCategory.AmountLogic, ValidationSeverity.Error, AllTypes, al.UnitsPositive, 5);

        var pv = _standardRules.ProviderValidation;
        Add("PV001", "Valid NPI Format",
            "Validates NPI number format using Luhn algorithm",
            ValidationCategory.ProviderValidation, ValidationSeverity.Error, AllTypes, pv.ValidateNpiFormat, 10);
        Add("PV002", "Valid Tax ID Format",
            "Validates tax identification number format",
            ValidationCategory.ProviderValidation, ValidationSeverity.Warning, AllTypes, pv.ValidateTaxIdFormat, 10);

        var mv = _standardRules.ModifierValidation;
        Add("MV001", "Valid Modifier Format",
            "Validates modifier code format",
            ValidationCategory.ModifierValidation, ValidationSeverity.Error,
            [ClaimType.Professional, ClaimType.Institutional], mv.ValidateModifierFormat, 10);
        Add("MV002", "No Duplicate Modifiers",
            "Checks for duplicate modifiers on service lines",
            ValidationCategory.ModifierValidation, ValidationSeverity.Error,
            [ClaimType.Professional, ClaimType.Institutional], mv.CheckDuplicateModifiers, 10);
    }

    private void Add(
        string ruleId, string name, string description,
        ValidationCategory category, ValidationSeverity severity,
        ClaimType[] appliesTo, bool enabled, int priority,
        Dictionary<string, object>? config = null)
    {
        _rules[ruleId] = new ValidationRule
        {
            RuleId = ruleId,
            RuleName = name,
            Description = description,
            Category = category,
            Severity = severity,
            AppliesTo = [.. appliesTo],
            Enabled = enabled,
            Priority = priority,
            Type = RuleType.Standard,
            Config = config,
        };
    }

    // ========================================================================
    // Rule Execution
    // ========================================================================

    private List<ValidationRule> GetApplicableRules(
        ClaimType claimType, ClaimValidationOptions? options)
    {
        var rules = GetEnabledRulesForClaimType(claimType).ToList();

        if (options?.OnlyRules is { Count: > 0 } only)
            rules = rules.Where(r => only.Contains(r.RuleId)).ToList();

        if (options?.SkipRules is { Count: > 0 } skip)
            rules = rules.Where(r => !skip.Contains(r.RuleId)).ToList();

        return rules;
    }

    private async Task<ValidationResult> ExecuteRuleAsync(ValidationRule rule, X12837Claim claim)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await Task.FromResult(ExecuteRuleLogic(rule, claim));
            sw.Stop();
            return result with { ExecutionTimeMs = sw.ElapsedMilliseconds };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Rule {RuleId} execution error", rule.RuleId);
            return new ValidationResult
            {
                RuleId = rule.RuleId,
                RuleName = rule.RuleName,
                Passed = false,
                Severity = ValidationSeverity.Error,
                Message = "Rule execution error. See server logs for details.",
                ExecutionTimeMs = sw.ElapsedMilliseconds,
            };
        }
    }

    private ValidationResult ExecuteRuleLogic(ValidationRule rule, X12837Claim claim) => rule.RuleId switch
    {
        "DC001" => ValidateMemberIdRequired(rule, claim),
        "DC002" => ValidateSubscriberDobRequired(rule, claim),
        "DC003" => ValidateBillingProviderNpiRequired(rule, claim),
        "DC004" => ValidateDiagnosisRequired(rule, claim),
        "DC005" => ValidateMinServiceLines(rule, claim),
        "DC006" => ValidateServiceDateRequired(rule, claim),
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

        _ => PassResult(rule),
    };

    // ========================================================================
    // Data Completeness Validators
    // ========================================================================

    private static ValidationResult ValidateMemberIdRequired(ValidationRule rule, X12837Claim claim)
    {
        if (string.IsNullOrWhiteSpace(claim.Subscriber.MemberId))
            return FailResult(rule, "Member ID is required", ["subscriber.memberId"], "DC001");
        return PassResult(rule);
    }

    private static ValidationResult ValidateSubscriberDobRequired(ValidationRule rule, X12837Claim claim)
    {
        if (string.IsNullOrWhiteSpace(claim.Subscriber.DateOfBirth))
            return FailResult(rule, "Subscriber date of birth is required", ["subscriber.dateOfBirth"], "DC002");
        return PassResult(rule);
    }

    private static ValidationResult ValidateBillingProviderNpiRequired(ValidationRule rule, X12837Claim claim)
    {
        if (string.IsNullOrWhiteSpace(claim.BillingProvider.Npi))
            return FailResult(rule, "Billing provider NPI is required", ["billingProvider.npi"], "DC003");
        return PassResult(rule);
    }

    private static ValidationResult ValidateDiagnosisRequired(ValidationRule rule, X12837Claim claim)
    {
        if (claim.ClaimHeader.DiagnosisCodes is not { Count: > 0 })
            return FailResult(rule, "At least one diagnosis code is required", ["claimHeader.diagnosisCodes"], "DC004");
        return PassResult(rule);
    }

    private static ValidationResult ValidateMinServiceLines(ValidationRule rule, X12837Claim claim)
    {
        var minLines = rule.Config?.TryGetValue("minLines", out var v) == true && v is int n ? n : 1;
        if (claim.ServiceLines.Count < minLines)
            return FailResult(rule, $"Claim must have at least {minLines} service line(s)", ["serviceLines"], "DC005");
        return PassResult(rule);
    }

    private static ValidationResult ValidateServiceDateRequired(ValidationRule rule, X12837Claim claim)
    {
        var missing = claim.ServiceLines
            .Where(l => string.IsNullOrWhiteSpace(l.ServiceDate))
            .Select(l => l.LineNumber).ToList();
        if (missing.Count > 0)
            return FailResult(rule, $"Service date is required on line(s): {string.Join(", ", missing)}",
                ["serviceLines.serviceDate"], "DC006", missing);
        return PassResult(rule);
    }

    // ========================================================================
    // Code Validation
    // ========================================================================

    private static ValidationResult ValidateIcd10Format(ValidationRule rule, X12837Claim claim)
    {
        var diagCodes = claim.ClaimHeader.DiagnosisCodes ?? [];
        var invalid = new List<string>();

        foreach (var diag in diagCodes)
        {
            if (diag.Qualifier is "ABK" or "ABF")
            {
                var code = diag.Code.Replace(".", "");
                if (!Icd10Regex().IsMatch(diag.Code) && !Icd10Regex().IsMatch(code))
                    invalid.Add(diag.Code);
            }
        }

        if (invalid.Count > 0)
            return FailResult(rule, $"Invalid ICD-10 code format: {string.Join(", ", invalid)}",
                ["claimHeader.diagnosisCodes"], "CV001");
        return PassResult(rule);
    }

    private static ValidationResult ValidateCptFormat(ValidationRule rule, X12837Claim claim)
    {
        var invalidLines = new List<int>();

        foreach (var line in claim.ServiceLines)
        {
            if (line.ProcedureCodeQualifier is "HC" or null)
            {
                if (!CptRegex().IsMatch(line.ProcedureCode))
                {
                    if (!HcpcsRegex().IsMatch(line.ProcedureCode))
                        invalidLines.Add(line.LineNumber);
                }
            }
        }

        if (invalidLines.Count > 0)
            return FailResult(rule, $"Invalid CPT code format on line(s): {string.Join(", ", invalidLines)}",
                ["serviceLines.procedureCode"], "CV002", invalidLines);
        return PassResult(rule);
    }

    private static ValidationResult ValidateHcpcsFormat(ValidationRule rule, X12837Claim claim)
    {
        var invalidLines = new List<int>();

        foreach (var line in claim.ServiceLines)
        {
            if (!string.IsNullOrEmpty(line.ProcedureCode) && char.IsLetter(line.ProcedureCode[0]))
            {
                if (!HcpcsRegex().IsMatch(line.ProcedureCode))
                    invalidLines.Add(line.LineNumber);
            }
        }

        if (invalidLines.Count > 0)
            return FailResult(rule, $"Invalid HCPCS code format on line(s): {string.Join(", ", invalidLines)}",
                ["serviceLines.procedureCode"], "CV003", invalidLines);
        return PassResult(rule);
    }

    private static ValidationResult ValidateRevenueCodeFormat(ValidationRule rule, X12837Claim claim)
    {
        if (claim.ClaimType != ClaimType.Institutional)
            return PassResult(rule);

        var invalidLines = new List<int>();
        foreach (var line in claim.ServiceLines)
        {
            if (!string.IsNullOrEmpty(line.RevenueCode) && !RevenueCodeRegex().IsMatch(line.RevenueCode))
                invalidLines.Add(line.LineNumber);
        }

        if (invalidLines.Count > 0)
            return FailResult(rule, $"Invalid revenue code format on line(s): {string.Join(", ", invalidLines)}",
                ["serviceLines.revenueCode"], "CV004", invalidLines);
        return PassResult(rule);
    }

    private static ValidationResult ValidatePlaceOfServiceCode(ValidationRule rule, X12837Claim claim)
    {
        if (claim.ClaimType != ClaimType.Professional)
            return PassResult(rule);

        var pos = claim.ClaimHeader.PlaceOfServiceCode;
        if (!string.IsNullOrEmpty(pos) && !ValidPosCodes.Contains(pos))
            return FailResult(rule, $"Invalid place of service code: {pos}",
                ["claimHeader.placeOfServiceCode"], "CV005");
        return PassResult(rule);
    }

    private static readonly HashSet<string> ValidPosCodes =
    [
        "01", "02", "03", "04", "05", "06", "07", "08", "09", "10",
        "11", "12", "13", "14", "15", "16", "17", "18", "19", "20",
        "21", "22", "23", "24", "25", "26", "31", "32", "33", "34",
        "41", "42", "49", "50", "51", "52", "53", "54", "55", "56",
        "57", "58", "60", "61", "62", "65", "71", "72", "81", "99"
    ];

    // ========================================================================
    // Date Logic Validators
    // ========================================================================

    private static ValidationResult ValidateServiceDateNotFuture(ValidationRule rule, X12837Claim claim)
    {
        var today = GetCurrentDateString();
        var futureLines = claim.ServiceLines
            .Where(l => string.Compare(l.ServiceDate, today, StringComparison.Ordinal) > 0)
            .Select(l => l.LineNumber).ToList();

        if (futureLines.Count > 0)
            return FailResult(rule, $"Service date is in the future on line(s): {string.Join(", ", futureLines)}",
                ["serviceLines.serviceDate"], "DL001", futureLines);
        return PassResult(rule);
    }

    private static ValidationResult ValidateServiceDateWithinFilingLimit(ValidationRule rule, X12837Claim claim)
    {
        var filingDays = rule.Config?.TryGetValue("filingLimitDays", out var v) == true && v is int n ? n : 365;
        var limitDate = GetDateMinusDays(filingDays);
        var lateLines = claim.ServiceLines
            .Where(l => string.Compare(l.ServiceDate, limitDate, StringComparison.Ordinal) < 0)
            .Select(l => l.LineNumber).ToList();

        if (lateLines.Count > 0)
            return FailResult(rule,
                $"Service date exceeds {filingDays}-day filing limit on line(s): {string.Join(", ", lateLines)}",
                ["serviceLines.serviceDate"], "DL002", lateLines);
        return PassResult(rule);
    }

    private static ValidationResult ValidateDischargeAfterAdmission(ValidationRule rule, X12837Claim claim)
    {
        if (claim.ClaimType != ClaimType.Institutional)
            return PassResult(rule);

        var admission = claim.ClaimHeader.AdmissionDate;
        var discharge = claim.ClaimHeader.DischargeDate;

        if (!string.IsNullOrEmpty(admission) && !string.IsNullOrEmpty(discharge)
            && string.Compare(discharge, admission, StringComparison.Ordinal) < 0)
        {
            return FailResult(rule, "Discharge date cannot be before admission date",
                ["claimHeader.admissionDate", "claimHeader.dischargeDate"], "DL003");
        }
        return PassResult(rule);
    }

    private static ValidationResult ValidatePatientDobBeforeService(ValidationRule rule, X12837Claim claim)
    {
        var patientDob = claim.Patient?.DateOfBirth ?? claim.Subscriber.DateOfBirth;
        var invalidLines = claim.ServiceLines
            .Where(l => string.Compare(l.ServiceDate, patientDob, StringComparison.Ordinal) < 0)
            .Select(l => l.LineNumber).ToList();

        if (invalidLines.Count > 0)
            return FailResult(rule,
                $"Service date is before patient date of birth on line(s): {string.Join(", ", invalidLines)}",
                ["serviceLines.serviceDate"], "DL004", invalidLines);
        return PassResult(rule);
    }

    // ========================================================================
    // Amount Logic Validators
    // ========================================================================

    private static ValidationResult ValidateChargeAmountsPositive(ValidationRule rule, X12837Claim claim)
    {
        var invalidLines = claim.ServiceLines
            .Where(l => l.ChargeAmount <= 0)
            .Select(l => l.LineNumber).ToList();

        if (invalidLines.Count > 0)
            return FailResult(rule, $"Charge amount must be positive on line(s): {string.Join(", ", invalidLines)}",
                ["serviceLines.chargeAmount"], "AL001", invalidLines);
        return PassResult(rule);
    }

    private static ValidationResult ValidateTotalMatchesLineSum(ValidationRule rule, X12837Claim claim)
    {
        var lineSum = claim.ServiceLines.Sum(l => l.ChargeAmount);
        if (Math.Abs(claim.TotalClaimedAmount - lineSum) > 0.01m)
            return FailResult(rule,
                $"Total claimed amount ({claim.TotalClaimedAmount}) does not match sum of line charges ({lineSum})",
                ["totalClaimedAmount", "serviceLines.chargeAmount"], "AL002");
        return PassResult(rule);
    }

    private static ValidationResult ValidateUnitsPositive(ValidationRule rule, X12837Claim claim)
    {
        var invalidLines = claim.ServiceLines
            .Where(l => l.Units <= 0)
            .Select(l => l.LineNumber).ToList();

        if (invalidLines.Count > 0)
            return FailResult(rule, $"Units must be positive on line(s): {string.Join(", ", invalidLines)}",
                ["serviceLines.units"], "AL003", invalidLines);
        return PassResult(rule);
    }

    // ========================================================================
    // Provider Validators
    // ========================================================================

    private static ValidationResult ValidateNpiFormat(ValidationRule rule, X12837Claim claim)
    {
        var npi = claim.BillingProvider.Npi;
        if (!IsValidNpi(npi))
            return FailResult(rule, $"Invalid NPI format: {npi}. NPI must be 10 digits and pass Luhn check.",
                ["billingProvider.npi"], "PV001");
        return PassResult(rule);
    }

    private static ValidationResult ValidateTaxIdFormat(ValidationRule rule, X12837Claim claim)
    {
        var taxId = claim.BillingProvider.TaxId;
        var qualifier = claim.BillingProvider.TaxIdQualifier;

        if (!string.IsNullOrEmpty(taxId))
        {
            var clean = taxId.Replace("-", "").Replace(" ", "");

            if (qualifier == "EI" && !NineDigitsRegex().IsMatch(clean))
                return FailResult(rule, "Invalid EIN format. Must be 9 digits.",
                    ["billingProvider.taxId"], "PV002");

            if (qualifier == "SY" && !NineDigitsRegex().IsMatch(clean))
                return FailResult(rule, "Invalid SSN format. Must be 9 digits.",
                    ["billingProvider.taxId"], "PV002");
        }
        return PassResult(rule);
    }

    // ========================================================================
    // Modifier Validators
    // ========================================================================

    private static ValidationResult ValidateModifierFormat(ValidationRule rule, X12837Claim claim)
    {
        var invalidLines = new List<int>();

        foreach (var line in claim.ServiceLines)
        {
            if (line.Modifiers is { Count: > 0 })
            {
                foreach (var mod in line.Modifiers)
                {
                    if (!ModifierRegex().IsMatch(mod))
                    {
                        if (!invalidLines.Contains(line.LineNumber))
                            invalidLines.Add(line.LineNumber);
                    }
                }
            }
        }

        if (invalidLines.Count > 0)
            return FailResult(rule, $"Invalid modifier format on line(s): {string.Join(", ", invalidLines)}",
                ["serviceLines.modifiers"], "MV001", invalidLines);
        return PassResult(rule);
    }

    private static ValidationResult ValidateNoDuplicateModifiers(ValidationRule rule, X12837Claim claim)
    {
        var duplicateLines = new List<int>();

        foreach (var line in claim.ServiceLines)
        {
            if (line.Modifiers is { Count: > 1 })
            {
                if (line.Modifiers.Distinct().Count() != line.Modifiers.Count)
                    duplicateLines.Add(line.LineNumber);
            }
        }

        if (duplicateLines.Count > 0)
            return FailResult(rule, $"Duplicate modifiers found on line(s): {string.Join(", ", duplicateLines)}",
                ["serviceLines.modifiers"], "MV002", duplicateLines);
        return PassResult(rule);
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    internal static bool IsValidNpi(string npi)
    {
        if (!TenDigitsRegex().IsMatch(npi))
            return false;

        // Luhn algorithm with NPI prefix (80840)
        var prefixed = "80840" + npi;
        var sum = 0;
        var alternate = false;

        for (var i = prefixed.Length - 1; i >= 0; i--)
        {
            var digit = prefixed[i] - '0';
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

    private static string GetCurrentDateString() =>
        DateTime.UtcNow.ToString("yyyyMMdd");

    private static string GetDateMinusDays(int days) =>
        DateTime.UtcNow.AddDays(-days).ToString("yyyyMMdd");

    private static string DetermineStatus(int errorCount, int warningCount)
    {
        if (errorCount > 0) return "rejected";
        if (warningCount > 0) return "flagged";
        return "clean";
    }

    private static ClaimRoutingDecision DetermineRouting(
        List<ValidationResult> results, int errorCount, int warningCount)
    {
        var editCodes = results
            .Where(r => !r.Passed && !string.IsNullOrEmpty(r.EditCode))
            .Select(r => r.EditCode!)
            .ToList();

        if (errorCount > 0)
        {
            return new ClaimRoutingDecision
            {
                Destination = "work-queue",
                QueueName = "claims-errors",
                Priority = "high",
                Reason = $"Claim has {errorCount} validation error(s) requiring review",
                EditCodes = editCodes,
                RequiresManualReview = true,
            };
        }

        if (warningCount > 0)
        {
            return new ClaimRoutingDecision
            {
                Destination = "work-queue",
                QueueName = "claims-warnings",
                Priority = "medium",
                Reason = $"Claim has {warningCount} warning(s) requiring review",
                EditCodes = editCodes,
                RequiresManualReview = true,
            };
        }

        return new ClaimRoutingDecision
        {
            Destination = "adjudication",
            Reason = "Claim passed all validation rules",
            EditCodes = [],
            RequiresManualReview = false,
        };
    }

    private static ValidationResult PassResult(ValidationRule rule) => new()
    {
        RuleId = rule.RuleId,
        RuleName = rule.RuleName,
        Passed = true,
    };

    private static ValidationResult FailResult(
        ValidationRule rule, string message, List<string> fields,
        string editCode, List<int>? serviceLines = null) => new()
    {
        RuleId = rule.RuleId,
        RuleName = rule.RuleName,
        Passed = false,
        Severity = rule.Severity,
        Message = message,
        Fields = fields,
        ServiceLines = serviceLines,
        EditCode = editCode,
    };

    // ========================================================================
    // Compiled Regex (source-generated)
    // ========================================================================

    [GeneratedRegex(@"^[A-TV-Z][0-9][0-9AB]\.?[0-9A-Z]{0,4}$", RegexOptions.IgnoreCase)]
    private static partial Regex Icd10Regex();

    [GeneratedRegex(@"^[0-9]{4}[0-9A-Z]$")]
    private static partial Regex CptRegex();

    [GeneratedRegex(@"^[A-Z][0-9]{4}$")]
    private static partial Regex HcpcsRegex();

    [GeneratedRegex(@"^[0-9]{4}$")]
    private static partial Regex RevenueCodeRegex();

    [GeneratedRegex(@"^[A-Z0-9]{2}$")]
    private static partial Regex ModifierRegex();

    [GeneratedRegex(@"^[0-9]{10}$")]
    private static partial Regex TenDigitsRegex();

    [GeneratedRegex(@"^[0-9]{9}$")]
    private static partial Regex NineDigitsRegex();
}
