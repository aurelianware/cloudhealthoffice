using CorrespondenceService.Models;

namespace CorrespondenceService.Services;

/// <summary>What a letter needs from its template: the case's context and the recipient's preferences.</summary>
public sealed record TemplateRequest(
    string LetterTypeCode,
    string Language,
    TemplateFormat Format,
    DateOnly OnDate,
    string? LineOfBusiness,
    string? State,
    string? Product,
    string? PlanId);

public sealed record TemplateResolution(LetterTemplate? Template, string? Error)
{
    public static TemplateResolution Found(LetterTemplate t) => new(t, null);
    public static TemplateResolution Failed(string error) => new(null, error);
}

/// <summary>
/// Picks the template version for a letter (§6 step 2): an approved version
/// of the letter type, in the recipient's language and format, effective on
/// the trigger date, whose scope matches the case. The most specific scope
/// wins (plan, then product, then state, then line of business); within a
/// template the latest effective version wins. No match, or two equally
/// specific templates, fails loudly: a notice is never silently skipped or
/// sent from a guessed template.
/// </summary>
public static class TemplateResolver
{
    public static TemplateResolution Resolve(IReadOnlyList<LetterTemplate> approved, TemplateRequest request)
    {
        var effective = approved
            .Where(t => t.Status == TemplateStatus.Approved
                        && t.EffectiveFrom <= request.OnDate
                        && (t.EffectiveTo is null || request.OnDate < t.EffectiveTo))
            .Where(t => Matches(t.Scope, request))
            .ToList();

        if (effective.Count == 0)
            return TemplateResolution.Failed(
                $"No approved {request.LetterTypeCode} template in language '{request.Language}' and format {request.Format} " +
                $"is effective on {request.OnDate:yyyy-MM-dd} for {Describe(request)}.");

        // Latest effective version of each template, then the most specific template.
        var current = effective
            .GroupBy(t => t.TemplateId)
            .Select(g => g.OrderByDescending(t => t.EffectiveFrom).ThenByDescending(t => t.Version).First())
            .OrderByDescending(t => Specificity(t.Scope))
            .ToList();

        if (current.Count > 1 && Specificity(current[0].Scope) == Specificity(current[1].Scope))
            return TemplateResolution.Failed(
                $"Templates {current[0].TemplateId} and {current[1].TemplateId} both apply to {request.LetterTypeCode} for {Describe(request)} " +
                "with the same scope; narrow one of them.");

        return TemplateResolution.Found(current[0]);
    }

    internal static bool Matches(TemplateScope scope, TemplateRequest r) =>
        Same(scope.PlanId, r.PlanId) && Same(scope.Product, r.Product) && Same(scope.State, r.State) && Same(scope.LineOfBusiness, r.LineOfBusiness);

    /// <summary>Plan outranks product, product outranks state, state outranks line of business.</summary>
    internal static int Specificity(TemplateScope s) =>
        (s.PlanId is null ? 0 : 8) + (s.Product is null ? 0 : 4) + (s.State is null ? 0 : 2) + (s.LineOfBusiness is null ? 0 : 1);

    private static bool Same(string? scoped, string? actual) =>
        scoped is null || string.Equals(scoped, actual, StringComparison.OrdinalIgnoreCase);

    private static string Describe(TemplateRequest r)
    {
        var parts = new List<string>();
        if (r.LineOfBusiness is not null) parts.Add($"line of business {r.LineOfBusiness}");
        if (r.State is not null) parts.Add($"state {r.State}");
        if (r.Product is not null) parts.Add($"product {r.Product}");
        if (r.PlanId is not null) parts.Add($"plan {r.PlanId}");
        return parts.Count == 0 ? "this tenant" : string.Join(", ", parts);
    }
}
