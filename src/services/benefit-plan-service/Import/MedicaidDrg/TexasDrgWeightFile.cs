using System.Globalization;
using System.Text.RegularExpressions;
using CloudHealthOffice.FeeScheduleEngine.Medicaid;
using UglyToad.PdfPig;

namespace BenefitPlanService.Import.MedicaidDrg;

// ═══════════════════════════════════════════════════════════════════
// TEXAS APR-DRG RELATIVE WEIGHT TABLE
//
// HHSC Provider Finance publishes each state fiscal year's APR-DRG table as
// a PDF printed from Excel ("tx-apr-drg-grouper-current-ver.pdf"): a title
// block with the fiscal year, the 3M grouper version, the discharge date it
// takes effect and the universal mean, then one row per DRG-SOI:
//
//   APR DRG | SOI | DRG-SOI | description | relative weight | MLOS | day outlier threshold
//
// Long descriptions wrap onto two or three lines, and Excel centres the
// numbers vertically, so a row's numbers often sit on a different baseline
// from its DRG-SOI key. Reading line by line would mis-pair them. Instead,
// on each page the reader takes the DRG-SOI keys top to bottom and each
// numeric column top to bottom, and pairs them by position: the k-th key
// with the k-th value in every column. The counts must agree on every page
// and each pair must sit within one row of each other, or the file is
// refused.
//
// The ungroupable DRGs (955, 956; SOI 0) print "-" for every number and are
// skipped: a stay that groups there has nothing to price and pends.
// ═══════════════════════════════════════════════════════════════════

/// <summary>A parsed Texas APR-DRG weight table.</summary>
public sealed record TexasDrgWeightFile
{
    public required int StateFiscalYear { get; init; }

    /// <summary>3M APR-DRG grouper version, e.g. "40".</summary>
    public required string GrouperVersion { get; init; }

    /// <summary>First discharge date the table applies to.</summary>
    public required DateTime EffectiveDate { get; init; }

    /// <summary>
    /// The universal mean printed in the title (SFY 2021 on). Grouper 35/36
    /// workbooks do not print it; the SDA file for the year does.
    /// </summary>
    public decimal? UniversalMean { get; init; }

    /// <summary>Priced DRG-SOI rows (SOI 1 to 4).</summary>
    public required IReadOnlyList<MedicaidDrgWeight> Rows { get; init; }

    /// <summary>Descriptions by base APR-DRG, for reports.</summary>
    public IReadOnlyDictionary<string, string> Descriptions { get; init; } = new Dictionary<string, string>();

    /// <summary>DRG-SOI keys printed without numbers (the ungroupable DRGs).</summary>
    public IReadOnlyList<string> Unpriced { get; init; } = [];
}

/// <summary>Reads HHSC's Texas APR-DRG weight table PDF.</summary>
public static partial class TexasDrgWeightFileReader
{
    private enum Column { Weight, Mlos, DayOutlier }

    private sealed record Cell(double X, double Y, string Text);

