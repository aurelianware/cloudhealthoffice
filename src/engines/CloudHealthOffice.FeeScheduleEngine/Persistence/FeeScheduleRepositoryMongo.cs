using CloudHealthOffice.FeeScheduleEngine.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace CloudHealthOffice.FeeScheduleEngine.Persistence;

/// <summary>
/// MongoDB implementations of IFeeScheduleRepository and IProviderContractRepository.
///
/// Recommended indexes (create once at tenant onboarding):
///   db.FeeSchedules.createIndex(
///     { tenantId: 1, defaultForPlanId: 1, effectiveDate: -1 },
///     { name: "idx_feeschedules_tenant_plan_date" }
///   )
///   db.ProviderContracts.createIndex(
///     { tenantId: 1, providerNpi: 1, planId: 1, effectiveDate: -1 },
///     { name: "idx_contracts_tenant_npi_plan" }
///   )
///   db.ProviderContracts.createIndex(
///     { tenantId: 1, groupTin: 1, planId: 1 },
///     { name: "idx_contracts_tenant_tin_plan" }
///   )
/// </summary>
public class FeeScheduleRepositoryMongo : IFeeScheduleRepository, IProviderContractRepository
{
    private readonly IMongoCollection<FeeSchedule> _scheduleCollection;
    private readonly IMongoCollection<ProviderContract> _contractCollection;
    private readonly ILogger<FeeScheduleRepositoryMongo> _logger;

    public FeeScheduleRepositoryMongo(
        IMongoDatabase database,
        IConfiguration configuration,
        ILogger<FeeScheduleRepositoryMongo> logger)
    {
        var scheduleCollection = configuration["FeeScheduleEngine:FeeScheduleCollection"] ?? "FeeSchedules";
        var contractCollection = configuration["FeeScheduleEngine:ProviderContractCollection"] ?? "ProviderContracts";

        _scheduleCollection = database.GetCollection<FeeSchedule>(scheduleCollection);
        _contractCollection = database.GetCollection<ProviderContract>(contractCollection);
        _logger = logger;
    }

    // ── IFeeScheduleRepository ──────────────────────────────────────────

