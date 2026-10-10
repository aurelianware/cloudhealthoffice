using System.Text;
using System.Text.RegularExpressions;

namespace Cms0057Evidence;

public sealed class StatusMatrixException : Exception
{
    public StatusMatrixException(string message) : base(message) { }
}

/// <summary>One row of the status table: the Cloud Health Office (Replace)
/// status of a scenario, with the matrix references and justification.</summary>
public sealed record StatusMatrixRow(string ScenarioId, string Status, string MatrixRefs, string Justification, int LineNumber);

/// <summary>The parsed status table, keyed by scenario id.</summary>
public sealed class StatusMatrix
{
    public StatusMatrix(string source, IReadOnlyDictionary<string, StatusMatrixRow> rows)
    {
        Source = source;
        Rows = rows;
    }

    /// <summary>Where the table was read from (for error messages).</summary>
    public string Source { get; }

    public IReadOnlyDictionary<string, StatusMatrixRow> Rows { get; }
}

/// <summary>
/// Reads the evidence scenario status table from docs/epa-gap-matrix.md (§11).
/// The table sits between two marker comments so the rest of the document can
/// change freely. Parsing is strict: anything the generator cannot interpret
/// unambiguously fails the build with a message naming the line.
/// </summary>
public static class StatusMatrixParser
{
    public const string DefaultPath = "docs/epa-gap-matrix.md";
    public const string BeginMarker = "<!-- cms0057-evidence-status:begin -->";
    public const string EndMarker = "<!-- cms0057-evidence-status:end -->";

    private static readonly string[] ExpectedHeader = { "Scenario", "Status", "Matrix refs", "Justification" };
    private static readonly Regex SeparatorCell = new(@"^:?-{3,}:?$", RegexOptions.CultureInvariant);
    private static readonly Regex ScenarioId = new(@"^[A-Z0-9]+(-[A-Z0-9]+)*$", RegexOptions.CultureInvariant);

    public static StatusMatrix Load(string path)
    {
        if (!File.Exists(path))
            throw new StatusMatrixException($"Status matrix not found: {path}");
        return Parse(File.ReadAllText(path), path);
    }

    public static StatusMatrix Parse(string markdown, string source)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');

        var begins = IndexesOf(lines, BeginMarker);
        var ends = IndexesOf(lines, EndMarker);
        if (begins.Count == 0)
            throw new StatusMatrixException($"{source}: status table begin marker '{BeginMarker}' not found.");
        if (begins.Count > 1)
            throw new StatusMatrixException(
                $"{source}: status table begin marker appears {begins.Count} times (lines {string.Join(", ", begins.Select(i => i + 1))}); expected exactly one.");
        if (ends.Count == 0)
            throw new StatusMatrixException($"{source}: status table end marker '{EndMarker}' not found.");
        if (ends.Count > 1)
            throw new StatusMatrixException(
                $"{source}: status table end marker appears {ends.Count} times (lines {string.Join(", ", ends.Select(i => i + 1))}); expected exactly one.");
        var begin = begins[0];
        var end = ends[0];
        if (end < begin)
            throw new StatusMatrixException($"{source}: status table end marker (line {end + 1}) comes before the begin marker (line {begin + 1}).");

        // Collect the table lines; only blank lines may sit between the markers otherwise.
        var tableLines = new List<(int LineNumber, string Text)>();
        for (var i = begin + 1; i < end; i++)
        {
            var text = lines[i].Trim();
            if (text.Length == 0) continue;
            if (!text.StartsWith('|'))
                throw new StatusMatrixException(
                    $"{source}:{i + 1}: unexpected non-table line inside the status table markers: '{text}'.");
            tableLines.Add((i + 1, text));
        }

        if (tableLines.Count < 2)
            throw new StatusMatrixException($"{source}:{begin + 1}: status table has no header and separator rows.");

