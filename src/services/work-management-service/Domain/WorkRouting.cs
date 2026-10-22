using WorkManagementService.Models;

namespace WorkManagementService.Domain;

/// <summary>
/// Routing: rules on the work item's attributes, never on people. The item
/// lands in exactly one queue: the first active queue of its work type by
/// <see cref="WorkQueue.Order"/> (ties by name, then id, so routing is
/// deterministic) whose criteria match; else the work type's active fallback.
/// </summary>
public static class WorkRouter
{
    public static WorkQueue? Route(WorkType type, WorkItemAttributes attributes, IEnumerable<WorkQueue> queues)
    {
        var candidates = Ordered(queues.Where(q => q.IsActive && q.WorkType == type)).ToList();
        return candidates.FirstOrDefault(q => !q.IsFallback && Matches(type, q, attributes))
               ?? candidates.FirstOrDefault(q => q.IsFallback);
    }

    public static IEnumerable<WorkQueue> Ordered(IEnumerable<WorkQueue> queues) =>
        queues.OrderBy(q => q.Order).ThenBy(q => q.Name, StringComparer.Ordinal).ThenBy(q => q.Id, StringComparer.Ordinal);

    public static bool Matches(WorkType type, WorkQueue queue, WorkItemAttributes a) => type switch
    {
        // Claim criteria are the only ones phase 1 defines. Enrollment queues
        // reuse them: the reason code travels as the item's pend code. Other
        // work types' queues route by order alone until their criteria land.
        WorkType.Claim or WorkType.Enrollment834 => MatchesClaim(queue.Criteria, a),
        _ => true,
    };

    public static bool MatchesClaim(ClaimQueueCriteria c, WorkItemAttributes a)
    {
        if (c.PendCodes.Count > 0)
        {
            var wanted = new HashSet<string>(c.PendCodes.Select(Norm), StringComparer.OrdinalIgnoreCase);
            var hit = c.MatchAnyPendCode
                ? a.PendCodes.Any(p => wanted.Contains(Norm(p))) || (a.PrimaryPendCode is { } pp && wanted.Contains(Norm(pp)))
                : a.PrimaryPendCode is { } primary && wanted.Contains(Norm(primary));
            if (!hit) return false;
        }

        if (!AnyOf(c.LinesOfBusiness, a.LineOfBusiness)) return false;
        if (!AnyOf(c.ClaimTypes, a.ClaimType)) return false;
        if (c.MinAmount is { } min && a.Amount < min) return false;
        if (c.MaxAmount is { } max && a.Amount > max) return false;
        if (!AnyOf(c.ProviderNpis, a.ProviderNpi)) return false;
        if (!AnyOf(c.ProviderTins, a.ProviderTin)) return false;
        if (!AnyOf(c.NetworkTiers, a.NetworkTier)) return false;
        if (c.Expedited is { } expedited && a.Expedited != expedited) return false;
        return true;
    }

    private static bool AnyOf(List<string> allowed, string? value) =>
        allowed.Count == 0
        || (value is not null && allowed.Any(v => string.Equals(Norm(v), Norm(value), StringComparison.OrdinalIgnoreCase)));

    private static string Norm(string s) => s.Trim();
}

/// <summary>
/// The one priority scale for every work type: deadline-driven, plus age,
/// amount and expedited, each weighted per queue (<see cref="PriorityWeights"/>).
///
/// <code>
/// deadline  = due ≤ now ? 150 + min(50, 10 × days overdue)
///                       : 100 × max(0, 1 − days to due ÷ SLA days)
/// age       = min(100, 2 × days since received)
/// amount    = min(100, 20 × log10(1 + amount))        ($10k ≈ 80, $100k = 100)
/// expedited = 100 if expedited else 0
/// score     = Σ weight × points  (rounded to 2 places)
/// </code>
/// </summary>
public static class PriorityScorer
{
    public sealed record Result(double Score, string Reason, DateTime DueAt);

    private static string Inv(FormattableString s) => FormattableString.Invariant(s);

    public static DateTime DueAt(WorkItemAttributes a, WorkQueue queue, DateTime createdAt)
    {
        if (a.Deadline is { } explicitDeadline) return explicitDeadline;
        var start = a.ReceivedDate ?? createdAt;
        return start.AddDays(Math.Max(queue.SlaDays, 1));
    }

    public static Result Score(WorkItemAttributes a, WorkQueue queue, DateTime createdAt, DateTime now)
    {
        var w = queue.Weights;
        var due = DueAt(a, queue, createdAt);
        var sla = Math.Max(queue.SlaDays, 1);
        var daysToDue = (due - now).TotalDays;
        var deadline = daysToDue <= 0
            ? 150 + Math.Min(50, -daysToDue * 10)
            : 100 * Math.Max(0, 1 - daysToDue / sla);
        var ageDays = Math.Max(0, (now - (a.ReceivedDate ?? createdAt)).TotalDays);
        var age = Math.Min(100, ageDays * 2);
        var amount = Math.Min(100, 20 * Math.Log10(1 + (double)Math.Max(0m, a.Amount)));
        var expedited = a.Expedited ? 100 : 0;

        var score = Math.Round(w.Deadline * deadline + w.Age * age + w.Amount * amount + w.Expedited * expedited, 2);

        var parts = new List<string>
        {
            daysToDue <= 0 ? Inv($"overdue {-daysToDue:0.#}d") : Inv($"due in {daysToDue:0.#}d"),
            Inv($"age {ageDays:0.#}d"),
            Inv($"${a.Amount:N0}"),
        };
        if (a.Expedited) parts.Add("expedited");
        var reason = Inv($"{score:0.##} = deadline {deadline:0}×{w.Deadline:0.##} + age {age:0}×{w.Age:0.##} + amount {amount:0}×{w.Amount:0.##}")
                     + (a.Expedited ? Inv($" + expedited 100×{w.Expedited:0.##}") : string.Empty)
                     + $" ({string.Join(", ", parts)})";
        return new Result(score, reason, due);
    }
}
