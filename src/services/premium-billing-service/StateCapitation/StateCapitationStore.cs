using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;

namespace PremiumBillingService.StateCapitation;

/// <summary>A state PMPM row as stored: one rate cell over one effective period.</summary>
public sealed class StoredStateRate
{
    [BsonId] public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string RateCell { get; set; } = string.Empty;
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public decimal Pmpm { get; set; }
    public string? SourceFile { get; set; }
    public string? LoadedBy { get; set; }
    public DateTime LoadedAt { get; set; }

    public StateCapitationRate ToRate() => new(RateCell, DateOnly.FromDateTime(EffectiveFrom),
        EffectiveTo == null ? null : DateOnly.FromDateTime(EffectiveTo.Value), Pmpm);
}

/// <summary>One expected member-month as stored; a reload of the month replaces it.</summary>
public sealed class StoredExpectedMonth
{
    [BsonId] public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public DateTime Month { get; set; }
    public string RateCell { get; set; } = string.Empty;
    public int? DaysEnrolled { get; set; }
    public string? SourceFile { get; set; }
    public string? LoadedBy { get; set; }
    public DateTime LoadedAt { get; set; }

    public ExpectedMemberMonth ToExpected() => new(MemberId, DateOnly.FromDateTime(Month), RateCell, DaysEnrolled);
}

/// <summary>One state 820 payment (one ST/SE) as received; the trace number is unique per tenant and state.</summary>
public sealed class StoredStatePayment
{
    [BsonId] public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string TraceNumber { get; set; } = string.Empty;
    public DateTime PaymentDate { get; set; }
    public decimal PaymentAmount { get; set; }
    public string? SourceFileName { get; set; }
    public string? ReceivedBy { get; set; }
    public DateTime ReceivedAt { get; set; }
    public RemittanceAdvice Advice { get; set; } = new();
}

/// <summary>
/// State capitation inputs for the request tenant: the state's rate sheet,
/// the plan's expected member-months, and the state's 820 payments. Kept apart
/// from group premium remittances, so a state 820 never goes through premium
/// cash application or its exceptions queue.
/// </summary>
public interface IStateCapitationStore
{
    /// <summary>Adds or replaces rate rows (same state, cell and effective start replaces).</summary>
    Task<int> UpsertRatesAsync(string state, IReadOnlyList<StoredStateRate> rates);

    Task<IReadOnlyList<StateCapitationRate>> GetRatesAsync(string state);

    /// <summary>
    /// Replaces the expected member-months of every month in <paramref name="months"/>
    /// with the rows given: a corrected roster for a month is loaded whole.
    /// </summary>
    Task<int> ReplaceExpectedAsync(string state, IReadOnlyCollection<DateOnly> months, IReadOnlyList<StoredExpectedMonth> rows);

    Task<IReadOnlyList<ExpectedMemberMonth>> GetExpectedAsync(string state, DateOnly fromMonth, DateOnly toMonth);

    /// <summary>Records a payment; false when this trace number was already recorded for the state.</summary>
    Task<bool> TryAddPaymentAsync(StoredStatePayment payment);

    Task<IReadOnlyList<RemittanceAdvice>> GetPaymentsAsync(string state);
}

public sealed class StateCapitationStoreMongo : IStateCapitationStore
{
    private readonly IMongoCollection<StoredStateRate> _rates;
    private readonly IMongoCollection<StoredExpectedMonth> _expected;
    private readonly IMongoCollection<StoredStatePayment> _payments;
    private readonly IHttpContextAccessor _http;

    public StateCapitationStoreMongo(IMongoDatabase database, IHttpContextAccessor http)
    {
        _rates = database.GetCollection<StoredStateRate>("StateCapitationRates");
        _expected = database.GetCollection<StoredExpectedMonth>("StateCapitationExpected");
        _payments = database.GetCollection<StoredStatePayment>("StateCapitationPayments");
        _http = http;

        _expected.Indexes.CreateOne(new CreateIndexModel<StoredExpectedMonth>(
            Builders<StoredExpectedMonth>.IndexKeys.Ascending(x => x.TenantId).Ascending(x => x.State).Ascending(x => x.Month)));
        _payments.Indexes.CreateOne(new CreateIndexModel<StoredStatePayment>(
            Builders<StoredStatePayment>.IndexKeys.Ascending(x => x.TenantId).Ascending(x => x.State)));
    }

