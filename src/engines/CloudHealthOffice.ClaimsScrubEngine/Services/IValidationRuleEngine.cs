using CloudHealthOffice.ClaimsScrubEngine.Models;

namespace CloudHealthOffice.ClaimsScrubEngine.Services;

/// <summary>
/// Claims scrub validation rule engine.
/// Executes configurable validation rules against X12 837 claims.
/// </summary>
public interface IValidationRuleEngine
{
    /// <summary>Validate a claim against all applicable rules.</summary>
    Task<ClaimValidationResult> ValidateClaimAsync(
        X12837Claim claim,
        ClaimValidationOptions? options = null,
        CancellationToken ct = default);

    /// <summary>Get all registered rules.</summary>
    IReadOnlyList<ValidationRule> GetRules();

    /// <summary>Get rules by category slug (e.g. "data-completeness").</summary>
    IReadOnlyList<ValidationRule> GetRulesByCategory(string categorySlug);

    /// <summary>Get enabled rules for a given claim type.</summary>
    IReadOnlyList<ValidationRule> GetEnabledRulesForClaimType(ClaimType claimType);

    /// <summary>Add a custom rule.</summary>
    void AddRule(ValidationRule rule);
}

public record ClaimValidationOptions
{
    public List<string>? SkipRules { get; init; }
    public List<string>? OnlyRules { get; init; }
}
