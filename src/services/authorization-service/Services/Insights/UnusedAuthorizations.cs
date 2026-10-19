using AuthorizationService.Models;
using AuthorizationService.Repositories;

namespace AuthorizationService.Services.Insights;

/// <summary>
/// "Approved but unused": approvals (Approved, Modified, or expired approvals) whose
/// approved service period ended in [from, to] (and has ended), that no
/// current claim carries. The one definition behind the Insights figure and
/// its drill-down. Providers can still bill within timely filing, so it reads
/// "no claim yet".
/// </summary>
public sealed class UnusedAuthorizations
{
    private readonly IAuthorizationRepository _repository;
    private readonly IClaimsUsageClient _claims;

    public UnusedAuthorizations(IAuthorizationRepository repository, IClaimsUsageClient claims)
    {
        _repository = repository;
        _claims = claims;
    }

    public sealed record Result(IReadOnlyList<Authorization> Ended, IReadOnlyList<Authorization>? Unused, string? UnavailableReason);

    public async Task<Result> ComputeAsync(DateTime from, DateTime to, LineOfBusiness? lineOfBusiness, DateTime asOf, CancellationToken ct = default)
    {
        var ended = (await _repository.ListApprovedEndingBetweenAsync(from, to))
            .Where(a => Cms0057.Cms0057Reporting.IsApproved(a))
            .Where(a => lineOfBusiness is null || a.LineOfBusiness == lineOfBusiness)
            .Where(a => (a.ApprovedServiceDateTo ?? a.ExpirationDate) <= asOf)
            .Where(a => !string.IsNullOrWhiteSpace(a.AuthorizationNumber))
            .ToList();
        if (ended.Count == 0) return new Result(ended, [], null);

        var usage = await _claims.GetUsageAsync(ended.Select(a => a.AuthorizationNumber.Trim()).ToList(), ct);
        if (usage.Counts is not { } counts) return new Result(ended, null, usage.UnavailableReason);

        return new Result(ended, ended.Where(a => !counts.ContainsKey(a.AuthorizationNumber.Trim())).ToList(), null);
    }
}
