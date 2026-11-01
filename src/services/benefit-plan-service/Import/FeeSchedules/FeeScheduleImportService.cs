using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BenefitPlanService.Controllers;
using BenefitPlanService.Repositories;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.ReferenceData.Domain;

namespace BenefitPlanService.Import.FeeSchedules;

public interface IFeeScheduleImportService
{
    Task<FeeScheduleImportReport> ImportAsync(
        Stream csv, FeeScheduleImportOptions options, string tenantId, string? actor, CancellationToken ct = default);
}

/// <summary>A plan named by planId does not exist in the caller's tenant.</summary>
public sealed class FeeSchedulePlanNotFoundException(string planId)
    : Exception($"Benefit plan {planId} was not found in this tenant.")
{
    public string PlanId { get; } = planId;
}

/// <summary>
/// Loads a flat-rate fee schedule CSV (a state Medicaid fee schedule, a plan's
/// contracted rate sheet, a Medicare fee schedule already priced per locality)
/// into the canonical fee schedule store that claims adjudication prices from
/// (<c>RateResolutionService</c>). The schedule is either standalone, for a
/// provider contract term to name by id, or a benefit plan's default, which
/// prices lines whose provider has no contract.
///
/// <para>
/// A dry run (the default) writes nothing and reports the columns it read,
/// what it would load, every skip reason with examples, and codes with
/// conflicting rates. Rows that cannot be priced as a dollar amount (by
/// report, manually priced, not covered, $0.00) are left out, so a claim line
/// for that code pends instead of paying a rate the file does not state.
/// </para>
/// See docs/integration/FEE-SCHEDULE-IMPORTER.md.
/// </summary>
public sealed partial class FeeScheduleImportService : IFeeScheduleImportService
{
    private const int SampleLines = 25;

    private static readonly string[] DateFormats =
        ["yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy", "M/d/yy", "MM/dd/yy", "yyyyMMdd", "M-d-yyyy", "MM-dd-yyyy", "yyyy/MM/dd"];

    private static readonly HashSet<string> NoDollarRate = new(StringComparer.OrdinalIgnoreCase)
    {
        "BR", "BYREPORT", "MP", "MANUAL", "MANUALLYPRICED", "MANUALPRICING", "IC", "INDIVIDUALCONSIDERATION",
        "NA", "NC", "NOTCOVERED", "NONCOVERED", "NOTPAYABLE", "NP", "X", "BUNDLED", "B", "RNE", "RC", "CP",
        "CONTRACTORPRICED", "CARRIERPRICED", "SEENOTE", "SEENOTES", "VARIES",
    };

    private readonly IFeeScheduleRepository _schedules;
    private readonly IBenefitPlanRepository _plans;
    private readonly ILogger<FeeScheduleImportService> _logger;

    public FeeScheduleImportService(
        IFeeScheduleRepository schedules,
        IBenefitPlanRepository plans,
        ILogger<FeeScheduleImportService> logger)
    {
        _schedules = schedules;
        _plans = plans;
        _logger = logger;
    }

    /// <summary>Schedule id: the plan-default id for a plan default, otherwise {tenant}:{name slug}:{effective}.</summary>
    public static string ScheduleId(string tenantId, FeeScheduleImportOptions options) =>
        string.IsNullOrWhiteSpace(options.PlanId) is false
            ? PlanDefaultFeeScheduleController.ScheduleId(tenantId, PlanPricingKey.Normalize(options.PlanId!))
            : FeeSchedule.MakeId(tenantId, Slug(options.Name), options.EffectiveDate);

    public async Task<FeeScheduleImportReport> ImportAsync(
        Stream csv, FeeScheduleImportOptions options, string tenantId, string? actor, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(options.PlanId))
        {
            var key = await PlanPricingKey.ResolveAsync(_plans, options.PlanId, tenantId)
                ?? throw new FeeSchedulePlanNotFoundException(options.PlanId.Trim());
            options = options with { PlanId = key };
        }

        var report = new FeeScheduleImportReport
        {
            DryRun = options.DryRun,
            ScheduleId = ScheduleId(tenantId, options),
            Name = options.Name.Trim(),
            Type = options.Type,
            DefaultForPlanId = string.IsNullOrWhiteSpace(options.PlanId) ? null : options.PlanId,
        };

        var lines = Parse(csv, options, report);

