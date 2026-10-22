using EncounterService.Models;
using MongoDB.Driver;

namespace EncounterService.Repositories;

/// <summary>
/// The query behind FHIR Encounter search (ADR 018, extended by ADR 024). Kept
/// apart from <see cref="IEncounterRepository"/>'s general search because FHIR
/// search needs what that one does not give: a total, a stable order across
/// pages, overlap semantics on the service period rather than containment, and
/// filters on the projected status and on last-updated.
/// </summary>
public interface IEncounterFhirSearchRepository
{
    /// <summary>
    /// One page of the tenant's encounters matching every filter in
    /// <paramref name="query"/>, ordered by service start (newest first) then
    /// id, and the total match count.
    /// </summary>
    Task<(IReadOnlyList<Encounter> Items, long Total)> SearchAsync(
        string tenantId, EncounterFhirSearchQuery query, CancellationToken ct = default);
}
