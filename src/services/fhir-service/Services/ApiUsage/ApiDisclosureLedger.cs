using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FhirService.Services.ApiUsage;

/// <summary>The CMS-0057-F interoperability APIs whose use a payer reports.</summary>
public enum InteroperabilityApi
{
    PatientAccess,
    ProviderAccess,
    PayerToPayer,
    PriorAuthorization,
}

/// <summary>
/// One day on which an API sent a patient's data to one client. The ledger keeps
/// one of these per (tenant, API, patient, client, UTC day), however many
/// requests the client made that day.
/// </summary>
public sealed record ApiDisclosure(
    string TenantId, InteroperabilityApi Api, string PatientId, string ClientId, DateOnly DayUtc);

/// <summary>
/// The two Patient Access API usage figures CMS-0057-F asks for (42 CFR
/// 422.119(f)(2) and its Medicaid, CHIP and QHP counterparts), for one tenant,
/// API and period.
/// </summary>
/// <param name="UniquePatients">Patients whose data the API sent to an app at least once.</param>
/// <param name="PatientsMoreThanOnce">
/// Patients whose data the API sent more than once: on more than one day, or to
/// more than one app. Requests one app makes on one day count as one transfer,
/// because a single sync makes many calls.
/// </param>
public sealed record ApiUsageCounts(long UniquePatients, long PatientsMoreThanOnce);

/// <summary>
/// Records when the interoperability APIs disclose a patient's data, so the
/// plan's yearly usage report can be computed from what CHO actually served.
/// Holds identifiers only (tenant, patient id, OAuth client id): no resource
/// content.
/// </summary>
public interface IApiDisclosureLedger
{
    /// <summary>Records the disclosure. Repeating it the same day adds to its request count only.</summary>
    Task RecordAsync(ApiDisclosure disclosure, CancellationToken ct = default);

    /// <summary>The usage figures for days in [<paramref name="fromDay"/>, <paramref name="toDayExclusive"/>).</summary>
    Task<ApiUsageCounts> CountAsync(
        string tenantId, InteroperabilityApi api, DateOnly fromDay, DateOnly toDayExclusive, CancellationToken ct = default);

    /// <summary>The first day anything was recorded for the tenant and API, or null. Earlier days are not covered.</summary>
    Task<DateOnly?> EarliestDayAsync(string tenantId, InteroperabilityApi api, CancellationToken ct = default);
}

public sealed class MongoApiDisclosureLedger : IApiDisclosureLedger
{
    public const string CollectionName = "api_disclosures";

    private readonly IMongoCollection<BsonDocument> _collection;

    public MongoApiDisclosureLedger(IMongoClient client, IConfiguration configuration)
    {
        var database = client.GetDatabase(configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice");
        _collection = database.GetCollection<BsonDocument>(CollectionName);
        _collection.Indexes.CreateOne(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys
                .Ascending("tenantId").Ascending("api").Ascending("day").Ascending("patientId").Ascending("clientId"),
            new CreateIndexOptions { Unique = true, Name = "ux_tenant_api_day_patient_client" }));
    }

    public async Task RecordAsync(ApiDisclosure d, CancellationToken ct = default)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("tenantId", d.TenantId)
            & Builders<BsonDocument>.Filter.Eq("api", d.Api.ToString())
            & Builders<BsonDocument>.Filter.Eq("day", Day(d.DayUtc))
            & Builders<BsonDocument>.Filter.Eq("patientId", d.PatientId)
            & Builders<BsonDocument>.Filter.Eq("clientId", d.ClientId);
        var update = Builders<BsonDocument>.Update
            .SetOnInsert("firstAt", DateTime.UtcNow)
            .Inc("requests", 1);
        try
        {
            await _collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Two upserts raced to insert the same key; the document now exists.
            await _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
        }
    }

    public async Task<ApiUsageCounts> CountAsync(
        string tenantId, InteroperabilityApi api, DateOnly fromDay, DateOnly toDayExclusive, CancellationToken ct = default)
    {
        var match = Builders<BsonDocument>.Filter.Eq("tenantId", tenantId)
            & Builders<BsonDocument>.Filter.Eq("api", api.ToString())
            & Builders<BsonDocument>.Filter.Gte("day", Day(fromDay))
            & Builders<BsonDocument>.Filter.Lt("day", Day(toDayExclusive));
        var perPatient = await _collection.Aggregate()
            .Match(match)
            .Group(new BsonDocument { ["_id"] = "$patientId", ["transfers"] = new BsonDocument("$sum", 1) })
            .Group(new BsonDocument
            {
                ["_id"] = BsonNull.Value,
                ["unique"] = new BsonDocument("$sum", 1),
                ["moreThanOnce"] = new BsonDocument("$sum",
                    new BsonDocument("$cond", new BsonArray { new BsonDocument("$gt", new BsonArray { "$transfers", 1 }), 1, 0 })),
            })
            .FirstOrDefaultAsync(ct);
        return perPatient is null
            ? new ApiUsageCounts(0, 0)
            : new ApiUsageCounts(perPatient["unique"].ToInt64(), perPatient["moreThanOnce"].ToInt64());
    }

    public async Task<DateOnly?> EarliestDayAsync(string tenantId, InteroperabilityApi api, CancellationToken ct = default)
    {
        var first = await _collection
            .Find(Builders<BsonDocument>.Filter.Eq("tenantId", tenantId) & Builders<BsonDocument>.Filter.Eq("api", api.ToString()))
            .Sort(Builders<BsonDocument>.Sort.Ascending("day"))
            .Limit(1)
            .FirstOrDefaultAsync(ct);
        return first is null
            ? null
            : DateOnly.ParseExact(first["day"].AsString, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Days are stored as yyyy-MM-dd strings, which sort and compare as dates.</summary>
    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>In-process ledger for tests and local runs; not durable.</summary>
public sealed class InMemoryApiDisclosureLedger : IApiDisclosureLedger
{
    private readonly ConcurrentDictionary<ApiDisclosure, int> _records = new();

    public IReadOnlyDictionary<ApiDisclosure, int> Records => new Dictionary<ApiDisclosure, int>(_records);

    public Task RecordAsync(ApiDisclosure disclosure, CancellationToken ct = default)
    {
        _records.AddOrUpdate(disclosure, 1, (_, n) => n + 1);
        return Task.CompletedTask;
    }

    public Task<ApiUsageCounts> CountAsync(
        string tenantId, InteroperabilityApi api, DateOnly fromDay, DateOnly toDayExclusive, CancellationToken ct = default)
    {
        var transfers = _records.Keys
            .Where(d => d.TenantId == tenantId && d.Api == api && d.DayUtc >= fromDay && d.DayUtc < toDayExclusive)
            .GroupBy(d => d.PatientId)
            .Select(g => g.Count())
            .ToList();
        return Task.FromResult(new ApiUsageCounts(transfers.Count, transfers.Count(n => n > 1)));
    }

    public Task<DateOnly?> EarliestDayAsync(string tenantId, InteroperabilityApi api, CancellationToken ct = default)
    {
        var days = _records.Keys.Where(d => d.TenantId == tenantId && d.Api == api).Select(d => d.DayUtc).ToList();
        return Task.FromResult(days.Count == 0 ? (DateOnly?)null : days.Min());
    }
}
