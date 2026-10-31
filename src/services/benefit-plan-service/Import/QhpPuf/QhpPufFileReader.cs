using System.Globalization;
using System.IO.Compression;
using CsvHelper;
using CsvHelper.Configuration;

namespace BenefitPlanService.Import.QhpPuf;

/// <summary>One CSV row from a QHP PUF, read by header name (case-insensitive).</summary>
public sealed class QhpRow
{
    private readonly Dictionary<string, string> _fields;

    public QhpRow(Dictionary<string, string> fields, int lineNumber)
    {
        _fields = fields;
        LineNumber = lineNumber;
    }

    public int LineNumber { get; }

    /// <summary>The trimmed value, or empty when the column is absent or blank.</summary>
    public string this[string column] =>
        _fields.TryGetValue(column, out var v) ? v.Trim() : string.Empty;

    public bool Has(string column) => _fields.ContainsKey(column);

    /// <summary>The first non-blank value among <paramref name="columns"/>.</summary>
    public string First(params string[] columns)
    {
        foreach (var c in columns)
        {
            var v = this[c];
            if (v.Length > 0) return v;
        }
        return string.Empty;
    }

    public bool IsYes(string column) =>
        string.Equals(this[column], "Yes", StringComparison.OrdinalIgnoreCase)
        || string.Equals(this[column], "Y", StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds a row from column/value pairs (tests and samples).</summary>
    public static QhpRow Of(params (string Column, string Value)[] values) =>
        new(values.ToDictionary(v => v.Column, v => v.Value, StringComparer.OrdinalIgnoreCase), 0);
}

/// <summary>A PUF file is missing a column the importer cannot work without.</summary>
public sealed class QhpPufFormatException(string message) : Exception(message);

/// <summary>
/// Streams the CMS Exchange PUF CSVs (Plan Attributes, Benefits and Cost
/// Sharing). Accepts the .zip CMS publishes as is, or the CSV inside it, so a
/// tenant admin can upload the download unchanged. The national Benefits and
/// Cost Sharing file is over a million rows, so rows are filtered while
/// reading and never held whole.
/// </summary>
public static class QhpPufFileReader
{
    public static readonly string[] PlanAttributesRequired =
        ["BusinessYear", "StateCode", "IssuerId", "StandardComponentId", "PlanId", "PlanMarketingName", "PlanType", "MarketCoverage"];

    public static readonly string[] BenefitsRequired =
        ["PlanId", "BenefitName", "CopayInnTier1", "CoinsInnTier1", "CopayOutofNet", "CoinsOutofNet", "IsCovered"];

    /// <summary>
    /// Opens <paramref name="content"/> as CSV. When it is a zip (by name or by
    /// its "PK" signature) the first .csv entry is read.
    /// </summary>
    public static Stream OpenCsv(Stream content, string? fileName)
    {
        var seekable = content;
        if (!content.CanSeek)
        {
            var copy = new MemoryStream();
            content.CopyTo(copy);
            copy.Position = 0;
            seekable = copy;
        }

        var isZip = fileName?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true;
        if (!isZip && seekable.Length >= 2)
        {
            var b0 = seekable.ReadByte();
            var b1 = seekable.ReadByte();
            seekable.Position = 0;
            isZip = b0 == 'P' && b1 == 'K';
        }
        if (!isZip) return seekable;

        var archive = new ZipArchive(seekable, ZipArchiveMode.Read, leaveOpen: false);
        var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            ?? throw new QhpPufFormatException($"The zip '{fileName}' has no .csv file in it.");
        return entry.Open();
    }

    /// <summary>
    /// Reads rows, keeping only those <paramref name="keep"/> accepts. Throws
    /// <see cref="QhpPufFormatException"/> when a required column is missing,
    /// which usually means the wrong file went in the wrong field.
    /// </summary>
    public static IEnumerable<QhpRow> Read(Stream csv, IReadOnlyList<string> requiredColumns, string fileLabel, Func<QhpRow, bool>? keep = null)
    {
        using var reader = new StreamReader(csv, detectEncodingFromByteOrderMarks: true);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            BadDataFound = null,
            MissingFieldFound = null,
            TrimOptions = TrimOptions.None,
            DetectColumnCountChanges = false,
        };
        using var parser = new CsvParser(reader, config);

        if (!parser.Read())
            throw new QhpPufFormatException($"The {fileLabel} file is empty.");
        var header = parser.Record ?? [];
        var names = header.Select(h => h.Trim().TrimStart('﻿')).ToArray();

        var missing = requiredColumns
            .Where(r => !names.Contains(r, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (missing.Count > 0)
            throw new QhpPufFormatException(
                $"The {fileLabel} file is missing column(s): {string.Join(", ", missing)}. Check that each file was uploaded in the right field.");

        while (parser.Read())
        {
            var record = parser.Record;
            if (record is null || record.All(string.IsNullOrWhiteSpace)) continue;

            var fields = new Dictionary<string, string>(names.Length, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < names.Length && i < record.Length; i++)
                fields[names[i]] = record[i];

            var row = new QhpRow(fields, parser.Row);
            if (keep is null || keep(row)) yield return row;
        }
    }
}
