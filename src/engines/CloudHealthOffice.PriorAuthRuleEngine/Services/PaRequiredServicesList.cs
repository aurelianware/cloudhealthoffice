using System.Text;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;

namespace CloudHealthOffice.PriorAuthRuleEngine.Services;

/// <summary>When a listed item or service needs prior authorization.</summary>
public enum PaRequirementCondition
{
    /// <summary>Always (optionally only at the listed places of service).</summary>
    Always,
    /// <summary>Only when billed with one of the listed diagnoses.</summary>
    WithDiagnosis,
    /// <summary>Only beyond the unit or visit limit for the benefit period.</summary>
    AboveQuantityLimit,
}

/// <summary>One line of the public list: a set of codes and when they need prior authorization.</summary>
public sealed record PaRequiredServiceEntry
{
    public required string RuleId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    /// <summary>True when the rule covers every item and service (no codes or ranges).</summary>
    public bool AllServices { get; init; }
    public IReadOnlyList<string> Codes { get; init; } = [];
    /// <summary>Code ranges by prefix, e.g. "K" for every K code.</summary>
    public IReadOnlyList<string> CodePrefixes { get; init; } = [];
    public required PaRequirementCondition Condition { get; init; }
    /// <summary>Empty = any place of service.</summary>
    public IReadOnlyList<string> PlacesOfService { get; init; } = [];
    public IReadOnlyList<string> Diagnoses { get; init; } = [];
    public int? UnitLimit { get; init; }
    public int? VisitLimit { get; init; }
    /// <summary>True when the rule denies outright rather than sending the request to clinical review.</summary>
    public bool DeniedWithoutReview { get; init; }
}

/// <summary>A rule that waives prior authorization for some requests (gold card, provider type, member age).</summary>
public sealed record PaRequirementExemption
{
    public required string RuleId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Kind { get; init; }
    public IReadOnlyList<string> Codes { get; init; } = [];
    public IReadOnlyList<string> CodePrefixes { get; init; } = [];
}

/// <summary>
/// CMS-0057-F list of items and services, excluding drugs, that require prior
/// authorization (42 CFR 422.122(c), 438.210(f), 440.230(e), 457.732(c),
/// 45 CFR 156.223(c)), derived from the PA rule set requests are actually
/// evaluated against (<see cref="PaRuleSetResolver"/>), so the published list
/// and the decisions cannot drift apart.
/// </summary>
public sealed record PaRequiredServicesList
{
    public required string StateCode { get; init; }
    public required PaLineOfBusiness LineOfBusiness { get; init; }
    public string? Program { get; init; }
    /// <summary>The rule set the list was built from (<see cref="RuleSetKey.AuditKey"/>); null when none is configured.</summary>
    public string? RuleSet { get; init; }
    public required DateTime GeneratedAt { get; init; }
    public IReadOnlyList<PaRequiredServiceEntry> Entries { get; init; } = [];
    public IReadOnlyList<PaRequirementExemption> Exemptions { get; init; } = [];
    /// <summary>Drug codes (HCPCS J codes) the rules name; the CMS-0057-F list excludes drugs.</summary>
    public IReadOnlyList<string> ExcludedDrugCodes { get; init; } = [];
    /// <summary>Enabled rules of a type this list does not know how to describe. Review before publishing.</summary>
    public IReadOnlyList<string> UnclassifiedRuleIds { get; init; } = [];

    private static readonly HashSet<string> ExemptionTypes = new(StringComparer.OrdinalIgnoreCase)
        { "TxGoldCardExemption", "ProviderTypeExemption", "MemberAgeLimit" };

    /// <summary>HCPCS Level II J codes are drugs administered other than orally.</summary>
    private static bool IsDrugCode(string code) => code.StartsWith('J') || code.StartsWith('j');

