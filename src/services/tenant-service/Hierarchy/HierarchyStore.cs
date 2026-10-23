using MongoDB.Driver;

namespace TenantService.Hierarchy;

/// <summary>
/// Carrier and program records. Every query is scoped to one tenant, and a
/// write succeeds only against the revision it read, so two edits cannot
/// silently overwrite each other (as on <see cref="Models.Tenant"/>).
/// </summary>
public interface IHierarchyStore
{
    Task<IReadOnlyList<Carrier>> ListCarriersAsync(string tenantId, CancellationToken ct = default);
    Task<Carrier?> GetCarrierAsync(string tenantId, string id, CancellationToken ct = default);

    /// <summary>Inserts a carrier; false when the tenant already has one with that code.</summary>
    Task<bool> TryInsertCarrierAsync(Carrier carrier, CancellationToken ct = default);

    /// <summary>Replaces a carrier if its stored revision is still <paramref name="expectedRevision"/>.</summary>
    Task<bool> TryReplaceCarrierAsync(Carrier carrier, long expectedRevision, CancellationToken ct = default);

    Task<IReadOnlyList<PlanProgram>> ListProgramsAsync(string tenantId, string? carrierId, CancellationToken ct = default);
    Task<PlanProgram?> GetProgramAsync(string tenantId, string id, CancellationToken ct = default);
    Task<bool> TryInsertProgramAsync(PlanProgram program, CancellationToken ct = default);
    Task<bool> TryReplaceProgramAsync(PlanProgram program, long expectedRevision, CancellationToken ct = default);
}

public sealed class MongoHierarchyStore(IMongoDatabase database) : IHierarchyStore
{
    public const string CarriersCollection = "carriers";
    public const string ProgramsCollection = "programs";

    private readonly IMongoCollection<Carrier> _carriers = database.GetCollection<Carrier>(CarriersCollection);
    private readonly IMongoCollection<PlanProgram> _programs = database.GetCollection<PlanProgram>(ProgramsCollection);

    public async Task<IReadOnlyList<Carrier>> ListCarriersAsync(string tenantId, CancellationToken ct = default) =>
        await _carriers.Find(c => c.TenantId == tenantId).SortBy(c => c.Code).ToListAsync(ct);

    public async Task<Carrier?> GetCarrierAsync(string tenantId, string id, CancellationToken ct = default) =>
        await _carriers.Find(c => c.TenantId == tenantId && c.Id == id).FirstOrDefaultAsync(ct);

    public async Task<bool> TryInsertCarrierAsync(Carrier carrier, CancellationToken ct = default)
    {
        try
        {
            await _carriers.InsertOneAsync(carrier, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // The id is {tenant}|{code}, so a duplicate is that code taken —
            // including by a default carrier two requests created at once.
            return false;
        }
    }

    public async Task<bool> TryReplaceCarrierAsync(Carrier carrier, long expectedRevision, CancellationToken ct = default)
    {
        carrier.Revision = expectedRevision + 1;
        var result = await _carriers.ReplaceOneAsync(
            c => c.TenantId == carrier.TenantId && c.Id == carrier.Id && c.Revision == expectedRevision,
            carrier, cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    public async Task<IReadOnlyList<PlanProgram>> ListProgramsAsync(string tenantId, string? carrierId, CancellationToken ct = default)
    {
        var f = Builders<PlanProgram>.Filter.Eq(p => p.TenantId, tenantId);
        if (!string.IsNullOrWhiteSpace(carrierId)) f &= Builders<PlanProgram>.Filter.Eq(p => p.CarrierId, carrierId);
        return await _programs.Find(f).SortBy(p => p.CarrierId).ThenBy(p => p.Code).ToListAsync(ct);
    }

    public async Task<PlanProgram?> GetProgramAsync(string tenantId, string id, CancellationToken ct = default) =>
        await _programs.Find(p => p.TenantId == tenantId && p.Id == id).FirstOrDefaultAsync(ct);

    public async Task<bool> TryInsertProgramAsync(PlanProgram program, CancellationToken ct = default)
    {
        try
        {
            await _programs.InsertOneAsync(program, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<bool> TryReplaceProgramAsync(PlanProgram program, long expectedRevision, CancellationToken ct = default)
    {
        program.Revision = expectedRevision + 1;
        var result = await _programs.ReplaceOneAsync(
            p => p.TenantId == program.TenantId && p.Id == program.Id && p.Revision == expectedRevision,
            program, cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    /// <summary>Creates the indexes (idempotent).</summary>
    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        await _carriers.Indexes.CreateOneAsync(new CreateIndexModel<Carrier>(
            Builders<Carrier>.IndexKeys.Ascending(c => c.TenantId).Ascending(c => c.Code),
            new CreateIndexOptions { Name = "ix_tenant_code", Unique = true }), cancellationToken: ct);
        await _programs.Indexes.CreateOneAsync(new CreateIndexModel<PlanProgram>(
            Builders<PlanProgram>.IndexKeys.Ascending(p => p.TenantId).Ascending(p => p.CarrierId).Ascending(p => p.Code),
            new CreateIndexOptions { Name = "ix_tenant_carrier_code", Unique = true }), cancellationToken: ct);
    }
}

/// <summary>Creates the carrier and program indexes at startup.</summary>
public sealed class HierarchyIndexInitializer(IMongoDatabase database, ILogger<HierarchyIndexInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await new MongoHierarchyStore(database).EnsureIndexesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not create the carrier and program indexes");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
