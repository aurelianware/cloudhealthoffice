namespace PremiumBillingService.Rating;

/// <summary>
/// The rate tables of a tenant, looked up by plan and date. Every table is
/// validated on load, and two tables of one plan may not cover the same day.
/// </summary>
public sealed class RateTableCatalog
{
    private readonly Dictionary<string, List<RateTable>> _byPlan;

    public RateTableCatalog(IEnumerable<RateTable> tables)
    {
        _byPlan = new Dictionary<string, List<RateTable>>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            table.Validate();
            if (!_byPlan.TryGetValue(table.PlanId, out var list))
                _byPlan[table.PlanId] = list = new List<RateTable>();
            list.Add(table);
        }

        foreach (var (planId, list) in _byPlan)
        {
            list.Sort((a, b) => a.EffectiveFrom.CompareTo(b.EffectiveFrom));
            for (var i = 1; i < list.Count; i++)
            {
                var previous = list[i - 1];
                if (previous.EffectiveTo == null || previous.EffectiveTo.Value.Date >= list[i].EffectiveFrom.Date)
                    throw new RateTableValidationException(planId, new[]
                    {
                        $"rate periods overlap: {Describe(previous)} and {Describe(list[i])}"
                    });
            }
        }
    }

    /// <summary>The table of <paramref name="planId"/> in effect on <paramref name="date"/>.</summary>
    public RateTable Resolve(string planId, DateTime date)
    {
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
        table = _byPlan.TryGetValue(planId, out var list) ? list.FirstOrDefault(t => t.CoversDate(date)) : null;
        return table != null;
    }

    private static string Describe(RateTable t) =>
        $"{t.EffectiveFrom:yyyy-MM-dd}..{(t.EffectiveTo.HasValue ? t.EffectiveTo.Value.ToString("yyyy-MM-dd") : "open")}";
}
