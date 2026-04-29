using System.Text.Json.Serialization;

namespace CloudHealthOffice.BenefitEngine.Domain;

/// <summary>
/// Declarative gate that restricts when a benefit applies to a given
/// member encounter. All facets are optional — an unset facet is
/// "no opinion" and never blocks the benefit. A predicate evaluates to
/// true (benefit applies) only when every set facet matches the supplied
/// <see cref="BenefitRuleEvaluationContext"/>.
///
/// <para>
/// BP 5.4 introduced the predicate shape and its in-process evaluator;
/// BP 5.10 (Adjudication API Stabilization) consumes it from the
/// <c>BenefitCalculationEngine</c> hot path through
/// <c>IBenefitRuleGate</c>. The type lives in the engine domain so the
/// engine and the benefit-plan-service can share it without a
/// circular reference.
/// </para>
/// </summary>
public class BenefitRulePredicate
{
    /// <summary>Inclusive lower age bound, in years.</summary>
    [JsonPropertyName("memberAgeMin")]
    public int? MemberAgeMin { get; set; }

    /// <summary>Inclusive upper age bound, in years.</summary>
    [JsonPropertyName("memberAgeMax")]
    public int? MemberAgeMax { get; set; }

    /// <summary>Required member gender, when the benefit is sex-specific.</summary>
    [JsonPropertyName("memberGender")]
    public BenefitMemberGender? MemberGender { get; set; }

    /// <summary>
    /// ICD-10 codes that gate this benefit. When set and non-empty, the
    /// member encounter must include at least one of these diagnoses for
    /// the benefit to apply.
    /// </summary>
    [JsonPropertyName("requiredDiagnosisCodes")]
    public List<string>? RequiredDiagnosisCodes { get; set; }

    /// <summary>
    /// True when the benefit is only available if a qualifying related
    /// encounter exists in <see cref="RelatedEncounterLookbackDays"/>. The
    /// caller supplies the related-encounter source via the evaluation
    /// context.
    /// </summary>
    [JsonPropertyName("requiresRelatedEncounter")]
    public bool RequiresRelatedEncounter { get; set; }

    /// <summary>Lookback window in days for the related-encounter check.</summary>
    [JsonPropertyName("relatedEncounterLookbackDays")]
    public int? RelatedEncounterLookbackDays { get; set; }

    /// <summary>
    /// Deep-copy of the predicate. <see cref="RequiredDiagnosisCodes"/>
    /// gets its own list (strings are immutable, so element references are
    /// safe to share); every other field is a value type or string and
    /// copies cleanly. Used at the adapter seam so a mutation on one side
    /// can't bleed across.
    /// </summary>
    public BenefitRulePredicate Clone() => new()
    {
        MemberAgeMin = MemberAgeMin,
        MemberAgeMax = MemberAgeMax,
        MemberGender = MemberGender,
        RequiredDiagnosisCodes = RequiredDiagnosisCodes is null ? null : new List<string>(RequiredDiagnosisCodes),
        RequiresRelatedEncounter = RequiresRelatedEncounter,
        RelatedEncounterLookbackDays = RelatedEncounterLookbackDays,
    };

    /// <summary>
    /// Evaluate the predicate against <paramref name="context"/>. Returns
    /// true when every set facet matches; an unset facet contributes no
    /// constraint. A predicate with no facets at all evaluates to true.
    /// </summary>
    public bool Evaluate(BenefitRuleEvaluationContext context)
    {
        if (context is null)
        {
            // No context to evaluate against — fail closed, so the benefit does not apply.
            return false;
        }

        if (MemberAgeMin.HasValue && (!context.MemberAgeYears.HasValue || context.MemberAgeYears.Value < MemberAgeMin.Value))
        {
            return false;
        }

        if (MemberAgeMax.HasValue && (!context.MemberAgeYears.HasValue || context.MemberAgeYears.Value > MemberAgeMax.Value))
        {
            return false;
        }

        if (MemberGender.HasValue && MemberGender.Value != BenefitMemberGender.Any)
        {
            if (!context.MemberGender.HasValue) return false;
            if (context.MemberGender.Value != BenefitMemberGender.Any && context.MemberGender.Value != MemberGender.Value)
            {
                return false;
            }
        }

        if (RequiredDiagnosisCodes is { Count: > 0 } required)
        {
            var presented = context.DiagnosisCodes ?? Array.Empty<string>();
            var anyMatch = required.Any(req => presented.Any(p => string.Equals(p, req, StringComparison.OrdinalIgnoreCase)));
            if (!anyMatch) return false;
        }

        if (RequiresRelatedEncounter)
        {
            if (context.HasRelatedEncounter is null)
            {
                // Caller hasn't wired a related-encounter source; treat
                // as not satisfied rather than silently passing.
                return false;
            }

            var lookback = RelatedEncounterLookbackDays ?? 0;
            if (!context.HasRelatedEncounter(lookback))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Member-gender enum for <see cref="BenefitRulePredicate.MemberGender"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BenefitMemberGender
{
    Any = 0,
    Female = 1,
    Male = 2,
    NonBinary = 3,
}

/// <summary>
/// Inputs for <see cref="BenefitRulePredicate.Evaluate"/>. Constructed at
/// the call site (typically by the calculation engine) from member
/// demographics, the encounter under adjudication, and an optional related-
/// encounter source.
/// </summary>
public sealed class BenefitRuleEvaluationContext
{
    public int? MemberAgeYears { get; init; }
    public BenefitMemberGender? MemberGender { get; init; }
    public IReadOnlyCollection<string>? DiagnosisCodes { get; init; }

    /// <summary>
    /// Function that answers "does the member have a qualifying related
    /// encounter within <paramref name="lookbackDays"/>?" — supplied by
    /// callers that have access to encounter history. Null when the caller
    /// can't answer; predicates that need a related encounter then fail closed.
    /// </summary>
    public Func<int, bool>? HasRelatedEncounter { get; init; }
}
