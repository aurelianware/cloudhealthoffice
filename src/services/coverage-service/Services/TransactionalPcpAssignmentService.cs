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
}

/// <summary>
/// Serializes competing panel/member assignments through Mongo write conflicts,
/// with roster read, history and coverage changes in the same transaction.
/// There is no lease timeout and no nontransactional fallback.
/// </summary>
public sealed class TransactionalPcpAssignmentService(
    PcpAssignmentService inner, IMongoDatabase database, PcpMongoSession context) : IPcpAssignmentService
{
    private readonly IMongoCollection<BsonDocument> _fences = database.GetCollection<BsonDocument>("PcpAssignmentFences");

    public Task<PcpAssignment?> GetCurrentAsync(string tenantId, string memberId, CancellationToken ct = default)
        => inner.GetCurrentAsync(tenantId, memberId, ct);
    public Task<IReadOnlyList<PcpAssignment>> GetHistoryAsync(string tenantId, string memberId, CancellationToken ct = default)
        => inner.GetHistoryAsync(tenantId, memberId, ct);

    internal static string FenceId(string tenant, string kind, string id) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { tenant, kind, id }))));

    public async Task<PcpAssignmentResult> AssignAsync(string tenantId, string memberId, AssignPcpCommand cmd,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("Tenant is required.", nameof(tenantId));
        // The current roster is not an interval reservation system. Refuse
        // historical/scheduled mutations rather than claim a temporal hard cap.
        if (cmd.EffectiveDate != default && cmd.EffectiveDate.Date != DateTime.UtcNow.Date)
            return PcpAssignmentResult.Fail(new PcpValidationError("PCP_EFFECTIVE_DATE_UNSUPPORTED", "effectiveDate",
                "Atomic PCP assignment supports today's UTC effective date; scheduled or historical changes require reconciliation."));
        var keys = new[] { FenceId(tenantId, "member", memberId), FenceId(tenantId, "panel", cmd.ProviderNpi) }
            .OrderBy(k => k, StringComparer.Ordinal).ToArray();
        try
        {
            // Create outside the transaction, so first-use races cannot turn a
            // duplicate upsert into a nonretryable transaction failure.
            foreach (var key in keys)
            {
                try
                {
                    await _fences.UpdateOneAsync(new BsonDocument("_id", key),
                        new BsonDocument("$setOnInsert", new BsonDocument { { "TenantId", tenantId }, { "Revision", 0L } }),
                        new UpdateOptions { IsUpsert = true }, ct);
                }
                catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
            }
            using var session = await database.Client.StartSessionAsync(cancellationToken: ct);
            return await session.WithTransactionAsync(async (active, token) =>
            {
                context.Current = active;
                try
                {
                    foreach (var key in keys)
                    {
                        var result = await _fences.UpdateOneAsync(active,
                            new BsonDocument { { "_id", key }, { "TenantId", tenantId } },
                            new BsonDocument("$inc", new BsonDocument("Revision", 1L)), cancellationToken: token);
                        if (result.MatchedCount != 1) throw new PanelCountUnavailableException();
                    }
                    return await inner.AssignAsync(tenantId, memberId, cmd, token);
                }
                finally { context.Current = null; }
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
