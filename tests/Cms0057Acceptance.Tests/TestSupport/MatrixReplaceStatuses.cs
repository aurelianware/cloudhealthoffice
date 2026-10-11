namespace Cms0057Acceptance.Tests.TestSupport;

/// <summary>
/// Minimal reader for the Replace status table in <c>docs/epa-gap-matrix.md</c>
/// §11 (copied beside the test assembly). It reads only the scenario id and
/// status columns. The strict parser, with every failure case, is
/// <c>tools/Cms0057Evidence</c>'s <c>StatusMatrixParser</c>; this exists so the
/// acceptance suite can run a static drift check without referencing the
/// generator.
/// </summary>
internal static class MatrixReplaceStatuses
{
    private const string BeginMarker = "<!-- cms0057-evidence-status:begin -->";
    private const string EndMarker = "<!-- cms0057-evidence-status:end -->";

    public static string MatrixPath => Path.Combine(AppContext.BaseDirectory, "epa-gap-matrix.md");

    public static IReadOnlyDictionary<string, string> Load()
    {
        var lines = File.ReadAllLines(MatrixPath).Select(l => l.Trim()).ToList();
        var begin = lines.IndexOf(BeginMarker);
        var end = lines.IndexOf(EndMarker);
        if (begin < 0 || end < begin)
            throw new InvalidOperationException($"{MatrixPath}: §11 status table markers not found.");

        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(begin + 1).Take(end - begin - 1).Where(l => l.StartsWith('|')).Skip(2))
        {
            var cells = line.Split('|');
            var id = cells[1].Trim();
            if (!rows.TryAdd(id, cells[2].Trim()))
                throw new InvalidOperationException($"{MatrixPath}: duplicate status row for {id}.");
        }
        return rows;
    }
}