        if (lines.Count == 0)
            report.Errors.Add("No priceable lines were found; check the columns report and the skip reasons.");
        if (lines.Count > FeeScheduleImportOptions.MaxLines)
            report.Errors.Add($"The file has {lines.Count} lines; one schedule holds at most {FeeScheduleImportOptions.MaxLines}. Split it with filter.");

        report.LinesLoaded = lines.Count;
        report.LinesWithFacilityRate = lines.Count(l => l.FacilityRate is not null);
        report.Checksum = Checksum(lines);
        report.Lines = options.IncludeLines ? lines : lines.Take(SampleLines).ToList();
        if (lines.Count > 0 && lines.All(l => l.MultipleProcedureIndicator is null))
            report.Warnings.Add(
                "The file has no multiple procedure indicator column, so no line is reduced as a multiple surgery " +
                "and multi-line claims carry a warning. Most state schedules apply their own reduction rules; " +
                "check the state's billing manual.");

        if (report.Errors.Count > 0)
        {
            report.Outcome = FeeScheduleImportOutcome.Rejected;
            return report;
        }

        var existing = await _schedules.GetByIdAsync(tenantId, report.ScheduleId, ct);
        var sameContent = existing is not null
            && existing.Checksum == report.Checksum
            && existing.Name == report.Name
            && existing.Type == options.Type
            && existing.EffectiveDate == Utc(options.EffectiveDate)
            && existing.TermDate == UtcOrNull(options.TermDate);

        if (sameContent)
        {
            report.Outcome = FeeScheduleImportOutcome.Unchanged;
            return report;
        }

        if (existing is not null && !options.Replace)
        {
            report.Outcome = FeeScheduleImportOutcome.Exists;
            report.Errors.Add($"Fee schedule {report.ScheduleId} already exists with other rates; pass replace=true to replace it.");
            return report;
        }

        if (options.DryRun)
        {
            report.Outcome = FeeScheduleImportOutcome.WouldWrite;
            if (existing is not null)
                report.Warnings.Add($"A real run replaces the existing schedule {report.ScheduleId} ({existing.Lines.Count} lines).");
            return report;
        }

        var schedule = new FeeSchedule
        {
            Id = report.ScheduleId,
            TenantId = tenantId,
            Name = report.Name,
            Type = options.Type,
            SourceType = options.SourceType
                ?? (options.Type == FeeScheduleType.Medicaid ? FeeScheduleSourceType.PublicGovernment : FeeScheduleSourceType.PayerContract),
            SourceId = options.SourceId?.Trim() ?? (string.IsNullOrWhiteSpace(options.PlanId) ? "csv-import" : "plan-default"),
            SourceVersion = options.SourceVersion?.Trim() ?? string.Empty,
            Jurisdiction = string.IsNullOrWhiteSpace(options.Jurisdiction) ? null : options.Jurisdiction.Trim().ToUpperInvariant(),
            CodeSystem = "HCPCS",
            Checksum = report.Checksum,
            LicenseClassification = existing?.LicenseClassification ?? LicenseClassification.Unknown,
            DefaultForPlanId = string.IsNullOrWhiteSpace(options.PlanId) ? null : options.PlanId,
            EffectiveDate = Utc(options.EffectiveDate),
            TermDate = UtcOrNull(options.TermDate),
            Lines = lines,
            CreatedDate = existing?.CreatedDate ?? DateTime.UtcNow,
            Revision = existing?.Revision ?? 0,
        };

        await _schedules.UpsertAsync(schedule, ct);
        report.Outcome = existing is null ? FeeScheduleImportOutcome.Created : FeeScheduleImportOutcome.Replaced;

        _logger.LogInformation(
            "AUDIT fee schedule import {Outcome} tenant={Tenant} schedule={Schedule} lines={Lines} source={Source} actor={Actor}",
            report.Outcome, LogSanitizer.Sanitize(tenantId, 256), LogSanitizer.Sanitize(report.ScheduleId, 256),
            lines.Count, LogSanitizer.Sanitize(schedule.SourceId, 256), LogSanitizer.Sanitize(actor, 256));

