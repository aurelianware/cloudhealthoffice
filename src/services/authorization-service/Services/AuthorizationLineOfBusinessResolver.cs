using AuthorizationService.Services.StatePrograms;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.PlanConfiguration;

namespace AuthorizationService.Services;

/// <summary>
/// The line of business a prior authorization request falls under, from the
/// member's program (docs/architecture/line-of-business.md, step 6): the
/// coverages active on the requested service date name benefit plans, and each
/// plan names its program. The submitter's own value is kept when this answers
/// null.
/// </summary>
public interface IAuthorizationLineOfBusinessResolver
{
    /// <summary>
    /// The one line of business the member's active programs agree on, or
    /// <c>null</c> when the member has no active coverage then, no plan has a
    /// program, the programs disagree (Medicare and Medicaid on the same day),
    /// or coverage-service or tenant-service could not be asked (logged).
    /// </summary>
    Task<LineOfBusiness?> ResolveAsync(string tenantId, string memberId, DateTime asOf, CancellationToken ct);
}

/// <summary>Reads coverage-service's active coverage, then tenant-service's programs.</summary>
public sealed class ProgramAuthorizationLineOfBusinessResolver(
    IStateDentalMemberLookup coverage,
    IProgramDirectory programs,
    ILogger<ProgramAuthorizationLineOfBusinessResolver> logger) : IAuthorizationLineOfBusinessResolver
{
    public async Task<LineOfBusiness?> ResolveAsync(string tenantId, string memberId, DateTime asOf, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(memberId)) return null;

        IReadOnlyList<ActiveCoveragePlan> active;
        try
        {
            active = await coverage.ActiveCoverageAsync(tenantId, memberId, asOf, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex,
                "Active coverage for member {MemberId} could not be read; the line of business is not taken from the program",
                LogSanitizer.MaskId(memberId));
            return null;
        }

        return await ProgramLineOfBusiness
            .ForBenefitPlansAsync(programs, tenantId, active.Select(c => c.PlanId), logger, ct)
            .ConfigureAwait(false);
    }
}
