namespace PremiumBillingService.Rating;

/// <summary>
/// The rate tables of a tenant, looked up by plan and date. Every table is
/// validated on load, two tables of one plan may not cover the same day, and a
/// plan's proration rule may change only on the first day of a month (so one
/// month is never charged under two rules).
/// </summary>
public sealed class RateTableCatalog
{
    private readonly Dictionary<string, List<RateTable>> _byPlan;
    private readonly Dictionary<string, string> _invalidPlans;

    /// <summary>Strict: any invalid table or conflict throws <see cref="RateTableValidationException"/>.</summary>
    public RateTableCatalog(IEnumerable<RateTable> tables) : this(tables, tolerant: false)
    {
    }

    private RateTableCatalog(IEnumerable<RateTable> tables, bool tolerant)
    {
        _byPlan = new Dictionary<string, List<RateTable>>(StringComparer.OrdinalIgnoreCase);
        _invalidPlans = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            try
            {
                table.Validate();
            }
            catch (RateTableValidationException ex) when (tolerant)
            {
                MarkInvalid(table.PlanId, ex.Message);
                continue;
            }
            if (!_byPlan.TryGetValue(table.PlanId, out var list))
                _byPlan[table.PlanId] = list = new List<RateTable>();
            list.Add(table);
        }

        foreach (var (planId, list) in _byPlan)
        {
            list.Sort((a, b) => a.EffectiveFrom.CompareTo(b.EffectiveFrom));
            var problem = Conflict(list);
            if (problem == null)
                continue;
            if (!tolerant)
                throw new RateTableValidationException(planId, new[] { problem });
            MarkInvalid(planId, $"Rate tables for plan '{planId}' conflict: {problem}");
        }
    }

    /// <summary>
    /// A catalog that does not throw for a bad table: the plans with an invalid
    /// table or conflicting tables are listed in <see cref="InvalidPlans"/>, and
    /// resolving any date of such a plan throws
    /// <see cref="RateTableValidationException"/>, so billing reports the plan
    /// instead of rating with a partial set of its tables.
    /// </summary>
    public static RateTableCatalog Tolerant(IEnumerable<RateTable> tables) => new(tables, tolerant: true);

    /// <summary>Plans that cannot be rated, with the reason (tolerant catalogs only).</summary>
    public IReadOnlyDictionary<string, string> InvalidPlans => _invalidPlans;

    /// <summary>The table of <paramref name="planId"/> in effect on <paramref name="date"/>.</summary>
    public RateTable Resolve(string planId, DateTime date)
    {
        ThrowIfInvalid(planId);
        if (_byPlan.TryGetValue(planId, out var list))
        {
            var table = list.FirstOrDefault(t => t.CoversDate(date));
            if (table != null)
                return table;
        }
        throw new RateTableNotFoundException(planId, date);
    }

    public bool TryResolve(string planId, DateTime date, out RateTable? table)
    {
        if (_invalidPlans.ContainsKey(planId))
        {
            table = null;
            return false;
        }
        table = _byPlan.TryGetValue(planId, out var list) ? list.FirstOrDefault(t => t.CoversDate(date)) : null;
        return table != null;
    }

    private void ThrowIfInvalid(string planId)
    {
        if (_invalidPlans.TryGetValue(planId, out var reason))
            throw new RateTableValidationException(planId, new[] { reason });
    }

    private void MarkInvalid(string planId, string reason)
    {
        _invalidPlans[planId] = _invalidPlans.TryGetValue(planId, out var earlier) ? $"{earlier}; {reason}" : reason;
        _byPlan.Remove(planId);
    }

    private static string? Conflict(List<RateTable> sorted)
    {
        for (var i = 1; i < sorted.Count; i++)
        {
            var previous = sorted[i - 1];
            var next = sorted[i];
            if (previous.EffectiveTo == null || previous.EffectiveTo.Value.Date >= next.EffectiveFrom.Date)
                return $"rate periods overlap: {Describe(previous)} and {Describe(next)}";
            if (previous.Proration != next.Proration && next.EffectiveFrom.Day != 1)
                return $"proration changes from {previous.Proration} to {next.Proration} on {next.EffectiveFrom:yyyy-MM-dd}; " +
                       "a plan's proration rule may change only on the first day of a month";
        }
        return null;
    }

    private static string Describe(RateTable t) =>
        $"{t.EffectiveFrom:yyyy-MM-dd}..{(t.EffectiveTo.HasValue ? t.EffectiveTo.Value.ToString("yyyy-MM-dd") : "open")}";
}
