using System.Globalization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace CloudHealthOffice.FeeScheduleEngine.Medicaid;

// ═══════════════════════════════════════════════════════════════════
// MEDICAID HOSPITAL INTERIM RATES
//
// A Texas hospital's interim rate is its own cost-to-charge ratio, set by
// TMHP from that hospital's cost report and sent to that hospital, one
// letter at a time. It is needed to estimate cost from charges: Texas
// inpatient outliers (1 TAC §355.8052(i)(4)) and outpatient reimbursement
// (§355.8061(b)).
//
// Unlike the SDA files there is no single state table, so rates are stored
// one document per hospital and effective date. The rate in force on a date
// is the hospital's latest one effective on or before it, looked up
// separately for inpatient and outpatient: a letter that changes only the
// outpatient rate leaves the inpatient rate where it was.
//
// Recommended index (create once at tenant onboarding):
//   db.MedicaidHospitalInterimRates.createIndex({ tenantId: 1, state: 1, npi: 1, effectiveDate: -1 })
// ═══════════════════════════════════════════════════════════════════

/// <summary>Which interim rate: a hospital has one for inpatient and one for outpatient.</summary>
public enum InterimRateKind
{
    Inpatient,
    Outpatient,
}

/// <summary>One hospital's interim rates in force from <see cref="EffectiveDate"/>.</summary>
[BsonIgnoreExtraElements]
public sealed class MedicaidInterimRate
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    [BsonElement("tenantId")] public string TenantId { get; set; } = string.Empty;
    [BsonElement("state")] public string State { get; set; } = string.Empty;
    [BsonElement("npi")] public string Npi { get; set; } = string.Empty;

    /// <summary>The state's provider id (Texas TPI), when the file gives one.</summary>
    public string? StateProviderId { get; set; }
    public string? HospitalName { get; set; }

    [BsonElement("effectiveDate")] public DateTime EffectiveDate { get; set; }

    /// <summary>Inpatient interim rate as a fraction (0.452 for 45.2%). Null when this letter does not set it.</summary>
    public decimal? InpatientRate { get; set; }

    /// <summary>Outpatient interim rate as a fraction. Null when this letter does not set it.</summary>
    public decimal? OutpatientRate { get; set; }

    public string SourceFile { get; set; } = string.Empty;
    public DateTime ImportedAt { get; set; }
    public string ImportedBy { get; set; } = string.Empty;

    public decimal? Rate(InterimRateKind kind) => kind == InterimRateKind.Inpatient ? InpatientRate : OutpatientRate;

    public static string MakeId(string tenantId, string state, string npi, DateTime effective) =>
        string.Create(CultureInfo.InvariantCulture, $"{tenantId}:{state.ToUpperInvariant()}:interim:{npi}:{effective:yyyyMMdd}");
}

/// <summary>Storage for hospital interim rates.</summary>
public interface IMedicaidInterimRateRepository
{
    /// <summary>
    /// The hospital's latest rate of <paramref name="kind"/> effective on or
    /// before <paramref name="date"/>, with the record it came from; null when none.
    /// </summary>
    Task<MedicaidInterimRate?> FindAsync(string tenantId, string state, string npi, InterimRateKind kind, DateTime date, CancellationToken ct = default);

    /// <summary>The records with these ids, for the importer's comparison.</summary>
    Task<IReadOnlyList<MedicaidInterimRate>> GetByIdsAsync(string tenantId, IReadOnlyCollection<string> ids, CancellationToken ct = default);

    /// <summary>Creates or replaces each record by id.</summary>
    Task UpsertAsync(IReadOnlyCollection<MedicaidInterimRate> rates, CancellationToken ct = default);
}

/// <summary>MongoDB store, collection <c>MedicaidHospitalInterimRates</c>.</summary>
public sealed class MedicaidInterimRateRepositoryMongo : IMedicaidInterimRateRepository
{
    private readonly IMongoCollection<MedicaidInterimRate> _rates;

    public MedicaidInterimRateRepositoryMongo(IMongoDatabase database) =>
        _rates = database.GetCollection<MedicaidInterimRate>("MedicaidHospitalInterimRates");

    public async Task<MedicaidInterimRate?> FindAsync(string tenantId, string state, string npi, InterimRateKind kind, DateTime date, CancellationToken ct = default)
    {
        var st = state.ToUpperInvariant();
        var filter = Builders<MedicaidInterimRate>.Filter.Where(r =>
            r.TenantId == tenantId && r.State == st && r.Npi == npi && r.EffectiveDate <= date.Date);
        filter &= kind == InterimRateKind.Inpatient
            ? Builders<MedicaidInterimRate>.Filter.Ne(r => r.InpatientRate, null)
            : Builders<MedicaidInterimRate>.Filter.Ne(r => r.OutpatientRate, null);
        return await _rates.Find(filter).SortByDescending(r => r.EffectiveDate).FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<MedicaidInterimRate>> GetByIdsAsync(string tenantId, IReadOnlyCollection<string> ids, CancellationToken ct = default) =>
        ids.Count == 0
            ? []
            : await _rates.Find(r => r.TenantId == tenantId && ids.Contains(r.Id)).ToListAsync(ct);

    public async Task UpsertAsync(IReadOnlyCollection<MedicaidInterimRate> rates, CancellationToken ct = default)
    {
        if (rates.Count == 0) return;
        var writes = rates.Select(r => new ReplaceOneModel<MedicaidInterimRate>(
            Builders<MedicaidInterimRate>.Filter.Where(x => x.Id == r.Id && x.TenantId == r.TenantId), r) { IsUpsert = true });
        await _rates.BulkWriteAsync(writes, cancellationToken: ct);
    }
}

/// <summary>In-memory store for tests and local runs.</summary>
public sealed class InMemoryMedicaidInterimRateRepository : IMedicaidInterimRateRepository
{
    private readonly Dictionary<string, MedicaidInterimRate> _rates = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public IReadOnlyCollection<MedicaidInterimRate> Rates { get { lock (_gate) return _rates.Values.ToList(); } }

    public Task<MedicaidInterimRate?> FindAsync(string tenantId, string state, string npi, InterimRateKind kind, DateTime date, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_rates.Values
                .Where(r => r.TenantId == tenantId && r.State == state.ToUpperInvariant() && r.Npi == npi
                    && r.EffectiveDate <= date.Date && r.Rate(kind) is not null)
                .OrderByDescending(r => r.EffectiveDate)
                .FirstOrDefault());
    }

    public Task<IReadOnlyList<MedicaidInterimRate>> GetByIdsAsync(string tenantId, IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<MedicaidInterimRate>>(
                ids.Select(id => _rates.TryGetValue(id, out var r) && r.TenantId == tenantId ? r : null)
                    .OfType<MedicaidInterimRate>().ToList());
    }

    public Task UpsertAsync(IReadOnlyCollection<MedicaidInterimRate> rates, CancellationToken ct = default)
    {
        lock (_gate)
            foreach (var r in rates) _rates[r.Id] = r;
        return Task.CompletedTask;
    }
}