        var (headerLine, headerText) = tableLines[0];
        var header = SplitRow(headerText, source, headerLine);
        if (header.Count != ExpectedHeader.Length
            || !header.Zip(ExpectedHeader).All(p => string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase)))
            throw new StatusMatrixException(
                $"{source}:{headerLine}: status table header must be '| {string.Join(" | ", ExpectedHeader)} |' (was '{headerText}').");

        var (sepLine, sepText) = tableLines[1];
        var sep = SplitRow(sepText, source, sepLine);
        if (sep.Count != ExpectedHeader.Length || !sep.All(c => SeparatorCell.IsMatch(c)))
            throw new StatusMatrixException($"{source}:{sepLine}: expected a table separator row (was '{sepText}').");

        var rows = new Dictionary<string, StatusMatrixRow>(StringComparer.Ordinal);
        foreach (var (lineNumber, text) in tableLines.Skip(2))
        {
            var cells = SplitRow(text, source, lineNumber);
            if (cells.Count != ExpectedHeader.Length)
                throw new StatusMatrixException(
                    $"{source}:{lineNumber}: expected {ExpectedHeader.Length} cells, found {cells.Count} (write a literal pipe as '\\|').");

            var (id, status, refs, justification) = (cells[0], cells[1], cells[2], cells[3]);

            if (!ScenarioId.IsMatch(id))
                throw new StatusMatrixException($"{source}:{lineNumber}: '{id}' is not a valid scenario id.");
            if (!Status.All.Contains(status))
                throw new StatusMatrixException(
                    $"{source}:{lineNumber}: scenario {id} status '{status}' is not a valid status "
                    + $"({Status.Passable}, {Status.Partial}, {Status.Gap}, {Status.NotApplicable}).");
            if (refs.Length == 0)
                throw new StatusMatrixException($"{source}:{lineNumber}: scenario {id} has no matrix refs.");
            if (justification.Length == 0)
                throw new StatusMatrixException($"{source}:{lineNumber}: scenario {id} has no justification.");
            if (rows.TryGetValue(id, out var existing))
                throw new StatusMatrixException(
                    $"{source}:{lineNumber}: duplicate row for scenario {id} (first at line {existing.LineNumber}).");

            rows[id] = new StatusMatrixRow(id, status, refs, justification, lineNumber);
        }

        if (rows.Count == 0)
            throw new StatusMatrixException($"{source}:{begin + 1}: status table has no scenario rows.");

        return new StatusMatrix(source, rows);
    }

    private static List<int> IndexesOf(string[] lines, string marker) =>
        lines.Select((l, i) => (l, i)).Where(x => x.l.Trim() == marker).Select(x => x.i).ToList();

    /// <summary>Split a Markdown table row on unescaped pipes. The row must
    /// start and end with a pipe; '\|' is a literal pipe inside a cell.</summary>
    private static List<string> SplitRow(string row, string source, int lineNumber)
    {
        if (row.Length < 2 || !row.StartsWith('|') || !row.EndsWith('|') || row.EndsWith("\\|", StringComparison.Ordinal))
            throw new StatusMatrixException($"{source}:{lineNumber}: table row must start and end with '|'.");

        var cells = new List<string>();
        var current = new StringBuilder();
        for (var i = 1; i < row.Length; i++)
        {
            var c = row[i];
            if (c == '\\' && i + 1 < row.Length && row[i + 1] == '|')
            {
                current.Append('|');
                i++;
            }
            else if (c == '|')
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        return cells;
    }
}

/// <summary>
/// Applies the status matrix to the manifest. The matrix is the only source of
/// the Replace status: every manifest scenario must have exactly one row, and
/// every row must name a manifest scenario.
/// </summary>
public static class StatusMatrixResolver
{
    public static ManifestDocument Apply(ManifestDocument manifest, StatusMatrix matrix)
    {
        var manifestIds = manifest.Scenarios.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

        var missing = manifest.Scenarios.Select(s => s.Id)
            .Where(id => !matrix.Rows.ContainsKey(id))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
            throw new StatusMatrixException(
                $"{matrix.Source}: no status row for manifest scenario(s): {string.Join(", ", missing)}.");

        var unknown = matrix.Rows.Keys
            .Where(id => !manifestIds.Contains(id))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
            throw new StatusMatrixException(
                $"{matrix.Source}: status row(s) for scenario(s) not in the manifest: {string.Join(", ", unknown)}.");

        return new ManifestDocument
        {
            SchemaVersion = manifest.SchemaVersion,
            Scenarios = manifest.Scenarios.Select(s =>
            {
                var row = matrix.Rows[s.Id];
                return new ManifestScenario
                {
                    Id = s.Id,
                    Name = s.Name,
                    Capability = s.Capability,
                    Replace = new ManifestBackend
                    {
                        Status = row.Status,
                        Rationale = $"{row.Justification} (matrix refs: {row.MatrixRefs})",
                    },
                    Augment = s.Augment,
                };
            }).ToList(),
        };
    }
}
