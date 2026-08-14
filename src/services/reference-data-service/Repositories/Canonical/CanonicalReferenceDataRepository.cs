using CloudHealthOffice.ReferenceData.Domain;
using CloudHealthOffice.ReferenceData.Persistence;
using CloudHealthOffice.ReferenceData.Sources;
using Microsoft.EntityFrameworkCore;

namespace ReferenceDataService.Repositories.Canonical;

public sealed class CanonicalReferenceDataRepository : CloudHealthOffice.ReferenceData.Persistence.IReferenceDataRepository
{
    private readonly ReferenceDataContext _context;

    public CanonicalReferenceDataRepository(ReferenceDataContext context)
    {
        _context = context;
    }

    public async Task<ReferenceCode?> GetAsync(
        string codeSystem,
        string code,
        DateOnly effectiveDate,
        string? version = null,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var normalizedSystem = Normalize(codeSystem);
        var normalizedCode = Normalize(code);

        var entity = await VisibleRecords(tenantId)
            .Where(x => x.CodeSystem.ToUpper() == normalizedSystem
                && x.Code.ToUpper() == normalizedCode
                && (version == null || x.Version == version)
                && x.Active
                && x.EffectiveFrom <= effectiveDate
                && (x.EffectiveTo == null || x.EffectiveTo >= effectiveDate))
            .OrderByDescending(x => x.EffectiveFrom)
            .ThenByDescending(x => x.ImportedAt)
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : ToDomain(entity);
    }

    public async Task<Page<ReferenceCode>> SearchAsync(ReferenceDataQuery query, CancellationToken ct = default)
    {
        ValidatePaging(query);

        var normalizedSystem = Normalize(query.CodeSystem);
        var records = VisibleRecords(query.TenantId)
            .Where(x => x.CodeSystem.ToUpper() == normalizedSystem);

        if (query.Version is not null)
            records = records.Where(x => x.Version == query.Version);
        if (query.Category is not null)
        {
            var category = Normalize(query.Category);
            records = records.Where(x => x.Category != null && x.Category.ToUpper() == category);
        }
        if (query.Active is not null)
            records = records.Where(x => x.Active == query.Active);
        if (query.EffectiveDate is not null)
        {
            var date = query.EffectiveDate.Value;
            records = records.Where(x => x.Active
                && x.EffectiveFrom <= date
                && (x.EffectiveTo == null || x.EffectiveTo >= date));
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = Normalize(query.Search);
            records = query.SearchMode switch
            {
                ReferenceSearchMode.Exact => records.Where(x => x.Code.ToUpper() == term),
                ReferenceSearchMode.Prefix => records.Where(x => x.Code.ToUpper().StartsWith(term)),
                _ => records.Where(x => x.Code.ToUpper().Contains(term)
                    || (x.Display != null && x.Display.ToUpper().Contains(term))
                    || (x.Description != null && x.Description.ToUpper().Contains(term)))
            };
        }

        var total = await records.CountAsync(ct);
        var items = await records
            .OrderBy(x => x.Code)
            .ThenByDescending(x => x.EffectiveFrom)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .AsNoTracking()
            .ToListAsync(ct);

        return new Page<ReferenceCode>(items.Select(ToDomain).ToList(), total, query.Page, query.PageSize);
    }

    public async Task<ImportResult> ImportAsync(IReadOnlyList<ReferenceCode> records, CancellationToken ct = default)
    {
        if (records.Count == 0)
            return new ImportResult(0, false, string.Empty);

        ValidateBatch(records);
        var first = records[0];
        var importKey = ImportKey(first.SourceId, first.SourceVersion, first.Checksum);

        if (await _context.CanonicalReferenceDataImports.AsNoTracking()
                .AnyAsync(x => x.ImportKey == importKey, ct))
            return new ImportResult(0, true, first.Checksum);

        var entities = records.Select(ToEntity).ToList();
        var storageKeys = entities.Select(x => x.StorageKey).ToList();
        var existing = await _context.CanonicalReferenceCodes
            .Where(x => storageKeys.Contains(x.StorageKey))
            .ToDictionaryAsync(x => x.StorageKey, ct);

        foreach (var entity in entities)
        {
            if (existing.TryGetValue(entity.StorageKey, out var current))
                _context.Entry(current).CurrentValues.SetValues(entity);
            else
                _context.CanonicalReferenceCodes.Add(entity);
        }

        _context.CanonicalReferenceDataImports.Add(new CanonicalReferenceDataImportEntity
        {
            ImportKey = importKey,
            SourceId = first.SourceId,
            SourceVersion = first.SourceVersion,
            Checksum = first.Checksum,
            ImportedAt = DateTimeOffset.UtcNow,
            RecordCount = records.Count
        });

        await _context.SaveChangesAsync(ct);
        return new ImportResult(records.Count, false, first.Checksum);
    }

