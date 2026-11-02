using System.Globalization;
using System.Text.RegularExpressions;

namespace BenefitPlanService.Import.MedicaidDrg;

// ═══════════════════════════════════════════════════════════════════
// TEXAS PSYCHIATRIC HOSPITAL PER DIEM FILES
//
// HHSC Provider Finance publishes each state fiscal year's psychiatric
// hospital per diems as a PDF, "{yyyy}-ffy-psychiatric-hosp-per-diem-rates.pdf",
// titled "SFY {yyyy} Psychiatric Hospital Rates", in two sections:
//
//   Private Freestanding Psychiatric Facilities (Effective on or before MM/DD/YYYY)
//   State-Owned Psychiatric Facilities (Effective MM/DD/YYYY)
//
// One row per TPI: TPI, NPI, provider, address, city, ZIP, county and the
// per diem ("$ 529.59"). Each rate loads effective on its section's date.
// As in the RCC files, an NPI whose TPIs carry different per diems is held
// back, because claims find the hospital by NPI.
// ═══════════════════════════════════════════════════════════════════

/// <summary>One TPI's row.</summary>
public sealed record TexasPsychPerDiemRow(
    int Page, string Section, DateTime EffectiveDate, string Tpi, string Npi, string ProviderName, decimal DailyRate);

/// <summary>A parsed psychiatric per diem file.</summary>
public sealed record TexasPsychPerDiemFile
{
    public required int FiscalYear { get; init; }
    public required IReadOnlyList<TexasPsychPerDiemRow> Rows { get; init; }

    /// <summary>One rate per NPI and effective date; NPIs whose TPIs disagree are left out.</summary>
    public required IReadOnlyList<TexasPsychPerDiemRow> Rates { get; init; }

    public required IReadOnlyList<TexasRccHeldBack> HeldBack { get; init; }
}

public static partial class TexasPsychPerDiemFileReader
{
    /// <summary>Reads a psychiatric per diem PDF as published.</summary>
    public static TexasPsychPerDiemFile Read(Stream pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        IEnumerable<(int, IReadOnlyList<string>)> lines;
        try
        {
            lines = TexasRccFileReader.PdfLines(pdf);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InterimRateFormatException($"Could not read the upload as a PDF: {ex.Message}");
        }
        return Parse(lines);
    }

    /// <summary>Parses the text lines (column segments per line, with page numbers).</summary>
    public static TexasPsychPerDiemFile Parse(IEnumerable<(int Page, IReadOnlyList<string> Segments)> lines)
    {
        int? year = null;
        (string Name, DateTime Effective)? section = null;
        var rows = new List<TexasPsychPerDiemRow>();
        var errors = new List<string>();

        foreach (var (page, segments) in lines)
        {
            var text = string.Join(' ', segments).Trim();

            if (Title().Match(text) is { Success: true } t)
            {
                var y = int.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture);
                if (year is { } prior && prior != y)
                    throw new InterimRateFormatException($"Page {page} is titled SFY {y} but page 1 says SFY {prior}.");
                year = y;
                continue;
            }

            if (Section().Match(text) is { Success: true } s)
            {
                section = (s.Groups["name"].Value, DateTime.ParseExact(s.Groups["date"].Value, "MM/dd/yyyy", CultureInfo.InvariantCulture));
                continue;
            }

            var ids = LeadingIds().Match(text);
            if (!ids.Success) continue;
            var tpi = ids.Groups["tpi"].Value;
            var npi = ids.Groups["npi"].Value;

            if (section is not { } sec)
            {
                errors.Add($"Page {page}: TPI {tpi} comes before any 'Private Freestanding' or 'State-Owned' section heading.");
                continue;
            }

            // The per diem is the last column, "$ 529.59"; the text layer can
            // split its digits, so whitespace after the "$" is dropped.
            var dollar = text.LastIndexOf('$');
            var amount = dollar < 0 ? string.Empty : Regex.Replace(text[(dollar + 1)..], @"\s+", "");
            if (!decimal.TryParse(amount, NumberStyles.AllowThousands | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate)
                || !Money().IsMatch(amount))
            {
                errors.Add($"Page {page}, TPI {tpi}: no per diem in '{(text.Length > 90 ? "…" + text[^90..] : text)}'.");
                continue;
            }
            if (rate <= 0m || rate > 10_000m)
            {
                errors.Add($"Page {page}, TPI {tpi}: per diem {amount} is outside $0-$10,000.");
                continue;
            }

            rows.Add(new TexasPsychPerDiemRow(page, sec.Name, sec.Effective, tpi, npi, NameAfterNpi(segments, npi), rate));
        }

        if (year is null)
            throw new InterimRateFormatException("This is not a Texas psychiatric per diem file: no 'SFY yyyy Psychiatric Hospital Rates' title was found.");
        if (errors.Count > 0)
            throw new InterimRateFormatException($"{errors.Count} row(s) could not be read; nothing was loaded. " + string.Join(" ", errors.Take(10)));
        if (rows.Count == 0)
            throw new InterimRateFormatException("No hospital rows were found.");

        var rates = new List<TexasPsychPerDiemRow>();
        var held = new List<TexasRccHeldBack>();
        foreach (var npi in rows.GroupBy(r => (r.Npi, r.EffectiveDate)))
        {
            if (npi.Select(r => r.DailyRate).Distinct().Count() > 1)
            {
                held.Add(new TexasRccHeldBack(npi.Key.Npi, npi.First().ProviderName,
                    "its TPIs carry different per diems (" + string.Join(", ", npi.Select(r => $"TPI {r.Tpi} {r.DailyRate:0.00}")) +
                    "); claims are priced by NPI, so no one rate can be chosen"));
                continue;
            }
            rates.Add(npi.First());
        }

        return new TexasPsychPerDiemFile { FiscalYear = year.Value, Rows = rows, Rates = rates, HeldBack = held };
    }

    /// <summary>The provider name: the text after the NPI up to the end of its segment.</summary>
    private static string NameAfterNpi(IReadOnlyList<string> segments, string npi)
    {
        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i].Trim();
            var at = seg.IndexOf(npi, StringComparison.Ordinal);
            if (at < 0) continue;
            var rest = seg[(at + npi.Length)..].Trim();
            if (rest.Length > 0) return rest;
            return i + 1 < segments.Count ? segments[i + 1].Trim() : string.Empty;
        }
        return string.Empty;
    }

    [GeneratedRegex(@"SFY (\d{4}) Psychiatric Hospital Rates", RegexOptions.IgnoreCase)]
    private static partial Regex Title();

    [GeneratedRegex(@"(?<name>Private Freestanding|State-Owned) Psychiatric Facilities \(Effective (?:on or before )?(?<date>\d\d/\d\d/\d{4})\)", RegexOptions.IgnoreCase)]
    private static partial Regex Section();

    [GeneratedRegex(@"^(?<tpi>[0-9A-Z]{9})\s+(?<npi>\d{10})\b")]
    private static partial Regex LeadingIds();

    [GeneratedRegex(@"^\d{1,3}(?:,?\d{3})*\.\d\d$")]
    private static partial Regex Money();
}
