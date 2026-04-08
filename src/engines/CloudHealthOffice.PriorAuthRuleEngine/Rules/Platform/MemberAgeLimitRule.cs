using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.PriorAuthRuleEngine.Rules;

namespace CloudHealthOffice.PriorAuthRuleEngine.Rules.Platform;

/// <summary>
/// Member Age Limit — approves or pends based on member age.
///
/// Primary use case: EPSDT (Early Periodic Screening, Diagnostic, and Treatment).
/// Federal law (42 USC §1396d(r)) requires states to cover all medically necessary
/// services for Medicaid members under age 21, regardless of whether the specific
/// service is listed in the state plan.
///
/// In CHO: STARKids members under 21 are auto-approved for services that would
/// otherwise require PA — because the EPSDT mandate supersedes the plan's PA
/// requirements for this age group.
///
/// Configuration:
///   MaxMemberAgeYears  — approve when member age ≤ this value (null = no max limit)
///   MinMemberAgeYears  — approve when member age ≥ this value (null = no min limit)
///   ProcedureCodes / Prefixes — scope to specific procedures (empty = all)
///
/// If MemberDateOfBirth is not present in context, the rule skips (returns null).
///
/// RuleType:  "MemberAgeLimit"
/// Category:  MemberAge (band 5)
/// </summary>
public sealed class MemberAgeLimitRule : PaRuleBase
{
    public override string RuleType       => "MemberAgeLimit";
    public override RuleCategory Category => RuleCategory.MemberAge;
    public override int Priority          => 50;

    public override Task<PaRuleDecision?> EvaluateAsync(
        PaRuleDocument config,
        PaRuleContext context,
        CancellationToken ct = default)
    {
        // Cannot evaluate without date of birth
        if (context.MemberDateOfBirth is null)
            return Task.FromResult<PaRuleDecision?>(null);

        if (!AppliesToProcedures(config, context.ProcedureCodes))
            return Task.FromResult<PaRuleDecision?>(null);

        var ageYears = CalculateAge(context.MemberDateOfBirth.Value, context.ServiceDate);

        var belowMax = !config.MaxMemberAgeYears.HasValue || ageYears <= config.MaxMemberAgeYears.Value;
        var aboveMin = !config.MinMemberAgeYears.HasValue || ageYears >= config.MinMemberAgeYears.Value;

        if (belowMax && aboveMin)
            return Task.FromResult<PaRuleDecision?>(Approve(config));

        // Age outside configured window — rule does not apply
        return Task.FromResult<PaRuleDecision?>(null);
    }

    private static int CalculateAge(DateOnly dob, DateOnly asOf)
    {
        var age = asOf.Year - dob.Year;
        if (asOf < dob.AddYears(age)) age--;
        return age;
    }
}
