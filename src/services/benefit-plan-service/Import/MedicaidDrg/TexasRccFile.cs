using System.Globalization;
using System.Text.RegularExpressions;

namespace BenefitPlanService.Import.MedicaidDrg;

// ═══════════════════════════════════════════════════════════════════
// TEXAS INPATIENT RATIO OF COST TO CHARGES (RCC) FILES
//
// HHSC Provider Finance publishes the inpatient interim rates TMHP sets for
// each hospital as a monthly PDF, "{yyyy}-{m}-inp-rcc.pdf", titled
// "Inpatient Ratio of Cost to Charges Effective MM/DD/YYYY". One row per
// TPI: TPI, NPI, provider name, street, city/state/ZIP, county, the date the
// hospital's rate took effect, the rate as a whole percentage, and the
// hospital class.
//
// Two things in the published files need care:
//
//   * Some rows print the rate effective date as an Excel date serial
//     formatted as a percentage ("4626600%" is serial 46266, 09/01/2026).
//     The reader turns those back into dates.
//   * One NPI can bill under several TPIs (one per site). When the TPIs of
//     one NPI carry different rates, pricing (which finds the hospital by
//     NPI) cannot tell which applies, so that NPI is held back and reported
//     rather than loaded with a guess.
// ═══════════════════════════════════════════════════════════════════

/// <summary>One TPI's row in an RCC file.</summary>
public sealed record TexasRccRow(
    int Page, string Tpi, string Npi, string ProviderName, string? County,
    DateTime RateEffectiveDate, decimal Rate, string HospitalClass);

/// <summary>An NPI whose TPIs disagree, held back from the import.</summary>
public sealed record TexasRccHeldBack(string Npi, string ProviderName, string Problem);

/// <summary>A parsed RCC file.</summary>
public sealed record TexasRccFile
{
    /// <summary>The file's "Effective" date from the title.</summary>
    public required DateTime FileEffectiveDate { get; init; }

    /// <summary>Every row as published.</summary>
    public required IReadOnlyList<TexasRccRow> Rows { get; init; }

    /// <summary>
    /// One rate per NPI and rate effective date, ready to store: identical
    /// TPI rows collapse, and NPIs whose TPIs disagree are left out.
    /// </summary>
    public required IReadOnlyList<TexasRccRow> Rates { get; init; }

    public required IReadOnlyList<TexasRccHeldBack> HeldBack { get; init; }

    /// <summary>How many rows printed their effective date as an Excel serial (as a percentage or bare).</summary>
    public int SerialDatesRepaired { get; init; }
}

public static partial class TexasRccFileReader
{
    /// <summary>Whether the PDF's first page carries the RCC title.</summary>
    public static bool LooksLikeRcc(IEnumerable<(int Page, IReadOnlyList<string> Segments)> lines) =>
        lines.TakeWhile(l => l.Page == 1).Any(l => Title().IsMatch(string.Join(' ', l.Segments)));

    /// <summary>Reads an RCC PDF as published.</summary>
    public static TexasRccFile Read(Stream pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        IEnumerable<(int, IReadOnlyList<string>)> lines;
        try
        {
            lines = PdfLines(pdf);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InterimRateFormatException($"Could not read the upload as a PDF: {ex.Message}");
        }
        return Parse(lines);
    }

