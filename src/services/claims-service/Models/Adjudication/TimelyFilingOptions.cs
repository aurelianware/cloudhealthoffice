namespace ClaimsService.Models.Adjudication;

/// <summary>
/// What <see cref="Services.Adjudication.Stages.TimelyFilingStage"/> does with
/// a service line received after its filing limit.
/// </summary>
public enum TimelyFilingEnforcementMode
{
    /// <summary>
    /// Deny with CARC 29 (group CO, "the time limit for filing has expired")
    /// when every line is late. A claim with some late and some timely lines
    /// pends instead: the pipeline has no partial line denial ahead of
    /// benefit calculation, so an examiner denies the late lines (manual line
    /// denial, CARC 29) or overrides the limit.
    /// </summary>
    Deny,

    /// <summary>Pend every late claim (pend code <c>TIMELY</c>) for an examiner.</summary>
    PendForReview,

    /// <summary>
    /// Log and tag late claims but let them through. For sizing the effect of
    /// a new limit before enforcing it.
    /// </summary>
    SoftValidation,
}

/// <summary>
/// One plan's timely filing rules: how long after the date of service a claim
/// may be received. Limits are in days and measured from the payer receipt
/// date (<see cref="Claim.ReceivedDate"/>, stamped by claims-service at
/// intake and never taken from the submitter).
/// </summary>
public class TimelyFilingPolicy
{
    /// <summary>What happens to a late claim. Default <see cref="TimelyFilingEnforcementMode.Deny"/>.</summary>
    public TimelyFilingEnforcementMode Mode { get; set; } = TimelyFilingEnforcementMode.Deny;

    /// <summary>
    /// The limit when no line-of-business or provider rule applies. Default
    /// 365 days (the Medicare limit of one year from the date of service);
    /// most commercial and Medicaid contracts are shorter and should say so.
    /// Null: no limit unless a more specific rule sets one.
    /// </summary>
    public int? DefaultLimitDays { get; set; } = 365;

    /// <summary>Limits by line of business, e.g. <c>{ "Medicaid": 95, "Commercial": 180 }</c>.</summary>
    public Dictionary<LineOfBusiness, int> LineOfBusinessLimitDays { get; set; } = new();

    /// <summary>
    /// Contract-specific limits for named billing providers. The most specific
    /// matching rule wins: a provider rule for the claim's line of business,
    /// then a provider rule for any line of business, then
    /// <see cref="LineOfBusinessLimitDays"/>, then <see cref="DefaultLimitDays"/>.
    /// </summary>
    public List<TimelyFilingProviderRule> ProviderRules { get; set; } = new();
}

/// <summary>A filing limit agreed in one provider contract.</summary>
public class TimelyFilingProviderRule
{
    /// <summary>A label for the rule (usually the contract), shown on the denial and in logs.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Billing provider NPIs (837 2010AA NM109) the contract covers.</summary>
    public List<string> BillingProviderNpis { get; set; } = new();

    /// <summary>The line of business the rule is limited to; null for every line of business.</summary>
    public LineOfBusiness? LineOfBusiness { get; set; }

    public int LimitDays { get; set; }
}

/// <summary>
/// Timely filing configuration, section <c>Adjudication:TimelyFiling</c>.
/// The top-level policy applies to every tenant; an entry under
/// <see cref="Tenants"/> replaces it entirely for that tenant (a plan's
/// filing rules are its own, so nothing is merged from the default).
/// </summary>
public class TimelyFilingOptions : TimelyFilingPolicy
{
    public const string SectionName = "Adjudication:TimelyFiling";

    public Dictionary<string, TimelyFilingPolicy> Tenants { get; set; } = new(StringComparer.Ordinal);

    public TimelyFilingPolicy For(string tenantId) =>
        !string.IsNullOrWhiteSpace(tenantId) && Tenants.TryGetValue(tenantId, out var policy) && policy is not null
            ? policy
            : this;
}
