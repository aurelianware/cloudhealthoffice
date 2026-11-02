using System.Globalization;

namespace PremiumBillingService.StateCapitation;

/// <summary>
/// One row of a state's rate cell crosswalk: the conditions under which a
/// member-month falls in <see cref="RateCell"/>. An empty condition matches
/// anything. Loaded by finance as CSV
/// (docs/integration/MEDICAID-EXPECTED-MEMBER-MONTHS.md).
/// </summary>
/// <param name="Field">
/// Which 834 value <see cref="Value"/> is compared with:
/// <c>category:&lt;N1*75 name&gt;</c> (a loop 2750 reporting category, by
/// name, case-insensitive), <c>hd04</c> (an HD line's plan coverage
/// description), or empty for no 834 condition.
/// </param>
public sealed record RateCellCrosswalkRow(
    string RateCell,
    string? PlanId,
    string? Field,
    string? Value,
    int? AgeMin,
    int? AgeMax,
    string? County,
    DateOnly? EffectiveFrom,
    DateOnly? EffectiveTo)
{
    public const string HdField = "hd04";
    public const string CategoryPrefix = "category:";
}

/// <summary>A member's coverage span in one of the plans being built (dates inclusive; null end is open).</summary>
public sealed record PlanCoverageSpan(string MemberId, string PlanId, DateOnly From, DateOnly? To);

/// <summary>What one 834 transaction said about the member, effective from <see cref="EffectiveFrom"/>.</summary>
public sealed record RateCellInputSnapshot(
    DateOnly EffectiveFrom,
    int Version,
    IReadOnlyList<(string? Name, string? Value)> Categories,
    IReadOnlyList<string> PlanCoverageDescriptions,
    DateOnly? DateOfBirth,
    string? County);

public enum MemberMonthBuildIssue
{
    /// <summary>No 834 transaction with rate cell inputs is in effect for the month.</summary>
    NoInputs = 1,

    /// <summary>No crosswalk row matches the member-month.</summary>
    NoMatch = 2,

    /// <summary>Rows naming different rate cells match the member-month.</summary>
    Ambiguous = 3,

    /// <summary>The member is covered by more than one of the plans in the month.</summary>
    MultiplePlans = 4,

    /// <summary>A matching row has an age band but the 834 sent no date of birth.</summary>
    NoDateOfBirth = 5,
}

public sealed record MemberMonthBuildException(string MemberId, DateOnly Month, string? PlanId, MemberMonthBuildIssue Issue, string Detail);

public sealed record ExpectedMemberMonthBuild(
    IReadOnlyList<ExpectedMemberMonth> MemberMonths,
    IReadOnlyList<MemberMonthBuildException> Exceptions);

