using ClaimsService.Models;
using MongoDB.Driver;

namespace ClaimsService.Repositories;

/// <summary>
/// The query behind FHIR Claim search (ADR 018, extended by ADR 024). Kept
/// apart from <see cref="IClaimRepository"/>'s searches because FHIR search
/// needs what they do not give together: current versions only (a version
/// superseded by an adjustment is history, and the FHIR id is the chain head),
/// a stable order across pages, a total, and filters on the projected status
/// and on last-updated.
/// </summary>
public interface IClaimFhirSearchRepository
{
    /// <summary>
    /// One page of the tenant's current claim versions matching every filter in
    /// <paramref name="query"/>, ordered by submission (newest first) then
    /// chain id, and the total match count.
    /// </summary>
    Task<(IReadOnlyList<Claim> Items, long Total)> SearchAsync(
        string tenantId, ClaimFhirSearchQuery query, CancellationToken ct = default);
}
