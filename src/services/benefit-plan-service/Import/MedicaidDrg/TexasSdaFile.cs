using System.Globalization;
using System.Text.RegularExpressions;
using CloudHealthOffice.FeeScheduleEngine.Medicaid;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace BenefitPlanService.Import.MedicaidDrg;

// ═══════════════════════════════════════════════════════════════════
// TEXAS APR-DRG STANDARD DOLLAR AMOUNT (SDA) FILES
//
// HHSC Provider Finance publishes each state fiscal year's hospital SDAs as
// PDFs printed from Excel, one per hospital class:
//
//   {sfy}-fy-hosp-urban-sda.pdf   base SDA + supplemental funds base
//                                 adjustment + wage index, IME, trauma and
//                                 safety-net add-ons = SDA with add-ons
//   {sfy}-fy-hosp-rural-sda.pdf   SDA + add-on = SDA with add-on, and a
//                                 separate OB-GYN delivery SDA (§355.8052(e)(2))
//
// Each page repeats a title block naming the class, the fiscal year and the
// universal mean with its effective date, then one row per hospital: TPI,
// NPI, Medicare number, name, street, city, ZIP, county and the dollar
// columns. This reader turns the PDF's text layer into rows and checks
// every row's arithmetic (the parts must add up to the published total), so
// a misread column refuses the file instead of loading a wrong rate.
//
// Children's hospitals are not read yet: their file has not been seen, and
// a layout this reader does not recognise is refused, never guessed at.
// ═══════════════════════════════════════════════════════════════════

/// <summary>One hospital's row in a Texas SDA file.</summary>
public sealed record TexasSdaRow
{
    /// <summary>Texas Provider Identifier (9 digits, the TMHP billing ID).</summary>
    public required string Tpi { get; init; }
    public required string Npi { get; init; }
    public string? MedicareNumber { get; init; }
    public required string HospitalName { get; init; }
    public string? City { get; init; }
    public required string Zip { get; init; }
    public required string County { get; init; }

    /// <summary>
    /// The SDA before add-ons: the urban base SDA plus the supplemental funds
    /// base adjustment, or the rural final SDA before its add-on.
    /// </summary>
    public required decimal BaseSda { get; init; }

    /// <summary>Each add-on by its column name, as published.</summary>
    public required IReadOnlyDictionary<string, decimal> AddOns { get; init; }

    /// <summary>The final SDA the stay is paid with: base plus every add-on.</summary>
    public required decimal FinalSda { get; init; }

    /// <summary>Rural only: the final SDA for labor and delivery stays (§355.8052(e)(2)).</summary>
    public decimal? DeliverySda { get; init; }

    /// <summary>The page the row was read from, for error messages.</summary>
    public int Page { get; init; }
}

/// <summary>A parsed Texas SDA file.</summary>
public sealed record TexasSdaFile
{
    public required TexasHospitalClass HospitalClass { get; init; }

    /// <summary>State fiscal year, e.g. 2027 (September 1, 2026 through August 31, 2027).</summary>
    public required int StateFiscalYear { get; init; }

    /// <summary>The universal mean (§355.8052(b)) printed in the title, for cost outliers.</summary>
    public required decimal UniversalMean { get; init; }

    /// <summary>The date the universal mean (and the SDAs) take effect.</summary>
    public required DateTime EffectiveDate { get; init; }

    /// <summary>Rows whose arithmetic checks out: the ones that can be loaded.</summary>
    public required IReadOnlyList<TexasSdaRow> Rows { get; init; }

    /// <summary>
    /// Rows HHSC printed with columns that contradict each other (the parts
    /// do not add up to the final SDA). They are not loaded, so those
    /// hospitals' stays pend until the plan confirms the right SDA.
    /// </summary>
    public IReadOnlyList<TexasSdaDiscrepancy> Discrepancies { get; init; } = [];

