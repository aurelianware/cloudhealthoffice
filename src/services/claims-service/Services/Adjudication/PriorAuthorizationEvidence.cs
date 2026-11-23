namespace ClaimsService.Services.Adjudication;

/// <summary>Why a line needed prior authorization.</summary>
public static class PriorAuthorizationRequiredBy
{
    /// <summary>Every line of an institutional Medicaid inpatient claim (place of service 21): the default scope.</summary>
    public const string MedicaidInpatient = "Medicaid inpatient (POS 21)";

    /// <summary>The claim's benefit plan flags the procedure as needing prior authorization.</summary>
    public const string PlanBenefit = "Plan benefit flag";

    /// <summary>A rule of the plan's PA rule set (the list CRD, PAS and the published list read).</summary>
    public const string RuleSet = "PA rule set";
}

/// <summary>Where the authorization number CHO checked came from.</summary>
public static class PriorAuthorizationNumberSource
{
    public const string ClaimLine = "Claim line";
    public const string Claim = "Claim";

    /// <summary>The claim had no number; CHO found an approved authorization by member, service date, procedure and provider.</summary>
    public const string Matched = "Matched by member, date and service";

    public const string None = "None";
}

/// <summary>What CHO's prior-authorization check found for a line.</summary>
public static class PriorAuthorizationLineOutcome
{
    public const string Valid = "Valid";
    public const string Invalid = "Invalid";
    public const string NotFound = "NotFound";

    /// <summary>No number on the claim, and none matched (or matching is off).</summary>
    public const string Missing = "Missing";

    public const string Unavailable = "Unavailable";

    /// <summary>No valid authorization, but the rule set has exemptions claims cannot check: an examiner decides.</summary>
    public const string ExemptionReview = "ExemptionReview";
}

/// <summary>
/// The evidence behind CHO's prior-authorization check of one claim line: why
/// the line needed an authorization (and the rule and medical policy version
/// that said so), which authorization CHO checked and how it found it, and what
/// the check decided. Recorded on the adjudication context by
/// <see cref="Stages.BenefitCalculationStage"/>; a shadow run carries it onto
/// each result, so a reviewer sees which version of the policy a finding applied.
/// </summary>
public sealed class PriorAuthorizationLineEvidence
{
    public int LineNumber { get; set; }
    public string? ProcedureCode { get; set; }

    /// <summary><see cref="PriorAuthorizationRequiredBy"/>.</summary>
    public string RequiredBy { get; set; } = PriorAuthorizationRequiredBy.MedicaidInpatient;

    /// <summary>The PA rule set the plan resolved to, and its content version.</summary>
    public string? RuleSet { get; set; }
    public string? RuleSetVersion { get; set; }

    /// <summary>The rule of the set that covers this line (whatever put the line in scope).</summary>
    public string? RuleId { get; set; }
    public string? RuleName { get; set; }

    /// <summary>The medical policy (and version) the rule was digitized from, as the plan named it.</summary>
    public string? PolicySource { get; set; }
    public string? PolicyVersion { get; set; }
    public DateTime? PolicyEffectiveDate { get; set; }

    /// <summary>The authorization CHO checked; null when there was none.</summary>
    public string? AuthorizationNumber { get; set; }

    /// <summary><see cref="PriorAuthorizationNumberSource"/>.</summary>
    public string NumberSource { get; set; } = PriorAuthorizationNumberSource.None;

    /// <summary>How many approved authorizations matched a claim with no number (the first is used).</summary>
    public int MatchCandidates { get; set; }

    /// <summary>What the match rested on: member, service date, procedure, provider.</summary>
    public List<string> MatchedOn { get; set; } = [];

    /// <summary><see cref="PriorAuthorizationLineOutcome"/>.</summary>
    public string Outcome { get; set; } = PriorAuthorizationLineOutcome.Missing;

    public string? Reason { get; set; }

    /// <summary>The medical policy or clinical criteria the authorization's decision applied, when the plan's UM file gave it.</summary>
    public string? CriteriaReference { get; set; }

    /// <summary>"Rule R (Policy vN, effective D)": the policy behind the line, for a report column.</summary>
    public string? PolicyLabel()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(PolicySource))
        {
            var policy = PolicySource!;
            if (!string.IsNullOrWhiteSpace(PolicyVersion)) policy += $" {PolicyVersion}";
            if (PolicyEffectiveDate is { } effective) policy += $" (effective {effective:yyyy-MM-dd})";
            parts.Add(policy);
        }
        if (!string.IsNullOrWhiteSpace(RuleId)) parts.Add($"rule {RuleId}");
        if (!string.IsNullOrWhiteSpace(RuleSetVersion)) parts.Add($"rule set {RuleSetVersion}");
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }
}
