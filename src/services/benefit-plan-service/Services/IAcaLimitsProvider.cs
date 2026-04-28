using System.Text.Json;
using BenefitPlanService.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BenefitPlanService.Services;

/// <summary>
/// ACA 45 CFR §156.130 out-of-pocket cap lookup, keyed by plan year
/// (capability BP 5.7). Loaded once at service startup from the file
/// configured via <see cref="AcaOopLimitsOptions.LimitsFilePath"/>;
/// re-loads happen only on process restart by design — regulatory caps
/// are point-in-time values and a hot-reload would obscure when a value
/// changed.
///
/// <para>
/// <b>Caller behavior by context.</b> When a plan year is not present in
/// the loaded file, write-time validation treats the absence as a hard
/// failure and rejects the plan with a structured error rather than
/// compare against stale or missing regulatory limits. Read-time
/// projection in <c>ChoBenefitPlanProvider</c> may, for legacy hydration,
/// log the missing year and continue with a <c>null</c> cap instead of
/// failing closed.
/// </para>
/// </summary>
public interface IAcaLimitsProvider
{
    /// <summary>
    /// Look up the §156.130 caps for <paramref name="planYear"/>.
    /// Returns <c>null</c> when the plan year is not configured; callers
    /// handle that according to context, with validation rejecting the
    /// plan and some read-time projection paths remaining best-effort for
    /// legacy hydration.
    /// </summary>
    AcaLimits? GetForPlanYear(int planYear);

    /// <summary>
    /// Plan years currently configured. Surfaced for diagnostic /
    /// validation-error messages so operators see exactly which years
    /// the file covers.
    /// </summary>
    IReadOnlyCollection<int> ConfiguredPlanYears { get; }
}

/// <summary>Per-plan-year ACA OOP caps in USD.</summary>
public sealed record AcaLimits(int PlanYear, decimal IndividualCap, decimal FamilyCap);

/// <summary>
/// File-backed implementation. Loads <see cref="AcaOopLimitsOptions.LimitsFilePath"/>
/// on construction; throws on a missing or malformed file so the service
/// fails fast rather than serve adjudications without ACA enforcement.
/// </summary>
public sealed class AcaLimitsProvider : IAcaLimitsProvider
{
    private readonly Dictionary<int, AcaLimits> _byYear;

    public AcaLimitsProvider(
        IOptions<AcaOopLimitsOptions> options,
        IHostEnvironment environment,
        ILogger<AcaLimitsProvider> logger)
    {
        var path = ResolvePath(options.Value.LimitsFilePath, environment);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"ACA OOP limits file not found at '{path}'. Configure via " +
                $"'{AcaOopLimitsOptions.SectionName}:{nameof(AcaOopLimitsOptions.LimitsFilePath)}' " +
                "or place schemas/aca-oop-limits/limits.json in the service content root.");
        }

        var raw = File.ReadAllText(path);
        LimitsFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<LimitsFile>(raw, JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"ACA OOP limits file at '{path}' is malformed: {ex.Message}", ex);
        }

        if (parsed is null || parsed.Limits is null || parsed.Limits.Count == 0)
        {
            throw new InvalidOperationException(
                $"ACA OOP limits file at '{path}' is empty or missing the 'limits' array.");
        }

        _byYear = new Dictionary<int, AcaLimits>(parsed.Limits.Count);
        foreach (var row in parsed.Limits)
        {
            if (row.PlanYear <= 0)
                throw new InvalidOperationException(
                    $"ACA OOP limits file at '{path}' has a row with invalid planYear '{row.PlanYear}'.");
            if (row.IndividualCap <= 0 || row.FamilyCap <= 0)
                throw new InvalidOperationException(
                    $"ACA OOP limits file at '{path}' has non-positive caps for plan year {row.PlanYear}.");
            if (row.FamilyCap < row.IndividualCap)
                throw new InvalidOperationException(
                    $"ACA OOP limits file at '{path}' has familyCap ({row.FamilyCap}) " +
                    $"less than individualCap ({row.IndividualCap}) for plan year {row.PlanYear}.");

            _byYear[row.PlanYear] = new AcaLimits(row.PlanYear, row.IndividualCap, row.FamilyCap);
        }

        logger.LogInformation(
            "Loaded ACA OOP limits for plan years {Years} from {Path}",
            string.Join(", ", _byYear.Keys.OrderBy(y => y)),
            path);
    }

    public AcaLimits? GetForPlanYear(int planYear)
        => _byYear.TryGetValue(planYear, out var row) ? row : null;

    public IReadOnlyCollection<int> ConfiguredPlanYears => _byYear.Keys;

    private static string ResolvePath(string configured, IHostEnvironment env)
        => Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(env.ContentRootPath, configured);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class LimitsFile
    {
        public string? Version { get; set; }
        public string? Source { get; set; }
        public string? LastReviewed { get; set; }
        public List<LimitsRow> Limits { get; set; } = new();
    }

    private sealed class LimitsRow
    {
        public int PlanYear { get; set; }
        public decimal IndividualCap { get; set; }
        public decimal FamilyCap { get; set; }
        public string? Rule { get; set; }
        public string? Note { get; set; }
    }
}
