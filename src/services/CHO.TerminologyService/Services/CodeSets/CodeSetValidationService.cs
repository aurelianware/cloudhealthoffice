using CHO.TerminologyService.Models;
using CloudHealthOffice.Infrastructure.Logging;

namespace CHO.TerminologyService.Services.CodeSets;

/// <summary>Storage for dated code sets (global data, no tenant).</summary>
public interface ICodeSetRepository
{
    Task<List<CodeSetRelease>> GetReleasesAsync(string? system = null, CancellationToken ct = default);
    Task<List<CodeValidityPeriod>> FindPeriodsAsync(string system, string code, CancellationToken ct = default);

    /// <summary>
    /// Many codes at once (batch validation). Returns every period of every
    /// requested code of that system.
    /// </summary>
    Task<List<CodeValidityPeriod>> FindPeriodsAsync(string system, IReadOnlyCollection<string> codes, CancellationToken ct = default);

    Task SaveReleaseAsync(CodeSetRelease release, IReadOnlyList<CodeValidityPeriod> periods, CancellationToken ct = default);

    /// <summary>Ends a release on <paramref name="effectiveTo"/>, and every period of it that ran later.</summary>
    Task EndReleaseAsync(string releaseId, DateTime effectiveTo, CancellationToken ct = default);

    Task DeleteReleaseAsync(string releaseId, CancellationToken ct = default);
}

public sealed class CodeSetLoadRequest
{
    public string System { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public DateOnly? EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public string? LoadedBy { get; set; }
}

/// <summary>
/// Loads dated code-set releases and answers "was this code valid on this date
/// of service?".
///
/// <para><b>Release windows.</b> Each release speaks for a window of dates. When
/// none is given, ICD-10 windows default to the federal fiscal year named in the
/// version (FY2026 = 2025-10-01 to 2026-09-30) and HCPCS windows to the calendar
/// quarter that holds the effective date. A release that starts inside an
/// earlier release's window ends that window the day before, so an April ICD-10
/// update or a quarterly HCPCS file replaces the earlier file from its own start
/// date onward. Reloading the same version replaces it.</para>
///
/// <para><b>Answers.</b> A date no release covers is "unknown", never "invalid":
/// a missing file must not turn into denials. Inside a covered date a code is
/// valid when one of its periods covers the date (and, for ICD-10, it is
/// billable); otherwise it is invalid as not yet effective, terminated, or not
/// in the code set.</para>
/// </summary>
public sealed class CodeSetValidationService
{
    public const int MaxBatch = 2_000;

    private readonly ICodeSetRepository _repository;
    private readonly ILogger<CodeSetValidationService> _logger;

