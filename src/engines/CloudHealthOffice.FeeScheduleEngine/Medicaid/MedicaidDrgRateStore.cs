using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace CloudHealthOffice.FeeScheduleEngine.Medicaid;

// ═══════════════════════════════════════════════════════════════════
// MEDICAID DRG RATE STORE
//
// The state-published inputs the Medicaid DRG pricers need, per tenant:
//
//   * weight tables: one per state and effective discharge date, holding
//     every DRG-SOI's relative weight, MLOS and day outlier threshold;
//   * hospital rate tables: one per state, hospital class and effective
//     date, holding each hospital's final base rate (Texas SDA) and, for
//     Texas rural hospitals, the delivery SDA.
//
// A table is a single document (about 1,300 weight rows or 300 hospital
// rows), written whole by an importer and read whole by pricing, like a fee
// schedule. The table in force on a date is the one with the latest
// effective date on or before it, so loading next year's table never
// changes how an earlier discharge prices.
//
// Recommended indexes (create once at tenant onboarding):
//   db.MedicaidDrgWeightTables.createIndex({ tenantId: 1, state: 1, effectiveDate: -1 })
//   db.MedicaidHospitalRateTables.createIndex({ tenantId: 1, state: 1, effectiveDate: -1 })
// ═══════════════════════════════════════════════════════════════════

/// <summary>A state's APR-DRG weight table in force from <see cref="EffectiveDate"/>.</summary>
[BsonIgnoreExtraElements]
public sealed class MedicaidDrgWeightTable
{
    /// <summary>"{tenantId}:{state}:weights:{effectiveDate:yyyyMMdd}".</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    [BsonElement("tenantId")] public string TenantId { get; set; } = string.Empty;

    /// <summary>Two-letter state, e.g. "TX".</summary>
    [BsonElement("state")] public string State { get; set; } = string.Empty;

    /// <summary>3M APR-DRG grouper version the weights were calibrated on, e.g. "40".</summary>
    public string GrouperVersion { get; set; } = string.Empty;

    /// <summary>First discharge date the table applies to.</summary>
    [BsonElement("effectiveDate")] public DateTime EffectiveDate { get; set; }

    public int? StateFiscalYear { get; set; }

    /// <summary>Texas universal mean, when the table prints it.</summary>
    public decimal? UniversalMean { get; set; }

    public string SourceFile { get; set; } = string.Empty;
    public string Checksum { get; set; } = string.Empty;
    public DateTime ImportedAt { get; set; }
    public string ImportedBy { get; set; } = string.Empty;

    public List<MedicaidDrgWeightRow> Rows { get; set; } = [];

    public static string MakeId(string tenantId, string state, DateTime effective) =>
        $"{tenantId}:{state.ToUpperInvariant()}:weights:{effective:yyyyMMdd}";

    /// <summary>The pricer's weight for a DRG-SOI ("139-2"), or null.</summary>
    public MedicaidDrgWeight? Find(string drgSoi)
    {
        var row = Rows.FirstOrDefault(r => r.DrgSoi == drgSoi);
        return row is null ? null : new MedicaidDrgWeight
        {
            DrgSoi = row.DrgSoi,
            RelativeWeight = row.RelativeWeight,
            MeanLengthOfStay = row.MeanLengthOfStay,
            DayOutlierThreshold = row.DayOutlierThreshold,
            CareCategory = row.CareCategory,
        };
    }
}

/// <summary>One DRG-SOI's statistics.</summary>
public sealed class MedicaidDrgWeightRow
{
    public string DrgSoi { get; set; } = string.Empty;
    public decimal RelativeWeight { get; set; }
    public decimal? MeanLengthOfStay { get; set; }
    public decimal? DayOutlierThreshold { get; set; }
    public string? CareCategory { get; set; }
    public string? Description { get; set; }
}

/// <summary>One hospital class's base rates in force from <see cref="EffectiveDate"/>.</summary>
[BsonIgnoreExtraElements]
public sealed class MedicaidHospitalRateTable
{
    /// <summary>"{tenantId}:{state}:hospitals:{class}:{effectiveDate:yyyyMMdd}".</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    [BsonElement("tenantId")] public string TenantId { get; set; } = string.Empty;
    [BsonElement("state")] public string State { get; set; } = string.Empty;

    /// <summary>"Urban", "Rural" or "Childrens" for Texas.</summary>
    public string HospitalClass { get; set; } = string.Empty;

    [BsonElement("effectiveDate")] public DateTime EffectiveDate { get; set; }
    public int? StateFiscalYear { get; set; }
    public decimal? UniversalMean { get; set; }

    public string SourceFile { get; set; } = string.Empty;
    public string Checksum { get; set; } = string.Empty;
    public DateTime ImportedAt { get; set; }
    public string ImportedBy { get; set; } = string.Empty;

