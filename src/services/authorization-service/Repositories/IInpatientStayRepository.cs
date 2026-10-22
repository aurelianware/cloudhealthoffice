using AuthorizationService.Models;

namespace AuthorizationService.Repositories;

/// <summary>
/// Inpatient stays, scoped to the caller's tenant exactly as
/// <see cref="IAuthorizationRepository"/> is. The background-callable
/// methods take their tenant explicitly, because a hosted service has no
/// request to take one from.
/// </summary>
public interface IInpatientStayRepository
{
    Task<InpatientStay?> GetByIdAsync(string id, CancellationToken ct = default);

    /// <summary>Open stays, optionally narrowed to one facility.</summary>
    Task<IReadOnlyList<InpatientStay>> ListOpenAsync(string? facilityNpi = null, CancellationToken ct = default);

    /// <summary>Every stay recorded against one authorization, open or not.</summary>
    Task<IReadOnlyList<InpatientStay>> ListByAuthorizationAsync(string authorizationId, CancellationToken ct = default);

    Task<InpatientStay> CreateAsync(InpatientStay stay, CancellationToken ct = default);

    /// <summary>
    /// Replaces the stay, but only while its stored revision is the one that
    /// was read. A reviewer whose census row went stale gets a conflict
    /// instead of overwriting the review someone else just recorded.
    /// </summary>
    Task<InpatientStay> UpdateAsync(InpatientStay stay, CancellationToken ct = default);

    /// <summary>Tenants with inpatient stays, so a sweep can iterate them without a second service.</summary>
    Task<IReadOnlyList<string>> ListTenantIdsAsync(CancellationToken ct = default);

    /// <summary>One tenant's open stays. Used by the review watchdog, which has no request.</summary>
    Task<IReadOnlyList<InpatientStay>> ListOpenForTenantAsync(string tenantId, CancellationToken ct = default);
}