    /// <summary>Reads the PDF as published.</summary>
    public static TexasDrgWeightFile Read(Stream pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        List<(int Page, List<Cell> Words)> pages;
        try
        {
            pages = [];
            using var document = PdfDocument.Open(pdf);
            foreach (var page in document.GetPages())
            {
                pages.Add((page.Number, page.GetWords()
                    .Select(w => new Cell(w.BoundingBox.Centroid.X, w.BoundingBox.Centroid.Y, w.Text))
                    .ToList()));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new TexasSdaFormatException($"Could not read the upload as a PDF: {ex.Message}");
        }
        return Parse(pages.Select(p => (p.Page, (IReadOnlyList<(double X, double Y, string Text)>)p.Words.Select(c => (c.X, c.Y, c.Text)).ToList())));
    }

    /// <summary>
    /// Parses each page's words (horizontal centre x, vertical centre y with y growing up
    /// the page, as PDF coordinates do, and text).
    /// </summary>
    public static TexasDrgWeightFile Parse(IEnumerable<(int Page, IReadOnlyList<(double X, double Y, string Text)> Words)> pages)
    {
        var title = new TitleFacts();
        var rows = new List<MedicaidDrgWeight>();
        var unpriced = new List<string>();
        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (pageNumber, raw) in pages)
        {
            var words = raw.Select(w => new Cell(w.X, w.Y, w.Text)).ToList();
            var lines = words.GroupBy(w => Math.Round(w.Y / 2.5)).OrderByDescending(g => g.Key)
                .Select(g => string.Join(' ', g.OrderBy(w => w.X).Select(w => w.Text))).ToList();

            foreach (var line in lines) title.Read(line);

            // Column positions from this page's header.
            var weightX = HeaderX(words, "Relative");
            var mlosX = HeaderX(words, "(MLOS)");
            var dayX = HeaderX(words, "Threshold");
            if (weightX is null || mlosX is null || dayX is null)
            {
                if (words.Any(w => KeyWord().IsMatch(w.Text)))
                    throw new TexasSdaFormatException($"Page {pageNumber} has DRG-SOI rows but no 'Relative Weight', 'MLOS' and 'Day Outlier Threshold' headings.");
                continue;
            }
            var headerY = words.Where(w => w.Text == "Relative").Max(w => w.Y);

            // Row anchors: the DRG, SOI and DRG-SOI key printed together at the left.
            var anchors = new List<(double Y, string Drg, string Soi, double KeyRight)>();
            foreach (var key in words.Where(w => w.Y < headerY && w.X < weightX.Value && KeyWord().IsMatch(w.Text)))
            {
                var sameLine = words.Where(w => Math.Abs(w.Y - key.Y) < 1.5 && w.X < key.X).OrderBy(w => w.X).ToList();
                if (sameLine.Count < 2) continue;
                var drg = sameLine[^2].Text;
                var soi = sameLine[^1].Text;
                if (!Drg().IsMatch(drg) || !Soi().IsMatch(soi) || key.Text != drg + soi) continue;
                anchors.Add((key.Y, drg, soi, key.X + 20));
            }
            anchors = anchors.OrderByDescending(a => a.Y).ToList();
            if (anchors.Count == 0) continue;

            // Numbers (or "-") right of the description, each in its nearest column.
            var columns = new Dictionary<Column, List<Cell>> { [Column.Weight] = [], [Column.Mlos] = [], [Column.DayOutlier] = [] };
            var leftOfNumbers = weightX.Value - 40;
            foreach (var w in words.Where(w => w.Y < headerY && w.X >= leftOfNumbers && NumberOrDash().IsMatch(w.Text)))
            {
                var col = new[] { (Column.Weight, weightX.Value), (Column.Mlos, mlosX.Value), (Column.DayOutlier, dayX.Value) }
                    .MinBy(c => Math.Abs(c.Item2 - w.X)).Item1;

                // A long description can run up to the weight column ("COMA < 1"):
                // a word that is not the column's shape is description text. A
                // genuinely malformed value then shows up as a count mismatch.
                var shaped = w.Text == "-" || col switch
                {
                    Column.Weight => WeightValue().IsMatch(w.Text),
                    Column.Mlos => MlosValue().IsMatch(w.Text),
                    _ => DayValue().IsMatch(w.Text),
                };
                if (shaped) columns[col].Add(w);
            }

            foreach (var (col, cells) in columns)
            {
                if (cells.Count != anchors.Count)
                    throw new TexasSdaFormatException(
                        $"Page {pageNumber}: {anchors.Count} DRG-SOI rows but {cells.Count} values in the {ColumnName(col)} column.");
            }

            var spacing = anchors.Count > 1
                ? anchors.Zip(anchors.Skip(1), (a, b) => a.Y - b.Y).Where(g => g > 0).DefaultIfEmpty(10).Min()
                : 10;
            var w8 = columns[Column.Weight].OrderByDescending(c => c.Y).ToList();
            var ml = columns[Column.Mlos].OrderByDescending(c => c.Y).ToList();
            var dt = columns[Column.DayOutlier].OrderByDescending(c => c.Y).ToList();

            for (var i = 0; i < anchors.Count; i++)
            {
                var a = anchors[i];
                var key = $"{int.Parse(a.Drg, CultureInfo.InvariantCulture):000}-{a.Soi}";
                foreach (var cell in new[] { w8[i], ml[i], dt[i] })
                {
                    // Wrapped descriptions centre the numbers between lines, so
                    // allow up to a full row of drift, never more.
                    if (Math.Abs(cell.Y - a.Y) > Math.Max(spacing, 8) * 1.6)
                        throw new TexasSdaFormatException(
                            $"Page {pageNumber}: the value '{cell.Text}' does not line up with DRG-SOI {key}; the layout was not recognised.");
                }

                var desc = DescriptionFor(words, anchors, i, a.KeyRight, leftOfNumbers);
                if (desc.Length > 0) descriptions.TryAdd(a.Drg, desc);

                AddRow(rows, unpriced, $"Page {pageNumber}", a.Drg, a.Soi, w8[i].Text, ml[i].Text, dt[i].Text, strictShape: true);
            }
        }

        return Build(title, rows, unpriced, descriptions);
    }

