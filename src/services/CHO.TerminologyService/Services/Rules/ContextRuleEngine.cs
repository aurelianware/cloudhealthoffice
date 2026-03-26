using CHO.TerminologyService.Models;

namespace CHO.TerminologyService.Services.Rules;

/// <summary>
/// Rule engine that disambiguates one-to-many code mappings using patient context.
/// 
/// NLM's SNOMED-to-ICD-10-CM map includes age and gender rules for ~7,000+ concepts.
/// Example: SNOMED "Fracture of femur" maps to different ICD-10-CM codes based on age:
///   - Under 18: S72.001A (initial encounter, pediatric)
///   - Over 65: S72.001A with M80.* (pathological fracture, osteoporosis context)
/// 
/// Plan-specific rules (TMPPM, state Medicaid) are applied via the same mechanism.
/// </summary>
public class ContextRuleEngine : IContextRuleEngine
{
    private readonly ILogger<ContextRuleEngine> _logger;

    public ContextRuleEngine(ILogger<ContextRuleEngine> logger)
    {
        _logger = logger;
    }

    public List<ConceptMapEntry> ApplyRules(List<ConceptMapEntry> candidates, PatientContext? context)
    {
        if (candidates.Count == 0)
            return candidates;

        // If no context provided, return all candidates ordered by priority
        if (context == null)
        {
            return candidates.OrderBy(c => c.Priority).ToList();
        }

        // Separate entries with rules from those without
        var ruledEntries = candidates.Where(c => c.Rule != null).ToList();
        var unruledEntries = candidates.Where(c => c.Rule == null).ToList();

        // If no entries have rules, context doesn't help - return priority-ordered
        if (ruledEntries.Count == 0)
        {
            return candidates.OrderBy(c => c.Priority).ToList();
        }

        // Evaluate each ruled entry against context
        var matchedRuled = new List<(ConceptMapEntry Entry, int Score)>();
        foreach (var entry in ruledEntries)
        {
            var (matches, score) = EvaluateRule(entry.Rule!, context);
            if (matches)
            {
                matchedRuled.Add((entry, score));
            }
        }

        // Build result: matched ruled entries first (by score desc, then priority),
        // then unruled entries as fallback
        var result = new List<ConceptMapEntry>();

        result.AddRange(matchedRuled
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Entry.Priority)
            .Select(m => m.Entry));

        // Only include unruled entries if no ruled entries matched
        if (result.Count == 0)
        {
            result.AddRange(unruledEntries.OrderBy(c => c.Priority));
        }

        _logger.LogDebug("Rule engine: {CandidateCount} candidates, {RuledCount} with rules, " +
                         "{MatchedCount} matched context",
            candidates.Count, ruledEntries.Count, matchedRuled.Count);

        return result;
    }

    /// <summary>
    /// Evaluate a single rule against patient context.
    /// Returns (matches, score) where score indicates specificity of match.
    /// Higher score = more specific match = preferred.
    /// </summary>
    private (bool Matches, int Score) EvaluateRule(MapRule rule, PatientContext context)
    {
        var score = 0;
        var ruleType = rule.RuleType?.ToLowerInvariant() ?? "";

        switch (ruleType)
        {
            case "age":
                if (context.AgeInYears == null) return (false, 0);
                var ageMatch = true;
                if (rule.AgeMin.HasValue && context.AgeInYears < rule.AgeMin) ageMatch = false;
                if (rule.AgeMax.HasValue && context.AgeInYears > rule.AgeMax) ageMatch = false;
                if (!ageMatch) return (false, 0);
                // Narrower age ranges score higher
                var range = (rule.AgeMax ?? 150) - (rule.AgeMin ?? 0);
                score = 100 - Math.Min(range, 100);
                return (true, score);

            case "gender":
                if (string.IsNullOrEmpty(context.Gender)) return (false, 0);
                var genderMatch = string.Equals(rule.Gender, context.Gender, StringComparison.OrdinalIgnoreCase);
                return (genderMatch, genderMatch ? 50 : 0);

            case "comorbidity":
                if (context.ActiveConditions == null || context.ActiveConditions.Count == 0)
                    return (false, 0);
                if (rule.CoMorbidCodes == null || rule.CoMorbidCodes.Count == 0)
                    return (false, 0);
                var matchedConditions = rule.CoMorbidCodes
                    .Count(rc => context.ActiveConditions.Contains(rc));
                if (matchedConditions == 0) return (false, 0);
                // Score based on proportion of required conditions met
                score = (matchedConditions * 100) / rule.CoMorbidCodes.Count;
                return (true, score);

            case "statespecific":
                if (string.IsNullOrEmpty(context.StateCode)) return (false, 0);
                var stateMatch = string.Equals(rule.StateCode, context.StateCode, StringComparison.OrdinalIgnoreCase);
                return (stateMatch, stateMatch ? 75 : 0);

            case "composite":
                // Composite rules: evaluate the expression (future: FHIRPath)
                // For now, combine age + gender + state if all specified
                var compositeScore = 0;
                var anyFailed = false;

                if (rule.AgeMin.HasValue || rule.AgeMax.HasValue)
                {
                    if (context.AgeInYears == null) { anyFailed = true; }
                    else
                    {
                        if (rule.AgeMin.HasValue && context.AgeInYears < rule.AgeMin) anyFailed = true;
                        if (rule.AgeMax.HasValue && context.AgeInYears > rule.AgeMax) anyFailed = true;
                        if (!anyFailed) compositeScore += 40;
                    }
                }

                if (!string.IsNullOrEmpty(rule.Gender))
                {
                    if (string.IsNullOrEmpty(context.Gender)) { anyFailed = true; }
                    else if (!string.Equals(rule.Gender, context.Gender, StringComparison.OrdinalIgnoreCase))
                    { anyFailed = true; }
                    else { compositeScore += 30; }
                }

                if (!string.IsNullOrEmpty(rule.StateCode))
                {
                    if (string.IsNullOrEmpty(context.StateCode)) { anyFailed = true; }
                    else if (!string.Equals(rule.StateCode, context.StateCode, StringComparison.OrdinalIgnoreCase))
                    { anyFailed = true; }
                    else { compositeScore += 30; }
                }

                return (anyFailed ? false : true, compositeScore);

            default:
                _logger.LogWarning("Unknown rule type: {RuleType}", ruleType);
                return (false, 0);
        }
    }
}
