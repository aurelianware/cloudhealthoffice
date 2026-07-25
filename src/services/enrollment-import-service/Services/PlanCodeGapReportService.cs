using EnrollmentImportService.Clients;
using EnrollmentImportService.Models;

namespace EnrollmentImportService.Services;

public interface IPlanCodeGapReportService
{
    /// <summary>
    /// Scans a parsed 834 batch for every distinct (group, insurance line,
    /// external plan code) triple and checks each against benefit-plan-service's
    /// plan-code-mapping crosswalk. Read-only — makes no writes, so it's safe
    /// to run against a trading partner's test file before go-live to see
    /// exactly which mappings still need to be loaded.
    /// </summary>
    Task<PlanCodeGapReport> BuildReportAsync(Enrollment834 enrollment, string tenantId, CancellationToken ct = default);
}

public sealed class PlanCodeGapReportService : IPlanCodeGapReportService
{
    private readonly IBenefitPlanServiceClient _benefitPlanClient;

    public PlanCodeGapReportService(IBenefitPlanServiceClient benefitPlanClient)
    {
        _benefitPlanClient = benefitPlanClient;
    }

    public async Task<PlanCodeGapReport> BuildReportAsync(
        Enrollment834 enrollment, string tenantId, CancellationToken ct = default)
    {
        var report = new PlanCodeGapReport { FileName = enrollment.FileName };
        var triples = new HashSet<(string GroupNumber, string InsuranceLineCode, string ExternalPlanCode)>();

        foreach (var member in enrollment.Enrollments)
        {
            Collect(member.Coverage, member.GroupNumber, triples, report);
            foreach (var dependent in member.Dependents)
            {
                // Dependents share the subscriber's group number — there's no
                // separate REF*1L inside the LS...LE dependent loop.
                Collect(dependent.Coverage, member.GroupNumber, triples, report);
            }
        }

        foreach (var (groupNumber, insuranceLineCode, externalPlanCode) in triples)
        {
            var planId = await _benefitPlanClient.ResolvePlanIdAsync(
                tenantId, groupNumber, insuranceLineCode, externalPlanCode, ct);
            var entry = new PlanCodeGapEntry
            {
                GroupNumber = groupNumber,
                InsuranceLineCode = insuranceLineCode,
                ExternalPlanCode = externalPlanCode,
                PlanId = planId
            };

            if (planId is null)
            {
                report.Unmapped.Add(entry);
            }
            else
            {
                report.Mapped.Add(entry);
            }
        }

        return report;
    }

    /// <summary>
    /// Adds each coverage line's (group, line, code) triple to the dedupe set.
    /// Lines missing a group number or plan code can never be resolved
    /// regardless of what's mapped — same guard as EnrollmentImportService's
    /// own ProcessCoverageAsync — so those are counted separately rather than
    /// silently dropped or misreported as "unmapped".
    /// </summary>
    private static void Collect(
        IEnumerable<CoverageDetail>? coverage,
        string? groupNumber,
        HashSet<(string, string, string)> triples,
        PlanCodeGapReport report)
    {
        if (coverage is null)
        {
            return;
        }

        foreach (var detail in coverage)
        {
            if (string.IsNullOrWhiteSpace(groupNumber) || string.IsNullOrWhiteSpace(detail.PlanCoverageDescription))
            {
                report.IncompleteCount++;
                continue;
            }

            triples.Add((groupNumber, detail.InsuranceLineCode, detail.PlanCoverageDescription));
        }
    }
}

public class PlanCodeGapReport
{
    public string FileName { get; set; } = string.Empty;
    public List<PlanCodeGapEntry> Mapped { get; set; } = new();
    public List<PlanCodeGapEntry> Unmapped { get; set; } = new();

    /// <summary>Coverage lines with no group number or plan code — can't be resolved regardless of mappings.</summary>
    public int IncompleteCount { get; set; }
}

public class PlanCodeGapEntry
{
    public string GroupNumber { get; set; } = string.Empty;
    public string InsuranceLineCode { get; set; } = string.Empty;
    public string ExternalPlanCode { get; set; } = string.Empty;
    public string? PlanId { get; set; }
}