    /// <summary>
    /// Reads the Excel workbook HHSC also publishes (.xlsx, or .xls for
    /// grouper 36 and earlier): the title in the first cell, then a header row
    /// (APR DRG, SOI, DRG-SOI, DRG Description, relative weight, MLOS, day
    /// outlier threshold) and one row per DRG-SOI.
    /// </summary>
    public static TexasDrgWeightFile ReadExcel(Stream workbook, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var sheet = new List<string[]>();
        try
        {
            // Older workbooks put the title on one sheet and the table on the
            // next: take sheets in order up to and including the first one
            // with a DRG-SOI heading, and ignore any after it (notes).
            using var excel = ExcelDataReader.ExcelReaderFactory.CreateReader(workbook);
            do
            {
                var hasTable = false;
                while (excel.Read())
                {
                    var cells = new string[excel.FieldCount];
                    for (var i = 0; i < excel.FieldCount; i++)
                    {
                        cells[i] = excel.GetValue(i) switch
                        {
                            null => string.Empty,
                            double n => n.ToString("0.############", CultureInfo.InvariantCulture),
                            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                            var v => v.ToString() ?? string.Empty,
                        };
                        if (cells[i].Replace(" ", "").ToUpperInvariant() is "DRG-SOI" or "APR-DRG") hasTable = true;
                    }
                    sheet.Add(cells);
                }
                if (hasTable) break;
            }
            while (excel.NextResult());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new TexasSdaFormatException($"The Excel file '{fileName}' could not be read: {ex.Message}");
        }
        return ParseSheet(sheet);
    }

    /// <summary>Parses the first sheet's cells as text.</summary>
    public static TexasDrgWeightFile ParseSheet(IReadOnlyList<string[]> sheet)
    {
        var title = new TitleFacts();
        var rows = new List<MedicaidDrgWeight>();
        var unpriced = new List<string>();
        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);
        int? drgCol = null, soiCol = null, keyCol = null, descCol = null, weightCol = null, mlosCol = null, dayCol = null;

        for (var r = 0; r < sheet.Count; r++)
        {
            var cells = sheet[r].Select(c => c.Trim()).ToArray();
            if (keyCol is null)
            {
                foreach (var cell in cells)
                    foreach (var line in cell.Split('\n')) title.Read(line.Trim());

                var lower = cells.Select(c => c.ToLowerInvariant().Replace('\n', ' ')).ToArray();
                int Find(Func<string, bool> match) => Array.FindIndex(lower, c => match(c));
                // SFY 2021 on: "DRG-SOI" keys like 0011 beside separate DRG and
                // SOI columns. Grouper 35/36 workbooks: one "APR-DRG" column of
                // keys like 001-1.
                var key = Find(c => c.Replace(" ", "") == "drg-soi");
                var combined = key < 0;
                if (combined) key = Find(c => c == "apr-drg");
                if (key < 0) continue;
                keyCol = key;
                drgCol = combined ? -1 : Find(c => c is "apr drg" or "apr-drg" or "drg");
                soiCol = Find(c => c == "soi");
                descCol = Find(c => c.Contains("description"));
                weightCol = Find(c => c.Contains("relative weight"));
                mlosCol = Find(c => c.Contains("mlos") || c.Contains("length of stay"));
                dayCol = Find(c => c.Contains("outlier threshold"));
                if (weightCol < 0 || mlosCol < 0 || dayCol < 0)
                    throw new TexasSdaFormatException($"Row {r + 1} has a DRG-SOI heading but no relative weight, MLOS and day outlier threshold columns.");
                continue;
            }

            string At(int? c) => c is { } i && i >= 0 && i < cells.Length ? cells[i] : string.Empty;
            var rawKey = At(keyCol);
            if (rawKey.Length == 0) continue;
            if (!KeyWord().IsMatch(rawKey) && !DashedKey().IsMatch(rawKey))
                throw new TexasSdaFormatException($"Row {r + 1}: '{rawKey}' is not a DRG-SOI key such as 0011 or 001-1.");

            var drg = rawKey[..3];
            var soi = rawKey[^1..];
            if (At(drgCol) is { Length: > 0 } d && d.PadLeft(3, '0') != drg || At(soiCol) is { Length: > 0 } so && so != soi)
                throw new TexasSdaFormatException($"Row {r + 1}: DRG '{At(drgCol)}' and SOI '{At(soiCol)}' do not match the key {rawKey}.");

            var desc = At(descCol);
            if (desc.Length > 0) descriptions.TryAdd(drg, desc);
            AddRow(rows, unpriced, $"Row {r + 1}", drg, soi, At(weightCol), At(mlosCol), At(dayCol), strictShape: false);
        }