    /// <summary>NPIs that appear on more than one row (one NPI billing under several TPIs).</summary>
    public IReadOnlyList<string> DuplicateNpis =>
        Rows.GroupBy(r => r.Npi).Where(g => g.Count() > 1).Select(g => g.Key).OrderBy(n => n, StringComparer.Ordinal).ToList();

    /// <summary>
    /// The pricer's hospital facts for one row. The cost-to-charge ratio
    /// (interim rate) is not in this file and has to come from elsewhere.
    /// </summary>
    public MedicaidDrgHospital ToPricerHospital(TexasSdaRow row, decimal? interimRate = null) => new()
    {
        Npi = row.Npi,
        BaseRate = row.FinalSda,
        DeliveryBaseRate = row.DeliverySda,
        CostToChargeRatio = interimRate,
        TexasClass = HospitalClass,
    };
}

/// <summary>A published row that contradicts itself, held back from loading.</summary>
public sealed record TexasSdaDiscrepancy(int Page, string Tpi, string Npi, string HospitalName, string Problem);

/// <summary>The upload is not a Texas SDA file this reader understands, or a row does not add up.</summary>
public sealed class TexasSdaFormatException(string message) : Exception(message);

/// <summary>Reads HHSC's Texas APR-DRG SDA PDFs.</summary>
public static partial class TexasSdaFileReader
{
    /// <summary>Gap between words, in points, that separates two columns (name, street, city).</summary>
    private const double ColumnGap = 6.0;

    private static readonly string[] UrbanAddOns =
        ["Supplemental Funds Base Adjustment", "Wage Index Add-on", "IME Add-on", "Trauma Add-on", "Safety Net Add-on"];

