using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using CoverageService.Models;
using CoverageService.Repositories;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CoverageService.Services;

/// <summary>Request-scoped session, flowing only within the transaction callback.</summary>
public sealed class PcpMongoSession
{
    private readonly AsyncLocal<IClientSessionHandle?> _current = new();
    public IClientSessionHandle? Current { get => _current.Value; set => _current.Value = value; }
    private readonly AsyncLocal<(string Tenant, string Member, string? Npi)?> _scope = new();
    internal (string Tenant, string Member, string? Npi)? WriteScope { get => _scope.Value; set => _scope.Value = value; }
}

/// <summary>
/// Serializes competing panel/member assignments through Mongo write conflicts,
/// with roster read, history and coverage changes in the same transaction.
/// There is no lease timeout and no nontransactional fallback.
/// </summary>
public sealed class TransactionalPcpAssignmentService(
    PcpAssignmentService inner, IMongoDatabase database, PcpMongoSession context) : IPcpAssignmentService
{
    private readonly PcpWriteTransaction _transaction = new(database, context);

    public Task<PcpAssignment?> GetCurrentAsync(string tenantId, string memberId, CancellationToken ct = default)
        => inner.GetCurrentAsync(tenantId, memberId, ct);
    public Task<IReadOnlyList<PcpAssignment>> GetHistoryAsync(string tenantId, string memberId, CancellationToken ct = default)
        => inner.GetHistoryAsync(tenantId, memberId, ct);

    internal static string FenceId(string tenant, string kind, string id) => PcpWriteTransaction.FenceId(tenant, kind, id);

    public async Task<PcpAssignmentResult> AssignAsync(string tenantId, string memberId, AssignPcpCommand cmd,
        CancellationToken ct = default)
    {
        if (cmd.EffectiveDate != default && cmd.EffectiveDate.Date != DateTime.UtcNow.Date)
            return PcpAssignmentResult.Fail(new PcpValidationError("PCP_EFFECTIVE_DATE_UNSUPPORTED", "effectiveDate",
                "Atomic PCP assignment supports today's UTC effective date; scheduled or historical changes require an interval workflow."));
        try
        {
            return await _transaction.ExecuteAsync(tenantId, memberId, cmd.ProviderNpi,
                () => inner.AssignAsync(tenantId, memberId, cmd, ct), ct);
        }
        catch (PcpRosterWriteException ex) { return PcpAssignmentResult.Fail(new PcpValidationError(ex.Code, "providerNpi", ex.Message)); }
    }
}

/// <summary>Shared boundary for assignment, coverage imports, reactivation and deletion.</summary>
public sealed class PcpWriteTransaction(IMongoDatabase database, PcpMongoSession context)
{
    private readonly IMongoCollection<BsonDocument> _fences = database.GetCollection<BsonDocument>("PcpAssignmentFences");

    internal static string FenceId(string tenant, string kind, string id) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { tenant, kind, id }))));

    public async Task<T> ExecuteAsync<T>(string tenant, string member, string? npi, Func<Task<T>> write,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(member))
            throw new ArgumentException("Tenant and member are required for a coverage write.");
        if (context.Current is not null)
        {
            var scope = context.WriteScope;
            if (scope is null || scope.Value.Tenant != tenant || scope.Value.Member != member
                || (!string.IsNullOrEmpty(npi) && scope.Value.Npi != npi))
                throw new InvalidOperationException("Coverage write does not match the active PCP fence scope.");
            return await write();
        }
        var keys = new List<string> { FenceId(tenant, "member", member) };
        if (!string.IsNullOrEmpty(npi)) keys.Add(FenceId(tenant, "panel", npi));
        keys.Sort(StringComparer.Ordinal);
        try
        {
            foreach (var key in keys)
            {
                try
                {
                    await _fences.UpdateOneAsync(new BsonDocument("_id", key),
                        new BsonDocument("$setOnInsert", new BsonDocument { { "TenantId", tenant }, { "Revision", 0L } }),
                        new UpdateOptions { IsUpsert = true }, ct);
                }
                catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
            }
            using var session = await database.Client.StartSessionAsync(cancellationToken: ct);
            return await session.WithTransactionAsync(async (active, token) =>
            {
                context.Current = active;
                context.WriteScope = (tenant, member, npi);
                try
                {
                    foreach (var key in keys)
                    {
                        var result = await _fences.UpdateOneAsync(active,
                            new BsonDocument { { "_id", key }, { "TenantId", tenant } },
                            new BsonDocument("$inc", new BsonDocument("Revision", 1L)), cancellationToken: token);
                        if (result.MatchedCount != 1) throw new PanelCountUnavailableException();
                    }
                    return await write();
                }
                finally { context.WriteScope = null; context.Current = null; }
            }, new TransactionOptions(readConcern: ReadConcern.Snapshot,
                readPreference: ReadPreference.Primary, writeConcern: WriteConcern.WMajority), ct);
        }
        catch (MongoException) { throw new PanelCountUnavailableException(); }
        catch (NotSupportedException) { throw new PanelCountUnavailableException(); }
    }
}

/// <summary>One member consumes one slot in the tenant/NPI current coverage roster.</summary>
public sealed class MongoPcpPanelCounter(ICoverageRepository coverage, PcpMongoSession context) : IPanelCounter
{
    public async Task<int> CurrentPanelCountAsync(string tenantId, string providerNpi, CancellationToken ct = default)
    {
        if (context.Current is null) throw new PanelCountUnavailableException();
        var roster = await coverage.GetByPcpNpiAsync(tenantId, providerNpi, CoverageStatus.Active);
        return roster.Where(c => c.InsuranceLineCode == InsuranceLineCodes.Health
            || string.IsNullOrEmpty(c.InsuranceLineCode)).Select(c => c.MemberId).Distinct(StringComparer.Ordinal).Count();
    }
}