        if (keyCol is null)
        {
            var seen = string.Join(" | ", sheet.Take(60).Select(r => string.Join(", ", r.Where(c => c.Trim().Length > 0).Take(8)))
                .Where(t => t.Length > 0).Select(t => t.Length > 120 ? t[..120] : t));
            throw new TexasSdaFormatException($"No header row with a DRG-SOI column was found. The sheet starts: {seen}");
        }
        return Build(title, rows, unpriced, descriptions);
    }

    /// <summary>Validates one DRG-SOI's numbers and adds it to the priced or unpriced list.</summary>
    private static void AddRow(List<MedicaidDrgWeight> rows, List<string> unpriced, string where,
        string drg, string soi, string weightText, string mlosText, string dayText, bool strictShape)
    {
        var key = $"{drg}-{soi}";
        // Ungroupable rows print "-" (SFY 2021 on), nothing, or 0 (grouper 35/36).
        static bool Blank(string t) => t is "" or "-" or "0";
        if (soi == "0" || Blank(weightText))
        {
            if (!(soi == "0" && Blank(weightText) && Blank(mlosText) && Blank(dayText)))
                throw new TexasSdaFormatException($"{where}: DRG-SOI {key} has a mix of numbers and blanks.");
            unpriced.Add(key);
            return;
        }

        if (strictShape && (!WeightValue().IsMatch(weightText) || !MlosValue().IsMatch(mlosText) || !DayValue().IsMatch(dayText)))
            throw new TexasSdaFormatException(
                $"{where}: DRG-SOI {key} reads as weight '{weightText}', MLOS '{mlosText}', threshold '{dayText}', which is not the expected shape.");
        if (!decimal.TryParse(weightText, NumberStyles.Number, CultureInfo.InvariantCulture, out var weight)
            || !decimal.TryParse(mlosText, NumberStyles.Number, CultureInfo.InvariantCulture, out var mlos)
            || !decimal.TryParse(dayText, NumberStyles.Number, CultureInfo.InvariantCulture, out var day))
            throw new TexasSdaFormatException($"{where}: DRG-SOI {key} has a value that is not a number ('{weightText}', '{mlosText}', '{dayText}').");

        // The threshold can sit below the MLOS (SFY 2027 prints 772-3 with
        // MLOS 9.5 and threshold 9); the day outlier test also requires
        // MLOS + 2 days, so that is harmless and loaded as published.
        if (weight <= 0 || mlos <= 0 || day <= 0)
            throw new TexasSdaFormatException(
                $"{where}: DRG-SOI {key} has weight {weight}, MLOS {mlos}, day outlier threshold {day}; all three must be positive.");

        rows.Add(new MedicaidDrgWeight { DrgSoi = key, RelativeWeight = weight, MeanLengthOfStay = mlos, DayOutlierThreshold = day });
    }

    private static TexasDrgWeightFile Build(TitleFacts title, List<MedicaidDrgWeight> rows, List<string> unpriced, Dictionary<string, string> descriptions)
    {
        if (title.Version is null || title.Effective is null)
            throw new TexasSdaFormatException("This is not a Texas APR-DRG weight table: the title must give the grouper version and the effective discharge date.");
        if (rows.Count == 0)
            throw new TexasSdaFormatException("No DRG-SOI rows were found.");

        var dup = rows.GroupBy(r => r.DrgSoi).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null)
            throw new TexasSdaFormatException($"DRG-SOI {dup.Key} appears more than once.");

        // Every priced base DRG should have all four severities.
        var partial = rows.GroupBy(r => r.DrgSoi[..3]).FirstOrDefault(g => g.Count() != 4);
        if (partial is not null)
            throw new TexasSdaFormatException($"APR-DRG {partial.Key} has {partial.Count()} severity rows instead of 4.");

        return new TexasDrgWeightFile
        {
            StateFiscalYear = title.FiscalYear ?? title.Effective.Value.AddMonths(4).Year,
            GrouperVersion = title.Version,
            EffectiveDate = title.Effective.Value,
            UniversalMean = title.UniversalMean,
            Rows = rows,
            Descriptions = descriptions,
            Unpriced = unpriced,
        };
    }

    /// <summary>The facts the title block gives, from whichever lines carry them.</summary>
    private sealed class TitleFacts
    {
        public int? FiscalYear;
        public string? Version;
        public DateTime? Effective;
        public decimal? UniversalMean;

        public void Read(string line)
        {
            if (FiscalYearRegex().Match(line) is { Success: true } fy) FiscalYear ??= int.Parse(fy.Groups[1].Value, CultureInfo.InvariantCulture);
            if (GrouperVersion().Match(line) is { Success: true } gv) Version ??= gv.Groups[1].Value;
            if (EffectiveLine().Match(line) is { Success: true } ef
                && DateTime.TryParseExact(ef.Groups[1].Value, ["MM/dd/yyyy", "M/d/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                Effective ??= d;
            if (UniversalMeanLine().Match(line) is { Success: true } um)
                UniversalMean ??= decimal.Parse(um.Groups[1].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture);
        }
    }

    private static string DescriptionFor(List<Cell> words, List<(double Y, string Drg, string Soi, double KeyRight)> anchors, int i, double left, double right)
    {
        var top = i == 0 ? anchors[i].Y + 12 : (anchors[i - 1].Y + anchors[i].Y) / 2;
        var bottom = i == anchors.Count - 1 ? anchors[i].Y - 12 : (anchors[i].Y + anchors[i + 1].Y) / 2;
        return string.Join(' ', words
            .Where(w => w.X > left && w.X < right && w.Y < top && w.Y > bottom && !NumberOrDash().IsMatch(w.Text))
            .OrderByDescending(w => Math.Round(w.Y))
            .ThenBy(w => w.X)
            .Select(w => w.Text));
    }

    private static double? HeaderX(List<Cell> words, string text)
    {
        var hit = words.Where(w => w.Text == text).ToList();
        return hit.Count == 0 ? null : hit.Average(w => w.X);
    }

    private static string ColumnName(Column c) => c switch
    {
        Column.Weight => "relative weight",
        Column.Mlos => "MLOS",
        _ => "day outlier threshold",
    };

    [GeneratedRegex(@"State Fiscal Year (\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex FiscalYearRegex();

    [GeneratedRegex(@"Grouper Version (\d{2,3})", RegexOptions.IgnoreCase)]
    private static partial Regex GrouperVersion();

    [GeneratedRegex(@"Effective for Discharges on or After (\d{1,2}/\d{1,2}/\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex EffectiveLine();

    [GeneratedRegex(@"Universal Mean\s*=\s*\$?([\d,]+\.\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex UniversalMeanLine();

    [GeneratedRegex(@"^\d{4}$")]
    private static partial Regex KeyWord();

    [GeneratedRegex(@"^\d{3}$")]
    private static partial Regex Drg();

    [GeneratedRegex(@"^\d{3}-[0-4]$")]
    private static partial Regex DashedKey();

    [GeneratedRegex(@"^[0-4]$")]
    private static partial Regex Soi();

    [GeneratedRegex(@"^(-|\d+(\.\d+)?)$")]
    private static partial Regex NumberOrDash();

    [GeneratedRegex(@"^\d+\.\d{4}$")]
    private static partial Regex WeightValue();

    [GeneratedRegex(@"^\d+\.\d$")]
    private static partial Regex MlosValue();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex DayValue();
}
