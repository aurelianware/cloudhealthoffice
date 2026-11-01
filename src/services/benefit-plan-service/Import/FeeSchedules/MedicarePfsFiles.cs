using System.Globalization;
using System.IO.Compression;
using CsvHelper;
using CsvHelper.Configuration;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;

namespace BenefitPlanService.Import.FeeSchedules;

/// <summary>One Medicare PFS payment locality from the CMS GPCI file.</summary>
public sealed record PfsLocality(
    string Mac, string LocalityNumber, string State, string Name,
    decimal WorkGpci, decimal PeGpci, decimal MpGpci)
{
    /// <summary>"{MAC}-{locality}", e.g. "04412-18": the key the importer and schedule ids use.</summary>
    public string Key => $"{Mac}-{LocalityNumber}";
}

/// <summary>The PPRRVU file read into engine RVU lines, with what was left out.</summary>
public sealed class PfsRvuFile
{
    public List<FeeScheduleLine> Lines { get; } = new();
    public HashSet<decimal> ConversionFactors { get; } = new();
    public Dictionary<string, int> Skipped { get; } = new();
    public List<FeeScheduleSkippedRow> SkippedExamples { get; } = new();
    public int RowsRead { get; set; }
    public int NonFacilityNotApplicable { get; set; }
    public int FacilityNotApplicable { get; set; }

    internal void Skip(int row, string code, string reason)
    {
        Skipped[reason] = Skipped.GetValueOrDefault(reason) + 1;
        if (Skipped[reason] <= FeeScheduleImportReport.ExamplesPerReason)
            SkippedExamples.Add(new FeeScheduleSkippedRow(row, code, reason));
    }
}

/// <summary>
/// Readers for the CMS Physician Fee Schedule relative value files
/// (https://www.cms.gov/medicare/payment/fee-schedules/physician/pfs-relative-value-files),
/// the quarterly RVUyyA–D zip as CMS publishes it or its CSVs on their own:
///
/// <list type="bullet">
///   <item><b>PPRRVU</b>: one row per HCPCS code and modifier. Its header spans
///   several rows, so data rows are read by the column positions CMS has kept
///   stable for years (HCPCS, MOD, DESCRIPTION, STATUS CODE, NOT USED FOR
///   MEDICARE PAYMENT, WORK RVU, NON-FAC PE RVU, NON-FAC NA INDICATOR, FACILITY
///   PE RVU, FACILITY NA INDICATOR, MP RVU, totals, PCTC IND, GLOB DAYS, PRE/INTRA/
///   POST OP, MULT PROC, BILAT SURG, ASST SURG, CO-SURG, TEAM SURG, ENDO BASE,
///   CONV FACTOR, ...). A row is a data row when its first cell is a 5-character
///   code and its RVU cells are numbers.</item>
///   <item><b>GPCI</b>: one row per MAC and locality, header found by name below
///   title rows. The work GPCI read is the one "with floor" when both are given.</item>
/// </list>
/// </summary>
public static class MedicarePfsFiles
{
    internal const int Hcpcs = 0, Mod = 1, Status = 3, Work = 5, NonFacPe = 6, NonFacNa = 7,
        FacPe = 8, FacNa = 9, Mp = 10, MultProc = 18, BilatSurg = 19, AsstSurg = 20;

    /// <summary>
    /// CONV FACTOR column in the CY2026 PPRRVU layout (after PRIC IND and ENDO
    /// BASE). The header rows are read for it, so a release that moves it is
    /// still read right.
    /// </summary>
    internal const int DefaultConvFactor = 25;

    /// <summary>
    /// Status codes CMS pays under the fee schedule: A active, R restricted
    /// (paid on review), T paid only when no other service is billed (the engine
    /// does not apply that rule; the report says so).
    /// </summary>
    private static readonly HashSet<string> PayableStatus = new(StringComparer.OrdinalIgnoreCase) { "A", "R", "T" };

    /// <summary>The PPRRVU and GPCI CSVs inside an RVU zip. <paramref name="qpp"/> picks the QPP-APM file when CMS publishes two.</summary>
    public static (Stream Pprrvu, Stream Gpci, string PprrvuName, string GpciName) OpenRvuZip(Stream zip, bool qpp)
    {
        using var archive = new ZipArchive(Seekable(zip), ZipArchiveMode.Read, leaveOpen: false);
        var csvs = archive.Entries.Where(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)).ToList();

