using AuthorizationService.Models;
using AuthorizationService.Services.Retention;

namespace AuthorizationService.Repositories;

public interface IAuthorizationRepository
{
    Task<Authorization?> GetByIdAsync(string id);
    Task<Authorization?> GetByAuthorizationNumberAsync(string authorizationNumber);
    Task<IEnumerable<Authorization>> SearchAsync(
        string? memberId,
        string? providerNPI,
        DateTime? serviceDateFrom,
        DateTime? serviceDateTo,
        AuthorizationStatus? status,
        LineOfBusiness? lineOfBusiness,
        int page,
        int pageSize);
    Task<AuthorizationsSummary> GetAuthorizationsSummaryAsync(DateTime from, DateTime to, LineOfBusiness? lineOfBusiness);
    Task<IEnumerable<Authorization>> GetOpenAuthorizationsAsync(string? tenantId = null);
    /// <summary>
    /// The current tenant's authorizations submitted or decided in [from, to],
    /// for CMS-0057-F reporting (metrics and the recent-decision list).
    /// </summary>
    Task<IEnumerable<Authorization>> ListActivityBetweenAsync(DateTime from, DateTime to);

    /// <summary>
    /// The current tenant's approvals (Approved, Modified, or expired approvals)
    /// whose approved service period ended in [from, to]: the approved end
    /// date, or the expiration date when no end date was approved.
    /// </summary>
    Task<IReadOnlyList<Authorization>> ListApprovedEndingBetweenAsync(DateTime from, DateTime to)
        => throw new NotSupportedException("Approved authorizations by end date need the MongoDB authorization store.");
    Task<Authorization> CreateAsync(Authorization authorization);
    Task<Authorization> UpdateAsync(Authorization authorization);
    Task DeleteAsync(string id);

    Task<IReadOnlyList<Authorization>> FindExpiryCandidatesAsync(string tenantId, DateTime cutoffDateUtc, int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Authorization>>(Array.Empty<Authorization>());
    Task<bool> ExpireIfStillEligibleAsync(string tenantId, Authorization authorization, DateTime cutoffDateUtc, DateTime changedAtUtc, CancellationToken ct = default)
        => Task.FromResult(false);

    // ── Retention (PAT-03) ───────────────────────────────────────────────────
    // Background-callable: every method takes the tenant EXPLICITLY, so a sweep
    // never depends on an ambient HttpContext, and a CancellationToken so a
    // shutdown stops mid-sweep rather than at the end of it.

    /// <summary>
    /// Tenants that have authorization data, so a sweep can iterate them
    /// without a second service. Mirrors provider-service's
    /// <c>ListProviderTenantIdsAsync</c>.
    /// </summary>
    Task<IReadOnlyList<string>> ListTenantIdsAsync(CancellationToken ct = default);

    /// <summary>
    /// Terminal authorizations for one tenant whose retention anchor is at or
    /// before <paramref name="anchorCutoffUtc"/>, capped at
    /// <paramref name="limit"/>. Coarse filtering only — the caller applies the
    /// retention policy per record before deleting anything.
    /// </summary>
    Task<IReadOnlyList<Authorization>> FindRetentionCandidatesAsync(
        string tenantId, DateTime anchorCutoffUtc, int limit, CancellationToken ct = default);

    /// <summary>
    /// Deletes one authorization ONLY IF it is still in
    /// <paramref name="expectedStatus"/>. Returns false when the record moved on
    /// or was already gone, so a sweep cannot delete a record that became active
    /// again between being listed and being purged. Tenant is explicit and
    /// always part of the delete predicate.
    /// </summary>
    Task<bool> PurgeIfStillEligibleAsync(
        string tenantId, string id, AuthorizationStatus expectedStatus, CancellationToken ct = default);

    /// <summary>Revision-fenced, tenant-explicit initialization of a legacy open record's receipt deadline.</summary>
    Task<bool> EnsureDecisionDeadlineAsync(string tenantId, Authorization authorization, CancellationToken ct = default)
        => Task.FromResult(false);

    // ── SLA watchdog (D-07) ──────────────────────────────────────────────────
    // Background-callable like the retention methods: the tenant is explicit,
    // and GetOpenAuthorizationsAsync(tenantId) lists that tenant's open records.

    /// <summary>
    /// Raises one authorization's SLA escalation to <paramref name="level"/>
    /// and stamps <paramref name="escalatedAtUtc"/>, ONLY IF it belongs to
    /// <paramref name="tenantId"/>, is still open and is below that level.
    /// Writes nothing else, so a reviewer's concurrent change is never
    /// overwritten. Returns false when the record moved on, was already at or
    /// above the level, or is gone.
    /// </summary>
    Task<bool> RaiseSlaEscalationAsync(
        string tenantId, string id, SlaEscalationLevel level, DateTime escalatedAtUtc, CancellationToken ct = default);
}
