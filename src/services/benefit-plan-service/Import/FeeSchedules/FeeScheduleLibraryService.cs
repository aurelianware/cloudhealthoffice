using System.Globalization;
using BenefitPlanService.Controllers;
using BenefitPlanService.Repositories;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Money;
using CloudHealthOffice.ReferenceData.Domain;

namespace BenefitPlanService.Import.FeeSchedules;

/// <summary>
/// The shared fee schedule library: reference schedules (Medicare PFS per
/// locality, Medi-Cal, TMHP, ...) that Aurelianware loads once under a
/// reserved library tenant, and that any tenant can browse and deploy into
/// its own store at a percentage, as a plan default or a standalone schedule.
///
/// <para>
/// A deploy is a copy, not a reference: the tenant's schedule is priced from
/// its own document and a later library update does not change it. The copy
/// scales every dollar value by the percentage: line rates and facility
/// rates, and the schedule's conversion factor (RVU schedules), DRG base
/// rate, per diem rate and percent of Medicare. So 110% of a Medicare PFS
/// locality prices each code at 110% of that locality's Medicare amount.
/// </para>
///
/// <para>
/// Schedules keyed by CPT or HCPCS carry AMA CPT content. A tenant deploying
/// one confirms it holds its own CPT license (<c>licenseAcknowledged</c>);
/// the confirmation is written to the audit log with the actor.
/// </para>
/// </summary>
public interface IFeeScheduleLibraryService
{
    string LibraryTenantId { get; }
    Task<IReadOnlyList<FeeScheduleSummary>> ListAsync(string tenantId, string? jurisdiction, CancellationToken ct);
    Task<FeeScheduleDetail?> GetAsync(string tenantId, string id, string? code, int skip, int take, CancellationToken ct);
    Task<FeeScheduleDeployReport> DeployAsync(string tenantId, string actor, FeeScheduleDeployRequest request, CancellationToken ct);
    Task<FeeScheduleDeployReport> PublishToLibraryAsync(string tenantId, string actor, FeeSchedulePublishRequest request, CancellationToken ct);
}

public sealed class FeeScheduleLibraryService : IFeeScheduleLibraryService
{
    public const string DefaultLibraryTenantId = "cho-fee-schedule-library";
    public const decimal MinPercent = 1m;
    public const decimal MaxPercent = 1000m;

    /// <summary>Codes shown in a deploy preview when the schedule has them (common E/M, imaging, labs, DME).</summary>
    private static readonly string[] PreviewCodes = { "99213", "99214", "99203", "99285", "70450", "80053", "85025", "97110", "E0601" };

    private readonly IFeeScheduleRepository _schedules;
    private readonly IBenefitPlanRepository _plans;
    private readonly ILogger<FeeScheduleLibraryService> _logger;

    public FeeScheduleLibraryService(
        IFeeScheduleRepository schedules,
        IBenefitPlanRepository plans,
        IConfiguration configuration,
        ILogger<FeeScheduleLibraryService> logger)
    {
        _schedules = schedules;
        _plans = plans;
        _logger = logger;
        var configured = configuration["FeeScheduleLibrary:TenantId"];
        LibraryTenantId = string.IsNullOrWhiteSpace(configured) ? DefaultLibraryTenantId : configured.Trim();
    }

    public string LibraryTenantId { get; }

