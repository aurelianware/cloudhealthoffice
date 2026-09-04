using System.Text.Json;
using AuthorizationService.Models;
using AuthorizationService.Repositories;

namespace Cms0057Acceptance.Tests.TestSupport;

/// <summary>
/// Test-only in-memory <see cref="IAuthorizationRepository"/>.
///
/// This is a UNIT-TEST FIXTURE, not the production Replace-mode implementation.
/// The production CHO-native backend (ChoAuthorizationBackend) runs against the
/// real Cosmos/Mongo repository; the acceptance suite exercises that SAME
/// production backend class against this fixture so the workflow — not a
/// parallel acceptance-only implementation — is what is proven.
///
/// Records are JSON-snapshotted on write and read so that mutations to the
/// caller's object do not leak into the store: the record genuinely "survives
/// persistence" rather than sharing a reference.
/// </summary>
internal sealed class InMemoryAuthorizationRepository : IAuthorizationRepository
{
    private readonly List<Authorization> _store = new();

    /// <summary>Number of times CreateAsync was invoked — proves the CHO backend was used.</summary>
    public int CreateCount { get; private set; }

    private static Authorization Clone(Authorization a) =>
        JsonSerializer.Deserialize<Authorization>(JsonSerializer.Serialize(a))!;

    public Task<Authorization> CreateAsync(Authorization authorization)
    {
        CreateCount++;
        var stored = Clone(authorization);
        _store.Add(stored);
        return Task.FromResult(Clone(stored));
    }

    public Task<Authorization> UpdateAsync(Authorization authorization)
    {
        var idx = _store.FindIndex(a => a.Id == authorization.Id);
        var stored = Clone(authorization);
        if (idx >= 0) _store[idx] = stored;
        else _store.Add(stored);
        return Task.FromResult(Clone(stored));
    }

    public Task<Authorization?> GetByIdAsync(string id)
        => Task.FromResult(_store.FirstOrDefault(a => a.Id == id) is { } a ? Clone(a) : null);

    public Task<Authorization?> GetByAuthorizationNumberAsync(string authorizationNumber)
        => Task.FromResult(
            _store.FirstOrDefault(a => a.AuthorizationNumber == authorizationNumber) is { } a ? Clone(a) : null);

    public Task<IEnumerable<Authorization>> SearchAsync(
        string? memberId, string? providerNPI, DateTime? serviceDateFrom, DateTime? serviceDateTo,
        AuthorizationStatus? status, LineOfBusiness? lineOfBusiness, int page, int pageSize)
    {
        IEnumerable<Authorization> q = _store;
        if (!string.IsNullOrEmpty(memberId)) q = q.Where(a => a.MemberId == memberId);
        if (!string.IsNullOrEmpty(providerNPI))
            q = q.Where(a => a.RequestingProviderNPI == providerNPI || a.ServicingProviderNPI == providerNPI);
        if (status.HasValue) q = q.Where(a => a.Status == status.Value);
        if (lineOfBusiness.HasValue) q = q.Where(a => a.LineOfBusiness == lineOfBusiness.Value);
        return Task.FromResult(q.Skip((page - 1) * pageSize).Take(pageSize).Select(Clone).AsEnumerable());
    }

    public Task<IEnumerable<Authorization>> GetOpenAuthorizationsAsync(string? tenantId = null)
    {
        var open = new[] { AuthorizationStatus.Submitted, AuthorizationStatus.InReview, AuthorizationStatus.Pended };
        var q = _store.Where(a => open.Contains(a.Status));
        if (!string.IsNullOrEmpty(tenantId)) q = q.Where(a => a.TenantId == tenantId);
        return Task.FromResult(q.Select(Clone).AsEnumerable());
    }

    public Task<AuthorizationsSummary> GetAuthorizationsSummaryAsync(
        DateTime from, DateTime to, LineOfBusiness? lineOfBusiness)
    {
        var rows = _store
            .Where(a => a.SubmittedDate >= from && a.SubmittedDate <= to)
            .Where(a => !lineOfBusiness.HasValue || a.LineOfBusiness == lineOfBusiness.Value)
            .ToList();

        var summary = new AuthorizationsSummary
        {
            TotalAuthorizations = rows.Count,
            ApprovedAuthorizations = rows.Count(a => a.Status == AuthorizationStatus.Approved),
            DeniedAuthorizations = rows.Count(a => a.Status == AuthorizationStatus.Denied),
            PendedAuthorizations = rows.Count(a => a.Status == AuthorizationStatus.Pended),
            ModifiedAuthorizations = rows.Count(a => a.Status == AuthorizationStatus.Modified),
        };

        if (summary.TotalAuthorizations > 0)
        {
            summary.ApprovalRate =
                (decimal)(summary.ApprovedAuthorizations + summary.ModifiedAuthorizations)
                / summary.TotalAuthorizations * 100;

            var decided = rows.Where(a => a.ReviewedDate.HasValue).ToList();
            if (decided.Count > 0)
            {
                summary.AverageReviewDays = (decimal)decided
                    .Average(a => (a.ReviewedDate!.Value - a.SubmittedDate).TotalDays);
                summary.AverageTurnaroundDays = (decimal)decided
                    .Average(AuthorizationsSummaryCalculator.CalculateTurnaroundDays);
            }
        }

        return Task.FromResult(summary);
    }

    public Task DeleteAsync(string id)
    {
        _store.RemoveAll(a => a.Id == id);
        return Task.CompletedTask;
    }
}