    public CodeSetValidationService(ICodeSetRepository repository, ILogger<CodeSetValidationService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    // ── Loading ────────────────────────────────────────────────────────

    public async Task<CodeSetLoadResult> LoadAsync(CodeSetLoadRequest request, Stream file, CancellationToken ct = default)
    {
        var result = new CodeSetLoadResult { Version = request.Version?.Trim() ?? string.Empty };
        var system = CodeSetSystems.Canonical(request.System);
        if (system is null)
        {
            result.Errors.Add($"system must be one of: {string.Join(", ", CodeSetSystems.All)}");
            return result;
        }
        result.System = system;
        if (string.IsNullOrWhiteSpace(result.Version))
        {
            result.Errors.Add("version is required (for example FY2026 or 2026-Q2)");
            return result;
        }

        ParsedCodeSet parsed;
        if (string.Equals(request.Format, Icd10OrderFileParser.Format, StringComparison.OrdinalIgnoreCase))
        {
            if (system == CodeSetSystems.Hcpcs)
            {
                result.Errors.Add("The ICD-10 order-file format is for ICD-10-CM or ICD-10-PCS");
                return result;
            }
            parsed = await Icd10OrderFileParser.ParseAsync(file, ct);
        }
        else if (string.Equals(request.Format, HcpcsCsvParser.Format, StringComparison.OrdinalIgnoreCase))
        {
            if (system != CodeSetSystems.Hcpcs)
            {
                result.Errors.Add("The HCPCS CSV format is for the HCPCS system");
                return result;
            }
            parsed = await HcpcsCsvParser.ParseAsync(file, ct);
        }
        else
        {
            result.Errors.Add($"format must be {Icd10OrderFileParser.Format} or {HcpcsCsvParser.Format}");
            return result;
        }

        result.RowsSkipped = parsed.RowsSkipped;
        result.Errors.AddRange(parsed.Errors);
        if (parsed.Codes.Count == 0)
        {
            result.Errors.Add("The file held no codes");
            return result;
        }

        var window = Window(system, result.Version, request.EffectiveFrom, request.EffectiveTo);
        if (window is null)
        {
            result.Errors.Add("effectiveFrom is required unless the version names a fiscal year (FY2026)");
            return result;
        }
        var (from, to) = window.Value;
        if (to is not null && to < from)
        {
            result.Errors.Add("effectiveTo is before effectiveFrom");
            return result;
        }

        // Place the release on the system's timeline.
        var existing = await _repository.GetReleasesAsync(system, ct);
        foreach (var same in existing.Where(r =>
                     string.Equals(r.Version, result.Version, StringComparison.OrdinalIgnoreCase) || r.EffectiveFrom == from))
        {
            await _repository.DeleteReleaseAsync(same.Id, ct);
            result.AdjustedReleases.Add($"{same.Version}: replaced");
        }
        existing = existing.Where(r =>
            !string.Equals(r.Version, result.Version, StringComparison.OrdinalIgnoreCase) && r.EffectiveFrom != from).ToList();

        foreach (var earlier in existing.Where(r => r.EffectiveFrom < from && (r.EffectiveTo is null || r.EffectiveTo >= from)))
        {
            var end = from.AddDays(-1);
            await _repository.EndReleaseAsync(earlier.Id, end, ct);
            result.AdjustedReleases.Add($"{earlier.Version}: now ends {DateOnly.FromDateTime(end):yyyy-MM-dd}");
        }

        var nextStart = existing.Where(r => r.EffectiveFrom > from).Select(r => (DateTime?)r.EffectiveFrom).Min();
        if (nextStart is not null && (to is null || to >= nextStart))
            to = nextStart.Value.AddDays(-1);

        var releaseId = $"{system}|{result.Version}|{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        var periods = new List<CodeValidityPeriod>(parsed.Codes.Count);
        foreach (var code in parsed.Codes)
        {
            var periodFrom = code.EffectiveFrom is { } f && f > from ? f : from;
            var periodTo = Min(code.EffectiveTo, to);
            // A code terminated before this release starts is not valid in it.
            if (periodTo is not null && periodTo < periodFrom) continue;
            periods.Add(new CodeValidityPeriod
            {
                Id = $"{releaseId}|{code.Code}",
                System = system,
                Code = code.Code,
                Display = code.Display,
                Billable = code.Billable,
                EffectiveFrom = periodFrom,
                EffectiveTo = periodTo,
                ReleaseId = releaseId,
            });
        }

        var release = new CodeSetRelease
        {
            Id = releaseId,
            System = system,
            Version = result.Version,
            Format = request.Format.ToUpperInvariant(),
            EffectiveFrom = from,
            EffectiveTo = to,
            CodeCount = periods.Count,
            LoadedBy = request.LoadedBy,
        };
        await _repository.SaveReleaseAsync(release, periods, ct);

        _logger.LogInformation("Loaded code set {System} {Version}: {Count} codes, {From:yyyy-MM-dd} to {To}",
            system, LogSanitizer.Sanitize(result.Version), periods.Count, from, to?.ToString("yyyy-MM-dd") ?? "open");

        result.Success = true;
        result.ReleaseId = releaseId;
        result.EffectiveFrom = DateOnly.FromDateTime(from);
        result.EffectiveTo = to is null ? null : DateOnly.FromDateTime(to.Value);
        result.CodesLoaded = periods.Count;
        return result;
    }

    /// <summary>The release window, or null when there is no start date to use.</summary>
    public static (DateTime From, DateTime? To)? Window(string system, string version, DateOnly? from, DateOnly? to)
    {
        DateTime? start = from is null ? null : Utc(from.Value);
        DateTime? end = to is null ? null : Utc(to.Value);

        if (system is CodeSetSystems.Icd10Cm or CodeSetSystems.Icd10Pcs && FiscalYear(version) is { } fy)
        {
            var fiscalYearEnd = new DateTime(fy, 9, 30, 0, 0, 0, DateTimeKind.Utc);
            start ??= new DateTime(fy - 1, 10, 1, 0, 0, 0, DateTimeKind.Utc);
            // An explicit start inside the fiscal year (the April update) still ends with it.
            if (end is null && start <= fiscalYearEnd) end = fiscalYearEnd;
        }

        if (start is null) return null;

        if (system == CodeSetSystems.Hcpcs && end is null)
        {
            var quarterStartMonth = (start.Value.Month - 1) / 3 * 3 + 1;
            end = new DateTime(start.Value.Year, quarterStartMonth, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(3).AddDays(-1);
        }

        return (start.Value, end);
    }

    /// <summary>The fiscal year in a version such as FY2026, FY26, 2026 or ICD10CM-FY2026-APR.</summary>
    public static int? FiscalYear(string version)
    {
        var fy = System.Text.RegularExpressions.Regex.Match(version, @"FY[-_ ]?(\d{4}|\d{2})(?!\d)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (fy.Success)
        {
            var year = int.Parse(fy.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            return year < 100 ? 2000 + year : year;
        }
        var plain = System.Text.RegularExpressions.Regex.Match(version, @"(?<!\d)(20\d{2})(?!\d)");
        return plain.Success ? int.Parse(plain.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    // ── Validation ─────────────────────────────────────────────────────

    public async Task<CodeValidityResponse> ValidateAsync(CodeValidityRequest request, CancellationToken ct = default)
        => (await ValidateManyAsync([request], ct))[0];

    /// <summary>Validates many codes; answers come back in request order.</summary>
    public async Task<List<CodeValidityResponse>> ValidateManyAsync(
        IReadOnlyList<CodeValidityRequest> requests, CancellationToken ct = default)
    {
        var responses = new CodeValidityResponse[requests.Count];
        var releases = await _repository.GetReleasesAsync(null, ct);

        foreach (var group in requests
                     .Select((r, i) => (Request: r, Index: i, System: CodeSetSystems.Canonical(r.System)))
                     .GroupBy(x => x.System))
        {
            if (group.Key is null || releases.All(r => r.System != group.Key))
            {
                foreach (var item in group)
                    responses[item.Index] = Unknown(item.Request, group.Key ?? item.Request.System,
                        CodeValidityReason.CodeSystemNotLoaded,
                        "No dated code set is loaded for this code system, so the code was not checked.");
                continue;
            }

            var system = group.Key;
            var codes = group.Select(x => CodeSetSystems.NormalizeCode(x.Request.Code)).Where(c => c.Length > 0)
                .Distinct(StringComparer.Ordinal).ToList();
            var periods = codes.Count == 0 ? [] : await _repository.FindPeriodsAsync(system, codes, ct);
            var byCode = periods.ToLookup(p => p.Code, StringComparer.Ordinal);
            var systemReleases = releases.Where(r => r.System == system).ToList();

            foreach (var item in group)
                responses[item.Index] = Evaluate(item.Request, system, systemReleases, byCode[CodeSetSystems.NormalizeCode(item.Request.Code)].ToList());
        }

        return [.. responses];
    }

    public static CodeValidityResponse Evaluate(
        CodeValidityRequest request, string system, IReadOnlyList<CodeSetRelease> releases, IReadOnlyList<CodeValidityPeriod> periods)
    {
        var date = request.Date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var day = Utc(date);
        var code = CodeSetSystems.NormalizeCode(request.Code);

        var release = releases.Where(r => r.Covers(day)).OrderByDescending(r => r.EffectiveFrom).FirstOrDefault();
        if (release is null)
            return Unknown(request, system, CodeValidityReason.NoReleaseForDate,
                $"No loaded release covers {date:yyyy-MM-dd}, so the code was not checked.");

        var response = new CodeValidityResponse
        {
            System = system,
            Code = code,
            Date = date,
            ReleaseVersion = release.Version,
            Display = periods.OrderByDescending(p => p.EffectiveFrom).FirstOrDefault()?.Display,
        };

        var covering = periods.Where(p => p.Covers(day)).OrderByDescending(p => p.EffectiveFrom).FirstOrDefault();
        if (covering is not null)
        {
            response.Display = covering.Display;
            response.EffectiveFrom = DateOnly.FromDateTime(covering.EffectiveFrom);
            response.EffectiveTo = covering.EffectiveTo is null ? null : DateOnly.FromDateTime(covering.EffectiveTo.Value);
            if (covering.Billable)
            {
                response.Result = true;
                response.Status = CodeValidityStatus.Valid;
                return response;
            }
            return Invalid(response, CodeValidityReason.NotBillable,
                $"{code} is a header code in {release.Version}; a claim needs a more specific code.");
        }

        var later = periods.Where(p => p.EffectiveFrom > day).OrderBy(p => p.EffectiveFrom).FirstOrDefault();
        var earlier = periods.Where(p => p.EffectiveTo < day).OrderByDescending(p => p.EffectiveTo).FirstOrDefault();

        if (earlier is not null)
        {
            response.EffectiveFrom = DateOnly.FromDateTime(earlier.EffectiveFrom);
            response.EffectiveTo = DateOnly.FromDateTime(earlier.EffectiveTo!.Value);
            return Invalid(response, CodeValidityReason.Terminated,
                $"{code} was valid through {response.EffectiveTo:yyyy-MM-dd}, before the {date:yyyy-MM-dd} date of service.");
        }
        if (later is not null)
        {
            response.EffectiveFrom = DateOnly.FromDateTime(later.EffectiveFrom);
            return Invalid(response, CodeValidityReason.NotYetEffective,
                $"{code} takes effect {response.EffectiveFrom:yyyy-MM-dd}, after the {date:yyyy-MM-dd} date of service.");
        }

        return Invalid(response, CodeValidityReason.NotInCodeSet,
            $"{code} is not in the {release.Version} code set that covers {date:yyyy-MM-dd}.");
    }

    private static CodeValidityResponse Invalid(CodeValidityResponse response, string reason, string message)
    {
        response.Result = false;
        response.Status = CodeValidityStatus.Invalid;
        response.Reason = reason;
        response.Message = message;
        return response;
    }

    private static CodeValidityResponse Unknown(CodeValidityRequest request, string system, string reason, string message) => new()
    {
        Result = null,
        Status = CodeValidityStatus.Unknown,
        System = system,
        Code = CodeSetSystems.NormalizeCode(request.Code),
        Date = request.Date ?? DateOnly.FromDateTime(DateTime.UtcNow),
        Reason = reason,
        Message = message,
    };

    private static DateTime Utc(DateOnly d) => d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    private static DateTime? Min(DateTime? a, DateTime? b) =>
        a is null ? b : b is null ? a : a < b ? a : b;
}
