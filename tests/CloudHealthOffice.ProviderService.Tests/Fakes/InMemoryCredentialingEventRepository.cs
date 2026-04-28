using System.Text;
using ProviderService.Models;
using ProviderService.Repositories;

namespace CloudHealthOffice.ProviderService.Tests.Fakes;

/// <summary>
/// In-memory fake for <see cref="ICredentialingEventRepository"/> backing
/// the <c>CredentialingService</c> unit tests. Append happens via
/// <see cref="FakeCredentialingEventPublisher"/>; this fake exposes only
/// reads — matching the production interface contract.
/// </summary>
public sealed class InMemoryCredentialingEventRepository : ICredentialingEventRepository
{
    public List<CredentialingEvent> Store { get; } = new();

    public Task<IReadOnlyList<CredentialingEvent>> ListAscendingAsync(
        string tenantId, string providerId, CancellationToken ct = default)
    {
        var rows = Store
            .Where(e => e.TenantId == tenantId && e.ProviderId == providerId)
            .OrderBy(e => e.Version)
            .ToList();
        return Task.FromResult<IReadOnlyList<CredentialingEvent>>(rows);
    }

    public Task<CredentialingEvent?> GetByEventIdAsync(
        string tenantId, string providerId, string eventId, CancellationToken ct = default)
    {
        var row = Store.FirstOrDefault(e =>
            e.TenantId == tenantId && e.ProviderId == providerId && e.EventId == eventId);
        return Task.FromResult<CredentialingEvent?>(row);
    }

    public Task<CredentialingHistoryPage> ListHistoryDescendingAsync(
        string tenantId,
        string providerId,
        string? continuationToken,
        int limit,
        CancellationToken ct = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 200);
        var afterVersion = DecodeCursor(continuationToken);

        var rows = Store
            .Where(e => e.TenantId == tenantId && e.ProviderId == providerId)
            .Where(e => !afterVersion.HasValue || e.Version < afterVersion.Value)
            .OrderByDescending(e => e.Version)
            .Take(safeLimit + 1)
            .ToList();

        string? next = null;
        if (rows.Count > safeLimit)
        {
            rows.RemoveAt(rows.Count - 1);
            next = EncodeCursor(rows[^1].Version);
        }
        return Task.FromResult(new CredentialingHistoryPage(rows, next));
    }

    private static int? DecodeCursor(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        try
        {
            var bytes = Convert.FromBase64String(token);
            return int.TryParse(Encoding.UTF8.GetString(bytes), out var v) ? v : null;
        }
        catch (FormatException) { return null; }
    }

    private static string EncodeCursor(int v) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(v.ToString()));
}