        return report;
    }

    // ── Parsing ───────────────────────────────────────────────────

    private sealed record Candidate(int Row, FeeScheduleLine Line, DateTime? EffectiveDate, long Rank = 0);

    internal static List<FeeScheduleLine> Parse(Stream csv, FeeScheduleImportOptions options, FeeScheduleImportReport report)
    {
        FeeScheduleColumns? cols = null;
        List<(int Index, string[] Values)> filters = [];
        var scheduleStart = options.EffectiveDate.Date;
        var byKey = new Dictionary<(string Code, string Revenue, string Modifier), List<Candidate>>();
        var facilityOnlyLines = new HashSet<FeeScheduleLine>(ReferenceEqualityComparer.Instance);

        foreach (var (row, f) in FeeScheduleCsvReader.Read(csv, options.Columns, c =>
                 {
                     cols = c;
                     report.Columns = c.Describe();
                     report.HeaderRow = c.HeaderRowNumber;
                     filters = ResolveFilters(c, options.Filters);
                 }))
        {
            report.RowsRead++;
            var c = cols!;
            string Field(int? i) => i is { } v && v < f.Length ? f[v].Trim() : string.Empty;

            if (filters.Count > 0 && !filters.All(x => x.Values.Contains(Field(x.Index), StringComparer.OrdinalIgnoreCase)))
            {
                report.RowsFilteredOut++;
                continue;
            }

            var code = Field(c.Code).Replace(" ", string.Empty).ToUpperInvariant();
            void Skip(string reason)
            {
                report.Skipped[reason] = report.Skipped.GetValueOrDefault(reason) + 1;
                if (report.Skipped[reason] <= FeeScheduleImportReport.ExamplesPerReason)
                    report.SkippedExamples.Add(new FeeScheduleSkippedRow(row, code, reason));
            }

            var revenue = Field(c.RevenueCode);
            if (revenue.Length > 0)
            {
                if (revenue.Length is < 3 or > 4 || !revenue.All(char.IsDigit)) { Skip("revenue code is not 3 or 4 digits"); continue; }
                revenue = revenue.PadLeft(4, '0');
            }

            if (code.Length == 0 && revenue.Length == 0) { Skip("no procedure or revenue code"); continue; }
            if (code.Length > 0 && !ProcedureCodePattern().IsMatch(code))
            {
                Skip("not a 5-character CPT, HCPCS or CDT code");
                continue;
            }

            var modifier = Field(c.Modifier).ToUpperInvariant();
            // TMHP lists a code's professional and technical components as their own rows with
            // type of service I and T and no modifier; on a claim they are billed with 26 and TC.
            if (modifier.Length == 0 && c.TypeOfService is not null)
            {
                modifier = Field(c.TypeOfService).ToUpperInvariant() switch
                {
                    "I" => "26",
                    "T" => "TC",
                    _ => modifier,
                };
            }
            if (modifier.Length > 0 && (modifier.Length != 2 || !modifier.All(char.IsLetterOrDigit)))
            {
                Skip("modifier is not 2 letters or digits");
                continue;
            }
            if (Field(c.SecondModifier).Length > 0) { Skip("second modifier (the engine matches one modifier per rate)"); continue; }

            DateTime? rowEffective = null;
            if (c.EffectiveDate is not null && Field(c.EffectiveDate).Length > 0)
            {
                if (!TryParseDate(Field(c.EffectiveDate), out var eff)) { Skip("unreadable effective date"); continue; }
                if (eff > scheduleStart) { Skip("rate takes effect after the schedule's effective date"); continue; }
                rowEffective = eff;
            }
            if (c.EndDate is not null && Field(c.EndDate).Length > 0)
            {
                if (!TryParseDate(Field(c.EndDate), out var end)) { Skip("unreadable end date"); continue; }
                if (end < scheduleStart) { Skip("rate ended before the schedule's effective date"); continue; }
            }

            decimal? facility = null;
            if (c.FacilityRate is not null)
            {
                var (fr, frProblem) = ParseMoney(Field(c.FacilityRate));
                if (frProblem is null && (fr > 0m || options.KeepZeroRates)) facility = fr;
            }

            var (rate, rateProblem) = ParseMoney(Field(c.Rate));
            // A code priced only in a facility (TMHP leaves the non-facility side blank for
            // inpatient surgery) pays its facility rate wherever it is billed.
            var facilityOnly = rateProblem == "no rate" && facility is not null;
            if (facilityOnly) (rate, rateProblem) = (facility!.Value, null);
            if (rateProblem is not null) { Skip(rateProblem); continue; }
            if (rate == 0m && !options.KeepZeroRates) { Skip("$0.00 rate (not loaded; pass keepZeroRates=true to load)"); continue; }

            MultipleProcedureIndicator? mpi = null;
            var mpiText = Field(c.MultipleProcedureIndicator);
            if (mpiText.Length == 1 && char.IsDigit(mpiText[0])
                && Enum.IsDefined(typeof(MultipleProcedureIndicator), (MultipleProcedureIndicator)(mpiText[0] - '0')))
            {
                mpi = (MultipleProcedureIndicator)(mpiText[0] - '0');
            }

            var line = new FeeScheduleLine
            {
                ProcedureCode = code,
                RevenueCode = revenue.Length == 0 ? null : revenue,
                Modifier = modifier.Length == 0 ? null : modifier,
                RateType = FeeScheduleRateType.FlatRate,
                Rate = rate,
                FacilityRate = facility,
                MultipleProcedureIndicator = mpi,
            };

            // Like the plan default endpoint: a revenue code only distinguishes a revenue-code-only line.
            var key = (code, code.Length == 0 ? revenue : string.Empty, modifier);
            if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = [];
            // FilterOrder: the position of this row's value in each filter that lists several,
            // the first such filter deciding first.
            long rank = 0;
            foreach (var (index, values) in filters.Where(x => x.Values.Length > 1))
            {
                var position = Array.FindIndex(values, v => string.Equals(v, Field(index), StringComparison.OrdinalIgnoreCase));
                rank = rank * 1_000 + position;
            }
            // TMHP sometimes prices a code's non-facility and facility sides on two rows: one
            // line with both rates, not two rates for one code.
            var other = list.FirstOrDefault(x => x.Rank == rank && x.EffectiveDate == rowEffective
                && (facilityOnly
                    ? x.Line.FacilityRate is null && !facilityOnlyLines.Contains(x.Line)
                    : facility is null && facilityOnlyLines.Contains(x.Line)));
            if (other is not null)
            {
                if (facilityOnly)
                {
                    other.Line.FacilityRate = facility;
                }
                else
                {
                    facilityOnlyLines.Remove(other.Line);
                    other.Line.FacilityRate = other.Line.Rate;
                    other.Line.Rate = rate;
                }
                continue;
            }
            if (facilityOnly) facilityOnlyLines.Add(line);
            list.Add(new Candidate(row, line, rowEffective, rank));
        }

        var result = new List<FeeScheduleLine>(byKey.Count);
        foreach (var (key, candidates) in byKey)
        {
            var pick = Resolve(candidates, options.OnDuplicate, out var conflict);
            if (conflict)
            {
                report.DuplicateKeys++;
                if (report.DuplicateExamples.Count < 10)
                    report.DuplicateExamples.Add(new FeeScheduleDuplicate(
                        key.Code.Length > 0 ? key.Code : "rev " + key.Revenue,
                        key.Modifier.Length == 0 ? null : key.Modifier,
                        candidates.Select(x => x.Row).ToList(),
                        candidates.Select(x => x.Line.Rate).ToList()));
            }
            if (pick is not null) result.Add(pick);
        }

        report.LinesPricedOnlyInFacility = result.Count(facilityOnlyLines.Contains);
        if (report.LinesPricedOnlyInFacility > 0)
        {
            report.Warnings.Add(
                $"{report.LinesPricedOnlyInFacility} line(s) have only a facility rate; it is also their rate outside a facility.");
        }

        if (report.DuplicateKeys > 0 && options.OnDuplicate == DuplicateRatePolicy.Reject)
        {
            report.Errors.Add(
                $"{report.DuplicateKeys} code(s) have more than one rate. Narrow the file with filter " +
                "(for example filter=Provider Type=Physician), or choose onDuplicate=First, Lowest, Highest or FilterOrder.");
        }
        else if (report.DuplicateKeys > 0)
        {
            report.Warnings.Add($"{report.DuplicateKeys} code(s) had more than one rate; kept by onDuplicate={options.OnDuplicate}.");
        }

        return result
            .OrderBy(l => l.ProcedureCode, StringComparer.Ordinal)
            .ThenBy(l => l.RevenueCode, StringComparer.Ordinal)
            .ThenBy(l => l.Modifier, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// One line per key. Identical repeats are not a conflict. When rows carry effective
    /// dates the latest one in force wins; whatever still differs is a conflict, kept by policy
    /// (none kept under Reject).
    /// </summary>
    private static FeeScheduleLine? Resolve(List<Candidate> candidates, DuplicateRatePolicy policy, out bool conflict)
    {
        conflict = false;
        if (candidates.Count == 1) return candidates[0].Line;

        var inForce = candidates;
        if (candidates.Any(c => c.EffectiveDate is not null))
        {
            var latest = candidates.Max(c => c.EffectiveDate ?? DateTime.MinValue);
            inForce = candidates.Where(c => (c.EffectiveDate ?? DateTime.MinValue) == latest).ToList();
        }

        if (inForce.Select(c => (c.Line.Rate, c.Line.FacilityRate)).Distinct().Count() == 1)
            return inForce[0].Line;

        conflict = true;
        return policy switch
        {
            DuplicateRatePolicy.First => inForce[0].Line,
            DuplicateRatePolicy.Lowest => inForce.MinBy(c => c.Line.Rate)!.Line,
            DuplicateRatePolicy.Highest => inForce.MaxBy(c => c.Line.Rate)!.Line,
            DuplicateRatePolicy.FilterOrder => inForce.MinBy(c => c.Rank)!.Line,
            _ => null,
        };
    }

    /// <summary>
    /// Each filter keeps rows whose column equals the value, or one of several values
    /// separated by "|" (e.g. "Proc Type" = "I|J|K|L|M|N"). Several filters must all match.
    /// </summary>
    private static List<(int Index, string[] Values)> ResolveFilters(
        FeeScheduleColumns columns, IReadOnlyList<(string Column, string Value)> filters)
    {
        var normalized = columns.Header.Select(FeeScheduleCsvReader.Normalize).ToList();
        var result = new List<(int, string[])>();
        foreach (var (column, value) in filters)
        {
            var i = normalized.IndexOf(FeeScheduleCsvReader.Normalize(column));
            if (i < 0)
                throw new FeeScheduleFormatException(
                    $"Filter column '{column}' is not in the header (row {columns.HeaderRowNumber}): {string.Join(", ", columns.Header.Where(h => h.Length > 0))}.");
            result.Add((i, value.Split('|').Select(v => v.Trim()).ToArray()));
        }
        return result;
    }

    /// <summary>A dollar amount ("$1,234.56", "12.5", "(3.00)" is negative), or the reason it is not one.</summary>
    internal static (decimal Amount, string? Problem) ParseMoney(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return (0m, "no rate");

        var letters = new string(t.Where(char.IsLetterOrDigit).ToArray());
        if (NoDollarRate.Contains(letters) || t is "*" or "-" or "--")
            return (0m, "no dollar rate (by report, manually priced or not covered)");

        var negative = t.StartsWith('(') && t.EndsWith(')');
        var cleaned = t.Trim('(', ')').Replace("$", string.Empty).Replace(",", string.Empty).Trim();
        if (!decimal.TryParse(cleaned, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var amount))
            return (0m, "unreadable rate");
        if (negative) amount = -amount;
        if (amount < 0m) return (0m, "negative rate");
        return (amount, null);
    }

    internal static bool TryParseDate(string text, out DateTime date)
    {
        var t = text.Trim();
        var space = t.IndexOf(' ');
        if (space > 0) t = t[..space]; // "1/1/2026 12:00:00 AM"
        if (DateTime.TryParseExact(t, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            date = date.Date;
            return true;
        }
        return false;
    }

    internal static string Checksum(IEnumerable<FeeScheduleLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var l in lines)
        {
            sb.Append(l.ProcedureCode).Append('|').Append(l.RevenueCode).Append('|').Append(l.Modifier).Append('|')
              .Append(l.Rate.ToString(CultureInfo.InvariantCulture)).Append('|')
              .Append(l.FacilityRate?.ToString(CultureInfo.InvariantCulture)).Append('|')
              .Append(l.MultipleProcedureIndicator is { } m ? ((byte)m).ToString(CultureInfo.InvariantCulture) : string.Empty)
              .Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    internal static string Slug(string name)
    {
        var slug = SlugPattern().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "fee-schedule" : slug[..Math.Min(slug.Length, 80)];
    }

    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d.Date, DateTimeKind.Utc);
    private static DateTime? UtcOrNull(DateTime? d) => d is { } v ? Utc(v) : null;

    [GeneratedRegex("^[A-Z0-9]{5}$")]
    private static partial Regex ProcedureCodePattern();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugPattern();
}