        var pprrvu = csvs.Where(e => Path.GetFileName(e.FullName).Contains("PPRRVU", StringComparison.OrdinalIgnoreCase)).ToList();
        if (pprrvu.Count > 1)
        {
            bool IsQpp(ZipArchiveEntry e)
            {
                var n = Path.GetFileName(e.FullName).ToUpperInvariant();
                return n.Contains("QPP") && !n.Contains("NONQPP") && !n.Contains("NON_QPP") && !n.Contains("NON-QPP");
            }
            pprrvu = pprrvu.Where(e => IsQpp(e) == qpp).ToList();
        }
        var gpci = csvs.Where(e => Path.GetFileName(e.FullName).Contains("GPCI", StringComparison.OrdinalIgnoreCase)).ToList();

        if (pprrvu.Count != 1)
            throw new FeeScheduleFormatException(
                $"The RVU zip should hold one PPRRVU CSV{(qpp ? " (QPP)" : string.Empty)}; found {pprrvu.Count}: " +
                string.Join(", ", csvs.Select(e => e.Name)));
        if (gpci.Count != 1)
            throw new FeeScheduleFormatException(
                $"The RVU zip should hold one GPCI CSV; found {gpci.Count}. Upload the GPCI file as 'gpci'.");

        return (CopyOut(pprrvu[0]), CopyOut(gpci[0]), pprrvu[0].Name, gpci[0].Name);
    }

    public static PfsRvuFile ReadPprrvu(Stream csv)
    {
        var result = new PfsRvuFile();
        var seen = new HashSet<(string, string)>();
        var anyCodeRow = false;

        var convFactor = DefaultConvFactor;
        var convFactorFromHeader = false;

        foreach (var (row, f) in Rows(csv))
        {
            if (f.Length == 0) continue;
            var code = f[Hcpcs].Trim().ToUpperInvariant();
            if (!anyCodeRow && !convFactorFromHeader)
            {
                // Header rows split "CONV" / "FACTOR" over two lines; either names the column.
                var at = Array.FindIndex(f, c => c.Trim().ToUpperInvariant() is "CONV" or "FACTOR" or "CONV FACTOR" or "CONVERSION FACTOR");
                if (at > Work) { convFactor = at; convFactorFromHeader = true; }
            }
            if (code.Length != 5 || !code.All(char.IsLetterOrDigit)) continue; // title, header and note rows
            if (f.Length <= convFactor)
            {
                if (!anyCodeRow && f.Length > Work && IsNumber(f[Work]))
                    throw new FeeScheduleFormatException(
                        $"Row {row} has {f.Length} columns; a PPRRVU row has at least {convFactor + 1}. Check that the file is the PPRRVU CSV.");
                continue;
            }
            if (!IsNumber(f[Work])) continue; // the "HCPCS" header cell
            anyCodeRow = true;
            result.RowsRead++;

            var modifier = f[Mod].Trim().ToUpperInvariant();
            var status = f[Status].Trim();
            if (!PayableStatus.Contains(status))
            {
                result.Skip(row, code, $"status {(status.Length == 0 ? "blank" : status)} is not paid under the fee schedule");
                continue;
            }

            var work = Number(f[Work]);
            var nonFacPe = Number(f[NonFacPe]);
            var facPe = Number(f[FacPe]);
            var mp = Number(f[Mp]);
            if (work + nonFacPe + facPe + mp == 0m)
            {
                result.Skip(row, code, "no RVUs (carrier or contractor priced)");
                continue;
            }

            // NA: CMS does not pay that setting's PE for the code, so price it at the other setting's PE.
            var nonFacNa = f[NonFacNa].Trim().Equals("NA", StringComparison.OrdinalIgnoreCase);
            var facNa = f[FacNa].Trim().Equals("NA", StringComparison.OrdinalIgnoreCase);
            if (nonFacNa) { result.NonFacilityNotApplicable++; nonFacPe = facPe; }
            if (facNa) { result.FacilityNotApplicable++; facPe = nonFacPe; }

            if (!seen.Add((code, modifier)))
            {
                result.Skip(row, code, "duplicate code and modifier (first row kept)");
                continue;
            }

            if (decimal.TryParse(f[convFactor].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var cf) && cf > 0m)
                result.ConversionFactors.Add(cf);

            var mpiText = f[MultProc].Trim();
            MultipleProcedureIndicator? mpi = mpiText.Length == 1 && char.IsDigit(mpiText[0])
                && Enum.IsDefined(typeof(MultipleProcedureIndicator), (MultipleProcedureIndicator)(mpiText[0] - '0'))
                ? (MultipleProcedureIndicator)(mpiText[0] - '0')
                : null;

            result.Lines.Add(new FeeScheduleLine
            {
                ProcedureCode = code,
                Modifier = modifier.Length == 0 ? null : modifier,
                RateType = FeeScheduleRateType.Rvu,
                Rate = 0m,
                WorkRvu = work,
                PeRvu = nonFacPe,
                PeRvuFacility = facPe,
                MpRvu = mp,
                MultipleProcedureIndicator = mpi,
                // BILAT SURG 1 = the 150% bilateral adjustment applies; 0, 2, 3, 9 do not.
                BilateralAdjustmentApplies = f[BilatSurg].Trim() == "1",
                // ASST SURG 1 = assistant at surgery may not be paid.
                AssistantAtSurgeryAllowed = f[AsstSurg].Trim() != "1",
            });
        }

        if (!anyCodeRow)
            throw new FeeScheduleFormatException("No PPRRVU data rows were found. Check that the file is the PPRRVU CSV from the CMS RVU zip.");

        result.Lines.Sort((a, b) =>
        {
            var c = string.CompareOrdinal(a.ProcedureCode, b.ProcedureCode);
            return c != 0 ? c : string.CompareOrdinal(a.Modifier, b.Modifier);
        });
        return result;
    }

    public static List<PfsLocality> ReadGpci(Stream csv)
    {
        int? mac = null, state = null, number = null, name = null, pw = null, pe = null, mp = null;
        var localities = new List<PfsLocality>();
        var headerFound = false;

        foreach (var (row, f) in Rows(csv))
        {
            if (!headerFound)
            {
                var n = f.Select(FeeScheduleCsvReader.Normalize).ToArray();
                int? Find(Func<string, bool> match) { var i = Array.FindIndex(n, x => match(x)); return i >= 0 ? i : null; }
                mac = Find(x => x.Contains("contractor") || x == "mac" || x == "carrier" || x == "carriernumber");
                number = Find(x => x.Contains("localitynumber") || x == "locality" || x == "localitycode");
                pe = Find(x => x.Contains("pegpci") || x.Contains("practiceexpensegpci"));
                mp = Find(x => x.Contains("mpgpci") || x.Contains("malpracticegpci"));
                pw = Find(x => (x.Contains("pwgpci") || x.Contains("workgpci")) && x.Contains("with") && x.Contains("floor") && !x.Contains("without"))
                     ?? Find(x => x.Contains("pwgpci") || x.Contains("workgpci"));
                if (mac is null || number is null || pw is null || pe is null || mp is null) continue;
                state = Find(x => x == "state");
                name = Find(x => x.Contains("localityname") || x == "name" || x == "feeschedulearea");
                headerFound = true;
                continue;
            }

            string Cell(int? i) => i is { } v && v < f.Length ? f[v].Trim() : string.Empty;
            var macText = Cell(mac);
            var loc = Cell(number);
            if (macText.Length == 0 || loc.Length == 0 || !macText.All(char.IsDigit) || !loc.All(char.IsDigit)) continue; // notes
            if (!IsNumber(Cell(pw)) || !IsNumber(Cell(pe)) || !IsNumber(Cell(mp))) continue;

            localities.Add(new PfsLocality(
                macText, loc.PadLeft(2, '0'), Cell(state).ToUpperInvariant(), Cell(name),
                Number(Cell(pw)), Number(Cell(pe)), Number(Cell(mp))));
        }

        if (!headerFound)
            throw new FeeScheduleFormatException(
                "No GPCI header (MAC, Locality Number, PW GPCI, PE GPCI, MP GPCI) was found. Check that the file is the CMS GPCI CSV.");
        if (localities.Count == 0)
            throw new FeeScheduleFormatException("The GPCI file has a header but no locality rows.");
        return localities;
    }

    private static IEnumerable<(int Row, string[] Fields)> Rows(Stream csv)
    {
        using var reader = new StreamReader(csv, detectEncodingFromByteOrderMarks: true);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false,
            BadDataFound = null,
            MissingFieldFound = null,
            DetectColumnCountChanges = false,
        };
        using var parser = new CsvParser(reader, config);
        while (parser.Read())
        {
            var record = parser.Record;
            if (record is null || record.All(string.IsNullOrWhiteSpace)) continue;
            yield return (parser.Row, record.Select(c => c.TrimStart('﻿')).ToArray());
        }
    }

    private static bool IsNumber(string s) =>
        decimal.TryParse(s.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out _);

    private static decimal Number(string s) =>
        decimal.TryParse(s.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0m;

    private static Stream Seekable(Stream s)
    {
        if (s.CanSeek) return s;
        var copy = new MemoryStream();
        s.CopyTo(copy);
        copy.Position = 0;
        return copy;
    }

    private static MemoryStream CopyOut(ZipArchiveEntry entry)
    {
        var ms = new MemoryStream();
        using (var e = entry.Open()) e.CopyTo(ms);
        ms.Position = 0;
        return ms;
    }
}
