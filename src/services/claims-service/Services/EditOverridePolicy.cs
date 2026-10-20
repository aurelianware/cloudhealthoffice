using System.Text.Json.Serialization;
using ClaimsService.Models;
using Microsoft.Extensions.Options;

namespace ClaimsService.Services;

/// <summary>Who may override an edit.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OverrideLevel
{
    /// <summary>No one: the edit can only be upheld.</summary>
    Never,

    /// <summary>Anyone working claims (<c>claims:work</c>).</summary>
    Examiner,

    /// <summary>Needs <c>claims:override-edit</c>.</summary>
    Supervisor,
}

/// <summary>One row of the override policy table.</summary>
public sealed record EditOverrideRule(
    string Category,
    string Description,
    OverrideLevel Level,
    bool RequiresReason,
    bool RequiresDocumentation);

public sealed class EditOverrideRuleOptions
{
    public OverrideLevel Level { get; set; }
    public bool RequiresReason { get; set; } = true;
    public bool RequiresDocumentation { get; set; }
}

/// <summary>
/// Per-tenant changes to the default policy, bound from
/// <c>Claims:EditOverridePolicy:Tenants:{tenantId}:{category}</c>.
/// </summary>
public sealed class EditOverridePolicyOptions
{
    public const string SectionName = "Claims:EditOverridePolicy";

    public Dictionary<string, Dictionary<string, EditOverrideRuleOptions>> Tenants { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The override policy table (docs/architecture/claim-review-workbench.md
/// §2.3): which edits a reviewer may override, at what level, and with what
/// justification. Defaults follow CMS's own indicators; a tenant can change
/// any row except the federal floors, which stay <see cref="OverrideLevel.Never"/>
/// for Medicare and Medicaid claims.
/// </summary>
public sealed class EditOverridePolicy
{
    public const string NcciModifierNotAllowed = "NCCI_MI0";
    public const string NcciModifierAllowed = "NCCI_MI1";
    public const string NcciIndicatorUnknown = "NCCI_MI_UNKNOWN";
    public const string MueClaimLine = "MUE_MAI1";
    public const string MueDatePolicy = "MUE_MAI2";
    public const string MueDateClinical = "MUE_MAI3";
    public const string MueIndicatorUnknown = "MUE_MAI_UNKNOWN";
    public const string Duplicate = "DUPLICATE";
    public const string PlanEdit = "PLAN_EDIT";

    public static readonly IReadOnlyList<EditOverrideRule> Defaults =
    [
        new(NcciModifierNotAllowed, "NCCI pair, modifier indicator 0: no modifier makes the pair payable", OverrideLevel.Never, true, false),
        new(NcciModifierAllowed, "NCCI pair, modifier indicator 1: distinct procedure allowed with a modifier", OverrideLevel.Examiner, true, false),
        new(NcciIndicatorUnknown, "NCCI pair, modifier indicator not recorded", OverrideLevel.Supervisor, true, false),
        new(MueClaimLine, "MUE, MAI 1: claim line limit", OverrideLevel.Examiner, true, false),
        new(MueDatePolicy, "MUE, MAI 2: per day, policy (absolute)", OverrideLevel.Never, true, false),
        new(MueDateClinical, "MUE, MAI 3: per day, clinical", OverrideLevel.Supervisor, true, true),
        new(MueIndicatorUnknown, "MUE, adjudication indicator not recorded", OverrideLevel.Supervisor, true, true),
        new(Duplicate, "Duplicate claim or line", OverrideLevel.Examiner, true, false),
        new(PlanEdit, "The plan's own edit (its edit service)", OverrideLevel.Examiner, true, false),
    ];

    /// <summary>Rows that stay Never for Medicare and Medicaid whatever a tenant sets.</summary>
    private static readonly HashSet<string> FederalFloors = [NcciModifierNotAllowed, MueDatePolicy];

    private readonly IOptionsMonitor<EditOverridePolicyOptions> _options;

    public EditOverridePolicy(IOptionsMonitor<EditOverridePolicyOptions> options) => _options = options;

    /// <summary>The CHO defaults, with no tenant changes.</summary>
    public static EditOverridePolicy Default { get; } = new(new FixedOptions(new EditOverridePolicyOptions()));

    private sealed class FixedOptions(EditOverridePolicyOptions value) : IOptionsMonitor<EditOverridePolicyOptions>
    {
        public EditOverridePolicyOptions CurrentValue => value;
        public EditOverridePolicyOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<EditOverridePolicyOptions, string?> listener) => null;
    }

    public static string Category(NcciEditFailureSnapshot edit)
    {
        if (string.Equals(edit.EditType, "Mue", StringComparison.OrdinalIgnoreCase))
        {
            return edit.MueAdjudicationIndicator switch
            {
                1 => MueClaimLine,
                2 => MueDatePolicy,
                3 => MueDateClinical,
                _ => MueIndicatorUnknown,
            };
        }

        return edit.ModifierIndicator switch
        {
            0 => NcciModifierNotAllowed,
            1 => NcciModifierAllowed,
            _ => NcciIndicatorUnknown,
        };
    }

    public static string Category(DuplicateFindingSnapshot _) => Duplicate;

    public static string Category(PlanEditFindingSnapshot _) => PlanEdit;

    /// <summary>The effective policy for a tenant and line of business.</summary>
    public IReadOnlyList<EditOverrideRule> For(string tenantId, LineOfBusiness lineOfBusiness)
    {
        _options.CurrentValue.Tenants.TryGetValue(tenantId ?? string.Empty, out var tenantRules);
        var federal = lineOfBusiness is LineOfBusiness.Medicare or LineOfBusiness.Medicaid;

        return Defaults.Select(rule =>
        {
            var effective = tenantRules is not null && tenantRules.TryGetValue(rule.Category, out var o)
                ? rule with { Level = o.Level, RequiresReason = o.RequiresReason, RequiresDocumentation = o.RequiresDocumentation }
                : rule;
            return federal && FederalFloors.Contains(rule.Category)
                ? effective with { Level = OverrideLevel.Never }
                : effective;
        }).ToList();
    }

    public EditOverrideRule RuleFor(string tenantId, LineOfBusiness lineOfBusiness, string category) =>
        For(tenantId, lineOfBusiness).FirstOrDefault(r => r.Category == category)
        ?? new EditOverrideRule(category, category, OverrideLevel.Supervisor, true, false);
}
