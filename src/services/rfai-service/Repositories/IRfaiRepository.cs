using RfaiService.Models;

namespace RfaiService.Repositories;

/// <summary>
/// Persistence for RFAI cases. EVERY method is tenant-scoped: there is no
/// lookup on this interface that can reach a case without naming the tenant, so
/// a caller cannot accidentally read across tenants by forgetting a filter.
/// </summary>
public interface IRfaiRepository
{
    Task<RfaiCase?> GetByIdAsync(string tenantId, string id);

    /// <summary>Cases for one authorization, newest first. Includes closed cycles — history is evidence.</summary>
    Task<List<RfaiCase>> GetByAuthNumberAsync(string tenantId, string authNumber);

    /// <summary>
    /// The case bearing this provider-facing tracking id, or null. This is the
    /// lookup the CDex response path uses to correlate a submission.
    /// </summary>
    Task<RfaiCase?> GetByTrackingIdAsync(string tenantId, string trackingId);

    Task<RfaiCase> CreateAsync(RfaiCase rfaiCase);

    /// <summary>
    /// Conditional create on the document's primary key.
    ///
    /// Returns the stored case and whether THIS call created it. Because the id
    /// is derived from the creating event (see
    /// <c>RfaiCaseLifecycle.DeterministicId</c>), two workers racing on the same
    /// A4 review decision both address the same document and exactly one insert
    /// succeeds; the loser reads back the winner's case rather than creating a
    /// second active request.
    /// </summary>
    Task<(RfaiCase Case, bool Created)> CreateIfAbsentAsync(RfaiCase rfaiCase);

    Task<RfaiCase> UpdateAsync(RfaiCase rfaiCase);
}
