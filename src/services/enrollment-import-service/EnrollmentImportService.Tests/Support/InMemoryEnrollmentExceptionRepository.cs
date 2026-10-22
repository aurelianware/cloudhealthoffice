using System.Text.Json;
using EnrollmentImportService.Models;
using EnrollmentImportService.Repositories;

namespace EnrollmentImportService.Tests.Support;

/// <summary>Tenant-scoped, copy-on-read/write like a real store.</summary>
public sealed class InMemoryEnrollmentExceptionRepository : IEnrollmentExceptionRepository
{
    private readonly object _gate = new();
    private readonly List<EnrollmentException> _items = new();

    public IReadOnlyList<EnrollmentException> All { get { lock (_gate) return _items.Select(Copy).ToList(); } }

    public Task<bool> TryCreateAsync(EnrollmentException exception, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_items.Any(x => x.Id == exception.Id)) return Task.FromResult(false);
            _items.Add(Copy(exception));
            return Task.FromResult(true);
        }
    }

    public Task<EnrollmentException?> GetAsync(string tenantId, string id, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_items.Where(x => x.TenantId == tenantId && x.Id == id).Select(Copy).FirstOrDefault());
    }

    public Task SaveAsync(EnrollmentException exception, long expectedVersion, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var i = _items.FindIndex(x => x.TenantId == exception.TenantId && x.Id == exception.Id && x.Version == expectedVersion);
            if (i < 0) throw new EnrollmentExceptionConcurrencyException("changed");
            exception.Version = expectedVersion + 1;
            _items[i] = Copy(exception);
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<EnrollmentException>> ListAsync(string tenantId, EnrollmentExceptionQuery query, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<EnrollmentException>>(_items
                .Where(x => x.TenantId == tenantId && (query.Status == null || x.Status == query.Status)
                    && (query.ReasonCode == null || x.ReasonCode == query.ReasonCode))
                .OrderByDescending(x => x.CreatedAt).Take(query.Limit).Select(Copy).ToList());
    }

    public Task<(IReadOnlyList<EnrollmentException> Items, long Total)> ListWorkableAsync(string tenantId, DateTime now, int page, int pageSize, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var all = _items.Where(x => x.TenantId == tenantId && x.IsWorkable(now)).OrderBy(x => x.CreatedAt).ToList();
            return Task.FromResult<(IReadOnlyList<EnrollmentException>, long)>(
                (all.Skip((page - 1) * pageSize).Take(pageSize).Select(Copy).ToList(), all.Count));
        }
    }

    private static EnrollmentException Copy(EnrollmentException e) =>
        JsonSerializer.Deserialize<EnrollmentException>(JsonSerializer.Serialize(e))!;
}