    /// <summary>Parses the text lines (column segments per line, with page numbers).</summary>
    public static TexasRccFile Parse(IEnumerable<(int Page, IReadOnlyList<string> Segments)> lines)
    {
        DateTime? fileDate = null;
        var rows = new List<TexasRccRow>();
        var errors = new List<string>();
        var repaired = 0;

        foreach (var (page, segments) in lines)
        {
            var text = string.Join(' ', segments).Trim();
            if (Title().Match(text) is { Success: true } t)
            {
                var date = DateTime.ParseExact(t.Groups[1].Value, "MM/dd/yyyy", CultureInfo.InvariantCulture);
                if (fileDate is { } prior && prior != date)
                    throw new InterimRateFormatException($"Page {page} is titled effective {date:MM/dd/yyyy} but page 1 says {prior:MM/dd/yyyy}.");
                fileDate = date;
                continue;
            }

            if (!LeadingIds().IsMatch(text)) continue;

            // The PDF's text layer splits numbers into single characters
            // ("0 8 / 0 1 / 2 0 2 6 9 8 %"), so spaces between digits, slashes
            // and percent signs are dropped before the date and rate are read.
            // The leading "TPI NPI " (21 characters) is kept as is so the two
            // identifiers are not run together.
            var normalized = text[..21] + DigitRuns().Replace(text[21..], "");
            var m = RowShape().Match(normalized);
            if (!m.Success)
            {
                errors.Add($"Page {page}: '{(text.Length > 90 ? text[..90] + "…" : text)}' is not TPI, NPI, name, address, county, rate effective date, rate and class.");
                continue;
            }

            var dateText = m.Groups["date"].Value;
            DateTime effective;
            if (dateText.EndsWith('%') || !dateText.Contains('/'))
            {
                // Excel printed the date cell as a percentage ("4626600%") or
                // as a bare serial ("41072", which the PDF lays out after the rate).
                var serial = dateText.EndsWith('%')
                    ? decimal.Parse(dateText[..^1], CultureInfo.InvariantCulture) / 100m
                    : decimal.Parse(dateText, CultureInfo.InvariantCulture);
                if (serial != decimal.Truncate(serial) || serial < 36526 || serial > 73050)
                {
                    errors.Add($"Page {page}, TPI {m.Groups["tpi"].Value}: rate effective date '{dateText}' is not a date.");
                    continue;
                }
                effective = new DateTime(1899, 12, 30).AddDays((double)serial);
                repaired++;
            }
            else if (!DateTime.TryParseExact(dateText, "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out effective))
            {
                errors.Add($"Page {page}, TPI {m.Groups["tpi"].Value}: rate effective date '{dateText}' is not a date.");
                continue;
            }

            var rate = decimal.Parse(m.Groups["rate"].Value, CultureInfo.InvariantCulture) / 100m;
            if (rate <= 0m || rate > 2m)
            {
                errors.Add($"Page {page}, TPI {m.Groups["tpi"].Value}: rate {m.Groups["rate"].Value}% is outside 0-200%.");
                continue;
            }

            // Segments: TPI, then NPI and name (often one segment), street,
            // city/state/ZIP, county, date and rate, class.
            var name = NameAfterNpi(segments, m.Groups["npi"].Value);
            var county = m.Groups["county"].Success ? m.Groups["county"].Value.Trim() : null;

            rows.Add(new TexasRccRow(page, m.Groups["tpi"].Value, m.Groups["npi"].Value, name, county,
                effective, rate, m.Groups["cls"].Value.Trim()));
        }

        if (fileDate is null)
            throw new InterimRateFormatException("This is not a Texas RCC file: no 'Inpatient Ratio of Cost to Charges Effective MM/DD/YYYY' title was found.");
        if (errors.Count > 0)
            throw new InterimRateFormatException($"{errors.Count} row(s) could not be read; nothing was loaded. " + string.Join(" ", errors.Take(10)));
        if (rows.Count == 0)
            throw new InterimRateFormatException("No hospital rows were found.");

        var rates = new List<TexasRccRow>();
        var held = new List<TexasRccHeldBack>();
        foreach (var npi in rows.GroupBy(r => r.Npi))
        {
            var distinctRates = npi.Select(r => r.Rate).Distinct().ToList();
            if (distinctRates.Count > 1)
            {
                held.Add(new TexasRccHeldBack(npi.Key, npi.First().ProviderName,
                    "its TPIs carry different rates (" + string.Join(", ", npi.Select(r => $"TPI {r.Tpi} {r.Rate * 100:0.##}% from {r.RateEffectiveDate:MM/dd/yyyy}")) +
                    "); claims are priced by NPI, so no one rate can be chosen"));
                continue;
            }
            rates.AddRange(npi.GroupBy(r => r.RateEffectiveDate).Select(g => g.First()));
        }

        return new TexasRccFile
        {
            FileEffectiveDate = fileDate.Value,
            Rows = rows,
            Rates = rates,
            HeldBack = held,
            SerialDatesRepaired = repaired,
        };
    }

    /// <summary>The provider name: the text after the NPI up to the end of its segment.</summary>
    private static string NameAfterNpi(IReadOnlyList<string> segments, string npi)
    {
        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i].Trim();
            if (seg == npi) return i + 1 < segments.Count ? segments[i + 1].Trim() : string.Empty;
            if (seg.StartsWith(npi + " ", StringComparison.Ordinal)) return seg[(npi.Length + 1)..].Trim();
        }
        return string.Empty;
    }

