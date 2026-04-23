using AppealsService.Models;
using AppealsService.Repositories;
using AppealsService.Services;
using MongoDB.Bson;
using MongoDB.Driver;

namespace AppealsService.HostedServices;

/// <summary>
/// One-shot migration: rewrite any pre-modernization appeal records that
/// carry a legacy terminal <c>Status</c> value (Approved, Denied,
/// PartialApproval, Withdrawn) into the new shape
/// <c>Status=Closed</c> + <see cref="Appeal.ClosureReasonCode"/>, and
/// append an <c>AppealStatusMigrated</c> audit event per record.
///
/// Idempotent — re-running finds zero eligible records and exits cleanly.
/// Bounded batches (100 per scan, configurable via
/// <c>AppealMigration:BatchSize</c>). Runs in <see cref="StartAsync"/> so
/// the subsequent <see cref="AppealIndexInitializer"/> sees a consistent
/// schema — the unique <c>ux_tenant_appeal_number</c> index would
/// otherwise fail to build if duplicate AppealNumbers exist.
///
/// Also scans for duplicate <c>AppealNumber</c> values under the same
/// tenant and logs them as warnings BEFORE the index initializer attempts
/// the unique-index build. Operators must resolve dupes manually and
/// re-deploy if the warning fires.
///
/// Cosmos deployments: this service is a no-op (the scan runs against
/// Mongo's BsonDocument collection). Cosmos-side migration ships as a
/// separate admin script if that deployment path is used in production.
/// The status-enum consolidation only affects records written by the
/// pre-addendum code path.
/// </summary>
public sealed class AppealStatusMigrationHostedService : IHostedService
{
    public const string LegacyStatusApproved = "Approved";
    public const string LegacyStatusDenied = "Denied";
    public const string LegacyStatusPartialApproval = "PartialApproval";
    public const string LegacyStatusWithdrawn = "Withdrawn";

    private static readonly string[] LegacyTerminalStatuses =
    {
        LegacyStatusApproved,
        LegacyStatusDenied,
        LegacyStatusPartialApproval,
        LegacyStatusWithdrawn
    };

    private readonly IMongoDatabase _db;
    private readonly IAppealEventSink _events;
    private readonly IAppealEventPublisher _publisher;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AppealStatusMigrationHostedService> _logger;

    public AppealStatusMigrationHostedService(
        IMongoDatabase db,
        IAppealEventSink events,
        IAppealEventPublisher publisher,
        IConfiguration configuration,
        ILogger<AppealStatusMigrationHostedService> logger)
    {
        _db = db;
        _events = events;
        _publisher = publisher;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var batchSize = _configuration.GetValue<int?>("AppealMigration:BatchSize") ?? 100;
        var raw = _db.GetCollection<BsonDocument>(AppealRepositoryMongo.AppealsCollectionName);

        await WarnDuplicateAppealNumbersAsync(raw, cancellationToken);

        var found = 0;
        var migrated = 0;
        var errors = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var filter = BuildLegacyStatusFilter();
            var batch = await raw.Find(filter).Limit(batchSize).ToListAsync(cancellationToken);
            if (batch.Count == 0) break;

            found += batch.Count;
            foreach (var doc in batch)
            {
                try
                {
                    await MigrateOneAsync(raw, doc, cancellationToken);
                    migrated++;
                }
                catch (Exception ex)
                {
                    errors++;
                    _logger.LogError(ex,
                        "Failed to migrate appeal {AppealId} for tenant {TenantId}",
                        LogSanitizer.SafeForLog(doc.GetValue("_id", BsonNull.Value).ToString()),
                        LogSanitizer.SafeForLog(doc.GetValue("tenantId", BsonNull.Value).ToString()));
                }
            }

            if (batch.Count < batchSize) break;
        }

        _logger.LogInformation(
            "AppealStatusMigration complete: found={Found} migrated={Migrated} errors={Errors}",
            found, migrated, errors);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static FilterDefinition<BsonDocument> BuildLegacyStatusFilter()
    {
        // Matches records whose status is any of the four legacy terminal
        // strings. Enum representation may be configured as integers in
        // pre-modernization deployments; include integer equivalents too.
        // Old enum was 0-indexed: Draft=0, Submitted=1, InReview=2,
        // PendingInfo=3, Approved=4, Denied=5, PartialApproval=6, Withdrawn=7.
        var stringMatches = LegacyTerminalStatuses
            .SelectMany(s => new BsonValue[] { s, s.ToLowerInvariant() })
            .ToArray();
        var intMatches = new BsonValue[] { 4, 5, 6, 7 };
        return Builders<BsonDocument>.Filter.In("status",
            stringMatches.Concat(intMatches));
    }

    private async Task MigrateOneAsync(IMongoCollection<BsonDocument> raw, BsonDocument doc, CancellationToken ct)
    {
        var tenantId = doc.GetValue("tenantId", BsonNull.Value).ToString() ?? string.Empty;
        var appealId = doc.GetValue("_id", BsonNull.Value).ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(appealId))
            throw new InvalidOperationException("Appeal document missing tenantId or _id.");

