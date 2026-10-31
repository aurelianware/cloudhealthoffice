using BenefitPlanService.Services;
using CloudHealthOffice.Infrastructure.Logging;

namespace BenefitPlanService.Import.QhpPuf;

public interface IQhpPufImportService
{
    /// <summary>
    /// Maps the selected plans from the two PUF files and, unless
    /// <see cref="QhpPufImportOptions.DryRun"/>, creates each one as a Draft
    /// plan version. Nothing is published: a plan administrator reviews the
    /// warnings and publishes.
    /// </summary>
    Task<QhpPufImportReport> ImportAsync(
        Stream planAttributesCsv,
        Stream benefitsCsv,
        QhpPufImportOptions options,
        string tenantId,
        string actorId,
        CancellationToken ct = default);
}

public sealed class QhpPufImportService : IQhpPufImportService
{
    private readonly IBenefitPlanService _plans;
    private readonly ILogger<QhpPufImportService> _logger;

    public QhpPufImportService(IBenefitPlanService plans, ILogger<QhpPufImportService> logger)
    {
        _plans = plans;
        _logger = logger;
    }

    public async Task<QhpPufImportReport> ImportAsync(
        Stream planAttributesCsv,
        Stream benefitsCsv,
        QhpPufImportOptions options,
        string tenantId,
        string actorId,
        CancellationToken ct = default)
    {
        if (!options.HasSelector)
            throw new ArgumentException("Choose plans by state, issuer or plan id; the national files are too large to import whole.", nameof(options));

        var maxPlans = Math.Clamp(options.MaxPlans, 1, QhpPufImportOptions.MaxPlansCeiling);
        var report = new QhpPufImportReport { DryRun = options.DryRun };

        // 1. Plan Attributes: pick the plans.
        var selected = new Dictionary<string, QhpRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in QhpPufFileReader.Read(planAttributesCsv, QhpPufFileReader.PlanAttributesRequired, "Plan Attributes", r => Matches(r, options)))
        {
            ct.ThrowIfCancellationRequested();
            var planId = row["PlanId"];
            if (planId.Length == 0 || selected.ContainsKey(planId)) continue;
            report.PlansMatched++;
            if (selected.Count < maxPlans) selected[planId] = row;
        }
        report.Truncated = report.PlansMatched > selected.Count;

        // 2. Benefits and Cost Sharing: stream, keep only selected plans' rows.
        var benefits = selected.Keys.ToDictionary(k => k, _ => new List<QhpRow>(), StringComparer.OrdinalIgnoreCase);
        foreach (var row in QhpPufFileReader.Read(benefitsCsv, QhpPufFileReader.BenefitsRequired, "Benefits and Cost Sharing",
                     r => benefits.ContainsKey(r["PlanId"])
                          && (options.BusinessYear is null || !r.Has("BusinessYear") || r["BusinessYear"] == options.BusinessYear.ToString())))
        {
            ct.ThrowIfCancellationRequested();
            benefits[row["PlanId"]].Add(row);
        }

        // 3. Map and (optionally) write.
        foreach (var (planId, attrs) in selected)
        {
            ct.ThrowIfCancellationRequested();
            var result = await ImportOneAsync(attrs, benefits[planId], options, tenantId, actorId);
            report.Plans.Add(result);
            report.Outcomes[result.Outcome.ToString()] = report.Outcomes.GetValueOrDefault(result.Outcome.ToString()) + 1;
            foreach (var u in result.UnmappedBenefits)
                report.UnmappedBenefitCounts[u.BenefitName] = report.UnmappedBenefitCounts.GetValueOrDefault(u.BenefitName) + 1;
        }
        report.PlansProcessed = report.Plans.Count;

        _logger.LogInformation(
            "QHP PUF import for tenant {TenantId}: dryRun={DryRun} matched={Matched} processed={Processed} outcomes={Outcomes}",
            LogSanitizer.Sanitize(tenantId), options.DryRun, report.PlansMatched, report.PlansProcessed,
            string.Join(",", report.Outcomes.Select(kv => $"{kv.Key}:{kv.Value}")));
        return report;
    }

    private async Task<QhpPlanImportResult> ImportOneAsync(
        QhpRow attrs, List<QhpRow> benefitRows, QhpPufImportOptions options, string tenantId, string actorId)
    {
        if (attrs.IsYes("DentalOnlyPlan"))
        {
            var dental = QhpPlanMapper.Describe(attrs);
            dental.Outcome = QhpPlanImportOutcome.Skipped;
            dental.Message = "Stand-alone dental plan; dental benefit mapping is not built yet.";
            return dental;
        }

        if (benefitRows.Count == 0)
        {
            var empty = QhpPlanMapper.Describe(attrs);
            empty.Outcome = QhpPlanImportOutcome.Skipped;
            empty.Message = "No rows for this plan id in the Benefits and Cost Sharing file. Check that both files are the same plan year.";
            return empty;
        }

        var (plan, result) = QhpPlanMapper.Map(attrs, benefitRows);
        var includePlan = options.IncludePlans || options.DryRun;

        var existing = await _plans.ListVersionsAsync(plan.PlanId, tenantId, 1, null);
        if (existing.Items.Count > 0)
        {
            result.Outcome = QhpPlanImportOutcome.Exists;
            result.Message = "A plan with this plan id already exists; it was not changed. Amend it to apply PUF changes.";
            result.VersionId = existing.Items[0].VersionId;
            if (includePlan) result.Plan = plan;
            return result;
        }

        if (options.DryRun)
        {
            result.Outcome = QhpPlanImportOutcome.WouldCreate;
            result.Plan = plan;
            return result;
        }

        try
        {
            var draft = await _plans.CreateDraftAsync(plan, tenantId, actorId);
            result.Outcome = QhpPlanImportOutcome.Created;
            result.VersionId = draft.VersionId;
            if (includePlan) result.Plan = draft;
        }
        catch (PlanLimitValidationException ex)
        {
            result.Outcome = QhpPlanImportOutcome.Invalid;
            result.Message = ex.Message;
        }
        catch (ArgumentException ex)
        {
            result.Outcome = QhpPlanImportOutcome.Invalid;
            result.Message = ex.Message;
        }
        return result;
    }

    internal static bool Matches(QhpRow r, QhpPufImportOptions o)
    {
        if (!string.IsNullOrWhiteSpace(o.StateCode) && !string.Equals(r["StateCode"], o.StateCode.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(o.IssuerId) && r["IssuerId"] != o.IssuerId.Trim())
            return false;
        if (o.BusinessYear is { } year && r["BusinessYear"] != year.ToString())
            return false;
        if (o.PlanIds.Count > 0 && !o.PlanIds.Contains(r["PlanId"]) && !o.PlanIds.Contains(r["StandardComponentId"]))
            return false;
        if (o.CsrVariants.Count > 0)
        {
            var planId = r["PlanId"];
            var dash = planId.LastIndexOf('-');
            var suffix = dash >= 0 ? planId[(dash + 1)..] : string.Empty;
            if (!o.CsrVariants.Contains(suffix)) return false;
        }
        return true;
    }
}
