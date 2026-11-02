using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Programs;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.PlanConfiguration;

/// <summary>
/// The line of business a benefit plan's program names (coverage.PlanId →
/// BenefitPlan.ProgramId → program, docs/architecture/line-of-business.md).
/// Intake (coverage, claims, authorizations) takes the line of business from
/// here, so a stored value is a copy of the program's, not a second answer
/// typed in by hand.
/// </summary>
public static class ProgramLineOfBusiness
{
    /// <summary>
    /// The program's line of business for <paramref name="planId"/>, or
    /// <c>null</c> when the plan has no program, its program names no line of
    /// business CHO knows, or tenant-service could not be asked (logged). A
    /// caller keeps the value it already had on <c>null</c>: a plan written
    /// before programs existed has none, and an outage must not block intake.
    /// </summary>
    public static async Task<LineOfBusiness?> ForBenefitPlanAsync(
        IProgramDirectory programs, string tenantId, string? planId, ILogger logger, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(planId)) return null;
        ProgramDescriptor? program;
        try
        {
            program = await programs.ForBenefitPlanAsync(tenantId, planId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (ex is TaskCanceledException && ct.IsCancellationRequested) throw;
            logger.LogWarning(ex,
                "Program for benefit plan {PlanId} could not be read; the line of business is not taken from the program",
                LogSanitizer.Sanitize(planId));
            return null;
        }
        return FromProgram(program);
    }

    /// <summary>
    /// The program's line of business, or <c>null</c> when there is no
    /// program or it names none CHO knows.
    /// </summary>
    public static LineOfBusiness? FromProgram(ProgramDescriptor? program) =>
        program is not null
        && LineOfBusinessNames.TryParse(program.LineOfBusiness, out var lob)
        && lob != LineOfBusiness.Unknown
            ? lob
            : null;

    /// <summary>
    /// The one line of business the programs of <paramref name="planIds"/>
    /// agree on, or <c>null</c> when none has a program, they disagree (a
    /// member with Medicare and Medicaid plans on the same day: the caller
    /// cannot tell which one the request is under), or tenant-service could
    /// not be asked.
    /// </summary>
    public static async Task<LineOfBusiness?> ForBenefitPlansAsync(
        IProgramDirectory programs, string tenantId, IEnumerable<string> planIds, ILogger logger, CancellationToken ct = default)
    {
        var found = new HashSet<LineOfBusiness>();
        foreach (var planId in planIds.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal))
        {
            if (await ForBenefitPlanAsync(programs, tenantId, planId, logger, ct).ConfigureAwait(false) is { } lob)
                found.Add(lob);
        }
        return found.Count == 1 ? found.Single() : null;
    }
}