    /// <summary>Reads a PDF as published.</summary>
    public static TexasSdaFile Read(Stream pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        try
        {
            return Parse(PdfLines(pdf));
        }
        catch (TexasSdaFormatException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new TexasSdaFormatException($"Could not read the upload as a PDF: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses the text lines of the file. Each line is its column segments
    /// left to right (words closer than <see cref="ColumnGap"/> joined by a
    /// space), tagged with its page number.
    /// </summary>
    public static TexasSdaFile Parse(IEnumerable<(int Page, IReadOnlyList<string> Segments)> lines)
    {
        TexasHospitalClass? cls = null;
        int? sfy = null;
        decimal? universalMean = null;
        DateTime? effective = null;
        var rows = new List<TexasSdaRow>();
        var discrepancies = new List<TexasSdaDiscrepancy>();
        var errors = new List<string>();

        foreach (var (page, segments) in lines)
        {
            var text = string.Join(' ', segments);

            if (TitleClass().Match(text) is { Success: true } t)
            {
                var found = t.Groups[1].Value.ToUpperInvariant() switch
                {
                    "URBAN" => TexasHospitalClass.Urban,
                    "RURAL" => TexasHospitalClass.Rural,
                    _ => TexasHospitalClass.Childrens,
                };
                if (found == TexasHospitalClass.Childrens)
                    throw new TexasSdaFormatException("Children's hospital SDA files are not supported yet; send the file so its layout can be added.");
                if (cls is { } c && c != found)
                    throw new TexasSdaFormatException($"Page {page} is for {found} hospitals but earlier pages are for {c} hospitals.");
                cls = found;
                continue;
            }

            if (FiscalYear().Match(text) is { Success: true } fy)
            {
                sfy ??= int.Parse(fy.Groups[1].Value, CultureInfo.InvariantCulture);
                continue;
            }

            if (UniversalMeanLine().Match(text) is { Success: true } um)
            {
                if (!DateTime.TryParseExact(um.Groups[1].Value.Trim(), "MMMM d, yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    throw new TexasSdaFormatException($"Page {page}: could not read the effective date '{um.Groups[1].Value}'.");
                var mean = Money(um.Groups[2].Value);
                if (universalMean is { } prior && (prior != mean || effective != date))
                    throw new TexasSdaFormatException($"Page {page} prints a different universal mean or effective date than page 1.");
                universalMean = mean;
                effective = date;
                continue;
            }

            if (!LeadingIds().IsMatch(text)) continue;

            if (cls is null)
                throw new TexasSdaFormatException("Hospital rows come before the title naming the hospital class (urban or rural).");

            var row = ParseRow(page, segments, cls.Value, errors, discrepancies);
            if (row is not null) rows.Add(row);
        }

        if (cls is null)
            throw new TexasSdaFormatException("This is not a Texas SDA file: no 'Standard Dollar Amounts for Urban/Rural Hospitals' title was found.");
        if (universalMean is null || effective is null)
            throw new TexasSdaFormatException("No 'Universal Mean Effective <date> - $<amount>' line was found.");
        if (errors.Count > 0)
            throw new TexasSdaFormatException(
                $"{errors.Count} row(s) could not be read; nothing was loaded. " + string.Join(" ", errors.Take(10)));
        if (rows.Count == 0)
            throw new TexasSdaFormatException(discrepancies.Count == 0
                ? "No hospital rows were found."
                : "No row's columns add up; nothing can be loaded.");

        var dupTpi = rows.Select(r => r.Tpi).Concat(discrepancies.Select(d => d.Tpi))
            .GroupBy(t => t).FirstOrDefault(g => g.Count() > 1);
        if (dupTpi is not null)
            throw new TexasSdaFormatException($"TPI {dupTpi.Key} appears on more than one row.");

        return new TexasSdaFile
        {
            HospitalClass = cls.Value,
            StateFiscalYear = sfy ?? effective.Value.AddMonths(4).Year,
            UniversalMean = universalMean.Value,
            EffectiveDate = effective.Value,
            Rows = rows,
            Discrepancies = discrepancies,
        };
    }

    private static TexasSdaRow? ParseRow(
        int page, IReadOnlyList<string> segments, TexasHospitalClass cls, List<string> errors, List<TexasSdaDiscrepancy> discrepancies)
    {
        var text = string.Join(' ', segments);
        var m = RowShape().Match(text);
        if (!m.Success)
        {
            errors.Add($"Page {page}: '{Shorten(text)}' is not TPI, NPI, Medicare number, name, address, ZIP, county and dollar amounts.");
            return null;
        }

        var amounts = m.Groups["amts"].Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(Money)
            .ToList();
        var tpi = m.Groups["tpi"].Value;

        var expected = cls == TexasHospitalClass.Urban ? 7 : 4;
        if (amounts.Count != expected)
        {
            errors.Add($"Page {page}, TPI {tpi}: {amounts.Count} dollar columns where the {cls} layout has {expected}.");
            return null;
        }

        decimal baseSda, final;
        decimal? delivery = null;
        var addOns = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (cls == TexasHospitalClass.Urban)
        {
            // Base SDA, supplemental funds base adjustment, wage index, IME,
            // trauma, safety net, SDA with applicable add-ons.
            baseSda = amounts[0];
            for (var i = 0; i < UrbanAddOns.Length; i++) addOns[UrbanAddOns[i]] = amounts[i + 1];
            final = amounts[6];
        }
        else
        {
            // SDA, add-on, SDA with add-on, OB-GYN delivery SDA.
            baseSda = amounts[0];
            addOns["Rural Add-on"] = amounts[1];
            final = amounts[2];
            delivery = amounts[3];
        }

        var middle = m.Groups["middle"].Value.Trim();
        var (name, city) = NameAndCity(segments, middle);
        var npi = m.Groups["npi"].Value;

        // A row that reads cleanly but contradicts itself is HHSC's error, not
        // a misread: hold that hospital back and load the rest.
        var sum = baseSda + addOns.Values.Sum();
        string? problem = null;
        if (Math.Abs(sum - final) > 0.02m)
            problem = $"the columns add to {sum:0.00} but the final SDA is {final:0.00}";
        else if (final <= 0)
            problem = "the final SDA is not positive";
        else if (delivery is { } d && d < final)
            problem = $"the OB-GYN delivery SDA {d:0.00} is below the final SDA {final:0.00}";
        if (problem is not null)
        {
            discrepancies.Add(new TexasSdaDiscrepancy(page, tpi, npi, name, problem));
            return null;
        }

        return new TexasSdaRow
        {
            Tpi = tpi,
            Npi = npi,
            MedicareNumber = m.Groups["mcr"].Value,
            HospitalName = name,
            City = city,
            Zip = m.Groups["zip"].Value,
            County = m.Groups["county"].Value.Trim(),
            BaseSda = baseSda,
            AddOns = addOns,
            FinalSda = final,
            DeliverySda = delivery,
            Page = page,
        };
    }

    /// <summary>
    /// Name and city from the column segments between the Medicare number and
    /// the ZIP: name, street, city when the gaps show three columns, otherwise
    /// the whole middle text as the name.
    /// </summary>
    private static (string Name, string? City) NameAndCity(IReadOnlyList<string> segments, string middle)
    {
        // Segments: TPI, NPI, Medicare number, then name / street / city, then
        // ZIP, county, amounts. Find the middle ones by position.
        var zipIndex = -1;
        for (var i = 3; i < segments.Count; i++)
        {
            if (Zip().IsMatch(segments[i]) && i + 1 < segments.Count) { zipIndex = i; }
            if (segments[i].StartsWith('$')) break;
        }
        if (zipIndex > 3 && segments.Count > 3 && segments[0].Length == 9)
        {
            var mid = segments.Skip(3).Take(zipIndex - 3).ToList();
            if (mid.Count == 3) return (mid[0], mid[2]);
        }
        return (middle, null);
    }

    /// <summary>The PDF's lines, each split into column segments by wide gaps between words.</summary>
    public static IEnumerable<(int Page, IReadOnlyList<string> Segments)> PdfLines(Stream pdf)
    {
        var result = new List<(int, IReadOnlyList<string>)>();
        using var document = PdfDocument.Open(pdf);
        foreach (var page in document.GetPages())
        {
            foreach (var line in page.GetWords()
                         .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 2.5))
                         .OrderByDescending(g => g.Key))
            {
                var segments = new List<string>();
                Word? previous = null;
                var current = new List<string>();
                foreach (var word in line.OrderBy(w => w.BoundingBox.Left))
                {
                    if (previous is not null && word.BoundingBox.Left - previous.BoundingBox.Right > ColumnGap)
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

    private static decimal Money(string text) =>
        decimal.Parse(text.Replace("$", "").Replace(",", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture);

    private static string Shorten(string text) => text.Length <= 80 ? text : text[..80] + "…";

    [GeneratedRegex(@"Standard Dollar Amounts for (Urban|Rural|Children'?s|Childrens) Hospitals", RegexOptions.IgnoreCase)]
    private static partial Regex TitleClass();

    [GeneratedRegex(@"^\s*State Fiscal Year (\d{4})\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex FiscalYear();

    [GeneratedRegex(@"Universal Mean Effective (.+?)\s*-\s*(\$[\d,]+\.\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex UniversalMeanLine();

    [GeneratedRegex(@"^\d{9} \d{10} ")]
    private static partial Regex LeadingIds();

    [GeneratedRegex(@"^\d{5}$")]
    private static partial Regex Zip();

    // The middle (name, street, city) is greedy so the ZIP is the last
    // five-digit token before the county, which has no digits.
    [GeneratedRegex(@"^(?<tpi>\d{9}) (?<npi>\d{10}) (?<mcr>[0-9A-Z]{6,10}) (?<middle>.+) (?<zip>\d{5}) (?<county>[A-Z][A-Z .'\-]*?) (?<amts>(?:\$[\d,]+\.\d{2} ?)+)$")]
    private static partial Regex RowShape();
}