    /// <param name="includeDrugCodes">
    /// Keep drug codes (HCPCS J) on the entries. Off for the public list, which
    /// excludes drugs; claims adjudication turns it on, because a J code billed
    /// on a medical claim is still checked against the plan's PA rules.
    /// </param>
    public static PaRequiredServicesList Build(string stateCode, PaLineOfBusiness lob, string? program,
        RuleSetKey? resolvedKey, IReadOnlyList<PaRuleDocument> rules, DateTime generatedAt, bool includeDrugCodes = false)
    {
        var entries = new List<PaRequiredServiceEntry>();
        var exemptions = new List<PaRequirementExemption>();
        var drugs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var unclassified = new List<string>();

        foreach (var rule in rules.Where(r => r.IsEnabled))
        {
            var codes = Normalize(rule.ProcedureCodes);
            var prefixes = Normalize(rule.ProcedureCodePrefixes);
            var allServices = codes.Count == 0 && prefixes.Count == 0;
            foreach (var code in codes.Concat(prefixes).Where(IsDrugCode)) drugs.Add(code);
            if (!includeDrugCodes)
            {
                codes = codes.Where(c => !IsDrugCode(c)).ToList();
                prefixes = prefixes.Where(p => !IsDrugCode(p)).ToList();
            }
            // Every code the rule named was a drug: nothing of it belongs on the list.
            var onlyDrugs = !allServices && codes.Count == 0 && prefixes.Count == 0;

            if (ExemptionTypes.Contains(rule.RuleType))
            {
                if (!onlyDrugs)
                    exemptions.Add(new PaRequirementExemption
                    {
                        RuleId = rule.RuleId, Name = rule.RuleName, Description = rule.Description,
                        Kind = rule.RuleType, Codes = codes, CodePrefixes = prefixes,
                    });
                continue;
            }

            PaRequirementCondition condition;
            switch (rule.RuleType)
            {
                case "ProcedureRequiresAuth": condition = PaRequirementCondition.Always; break;
                case "DiagnosisRequired": condition = PaRequirementCondition.WithDiagnosis; break;
                case "QuantityLimit" when rule.UnitLimit.HasValue || rule.VisitLimit.HasValue:
                    condition = PaRequirementCondition.AboveQuantityLimit; break;
                // A quantity rule without limits never asks for review.
                case "QuantityLimit": continue;
                default: unclassified.Add(rule.RuleId); continue;
            }
            if (onlyDrugs) continue;

            entries.Add(new PaRequiredServiceEntry
            {
                RuleId = rule.RuleId, Name = rule.RuleName, Description = rule.Description,
                AllServices = allServices, Codes = codes, CodePrefixes = prefixes, Condition = condition,
                PlacesOfService = condition == PaRequirementCondition.Always ? Normalize(rule.PlaceOfServiceCodes) : [],
                Diagnoses = condition == PaRequirementCondition.WithDiagnosis ? Normalize(rule.RequiredDiagnosisCodes) : [],
                UnitLimit = condition == PaRequirementCondition.AboveQuantityLimit ? rule.UnitLimit : null,
                VisitLimit = condition == PaRequirementCondition.AboveQuantityLimit ? rule.VisitLimit : null,
                DeniedWithoutReview = rule.RuleType == "ProcedureRequiresAuth" && !string.IsNullOrEmpty(rule.DenialCode),
            });
        }

        return new PaRequiredServicesList
        {
            StateCode = stateCode, LineOfBusiness = lob, Program = program,
            RuleSet = resolvedKey?.AuditKey, GeneratedAt = generatedAt,
            Entries = entries, Exemptions = exemptions, ExcludedDrugCodes = drugs.ToList(),
            UnclassifiedRuleIds = unclassified,
        };
    }

    /// <summary>One row per entry, for posting as a file. Codes, ranges, places of service and diagnoses are space-separated.</summary>
    public string ToCsv()
    {
        var csv = new StringBuilder();
        csv.AppendLine("rule_id,name,codes,code_ranges,all_services,condition,places_of_service,diagnoses,unit_limit,visit_limit,denied_without_review");
        foreach (var e in Entries)
        {
            csv.AppendJoin(',', Field(e.RuleId), Field(e.Name), Field(string.Join(' ', e.Codes)),
                Field(string.Join(' ', e.CodePrefixes.Select(p => p + "*"))), e.AllServices ? "true" : "false",
                e.Condition.ToString(), Field(string.Join(' ', e.PlacesOfService)), Field(string.Join(' ', e.Diagnoses)),
                e.UnitLimit?.ToString() ?? "", e.VisitLimit?.ToString() ?? "", e.DeniedWithoutReview ? "true" : "false");
            csv.AppendLine();
        }
        return csv.ToString();
    }

    private static List<string> Normalize(IReadOnlyList<string> values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim().ToUpperInvariant())
            .Distinct().OrderBy(v => v, StringComparer.Ordinal).ToList();

    /// <summary>RFC 4180 quoting; a leading formula character is neutralised for spreadsheet safety.</summary>
    private static string Field(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