    public List<MedicaidHospitalRateRow> Rows { get; set; } = [];

    /// <summary>Rows the file printed with contradictory numbers; not loaded, so those hospitals pend.</summary>
    public List<MedicaidHospitalRateDiscrepancy> Discrepancies { get; set; } = [];

    public static string MakeId(string tenantId, string state, string hospitalClass, DateTime effective) =>
        $"{tenantId}:{state.ToUpperInvariant()}:hospitals:{hospitalClass.ToLowerInvariant()}:{effective:yyyyMMdd}";
}

/// <summary>One hospital's base rates.</summary>
public sealed class MedicaidHospitalRateRow
{
    /// <summary>State provider id (Texas TPI).</summary>
    public string StateProviderId { get; set; } = string.Empty;
    public string Npi { get; set; } = string.Empty;
    public string? MedicareNumber { get; set; }
    public string HospitalName { get; set; } = string.Empty;
    public string? City { get; set; }
    public string? Zip { get; set; }
    public string? County { get; set; }

    /// <summary>The base rate before add-ons.</summary>
    public decimal BaseRate { get; set; }
    public Dictionary<string, decimal> AddOns { get; set; } = [];

    /// <summary>The base rate a stay is paid with (Texas final SDA).</summary>
    public decimal FinalBaseRate { get; set; }

    /// <summary>Texas rural: the OB-GYN delivery SDA.</summary>
    public decimal? DeliveryBaseRate { get; set; }
}

public sealed class MedicaidHospitalRateDiscrepancy
{
    public string StateProviderId { get; set; } = string.Empty;
    public string Npi { get; set; } = string.Empty;
    public string HospitalName { get; set; } = string.Empty;
    public string Problem { get; set; } = string.Empty;
}

/// <summary>A hospital's rates as found for a date, with the table they came from.</summary>
public sealed record MedicaidHospitalRateMatch(MedicaidHospitalRateTable Table, MedicaidHospitalRateRow Row)
{
    /// <summary>The pricer's hospital facts. The cost-to-charge ratio is not in these tables.</summary>
    public MedicaidDrgHospital ToPricerHospital(decimal? costToChargeRatio = null) => new()
    {
        Npi = Row.Npi,
        BaseRate = Row.FinalBaseRate,
        DeliveryBaseRate = Row.DeliveryBaseRate,
        CostToChargeRatio = costToChargeRatio,
        TexasClass = Enum.TryParse<TexasHospitalClass>(Table.HospitalClass, ignoreCase: true, out var c) ? c : TexasHospitalClass.Urban,
    };
}

/// <summary>Storage for state Medicaid DRG weights and hospital base rates.</summary>
public interface IMedicaidDrgRateRepository
{
    /// <summary>The weight table in force for a discharge on <paramref name="dischargeDate"/>, or null.</summary>
    Task<MedicaidDrgWeightTable?> GetWeightTableAsync(string tenantId, string state, DateTime dischargeDate, CancellationToken ct = default);

    /// <summary>
    /// The hospital's rows in the tables in force on <paramref name="dischargeDate"/>
    /// (the latest table of each class). More than one match means the NPI bills
    /// under several state ids; the caller decides whether they agree.
    /// </summary>
    Task<IReadOnlyList<MedicaidHospitalRateMatch>> FindHospitalAsync(string tenantId, string state, string npi, DateTime dischargeDate, CancellationToken ct = default);

    Task<MedicaidDrgWeightTable?> GetWeightTableByIdAsync(string tenantId, string id, CancellationToken ct = default);
    Task<MedicaidHospitalRateTable?> GetHospitalTableByIdAsync(string tenantId, string id, CancellationToken ct = default);

    /// <summary>Creates or replaces the table with the same id.</summary>
    Task UpsertWeightTableAsync(MedicaidDrgWeightTable table, CancellationToken ct = default);

    /// <summary>Creates or replaces the table with the same id.</summary>
    Task UpsertHospitalTableAsync(MedicaidHospitalRateTable table, CancellationToken ct = default);
}

/// <summary>Shared lookup rules, so the in-memory and MongoDB stores agree.</summary>
public static class MedicaidDrgRateLookup
{
    /// <summary>The latest table of each class effective on or before the date, then the NPI's rows in them.</summary>
    public static IReadOnlyList<MedicaidHospitalRateMatch> FindHospital(IEnumerable<MedicaidHospitalRateTable> tablesInForce, string npi) =>
        tablesInForce
            .GroupBy(t => t.HospitalClass, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(t => t.EffectiveDate).First())
            .SelectMany(t => t.Rows.Where(r => r.Npi == npi).Select(r => new MedicaidHospitalRateMatch(t, r)))
            .ToList();
}