    public async Task<IReadOnlyList<FeeScheduleSummary>> ListAsync(string tenantId, string? jurisdiction, CancellationToken ct)
    {
        var all = new List<FeeSchedule>();
        for (var page = 1; page <= 20; page++)
        {
            var batch = await _schedules.ListAsync(tenantId, page, 100, ct);
            all.AddRange(batch);
            if (batch.Count < 100) break;
        }

        return all
            .Where(s => string.IsNullOrWhiteSpace(jurisdiction)
                        || string.Equals(s.Jurisdiction, jurisdiction.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Jurisdiction ?? "~").ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(FeeScheduleSummary.From)
            .ToList();
    }

    public async Task<FeeScheduleDetail?> GetAsync(string tenantId, string id, string? code, int skip, int take, CancellationToken ct)
    {
        var s = await _schedules.GetByIdAsync(tenantId, id, ct);
        if (s is null) return null;

        take = Math.Clamp(take, 1, 500);
        skip = Math.Max(0, skip);
        var matching = s.Lines
            .Where(l => string.IsNullOrWhiteSpace(code)
                        || l.ProcedureCode.StartsWith(code.Trim(), StringComparison.OrdinalIgnoreCase)
                        || (l.RevenueCode?.StartsWith(code.Trim(), StringComparison.Ordinal) ?? false))
            .OrderBy(l => l.ProcedureCode, StringComparer.Ordinal).ThenBy(l => l.Modifier ?? string.Empty, StringComparer.Ordinal)
            .ToList();

        return new FeeScheduleDetail
        {
            Schedule = FeeScheduleSummary.From(s),
            LineCount = s.Lines.Count,
            MatchingCount = matching.Count,
            Lines = matching.Skip(skip).Take(take).Select(l => FeeScheduleLineView.From(s, l)).ToList(),
        };
    }

    public async Task<FeeScheduleDeployReport> DeployAsync(string tenantId, string actor, FeeScheduleDeployRequest request, CancellationToken ct)
    {
        var report = new FeeScheduleDeployReport { DryRun = request.DryRun, Percent = request.Percent };
        if (string.Equals(tenantId, LibraryTenantId, StringComparison.Ordinal))
            return report.Fail("The library tenant cannot deploy into itself.");
        if (string.IsNullOrWhiteSpace(request.SourceId))
            return report.Fail("sourceId is required.");
        if (request.Percent is < MinPercent or > MaxPercent)
            return report.Fail($"percent must be between {MinPercent} and {MaxPercent} (100 = the library rates).");
        if (request.EffectiveDate is null)
            return report.Fail("effectiveDate is required.");
        if (request.TermDate is { } term && term.Date < request.EffectiveDate.Value.Date)
            return report.Fail("termDate is before effectiveDate.");
        var hasPlan = !string.IsNullOrWhiteSpace(request.PlanId);
        if (!hasPlan && string.IsNullOrWhiteSpace(request.Name))
            return report.Fail("Give planId (deploy as the plan's default schedule) or name (a standalone schedule).");

        var source = await _schedules.GetByIdAsync(LibraryTenantId, request.SourceId.Trim(), ct);
        if (source is null)
            return report.NotFound($"Library schedule '{request.SourceId}' not found.");

        report.Source = FeeScheduleSummary.From(source);
        report.RequiresCptLicense = CarriesCpt(source);
        if (report.RequiresCptLicense && !request.LicenseAcknowledged && !request.DryRun)
            return report.Fail("This schedule is keyed by CPT/HCPCS codes. Confirm your organization holds an AMA CPT license (licenseAcknowledged=true) to deploy it.");

        string targetId;
        string? planKey = null;
        if (hasPlan)
        {
            planKey = await PlanPricingKey.ResolveAsync(_plans, request.PlanId!, tenantId);
            if (planKey is null)
                return report.NotFound($"Plan '{request.PlanId}' not found in this tenant.");
            targetId = PlanDefaultFeeScheduleController.ScheduleId(tenantId, planKey);
        }
        else
        {
            targetId = FeeSchedule.MakeId(tenantId, request.Name!.Trim(), request.EffectiveDate.Value.Date);
        }

        var factor = request.Percent / 100m;
        var copy = Scale(source, factor);
        copy.Id = targetId;
        copy.TenantId = tenantId;
        copy.Name = hasPlan
            ? (string.IsNullOrWhiteSpace(request.Name) ? $"{source.Name} at {Pct(request.Percent)}" : request.Name.Trim())
            : request.Name!.Trim();
        copy.DefaultForPlanId = planKey;
        copy.SourceType = FeeScheduleSourceType.PayerContract;
        copy.SourceId = $"library:{source.Id}";
        copy.SourceVersion = $"{source.SourceVersion}@{Pct(request.Percent)}";
        copy.IsGlobal = false;
        copy.LicenseClassification = report.RequiresCptLicense ? LicenseClassification.CustomerProvided : source.LicenseClassification;
        copy.EffectiveDate = DateTime.SpecifyKind(request.EffectiveDate.Value.Date, DateTimeKind.Utc);
        copy.TermDate = request.TermDate is { } t ? DateTime.SpecifyKind(t.Date, DateTimeKind.Utc) : null;

        report.TargetId = targetId;
        report.TargetName = copy.Name;
        report.PlanKey = planKey;
        if (!string.IsNullOrWhiteSpace(source.BaseMpfsFeeScheduleId))
            report.Warnings.Add($"This schedule prices from Medicare schedule '{source.BaseMpfsFeeScheduleId}'. Deploy that schedule too and point this one at your copy, or its percent-of-Medicare lines will not price.");
        report.LineCount = copy.Lines.Count;
        report.Preview = Preview(source, copy);

        var existing = await _schedules.GetByIdAsync(tenantId, targetId, ct);
        if (existing is not null && !request.Replace)
        {
            report.Outcome = FeeScheduleImportOutcome.Exists;
            report.Warnings.Add(hasPlan
                ? "The plan already has a default schedule; pass replace=true to replace it."
                : "A schedule with this name and effective date exists; pass replace=true to replace it.");
            return report;
        }
        if (request.DryRun)
        {
            report.Outcome = FeeScheduleImportOutcome.WouldWrite;
            return report;
        }

        copy.CreatedDate = existing?.CreatedDate ?? DateTime.UtcNow;
        copy.LastUpdatedDate = DateTime.UtcNow;
        copy.Revision = existing?.Revision ?? 0;
        await _schedules.UpsertAsync(copy, ct);
        report.Outcome = existing is null ? FeeScheduleImportOutcome.Created : FeeScheduleImportOutcome.Replaced;

        _logger.LogInformation(
            "AUDIT fee schedule library deploy tenant={Tenant} source={Source} target={Target} plan={Plan} percent={Percent} lines={Lines} cptLicenseAcknowledged={Ack} actor={Actor}",
            LogSanitizer.Sanitize(tenantId, 256), LogSanitizer.Sanitize(source.Id, 256), LogSanitizer.Sanitize(targetId, 256),
            LogSanitizer.Sanitize(planKey, 128), request.Percent, copy.Lines.Count, request.LicenseAcknowledged, LogSanitizer.Sanitize(actor, 256));
        return report;
    }

    public async Task<FeeScheduleDeployReport> PublishToLibraryAsync(string tenantId, string actor, FeeSchedulePublishRequest request, CancellationToken ct)
    {
        var report = new FeeScheduleDeployReport { DryRun = request.DryRun, Percent = 100m };
        if (string.IsNullOrWhiteSpace(request.SourceId))
            return report.Fail("sourceId is required.");

        var source = await _schedules.GetByIdAsync(tenantId, request.SourceId.Trim(), ct);
        if (source is null)
            return report.NotFound($"Schedule '{request.SourceId}' not found in your tenant.");

        var name = string.IsNullOrWhiteSpace(request.Name) ? source.Name : request.Name.Trim();
        var copy = Scale(source, 1m);
        copy.Id = FeeSchedule.MakeId(LibraryTenantId, name, source.EffectiveDate.Date);
        copy.TenantId = LibraryTenantId;
        copy.Name = name;
        copy.DefaultForPlanId = null;
        copy.IsGlobal = true;
        if (!string.IsNullOrWhiteSpace(request.Jurisdiction)) copy.Jurisdiction = request.Jurisdiction.Trim().ToUpperInvariant();

        report.Source = FeeScheduleSummary.From(source);
        report.TargetId = copy.Id;
        report.TargetName = copy.Name;
        report.LineCount = copy.Lines.Count;
        report.RequiresCptLicense = CarriesCpt(source);

        var existing = await _schedules.GetByIdAsync(LibraryTenantId, copy.Id, ct);
        if (existing is not null && !request.Replace)
        {
            report.Outcome = FeeScheduleImportOutcome.Exists;
            report.Warnings.Add("The library already has this schedule; pass replace=true to replace it.");
            return report;
        }
        if (request.DryRun)
        {
            report.Outcome = FeeScheduleImportOutcome.WouldWrite;
            return report;
        }

        copy.CreatedDate = existing?.CreatedDate ?? DateTime.UtcNow;
        copy.LastUpdatedDate = DateTime.UtcNow;
        copy.Revision = existing?.Revision ?? 0;
        await _schedules.UpsertAsync(copy, ct);
        report.Outcome = existing is null ? FeeScheduleImportOutcome.Created : FeeScheduleImportOutcome.Replaced;

        _logger.LogInformation(
            "AUDIT fee schedule library publish fromTenant={Tenant} source={Source} libraryId={Target} lines={Lines} actor={Actor}",
            LogSanitizer.Sanitize(tenantId, 256), LogSanitizer.Sanitize(source.Id, 256), LogSanitizer.Sanitize(copy.Id, 256),
            copy.Lines.Count, LogSanitizer.Sanitize(actor, 256));
        return report;
    }

    internal static bool CarriesCpt(FeeSchedule s) =>
        s.CodeSystem.Contains("CPT", StringComparison.OrdinalIgnoreCase)
        || s.CodeSystem.Contains("HCPCS", StringComparison.OrdinalIgnoreCase)
        || s.Type == FeeScheduleType.MedicareMpfs;

    /// <summary>A copy of <paramref name="s"/> with every dollar value multiplied by <paramref name="factor"/>.</summary>
    internal static FeeSchedule Scale(FeeSchedule s, decimal factor) => new()
    {
        Name = s.Name,
        Type = s.Type,
        SourceType = s.SourceType,
        SourceId = s.SourceId,
        SourceVersion = s.SourceVersion,
        PayerId = s.PayerId,
        NetworkId = s.NetworkId,
        Jurisdiction = s.Jurisdiction,
        CodeSystem = s.CodeSystem,
        IsGlobal = s.IsGlobal,
        LicenseClassification = s.LicenseClassification,
        EffectiveDate = s.EffectiveDate,
        TermDate = s.TermDate,
        Locality = s.Locality,
        ConversionFactor = s.ConversionFactor is { } cf ? Math.Round(cf * factor, 4) : null,
        WorkGpci = s.WorkGpci,
        PeGpci = s.PeGpci,
        MpGpci = s.MpGpci,
        PercentOfMedicare = s.PercentOfMedicare is { } p ? Math.Round(p * factor, 4) : null,
        BaseMpfsFeeScheduleId = s.BaseMpfsFeeScheduleId,
        PerDiemRate = s.PerDiemRate is { } pd ? MoneyRounding.ToCents(pd * factor) : null,
        DrgBaseRate = s.DrgBaseRate is { } drg ? MoneyRounding.ToCents(drg * factor) : null,
        Lines = s.Lines.Select(l => ScaleLine(l, factor)).ToList(),
    };

    private static FeeScheduleLine ScaleLine(FeeScheduleLine l, decimal factor) => new()
    {
        ProcedureCode = l.ProcedureCode,
        RevenueCode = l.RevenueCode,
        Modifier = l.Modifier,
        RateType = l.RateType,
        // A percentage-type line's Rate is a fraction (0.80 of billed), so it
        // keeps four places; a dollar rate is rounded to cents.
        Rate = l.RateType is FeeScheduleRateType.PercentOfBilled or FeeScheduleRateType.PercentOfMedicare
            ? Math.Round(l.Rate * factor, 4)
            : MoneyRounding.ToCents(l.Rate * factor),
        FacilityRate = l.FacilityRate is { } f ? MoneyRounding.ToCents(f * factor) : null,
        WorkRvu = l.WorkRvu,
        PeRvu = l.PeRvu,
        MpRvu = l.MpRvu,
        PeRvuFacility = l.PeRvuFacility,
        DrgWeight = l.DrgWeight,
        MaxUnitsPerDay = l.MaxUnitsPerDay,
        BilateralAdjustmentApplies = l.BilateralAdjustmentApplies,
        MultipleProcedureIndicator = l.MultipleProcedureIndicator,
        AssistantAtSurgeryAllowed = l.AssistantAtSurgeryAllowed,
    };

    private static List<FeeScheduleDeployPreviewLine> Preview(FeeSchedule source, FeeSchedule copy)
    {
        var picked = PreviewCodes
            .Select(code => source.Lines.FindIndex(l => l.ProcedureCode == code && l.Modifier is null))
            .Where(i => i >= 0)
            .ToList();
        if (picked.Count == 0) picked = Enumerable.Range(0, Math.Min(5, source.Lines.Count)).ToList();

        return picked.Select(i => new FeeScheduleDeployPreviewLine
        {
            ProcedureCode = source.Lines[i].ProcedureCode,
            RevenueCode = source.Lines[i].RevenueCode,
            Modifier = source.Lines[i].Modifier,
            LibraryAmount = FeeScheduleLineView.UnitAmount(source, source.Lines[i], facility: false),
            DeployedAmount = FeeScheduleLineView.UnitAmount(copy, copy.Lines[i], facility: false),
        }).ToList();
    }

    private static string Pct(decimal p) => p.ToString("0.##", CultureInfo.InvariantCulture) + "%";
}

public sealed class FeeScheduleDeployRequest
{
    /// <summary>The library schedule's id.</summary>
    public string? SourceId { get; set; }
    /// <summary>Percent of the library rates, 100 = the same rates (110 = 110% of Medicare for a Medicare PFS schedule).</summary>
    public decimal Percent { get; set; } = 100m;
    /// <summary>Deploy as this plan's default schedule (a plan code for every version, or a version id).</summary>
    public string? PlanId { get; set; }
    /// <summary>The new schedule's name; required without planId.</summary>
    public string? Name { get; set; }
    public DateTime? EffectiveDate { get; set; }
    public DateTime? TermDate { get; set; }
    /// <summary>The tenant holds its own AMA CPT license; required to deploy a CPT/HCPCS-keyed schedule.</summary>
    public bool LicenseAcknowledged { get; set; }
    public bool Replace { get; set; }
    public bool DryRun { get; set; } = true;
}

public sealed class FeeSchedulePublishRequest
{
    /// <summary>A schedule in the caller's tenant, loaded with one of the importers.</summary>
    public string? SourceId { get; set; }
    public string? Name { get; set; }
    public string? Jurisdiction { get; set; }
    public bool Replace { get; set; }
    public bool DryRun { get; set; } = true;
}

public sealed class FeeScheduleDeployReport
{
    public bool DryRun { get; set; }
    public decimal Percent { get; set; }
    public FeeScheduleImportOutcome? Outcome { get; set; }
    public FeeScheduleSummary? Source { get; set; }
    public string? TargetId { get; set; }
    public string? TargetName { get; set; }
    public string? PlanKey { get; set; }
    public int LineCount { get; set; }
    public bool RequiresCptLicense { get; set; }
    public List<FeeScheduleDeployPreviewLine> Preview { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public string? Error { get; set; }
    public bool IsNotFound { get; set; }

    internal FeeScheduleDeployReport Fail(string error) { Error = error; return this; }
    internal FeeScheduleDeployReport NotFound(string error) { Error = error; IsNotFound = true; return this; }
}

public sealed class FeeScheduleDeployPreviewLine
{
    public string ProcedureCode { get; set; } = string.Empty;
    public string? RevenueCode { get; set; }
    public string? Modifier { get; set; }
    public decimal? LibraryAmount { get; set; }
    public decimal? DeployedAmount { get; set; }
}

public sealed class FeeScheduleSummary
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string SourceVersion { get; set; } = string.Empty;
    public string? Jurisdiction { get; set; }
    public string? Locality { get; set; }
    public string CodeSystem { get; set; } = string.Empty;
    public string LicenseClassification { get; set; } = string.Empty;
    public decimal? ConversionFactor { get; set; }
    public string? DefaultForPlanId { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? TermDate { get; set; }
    public DateTime LastUpdatedDate { get; set; }

    public static FeeScheduleSummary From(FeeSchedule s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Type = s.Type.ToString(),
        SourceType = s.SourceType.ToString(),
        SourceId = s.SourceId,
        SourceVersion = s.SourceVersion,
        Jurisdiction = s.Jurisdiction,
        Locality = s.Locality,
        CodeSystem = s.CodeSystem,
        LicenseClassification = s.LicenseClassification.ToString(),
        ConversionFactor = s.ConversionFactor,
        DefaultForPlanId = s.DefaultForPlanId,
        EffectiveDate = s.EffectiveDate,
        TermDate = s.TermDate,
        LastUpdatedDate = s.LastUpdatedDate,
    };
}

public sealed class FeeScheduleDetail
{
    public FeeScheduleSummary Schedule { get; set; } = new();
    public int LineCount { get; set; }
    public int MatchingCount { get; set; }
    public List<FeeScheduleLineView> Lines { get; set; } = new();
}

public sealed class FeeScheduleLineView
{
    public string ProcedureCode { get; set; } = string.Empty;
    public string? RevenueCode { get; set; }
    public string? Modifier { get; set; }
    public string RateType { get; set; } = string.Empty;
    /// <summary>The dollar amount the engine prices a non-facility unit at (RVU schedules computed from the locality's GPCIs and conversion factor).</summary>
    public decimal? Amount { get; set; }
    public decimal? FacilityAmount { get; set; }