    private string Tenant => RepositoryTenant.From(_http);

    /// <summary>Ids carry tenant and natural key, so a reload replaces and a repeat payment collides.</summary>
    public static string RateId(string tenant, string state, string cell, DateTime from) =>
        $"{tenant}|{state}|{cell.ToUpperInvariant()}|{from:yyyy-MM-dd}";

    public static string ExpectedId(string tenant, string state, string member, DateTime month) =>
        $"{tenant}|{state}|{member.Trim().ToUpperInvariant()}|{month:yyyy-MM}";

    public static string PaymentId(string tenant, string state, string trace) => $"{tenant}|{state}|{trace}";

    public async Task<int> UpsertRatesAsync(string state, IReadOnlyList<StoredStateRate> rates)
    {
        var tenant = Tenant;
        if (rates.Count == 0) return 0;
        var writes = rates.Select(r =>
        {
            r.TenantId = tenant;
            r.State = state;
            r.Id = RateId(tenant, state, r.RateCell, r.EffectiveFrom);
            return new ReplaceOneModel<StoredStateRate>(Builders<StoredStateRate>.Filter.Eq(x => x.Id, r.Id), r) { IsUpsert = true };
        }).ToList();
        await _rates.BulkWriteAsync(writes);
        return rates.Count;
    }

    public async Task<IReadOnlyList<StateCapitationRate>> GetRatesAsync(string state)
    {
        var tenant = Tenant;
        var rows = await _rates.Find(x => x.TenantId == tenant && x.State == state).ToListAsync();
        return rows.Select(r => r.ToRate()).ToList();
    }

    public async Task<int> ReplaceExpectedAsync(string state, IReadOnlyCollection<DateOnly> months, IReadOnlyList<StoredExpectedMonth> rows)
    {
        var tenant = Tenant;
        var monthStarts = months.Select(m => m.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).ToList();
        await _expected.DeleteManyAsync(x => x.TenantId == tenant && x.State == state && monthStarts.Contains(x.Month));
        if (rows.Count == 0) return 0;
        foreach (var r in rows)
        {
            r.TenantId = tenant;
            r.State = state;
            r.Id = ExpectedId(tenant, state, r.MemberId, r.Month);
        }
        // Upsert, not insert: a roster that names a member-month twice keeps the last row.
        await _expected.BulkWriteAsync(rows.Select(r =>
            new ReplaceOneModel<StoredExpectedMonth>(Builders<StoredExpectedMonth>.Filter.Eq(x => x.Id, r.Id), r) { IsUpsert = true }));
        return rows.Count;
    }

    public async Task<IReadOnlyList<ExpectedMemberMonth>> GetExpectedAsync(string state, DateOnly fromMonth, DateOnly toMonth)
    {
        var tenant = Tenant;
        var from = fromMonth.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var to = toMonth.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var rows = await _expected.Find(x => x.TenantId == tenant && x.State == state && x.Month >= from && x.Month <= to).ToListAsync();
        return rows.Select(r => r.ToExpected()).ToList();
    }

    public async Task<bool> TryAddPaymentAsync(StoredStatePayment payment)
    {
        payment.TenantId = Tenant;
        payment.Id = PaymentId(payment.TenantId, payment.State, payment.TraceNumber);
        try
        {
            await _payments.InsertOneAsync(payment);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<RemittanceAdvice>> GetPaymentsAsync(string state)
    {
        var tenant = Tenant;
        var rows = await _payments.Find(x => x.TenantId == tenant && x.State == state)
            .SortBy(x => x.PaymentDate).ToListAsync();
        return rows.Select(r => r.Advice).ToList();
    }
}