        var rawStatus = doc.GetValue("status", BsonNull.Value);
        var legacyLabel = rawStatus.BsonType == BsonType.Int32
            ? rawStatus.AsInt32 switch
              {
                  4 => LegacyStatusApproved,
                  5 => LegacyStatusDenied,
                  6 => LegacyStatusPartialApproval,
                  7 => LegacyStatusWithdrawn,
                  _ => rawStatus.ToString() ?? "Unknown"
              }
            : rawStatus.ToString() ?? "Unknown";

        var mappedReason = MapLegacyStatus(legacyLabel);
        var now = DateTime.UtcNow;

        var update = Builders<BsonDocument>.Update
            .Set("status", AppealStatus.Closed.ToString())
            .Set("closureReasonCode", mappedReason.ToString())
            .Set("closedAt", now)
            .Set("closedBy", "system:migration")
            .Set("updatedAt", now)
            .Set("updatedBy", "system:migration");

        var filter = Builders<BsonDocument>.Filter.Eq("_id", doc.GetValue("_id"));
        await raw.UpdateOneAsync(filter, update, cancellationToken: ct);

        // Build the audit event and the Kafka event from a typed snapshot of
        // the post-migration record. We populate a minimal Appeal instance
        // for envelope/headers — the payload only carries legacy/mapped data.
        var snapshot = new Appeal
        {
            TenantId = tenantId,
            Id = appealId,
            AppealNumber = doc.GetValue("appealNumber", BsonNull.Value).ToString() ?? string.Empty,
            ClaimId = doc.GetValue("claimId", BsonNull.Value).ToString() ?? string.Empty,
            ClaimNumber = doc.GetValue("claimNumber", BsonNull.Value).ToString() ?? string.Empty,
            MemberId = doc.GetValue("memberId", BsonNull.Value).ToString() ?? string.Empty,
            PatientName = string.Empty,
            ProviderNPI = doc.GetValue("providerNPI", BsonNull.Value).ToString() ?? string.Empty,
            AppealReason = string.Empty,
            LineOfBusiness = LineOfBusiness.Commercial,
            Status = AppealStatus.Closed,
            ClosureReasonCode = mappedReason
        };

        var auditEvent = new AppealEvent
        {
            TenantId = tenantId,
            AppealId = appealId,
            EventId = Guid.NewGuid().ToString(),
            EventType = AppealEventType.AppealStatusMigrated,
            FromStatus = null,
            ToStatus = AppealStatus.Closed,
            ActorId = "system:migration",
            OccurredAt = now,
            Payload = new System.Text.Json.Nodes.JsonObject
            {
                ["legacyStatus"] = legacyLabel,
                ["mappedReasonCode"] = mappedReason.ToString()
            }
        };

        await _events.AppendAsync(auditEvent, ct);
        await _publisher.PublishStatusMigratedAsync(
            snapshot, legacyLabel, mappedReason, "system:migration", correlationId: null, ct);

        _logger.LogInformation(
            "Migrated appeal {AppealId} tenant {TenantId} from legacy status {Legacy} -> Closed + {Reason}",
            LogSanitizer.SafeForLog(appealId), LogSanitizer.SafeForLog(tenantId),
            LogSanitizer.SafeForLog(legacyLabel), LogSanitizer.SafeForLog(mappedReason.ToString()));
    }

    /// <summary>
    /// Maps a legacy-status string to the new closure reason code.
    /// Internal so <c>AppealStatusMigrationTests</c> can assert the mapping.
    /// </summary>
    internal static AppealClosureReasonCode MapLegacyStatus(string legacy) => legacy switch
    {
        LegacyStatusApproved => AppealClosureReasonCode.Approved,
        LegacyStatusDenied => AppealClosureReasonCode.Denied,
        LegacyStatusPartialApproval => AppealClosureReasonCode.PartialApproval,
        LegacyStatusWithdrawn => AppealClosureReasonCode.Withdrawn,
        _ => AppealClosureReasonCode.Other
    };

    private async Task WarnDuplicateAppealNumbersAsync(
        IMongoCollection<BsonDocument> raw, CancellationToken ct)
    {
        // Run a light aggregation to surface any (tenantId, appealNumber)
        // pairs that appear more than once. If any exist, the subsequent
        // unique-index build WILL FAIL and block service startup —
        // operators must resolve dupes manually and re-deploy.
        try
        {
            var pipeline = new[]
            {
                new BsonDocument("$group", new BsonDocument
                {
                    { "_id", new BsonDocument { { "tenantId", "$tenantId" }, { "appealNumber", "$appealNumber" } } },
                    { "count", new BsonDocument("$sum", 1) }
                }),
                new BsonDocument("$match", new BsonDocument("count", new BsonDocument("$gt", 1)))
            };

            using var cursor = await raw.AggregateAsync<BsonDocument>(pipeline, cancellationToken: ct);
            while (await cursor.MoveNextAsync(ct))
            {
                foreach (var dup in cursor.Current)
                {
                    var key = dup.GetValue("_id", BsonNull.Value);
                    var count = dup.GetValue("count", BsonNull.Value);
                    _logger.LogWarning(
                        "Duplicate AppealNumber detected pre-index-build: {Key} count={Count}. " +
                        "ux_tenant_appeal_number unique index creation WILL FAIL until manually resolved.",
                        LogSanitizer.SafeForLog(key.ToString()),
                        LogSanitizer.SafeForLog(count.ToString()));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Duplicate-AppealNumber pre-scan failed — continuing. The index build may still fail if duplicates exist.");
        }
    }
}