    public static FeeScheduleLineView From(FeeSchedule s, FeeScheduleLine l) => new()
    {
        ProcedureCode = l.ProcedureCode,
        RevenueCode = l.RevenueCode,
        Modifier = l.Modifier,
        RateType = l.RateType.ToString(),
        Amount = UnitAmount(s, l, facility: false),
        FacilityAmount = UnitAmount(s, l, facility: true),
    };

    /// <summary>
    /// The per-unit amount for display, the same formula the engine uses for
    /// RVU lines; percentage lines have no fixed amount (null).
    /// </summary>
    internal static decimal? UnitAmount(FeeSchedule s, FeeScheduleLine l, bool facility)
    {
        var hasRvu = l.WorkRvu.HasValue || l.PeRvu.HasValue || l.PeRvuFacility.HasValue || l.MpRvu.HasValue;
        if (s.Type == FeeScheduleType.MedicareMpfs && hasRvu && s.ConversionFactor is { } cf)
        {
            var pe = (facility ? l.PeRvuFacility : l.PeRvu) ?? l.PeRvu ?? 0m;
            return MoneyRounding.ToCents(((l.WorkRvu ?? 0m) * s.WorkGpci + pe * s.PeGpci + (l.MpRvu ?? 0m) * s.MpGpci) * cf);
        }
        if (l.RateType is FeeScheduleRateType.PercentOfBilled or FeeScheduleRateType.PercentOfMedicare) return null;
        return facility ? l.FacilityRate ?? l.Rate : l.Rate;
    }
}
