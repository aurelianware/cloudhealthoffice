using CloudHealthOffice.ClaimsScrubEngine.Models;

namespace CloudHealthOffice.ClaimsScrubEngine.Data;

/// <summary>
/// Default standard rule set configuration.
/// Ported from DEFAULT_STANDARD_RULES in rule-engine.ts.
/// </summary>
public static class DefaultStandardRules
{
    public static StandardRuleSet Create() => new()
    {
        DataCompleteness = new StandardDataCompletenessRules
        {
            MemberIdRequired = true,
            SubscriberDobRequired = true,
            BillingProviderNpiRequired = true,
            DiagnosisRequired = true,
            MinServiceLines = 1,
            ServiceDateRequired = true,
            ChargeAmountRequired = true,
        },
        CodeValidation = new StandardCodeValidationRules
        {
            ValidateIcd10 = true,
            ValidateCpt = true,
            ValidateHcpcs = true,
            ValidateRevenueCodes = true,
            ValidatePlaceOfService = true,
            CheckObsoleteCodes = true,
            CheckGenderSpecificCodes = true,
            CheckAgeSpecificCodes = true,
        },
        DateLogic = new StandardDateLogicRules
        {
            ServiceDateNotFuture = true,
            ServiceDateWithinFilingLimit = true,
            FilingLimitDays = 365,
            DischargeDateAfterAdmission = true,
            PatientDobBeforeService = true,
            ServiceDatesInSequence = true,
        },
        AmountLogic = new StandardAmountLogicRules
        {
            ChargeAmountsPositive = true,
            TotalMatchesLineSum = true,
            MaxSingleLineAmount = 1_000_000m,
            MaxClaimTotal = 10_000_000m,
            UnitsPositive = true,
            MaxUnitsPerLine = 9999,
        },
        ProviderValidation = new StandardProviderValidationRules
        {
            ValidateNpiFormat = true,
            ValidateNpiRegistry = false,
            ValidateTaxonomyFormat = true,
            ValidateTaxIdFormat = true,
            RenderingProviderRequired = false,
        },
        ModifierValidation = new StandardModifierValidationRules
        {
            ValidateModifierFormat = true,
            CheckDuplicateModifiers = true,
            ValidateModifierOrder = true,
            CheckMutuallyExclusiveModifiers = true,
        },
    };
}