/// <summary>
/// Builds expected capitation member-months from coverage spans, the rate
/// cell inputs the state's 834 sent, and the plan's crosswalk.
///
/// <para>
/// For each month, a member covered at any point by one of the plans has one
/// member-month. Its inputs are those of the latest 834 transaction effective
/// on or before the first day the member is covered that month (by effective
/// date, then event version). Age is whole years on that day. A member-month
/// that matches no crosswalk row, or rows naming more than one rate cell, is
/// an exception and is not expected: CHO never guesses a rate cell.
/// </para>
/// </summary>
public static class ExpectedMemberMonthBuilder
{
    public static ExpectedMemberMonthBuild Build(
        DateOnly fromMonth,
        DateOnly toMonth,
        IEnumerable<PlanCoverageSpan> coverage,
        IReadOnlyDictionary<string, IReadOnlyList<RateCellInputSnapshot>> inputs,
        IReadOnlyList<RateCellCrosswalkRow> crosswalk)
    {
        var months = new List<ExpectedMemberMonth>();
        var exceptions = new List<MemberMonthBuildException>();
        var byMember = coverage.GroupBy(c => c.MemberId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var member in byMember)
        {
            inputs.TryGetValue(member.Key, out var history);
            for (var month = First(fromMonth); month <= First(toMonth); month = month.AddMonths(1))
            {
                var end = month.AddMonths(1).AddDays(-1);
                var spans = member
                    .Select(s => (s.PlanId, From: Max(s.From, month), To: Min(s.To ?? end, end)))
                    .Where(s => s.From <= s.To)
                    .ToList();
                if (spans.Count == 0) continue;

                var plans = spans.Select(s => s.PlanId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (plans.Count > 1)
                {
                    exceptions.Add(new(member.Key, month, null, MemberMonthBuildIssue.MultiplePlans,
                        $"Covered by {string.Join(", ", plans.Order(StringComparer.Ordinal))} in the month"));
                    continue;
                }

                var plan = plans[0];
                var firstDay = spans.Min(s => s.From);
                var days = CoveredDays(spans.Select(s => (s.From, s.To)));
                var snapshot = history?
                    .Where(s => s.EffectiveFrom <= firstDay)
                    .OrderBy(s => s.EffectiveFrom).ThenBy(s => s.Version)
                    .LastOrDefault();
                if (snapshot is null)
                {
                    exceptions.Add(new(member.Key, month, plan, MemberMonthBuildIssue.NoInputs,
                        $"No 834 rate cell inputs on or before {firstDay:yyyy-MM-dd}"));
                    continue;
                }

                var age = snapshot.DateOfBirth is { } dob ? Age(dob, firstDay) : (int?)null;
                var candidates = crosswalk.Where(r => Applies(r, month, plan, snapshot)).ToList();
                if (candidates.Count > 0 && age is null && candidates.All(r => r.AgeMin is not null || r.AgeMax is not null))
                {
                    exceptions.Add(new(member.Key, month, plan, MemberMonthBuildIssue.NoDateOfBirth,
                        "Every matching crosswalk row has an age band and the 834 sent no date of birth"));
                    continue;
                }
                var cells = candidates.Where(r => InAgeBand(r, age))
                    .Select(r => r.RateCell).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (cells.Count == 0)
                {
                    exceptions.Add(new(member.Key, month, plan, MemberMonthBuildIssue.NoMatch, Describe(snapshot, age)));
                    continue;
                }
                if (cells.Count > 1)
                {
                    exceptions.Add(new(member.Key, month, plan, MemberMonthBuildIssue.Ambiguous,
                        $"Rate cells {string.Join(", ", cells.Order(StringComparer.Ordinal))} all match: {Describe(snapshot, age)}"));
                    continue;
                }
                months.Add(new ExpectedMemberMonth(member.Key, month, cells[0], days));
            }
        }
        return new ExpectedMemberMonthBuild(months, exceptions);
    }

    private static bool Applies(RateCellCrosswalkRow r, DateOnly month, string plan, RateCellInputSnapshot s)
    {
        if (r.EffectiveFrom is { } from && month < First(from)) return false;
        if (r.EffectiveTo is { } to && month > First(to)) return false;
        if (!string.IsNullOrEmpty(r.PlanId) && !string.Equals(r.PlanId, plan, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(r.County) && !string.Equals(r.County, s.County, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrEmpty(r.Field)) return true;
        if (string.Equals(r.Field, RateCellCrosswalkRow.HdField, StringComparison.OrdinalIgnoreCase))
            return s.PlanCoverageDescriptions.Any(d => string.Equals(d, r.Value, StringComparison.OrdinalIgnoreCase));
        if (r.Field.StartsWith(RateCellCrosswalkRow.CategoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = r.Field[RateCellCrosswalkRow.CategoryPrefix.Length..].Trim();
            return s.Categories.Any(c => string.Equals(c.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase)
                                         && string.Equals(c.Value, r.Value, StringComparison.OrdinalIgnoreCase));
        }
        return false;
    }

    private static bool InAgeBand(RateCellCrosswalkRow r, int? age) =>
        (r.AgeMin is null && r.AgeMax is null)
        || (age is { } a && (r.AgeMin is null || a >= r.AgeMin) && (r.AgeMax is null || a <= r.AgeMax));

    private static string Describe(RateCellInputSnapshot s, int? age)
    {
        var parts = s.Categories.Select(c => $"{c.Name}={c.Value}")
            .Concat(s.PlanCoverageDescriptions.Select(d => $"hd04={d}"))
            .ToList();
        if (age is not null) parts.Add($"age={age}");
        if (!string.IsNullOrEmpty(s.County)) parts.Add($"county={s.County}");
        return parts.Count == 0 ? "no inputs" : string.Join("; ", parts);
    }

    public static int Age(DateOnly dob, DateOnly on)
    {
        var age = on.Year - dob.Year;
        if (on < dob.AddYears(age)) age--;
        return age;
    }

    private static int CoveredDays(IEnumerable<(DateOnly From, DateOnly To)> spans)
    {
        var days = new HashSet<DateOnly>();
        foreach (var (from, to) in spans)
            for (var d = from; d <= to; d = d.AddDays(1)) days.Add(d);
        return days.Count;
    }

    private static DateOnly First(DateOnly d) => new(d.Year, d.Month, 1);
    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

    /// <summary>A CCYYMMDD date as the 834 sends it, or null.</summary>
    public static DateOnly? X12Date(string? text) =>
        DateOnly.TryParseExact(text?.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
