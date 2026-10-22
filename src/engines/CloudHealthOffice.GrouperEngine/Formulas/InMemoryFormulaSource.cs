using System.Collections.Frozen;

namespace CloudHealthOffice.GrouperEngine.Formulas;

/// <summary>
/// CMS's DRG logic table held in memory, one ranking per MDC.
/// </summary>
public sealed class InMemoryFormulaSource : IMsDrgFormulaSource
{
    private readonly FrozenDictionary<int, IReadOnlyList<MsDrgFormulaRow>> byMdc;

    /// <summary>
    /// Builds a source from rows in any order. Each MDC's rows are sorted by
    /// rank with a stable sort, so rows sharing a rank keep the order they
    /// arrived in — which is the order CMS's own sort leaves them in.
    /// </summary>
    public InMemoryFormulaSource(string version, IEnumerable<MsDrgFormulaRow> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(rows);

        Version = version;

        byMdc = rows
            .GroupBy(row => row.Mdc)
            .ToFrozenDictionary(
                group => group.Key,
                group => (IReadOnlyList<MsDrgFormulaRow>)
                    [.. group.OrderBy(row => row.Rank)]);
    }

    /// <inheritdoc/>
    public string Version { get; }

    /// <inheritdoc/>
    public IReadOnlyCollection<int> Mdcs => byMdc.Keys;

    /// <summary>How many rows the table holds in all.</summary>
    public int RowCount => byMdc.Values.Sum(rows => rows.Count);

    /// <inheritdoc/>
    public IReadOnlyList<MsDrgFormulaRow> ForMdc(int mdc) =>
        byMdc.TryGetValue(mdc, out var rows) ? rows : [];
}
