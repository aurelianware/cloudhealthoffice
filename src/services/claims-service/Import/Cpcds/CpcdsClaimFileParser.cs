using CloudHealthOffice.Infrastructure.Import.Cpcds;

namespace ClaimsService.Import.Cpcds;

/// <summary>
/// Reads a CPCDS claim file into claims: resolves the header row, then groups
/// the data rows by "Claim unique identifier". It checks the file's shape
/// only; whether each claim's contents make sense is
/// <see cref="CpcdsClaimMapper"/>'s question.
/// </summary>
internal static class CpcdsClaimFileParser
{
    internal sealed class ParsedFile
    {
        public char Delimiter { get; init; }
        public int RowsRead { get; set; }
        public List<string> FileErrors { get; } = [];
        public List<string> UnrecognizedColumns { get; set; } = [];

        /// <summary>Claims in the order the file first mentions them.</summary>
        public List<CpcdsClaimRecord> Claims { get; } = [];

        /// <summary>Data rows with no claim identifier; they cannot belong to any claim.</summary>
        public List<int> OrphanRows { get; } = [];
    }

    public static ParsedFile Parse(string content, IReadOnlyDictionary<string, string>? columnMap)
    {
        var delimiter = CpcdsDelimitedReader.DetectDelimiter(content ?? string.Empty);
        var parsed = new ParsedFile { Delimiter = delimiter };

        using var records = CpcdsDelimitedReader.Read(content ?? string.Empty, delimiter).GetEnumerator();
        if (!records.MoveNext())
        {
            parsed.FileErrors.Add("The file is empty: a CPCDS claim file starts with a header row.");
            return parsed;
        }

        var (positions, unrecognized) = CpcdsClaimColumns.Resolve(records.Current.Fields, columnMap);
        parsed.UnrecognizedColumns = unrecognized;

        foreach (var required in CpcdsClaimColumns.Required)
        {
            if (!positions.ContainsKey(required))
            {
                parsed.FileErrors.Add(
                    $"Required column \"{CpcdsClaimColumns.DisplayName(required)}\" is missing. " +
                    "If the file names it differently, supply a column map.");
            }
        }
        if (parsed.FileErrors.Count > 0)
            return parsed;

        var byClaim = new Dictionary<string, CpcdsClaimRecord>(StringComparer.Ordinal);
        while (records.MoveNext())
        {
            var record = records.Current;
            parsed.RowsRead++;

            var values = new Dictionary<CpcdsClaimColumn, string>(positions.Count);
            foreach (var (column, index) in positions)
            {
                if (index < record.Fields.Count)
                    values[column] = record.Fields[index];
            }

            var row = new CpcdsRow(record.LineNumber, values);
            var claimId = row.Get(CpcdsClaimColumn.ClaimUniqueIdentifier);
            if (claimId is null)
            {
                parsed.OrphanRows.Add(record.LineNumber);
                continue;
            }

            if (!byClaim.TryGetValue(claimId, out var claim))
            {
                claim = new CpcdsClaimRecord { ClaimId = claimId };
                byClaim[claimId] = claim;
                parsed.Claims.Add(claim);
            }
            claim.Rows.Add(row);
        }

        return parsed;
    }
}