    /// <summary>
    /// Text lines of the PDF, each as column segments (words more than 6pt
    /// apart start a new segment). Words are grouped into a line by their
    /// vertical centre within 3pt of the line's first word, so a row whose
    /// numbers sit a fraction of a point off the name's baseline stays one row.
    /// </summary>
    public static IEnumerable<(int Page, IReadOnlyList<string> Segments)> PdfLines(Stream pdf)
    {
        var result = new List<(int, IReadOnlyList<string>)>();
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        foreach (var page in document.GetPages())
        {
            var lines = new List<(double Y, List<UglyToad.PdfPig.Content.Word> Words)>();
            foreach (var word in page.GetWords().OrderByDescending(w => w.BoundingBox.Centroid.Y))
            {
                var y = word.BoundingBox.Centroid.Y;
                if (lines.Count > 0 && Math.Abs(lines[^1].Y - y) <= 3.0)
                    lines[^1].Words.Add(word);
                else
                    lines.Add((y, [word]));
            }

            foreach (var (_, words) in lines)
            {
                var segments = new List<string>();
                var current = new List<string>();
                UglyToad.PdfPig.Content.Word? previous = null;
                foreach (var word in words.OrderBy(w => w.BoundingBox.Left))
                {
                    if (previous is not null && word.BoundingBox.Left - previous.BoundingBox.Right > 6.0)
                    {
                        segments.Add(string.Join(' ', current));
                        current.Clear();
                    }
                    current.Add(word.Text);
                    previous = word;
                }
                if (current.Count > 0) segments.Add(string.Join(' ', current));
                result.Add((page.Number, segments));
            }
        }
        return result;
    }

    [GeneratedRegex(@"(?<=[\d/%])\s+(?=[\d/%])")]
    private static partial Regex DigitRuns();

    [GeneratedRegex(@"Inpatient Ratio of Cost to Charges Effective (\d\d/\d\d/\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex Title();

    [GeneratedRegex(@"^[0-9A-Z]{9} \d{10} ")]
    private static partial Regex LeadingIds();

    // After digit runs are joined: "... ANDREWS, TX 79714-3617 ANDREWS 08/01/202698% Rural".
    // The date is MM/DD/YYYY or an Excel serial printed as a percentage
    // (46266 as "4626600%"); the rate follows it directly. A few rows print
    // the date as a bare serial, which lands after the rate ("50% 41072").
    [GeneratedRegex(@"^(?<tpi>[0-9A-Z]{9}) (?<npi>\d{10})\b(?<middle>.*?)(?: (?<county>[A-Z][A-Z .'-]*))? ?(?:(?<date>\d\d/\d\d/\d{4}|\d{5}00%)(?<rate>\d{1,3}(?:\.\d+)?)%|(?<rate>\d{1,3}(?:\.\d+)?)%(?<date>\d{5})) ?(?<cls>[A-Za-z][A-Za-z ]*)$")]
    private static partial Regex RowShape();
}