    private IQueryable<CanonicalReferenceCodeEntity> VisibleRecords(string? tenantId) =>
        _context.CanonicalReferenceCodes.Where(x =>
            x.TenantId == null || (tenantId != null && x.TenantId == tenantId));

    private static void ValidatePaging(ReferenceDataQuery query)
    {
        if (query.Page < 1)
            throw new ArgumentOutOfRangeException(nameof(query.Page), query.Page, "Page must be at least 1.");
        if (query.PageSize is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(query.PageSize), query.PageSize, "PageSize must be between 1 and 500.");
    }

    private static void ValidateBatch(IReadOnlyList<ReferenceCode> records)
    {
        var first = records[0];
        if (string.IsNullOrWhiteSpace(first.Checksum))
            throw new ArgumentException("Every import record must have a checksum.", nameof(records));
        if (records.Any(record =>
                string.IsNullOrWhiteSpace(record.Checksum)
                || !string.Equals(record.Checksum, first.Checksum, StringComparison.Ordinal)
                || !string.Equals(record.SourceId, first.SourceId, StringComparison.Ordinal)
                || !string.Equals(record.SourceVersion, first.SourceVersion, StringComparison.Ordinal)))
            throw new ArgumentException("All import records must have the same source ID, source version, and checksum.", nameof(records));

        var duplicateKey = records.GroupBy(StorageKey, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicateKey is not null)
            throw new ArgumentException($"The import contains duplicate logical record '{duplicateKey.Key}'.", nameof(records));
    }

    private static CanonicalReferenceCodeEntity ToEntity(ReferenceCode record) => new()
    {
        StorageKey = StorageKey(record),
        Id = record.Id,
        TenantId = record.TenantId,
        CodeSystem = record.Coding.CodeSystem,
        CodeSystemUri = record.Coding.CodeSystemUri,
        Code = record.Coding.Code,
        Version = record.Coding.Version,
        Display = record.Coding.Display,
        Description = record.Description,
        Category = record.Category,
        EffectiveFrom = record.EffectiveFrom,
        EffectiveTo = record.EffectiveTo,
        Active = record.Active,
        SourceId = record.SourceId,
        SourceVersion = record.SourceVersion,
        LicenseClassification = record.LicenseClassification,
        ExposureClassification = record.ExposureClassification,
        ImportedAt = record.ImportedAt,
        Checksum = record.Checksum
    };

    private static ReferenceCode ToDomain(CanonicalReferenceCodeEntity entity) => new()
    {
        Id = entity.Id,
        TenantId = entity.TenantId,
        Coding = new ChoCoding
        {
            CodeSystem = entity.CodeSystem,
            CodeSystemUri = entity.CodeSystemUri,
            Code = entity.Code,
            Version = entity.Version,
            Display = entity.Display
        },
        Description = entity.Description,
        Category = entity.Category,
        EffectiveFrom = entity.EffectiveFrom,
        EffectiveTo = entity.EffectiveTo,
        Active = entity.Active,
        SourceId = entity.SourceId,
        SourceVersion = entity.SourceVersion,
        LicenseClassification = entity.LicenseClassification,
        ExposureClassification = entity.ExposureClassification,
        ImportedAt = entity.ImportedAt,
        Checksum = entity.Checksum
    };

    private static string StorageKey(ReferenceCode record) => string.Join('|',
        record.TenantId ?? "global",
        Normalize(record.Coding.CodeSystem),
        Normalize(record.Coding.Code),
        record.Coding.Version is null ? string.Empty : Normalize(record.Coding.Version),
        record.EffectiveFrom.ToString("yyyyMMdd"));

    private static string ImportKey(string sourceId, string sourceVersion, string checksum) =>
        $"{sourceId}|{sourceVersion}|{checksum}";

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
}