    public async Task<FeeSchedule?> GetByIdAsync(
        string tenantId, string id, CancellationToken ct = default)
    {
        var filter = Builders<FeeSchedule>.Filter.And(
            Builders<FeeSchedule>.Filter.Eq(x => x.Id, id),
            Builders<FeeSchedule>.Filter.Eq(x => x.TenantId, tenantId));

        return await _scheduleCollection.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task<FeeSchedule?> GetDefaultForPlanAsync(
        string tenantId, string planId, DateTime serviceDate, CancellationToken ct = default)
    {
        var filter = Builders<FeeSchedule>.Filter.And(
            Builders<FeeSchedule>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<FeeSchedule>.Filter.Eq("defaultForPlanId", planId),
            Builders<FeeSchedule>.Filter.Lte(x => x.EffectiveDate, serviceDate),
            Builders<FeeSchedule>.Filter.Or(
                Builders<FeeSchedule>.Filter.Eq(x => x.TermDate, null),
                Builders<FeeSchedule>.Filter.Gt(x => x.TermDate, serviceDate)));

        var sort = Builders<FeeSchedule>.Sort.Descending(x => x.EffectiveDate);

        return await _scheduleCollection.Find(filter).Sort(sort).FirstOrDefaultAsync(ct);
    }

    public async Task<FeeScheduleLine?> GetLineAsync(
        string feeScheduleId, string procedureCode, string? modifier, CancellationToken ct = default)
    {
        var tenantId = ExtractTenantId(feeScheduleId);
        var schedule = await GetByIdAsync(tenantId, feeScheduleId, ct);
        if (schedule is null) return null;

        return ResolveLinePriority(schedule.Lines, procedureCode, modifier);
    }

    public async Task<FeeSchedule> UpsertAsync(FeeSchedule schedule, CancellationToken ct = default)
    {
        schedule.LastUpdatedDate = DateTime.UtcNow;

        var filter = Builders<FeeSchedule>.Filter.And(
            Builders<FeeSchedule>.Filter.Eq(x => x.Id, schedule.Id),
            Builders<FeeSchedule>.Filter.Eq(x => x.TenantId, schedule.TenantId));

        var options = new ReplaceOptions { IsUpsert = true };
        await _scheduleCollection.ReplaceOneAsync(filter, schedule, options, ct);

        _logger.LogDebug("Upserted fee schedule {Id}", schedule.Id);
        return schedule;
    }

    public async Task<IReadOnlyList<FeeSchedule>> ListAsync(
        string tenantId, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        var filter = Builders<FeeSchedule>.Filter.Eq(x => x.TenantId, tenantId);
        var sort = Builders<FeeSchedule>.Sort.Descending(x => x.EffectiveDate);

        var results = await _scheduleCollection
            .Find(filter)
            .Sort(sort)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        return results;
    }

    // ── IProviderContractRepository ────────────────────────────────────

    Task<ProviderContract?> IProviderContractRepository.GetContractAsync(
        string tenantId, string providerNpi, string planId, DateTime serviceDate,
        CancellationToken ct)
        => GetContractInternalAsync(tenantId, providerNpi, null, planId, serviceDate, ct);

    public async Task<ProviderContract?> GetContractInternalAsync(
        string tenantId, string providerNpi, string? groupTin, string planId,
        DateTime serviceDate, CancellationToken ct)
    {
        var contract = await QueryContractAsync(tenantId, providerNpi, planId, serviceDate, ct);
        if (contract is not null) return contract;

        if (!string.IsNullOrEmpty(groupTin))
            contract = await QueryContractAsync(tenantId, groupTin, planId, serviceDate, ct);

        return contract;
    }

    public async Task<ProviderContract> UpsertAsync(ProviderContract contract, CancellationToken ct = default)
    {
        contract.LastUpdatedDate = DateTime.UtcNow;

        var filter = Builders<ProviderContract>.Filter.And(
            Builders<ProviderContract>.Filter.Eq(x => x.Id, contract.Id),
            Builders<ProviderContract>.Filter.Eq(x => x.TenantId, contract.TenantId));

        var options = new ReplaceOptions { IsUpsert = true };
        await _contractCollection.ReplaceOneAsync(filter, contract, options, ct);

        _logger.LogDebug("Upserted provider contract {Id}", contract.Id);
        return contract;
    }

    public async Task<IReadOnlyList<ProviderContract>> ListByProviderAsync(
        string tenantId, string providerNpi, CancellationToken ct = default)
    {
        var filter = Builders<ProviderContract>.Filter.And(
            Builders<ProviderContract>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<ProviderContract>.Filter.Or(
                Builders<ProviderContract>.Filter.Eq(x => x.ProviderNpi, providerNpi),
                Builders<ProviderContract>.Filter.Eq(x => x.GroupTin, providerNpi)));

        var sort = Builders<ProviderContract>.Sort.Descending(x => x.EffectiveDate);

        var results = await _contractCollection
            .Find(filter)
            .Sort(sort)
            .ToListAsync(ct);

        return results;
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private async Task<ProviderContract?> QueryContractAsync(
        string tenantId, string npiOrTin, string planId, DateTime serviceDate,
        CancellationToken ct)
    {
        var filter = Builders<ProviderContract>.Filter.And(
            Builders<ProviderContract>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<ProviderContract>.Filter.Or(
                Builders<ProviderContract>.Filter.Eq(x => x.ProviderNpi, npiOrTin),
                Builders<ProviderContract>.Filter.Eq(x => x.GroupTin, npiOrTin)),
            Builders<ProviderContract>.Filter.Eq(x => x.PlanId, planId),
            Builders<ProviderContract>.Filter.Lte(x => x.EffectiveDate, serviceDate),
            Builders<ProviderContract>.Filter.Or(
                Builders<ProviderContract>.Filter.Eq(x => x.TermDate, null),
                Builders<ProviderContract>.Filter.Gt(x => x.TermDate, serviceDate)));

        var sort = Builders<ProviderContract>.Sort.Descending(x => x.EffectiveDate);

        return await _contractCollection.Find(filter).Sort(sort).FirstOrDefaultAsync(ct);
    }

    private static FeeScheduleLine? ResolveLinePriority(
        List<FeeScheduleLine> lines, string procedureCode, string? modifier)
    {
        FeeScheduleLine? baseRate = null;

        foreach (var line in lines)
        {
            if (!string.Equals(line.ProcedureCode, procedureCode, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(line.Modifier, modifier, StringComparison.OrdinalIgnoreCase))
                return line;

            if (line.Modifier is null)
                baseRate = line;
        }

        return baseRate;
    }

    private static string ExtractTenantId(string feeScheduleId)
    {
        var colonIndex = feeScheduleId.IndexOf(':');
        return colonIndex > 0 ? feeScheduleId[..colonIndex] : feeScheduleId;
    }
}
