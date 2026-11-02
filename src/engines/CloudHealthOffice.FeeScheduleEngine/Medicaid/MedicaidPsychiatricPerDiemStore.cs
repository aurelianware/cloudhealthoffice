using System.Globalization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace CloudHealthOffice.FeeScheduleEngine.Medicaid;

// ═══════════════════════════════════════════════════════════════════
// MEDICAID PSYCHIATRIC HOSPITAL PER DIEMS
//
// One document per hospital (NPI) and effective date, in collection
// MedicaidPsychiatricPerDiems. The rate in force on a date is the
// hospital's latest one effective on or before it.
//
// Recommended index (create once at tenant onboarding):
//   db.MedicaidPsychiatricPerDiems.createIndex({ tenantId: 1, state: 1, npi: 1, effectiveDate: -1 })
// ═══════════════════════════════════════════════════════════════════

/// <summary>One psychiatric hospital's per diem in force from <see cref="EffectiveDate"/>.</summary>
[BsonIgnoreExtraElements]
public sealed class MedicaidPsychiatricPerDiem
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    [BsonElement("tenantId")] public string TenantId { get; set; } = string.Empty;
    [BsonElement("state")] public string State { get; set; } = string.Empty;
    [BsonElement("npi")] public string Npi { get; set; } = string.Empty;

    /// <summary>The state's provider id (Texas TPI).</summary>
    public string? StateProviderId { get; set; }
    public string? HospitalName { get; set; }

    /// <summary>The section the state lists the hospital under, e.g. "Private Freestanding" or "State-Owned".</summary>
    public string? FacilityClass { get; set; }

    [BsonElement("effectiveDate")] public DateTime EffectiveDate { get; set; }

    public decimal DailyRate { get; set; }

    public string SourceFile { get; set; } = string.Empty;
    public DateTime ImportedAt { get; set; }
    public string ImportedBy { get; set; } = string.Empty;

    public static string MakeId(string tenantId, string state, string npi, DateTime effective) =>
        string.Create(CultureInfo.InvariantCulture, $"{tenantId}:{state.ToUpperInvariant()}:psych:{npi}:{effective:yyyyMMdd}");

    /// <summary>As the pricer takes it, open-ended from its effective date.</summary>
    public PsychiatricPerDiemRate ToRate() => new()
    {
        Npi = Npi,
        DailyRate = DailyRate,
        EffectiveFrom = DateOnly.FromDateTime(EffectiveDate),
        Source = string.IsNullOrEmpty(SourceFile) ? null : SourceFile,
    };
}

/// <summary>Storage for psychiatric hospital per diems.</summary>
public interface IMedicaidPsychiatricPerDiemRepository
{
    /// <summary>The hospital's latest per diem effective on or before <paramref name="date"/>; null when none.</summary>
    Task<MedicaidPsychiatricPerDiem?> FindAsync(string tenantId, string state, string npi, DateTime date, CancellationToken ct = default);

    /// <summary>The records with these ids, for the importer's comparison.</summary>
    Task<IReadOnlyList<MedicaidPsychiatricPerDiem>> GetByIdsAsync(string tenantId, IReadOnlyCollection<string> ids, CancellationToken ct = default);

    /// <summary>Creates or replaces each record by id.</summary>
    Task UpsertAsync(IReadOnlyCollection<MedicaidPsychiatricPerDiem> rates, CancellationToken ct = default);
}

/// <summary>MongoDB store, collection <c>MedicaidPsychiatricPerDiems</c>.</summary>
public sealed class MedicaidPsychiatricPerDiemRepositoryMongo : IMedicaidPsychiatricPerDiemRepository
{
    private readonly IMongoCollection<MedicaidPsychiatricPerDiem> _rates;

    public MedicaidPsychiatricPerDiemRepositoryMongo(IMongoDatabase database) =>
        _rates = database.GetCollection<MedicaidPsychiatricPerDiem>("MedicaidPsychiatricPerDiems");

    public async Task<MedicaidPsychiatricPerDiem?> FindAsync(string tenantId, string state, string npi, DateTime date, CancellationToken ct = default)
    {
        var st = state.ToUpperInvariant();
        return await _rates.Find(r => r.TenantId == tenantId && r.State == st && r.Npi == npi && r.EffectiveDate <= date.Date)
            .SortByDescending(r => r.EffectiveDate)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<MedicaidPsychiatricPerDiem>> GetByIdsAsync(string tenantId, IReadOnlyCollection<string> ids, CancellationToken ct = default) =>
        ids.Count == 0
            ? []
            : await _rates.Find(r => r.TenantId == tenantId && ids.Contains(r.Id)).ToListAsync(ct);

    public async Task UpsertAsync(IReadOnlyCollection<MedicaidPsychiatricPerDiem> rates, CancellationToken ct = default)
    {
        if (rates.Count == 0) return;
        var writes = rates.Select(r => new ReplaceOneModel<MedicaidPsychiatricPerDiem>(
            Builders<MedicaidPsychiatricPerDiem>.Filter.Where(x => x.Id == r.Id && x.TenantId == r.TenantId), r) { IsUpsert = true });
        await _rates.BulkWriteAsync(writes, cancellationToken: ct);
    }
}

/// <summary>In-memory store for tests and local runs.</summary>
public sealed class InMemoryMedicaidPsychiatricPerDiemRepository : IMedicaidPsychiatricPerDiemRepository
{
    private readonly Dictionary<string, MedicaidPsychiatricPerDiem> _rates = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public IReadOnlyCollection<MedicaidPsychiatricPerDiem> Rates { get { lock (_gate) return _rates.Values.ToList(); } }

    public Task<MedicaidPsychiatricPerDiem?> FindAsync(string tenantId, string state, string npi, DateTime date, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_rates.Values
                .Where(r => r.TenantId == tenantId && r.State == state.ToUpperInvariant() && r.Npi == npi && r.EffectiveDate <= date.Date)
                .OrderByDescending(r => r.EffectiveDate)
                .FirstOrDefault());
    }

    public Task<IReadOnlyList<MedicaidPsychiatricPerDiem>> GetByIdsAsync(string tenantId, IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<MedicaidPsychiatricPerDiem>>(
                ids.Select(id => _rates.TryGetValue(id, out var r) && r.TenantId == tenantId ? r : null)
                    .OfType<MedicaidPsychiatricPerDiem>().ToList());
    }

    public Task UpsertAsync(IReadOnlyCollection<MedicaidPsychiatricPerDiem> rates, CancellationToken ct = default)
    {
        lock (_gate)
            foreach (var r in rates) _rates[r.Id] = r;
        return Task.CompletedTask;
    }
}