/// <summary>MongoDB store.</summary>
public sealed class MedicaidDrgRateRepositoryMongo : IMedicaidDrgRateRepository
{
    private readonly IMongoCollection<MedicaidDrgWeightTable> _weights;
    private readonly IMongoCollection<MedicaidHospitalRateTable> _hospitals;

    public MedicaidDrgRateRepositoryMongo(IMongoDatabase database)
    {
        _weights = database.GetCollection<MedicaidDrgWeightTable>("MedicaidDrgWeightTables");
        _hospitals = database.GetCollection<MedicaidHospitalRateTable>("MedicaidHospitalRateTables");
    }

    public async Task<MedicaidDrgWeightTable?> GetWeightTableAsync(string tenantId, string state, DateTime dischargeDate, CancellationToken ct = default) =>
        await _weights.Find(t => t.TenantId == tenantId && t.State == state.ToUpperInvariant() && t.EffectiveDate <= dischargeDate.Date)
            .SortByDescending(t => t.EffectiveDate)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<MedicaidHospitalRateMatch>> FindHospitalAsync(string tenantId, string state, string npi, DateTime dischargeDate, CancellationToken ct = default)
    {
        // Every class's tables up to the date; small (a few per year per class).
        var tables = await _hospitals
            .Find(t => t.TenantId == tenantId && t.State == state.ToUpperInvariant() && t.EffectiveDate <= dischargeDate.Date)
            .SortByDescending(t => t.EffectiveDate)
            .Limit(24)
            .ToListAsync(ct);
        return MedicaidDrgRateLookup.FindHospital(tables, npi);
    }

    public async Task<MedicaidDrgWeightTable?> GetWeightTableByIdAsync(string tenantId, string id, CancellationToken ct = default) =>
        await _weights.Find(t => t.Id == id && t.TenantId == tenantId).FirstOrDefaultAsync(ct);

    public async Task<MedicaidHospitalRateTable?> GetHospitalTableByIdAsync(string tenantId, string id, CancellationToken ct = default) =>
        await _hospitals.Find(t => t.Id == id && t.TenantId == tenantId).FirstOrDefaultAsync(ct);

    public Task UpsertWeightTableAsync(MedicaidDrgWeightTable table, CancellationToken ct = default) =>
        _weights.ReplaceOneAsync(t => t.Id == table.Id && t.TenantId == table.TenantId, table, new ReplaceOptions { IsUpsert = true }, ct);

    public Task UpsertHospitalTableAsync(MedicaidHospitalRateTable table, CancellationToken ct = default) =>
        _hospitals.ReplaceOneAsync(t => t.Id == table.Id && t.TenantId == table.TenantId, table, new ReplaceOptions { IsUpsert = true }, ct);
}

/// <summary>In-memory store for tests and local runs.</summary>
public sealed class InMemoryMedicaidDrgRateRepository : IMedicaidDrgRateRepository
{
    private readonly Dictionary<string, MedicaidDrgWeightTable> _weights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MedicaidHospitalRateTable> _hospitals = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public IReadOnlyCollection<MedicaidDrgWeightTable> WeightTables { get { lock (_gate) return _weights.Values.ToList(); } }
    public IReadOnlyCollection<MedicaidHospitalRateTable> HospitalTables { get { lock (_gate) return _hospitals.Values.ToList(); } }

    public Task<MedicaidDrgWeightTable?> GetWeightTableAsync(string tenantId, string state, DateTime dischargeDate, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_weights.Values
                .Where(t => t.TenantId == tenantId && t.State == state.ToUpperInvariant() && t.EffectiveDate <= dischargeDate.Date)
                .OrderByDescending(t => t.EffectiveDate)
                .FirstOrDefault());
    }

    public Task<IReadOnlyList<MedicaidHospitalRateMatch>> FindHospitalAsync(string tenantId, string state, string npi, DateTime dischargeDate, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(MedicaidDrgRateLookup.FindHospital(
                _hospitals.Values.Where(t => t.TenantId == tenantId && t.State == state.ToUpperInvariant() && t.EffectiveDate <= dischargeDate.Date).ToList(), npi));
    }

    public Task<MedicaidDrgWeightTable?> GetWeightTableByIdAsync(string tenantId, string id, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_weights.TryGetValue(id, out var t) && t.TenantId == tenantId ? t : null);
    }

    public Task<MedicaidHospitalRateTable?> GetHospitalTableByIdAsync(string tenantId, string id, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_hospitals.TryGetValue(id, out var t) && t.TenantId == tenantId ? t : null);
    }

    public Task UpsertWeightTableAsync(MedicaidDrgWeightTable table, CancellationToken ct = default)
    {
        lock (_gate) _weights[table.Id] = table;
        return Task.CompletedTask;
    }

    public Task UpsertHospitalTableAsync(MedicaidHospitalRateTable table, CancellationToken ct = default)
    {
        lock (_gate) _hospitals[table.Id] = table;
        return Task.CompletedTask;
    }
}
