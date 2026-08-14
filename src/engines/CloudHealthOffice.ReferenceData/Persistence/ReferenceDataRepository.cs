using CloudHealthOffice.ReferenceData.Domain;
using CloudHealthOffice.ReferenceData.Sources;

namespace CloudHealthOffice.ReferenceData.Persistence;

public enum ReferenceSearchMode { Exact, Prefix, Text }

public sealed record ReferenceDataQuery
{
    public required string CodeSystem { get; init; }
    public string? Search { get; init; }
    public ReferenceSearchMode SearchMode { get; init; } = ReferenceSearchMode.Exact;
    public string? Category { get; init; }
    public string? Version { get; init; }
    public DateOnly? EffectiveDate { get; init; }
    public bool? Active { get; init; }
    public string? TenantId { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

public sealed record Page<T>(IReadOnlyList<T> Items, int Total, int PageNumber, int PageSize);

public interface IReferenceDataRepository
{
    Task<ReferenceCode?> GetAsync(string codeSystem, string code, DateOnly effectiveDate, string? version = null, string? tenantId = null, CancellationToken ct = default);
    Task<Page<ReferenceCode>> SearchAsync(ReferenceDataQuery query, CancellationToken ct = default);
    Task<ImportResult> ImportAsync(IReadOnlyList<ReferenceCode> records, CancellationToken ct = default);
}

/// <summary>Deterministic repository used by tests and local composition roots.</summary>
public sealed class InMemoryReferenceDataRepository : IReferenceDataRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ReferenceCode> _records = new(StringComparer.Ordinal);

    public Task<ReferenceCode?> GetAsync(string codeSystem, string code, DateOnly effectiveDate, string? version = null, string? tenantId = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var match = VisibleRecords(tenantId)
                .Where(x => x.Coding.CodeSystem.Equals(codeSystem, StringComparison.OrdinalIgnoreCase)
                    && x.Coding.Code.Equals(code, StringComparison.OrdinalIgnoreCase)
                    && (version is null || x.Coding.Version == version)
                    && x.IsEffectiveOn(effectiveDate))
                .OrderByDescending(x => x.EffectiveFrom)
                .ThenByDescending(x => x.ImportedAt)
                .FirstOrDefault();
            return Task.FromResult(match);
        }
    }

    public Task<Page<ReferenceCode>> SearchAsync(ReferenceDataQuery query, CancellationToken ct = default)
    {
        if (query.Page < 1)
            throw new ArgumentOutOfRangeException(nameof(query.Page), query.Page, "Page must be at least 1.");
        if (query.PageSize is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(query.PageSize), query.PageSize, "PageSize must be between 1 and 500.");
        lock (_gate)
        {
            IEnumerable<ReferenceCode> result = VisibleRecords(query.TenantId)
                .Where(x => x.Coding.CodeSystem.Equals(query.CodeSystem, StringComparison.OrdinalIgnoreCase));
            if (query.Version is not null) result = result.Where(x => x.Coding.Version == query.Version);
            if (query.Category is not null) result = result.Where(x => string.Equals(x.Category, query.Category, StringComparison.OrdinalIgnoreCase));
            if (query.Active is not null) result = result.Where(x => x.Active == query.Active);
            if (query.EffectiveDate is not null) result = result.Where(x => x.IsEffectiveOn(query.EffectiveDate.Value));
            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                var term = query.Search.Trim();
                result = query.SearchMode switch
                {
                    ReferenceSearchMode.Exact => result.Where(x => x.Coding.Code.Equals(term, StringComparison.OrdinalIgnoreCase)),
                    ReferenceSearchMode.Prefix => result.Where(x => x.Coding.Code.StartsWith(term, StringComparison.OrdinalIgnoreCase)),
                    _ => result.Where(x => x.Coding.Code.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || (x.Coding.Display?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (x.Description?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
                };
            }
            var materialized = result.OrderBy(x => x.Coding.Code).ThenByDescending(x => x.EffectiveFrom).ToList();
            return Task.FromResult(new Page<ReferenceCode>(materialized.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToList(), materialized.Count, query.Page, query.PageSize));
        }
    }

    public Task<ImportResult> ImportAsync(IReadOnlyList<ReferenceCode> records, CancellationToken ct = default)
    {
        if (records.Count == 0) return Task.FromResult(new ImportResult(0, false, string.Empty));
        var first = records[0];
        if (string.IsNullOrWhiteSpace(first.Checksum))
            throw new ArgumentException("Every import record must have a checksum.", nameof(records));
        if (records.Any(record =>
                string.IsNullOrWhiteSpace(record.Checksum)
                || !string.Equals(record.Checksum, first.Checksum, StringComparison.Ordinal)
                || !string.Equals(record.SourceId, first.SourceId, StringComparison.Ordinal)
                || !string.Equals(record.SourceVersion, first.SourceVersion, StringComparison.Ordinal)))
            throw new ArgumentException("All import records must have the same source ID, source version, and checksum.", nameof(records));

        var checksum = first.Checksum;
        lock (_gate)
        {
            var alreadyImported = _records.Values.Any(x => x.Checksum == checksum && x.SourceId == first.SourceId && x.SourceVersion == first.SourceVersion);
            if (alreadyImported) return Task.FromResult(new ImportResult(0, true, checksum));
            foreach (var record in records) _records[StorageKey(record)] = record;
            return Task.FromResult(new ImportResult(records.Count, false, checksum));
        }
    }

    private IEnumerable<ReferenceCode> VisibleRecords(string? tenantId) =>
        _records.Values.Where(x => x.TenantId is null || (tenantId is not null && x.TenantId == tenantId));

    private static string StorageKey(ReferenceCode x) => string.Join('|',
        x.TenantId ?? "global",
        x.Coding.CodeSystem.ToUpperInvariant(),
        x.Coding.Code.ToUpperInvariant(),
        x.Coding.Version?.ToUpperInvariant(),
        x.EffectiveFrom.ToString("yyyyMMdd"));
}
