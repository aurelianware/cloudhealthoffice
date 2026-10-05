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
    /// <summary>
    /// Imports one batch. "Already imported" is decided within the batch's
    /// scope only (<see cref="ReferenceDataImportScope.Of"/>: its tenant, or
    /// global): source id, version and checksum for the same scope. One tenant's
    /// import never suppresses, or reveals, another's.
    /// <paramref name="importedBy"/> is the acting identity from the caller's
    /// token, recorded on the import ledger.
    /// </summary>
    Task<ImportResult> ImportAsync(IReadOnlyList<ReferenceCode> records, string? importedBy = null, CancellationToken ct = default);
}

/// <summary>Import scope helpers shared by the repositories.</summary>
public static class ReferenceDataImportScope
{
    /// <summary>The ledger scope of global (cross-tenant) imports.</summary>
    public const string Global = "global";

    /// <summary>
    /// The batch's scope: its tenant, or <see cref="Global"/> for global records.
    /// A batch that carries both (only a platform administrator can send one
    /// for its own tenant) is scoped by both, e.g. <c>global+tenant-1</c>, so it
    /// never matches another tenant's import either.
    /// </summary>
    public static string Of(IReadOnlyList<ReferenceCode> records)
    {
        var scopes = records
            .Select(record => record.TenantId)
            .Distinct(StringComparer.Ordinal)
            .Select(tenant =>
            {
                if (tenant is null) return Global;
                if (string.IsNullOrWhiteSpace(tenant) || tenant.Contains('+') || tenant.Contains('|')
                    || string.Equals(tenant, Global, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"'{tenant}' is not a valid tenant for reference data.", nameof(records));
                return tenant;
            })
            .OrderBy(scope => scope == Global ? 0 : 1)
            .ThenBy(scope => scope, StringComparer.Ordinal);
        return string.Join('+', scopes);
    }
}

/// <summary>An import ledger entry.</summary>
public sealed record ReferenceDataImportRecord(
    string Scope, string SourceId, string SourceVersion, string Checksum, int RecordCount, string? ImportedBy, DateTimeOffset ImportedAt);

/// <summary>Deterministic repository used by tests and local composition roots.</summary>
public sealed class InMemoryReferenceDataRepository : IReferenceDataRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ReferenceCode> _records = new(StringComparer.Ordinal);
    private readonly List<ReferenceDataImportRecord> _imports = new();

    /// <summary>The import ledger (scope, source, checksum, actor).</summary>
    public IReadOnlyList<ReferenceDataImportRecord> Imports
    {
        get { lock (_gate) return _imports.ToList(); }
    }

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

    public Task<ImportResult> ImportAsync(IReadOnlyList<ReferenceCode> records, string? importedBy = null, CancellationToken ct = default)
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

        var scope = ReferenceDataImportScope.Of(records);
        var checksum = first.Checksum;
        lock (_gate)
        {
            var alreadyImported = _imports.Any(x => x.Scope == scope && x.Checksum == checksum
                && x.SourceId == first.SourceId && x.SourceVersion == first.SourceVersion);
            if (alreadyImported) return Task.FromResult(new ImportResult(0, true, checksum));
            foreach (var record in records) _records[StorageKey(record)] = record;
            _imports.Add(new ReferenceDataImportRecord(scope, first.SourceId, first.SourceVersion, checksum, records.Count, importedBy, DateTimeOffset.UtcNow));
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
