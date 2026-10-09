using AppealsService.Models;
using AppealsService.Repositories;
using AppealsService.Services;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace AppealsService.HostedServices;

/// <summary>
/// One-shot migration: rewrite any pre-modernization appeal records that
/// carry a legacy terminal <c>Status</c> value (Approved, Denied,
/// PartialApproval, Withdrawn) into the new shape
/// <c>Status=Closed</c> + <see cref="Appeal.ClosureReasonCode"/>, and
/// append an <c>AppealStatusMigrated</c> audit event per record.
///
/// Field names: the <see cref="Appeal"/> BSON class map keeps the .NET
/// (PascalCase) property names — appeals-service registers no camelCase
/// convention — so the migration reads and writes the class-map element
/// names (<see cref="Fields"/>). It also matches the camelCase spelling
/// (<c>status</c>, <c>tenantId</c>, ...) in case an older writer produced
/// it.
///
/// Idempotent — re-running finds zero eligible records and exits cleanly.
/// Bounded batches (100 per scan, configurable via
/// <c>AppealMigration:BatchSize</c>). Waits for the appeal event
/// publisher to start before migrating anything (see
/// <see cref="StartAsync"/>), so each row's <c>AppealStatusMigrated</c>
/// event is produced rather than skipped; if the publisher fails to
/// start, nothing is migrated and the next start retries.
///
/// Also scans for duplicate <c>AppealNumber</c> values under the same
/// tenant and logs them as warnings BEFORE the index initializer attempts
/// the unique-index build. Operators must resolve dupes manually and
/// re-deploy if the warning fires.
///
/// Cosmos deployments: this service is not registered (Program.cs adds it
/// only for the Mongo provider). Cosmos-side migration ships as a
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

    private const string MigrationActor = "system:migration";

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
    private readonly IAppealEventPublisherReadiness _publisherReadiness;
    private readonly CancellationTokenSource _stopping = new();
    private readonly IConfiguration _configuration;
    private readonly ILogger<AppealStatusMigrationHostedService> _logger;

    public AppealStatusMigrationHostedService(
        IMongoDatabase db,
        IAppealEventSink events,
        IAppealEventPublisher publisher,
        IAppealEventPublisherReadiness publisherReadiness,
        IConfiguration configuration,
        ILogger<AppealStatusMigrationHostedService> logger)
    {
        _db = db;
        _events = events;
        _publisher = publisher;
        _publisherReadiness = publisherReadiness;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// The migration run when it had to wait for the publisher; <c>null</c>
    /// when it ran inside <see cref="StartAsync"/>. Exposed for tests.
    /// </summary>
    internal Task? DeferredRun { get; private set; }

    /// <summary>
    /// Runs the duplicate / ambiguity scans, then the migration — but only
    /// once <see cref="IAppealEventPublisherReadiness.Started"/> completes:
    /// before the publisher's own <c>StartAsync</c> a publish is silently
    /// skipped, and each migrated row's <c>AppealStatusMigrated</c> event
    /// would be lost. Already started → migrate here. Not yet (registered
    /// after this service) → migrate in the background once it starts, so
    /// sequential hosted-service startup cannot deadlock.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var raw = _db.GetCollection<BsonDocument>(AppealRepositoryMongo.AppealsCollectionName);

        await WarnDuplicateAppealNumbersAsync(raw, cancellationToken);
        await WarnAmbiguousIntegerStatusesAsync(raw, cancellationToken);

        if (_publisherReadiness.Started.IsCompleted)
        {
            await MigrateWhenPublisherReadyAsync(background: false, cancellationToken);
            return;
        }

        _logger.LogInformation("AppealStatusMigration waiting for the appeal event publisher to start.");
        DeferredRun = Task.Run(() => MigrateWhenPublisherReadyAsync(background: true, _stopping.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        if (DeferredRun is { } run)
            await Task.WhenAny(run, Task.Delay(Timeout.Infinite, cancellationToken));
    }

    /// <summary>
    /// Waits (bounded by <c>AppealMigration:PublisherReadyTimeoutSeconds</c>,
    /// default 60) for the publisher. Unavailable or never started → the
    /// run is skipped with an error and every legacy row stays as it is, so
    /// the next start migrates it and emits its event. Disabled by
    /// configuration (no <c>Kafka:BootstrapServers</c>) → the service
    /// publishes no events at all, so migrate with a warning; the audit row
    /// is still written.
    /// </summary>
    private async Task MigrateWhenPublisherReadyAsync(bool background, CancellationToken ct)
    {
        try
        {
            var timeout = TimeSpan.FromSeconds(
                _configuration.GetValue<double?>("AppealMigration:PublisherReadyTimeoutSeconds") ?? 60);
            var started = _publisherReadiness.Started;
            if (await Task.WhenAny(started, Task.Delay(timeout, ct)) != started)
            {
                if (ct.IsCancellationRequested) return;
                _logger.LogError(
                    "AppealStatusMigration skipped: the appeal event publisher did not start within {Timeout}. " +
                    "Legacy rows are left unmigrated and will be migrated (and published) on the next start.",
                    timeout);
                return;
            }

            switch (await started)
            {
                case AppealEventPublisherState.Unavailable:
                    _logger.LogError(
                        "AppealStatusMigration skipped: the appeal event publisher failed to start. " +
                        "Legacy rows are left unmigrated and will be migrated (and published) on the next start.");
                    return;
                case AppealEventPublisherState.Disabled:
                    _logger.LogWarning(
                        "AppealStatusMigration running with Kafka publishing disabled by configuration: " +
                        "AppealStatusMigrated events are not published (audit rows are still written).");
                    break;
            }

            await RunMigrationAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogWarning("AppealStatusMigration cancelled; remaining legacy rows migrate on the next start.");
        }
        catch (Exception ex) when (background)
        {
            // Background run: nothing awaits it, so log rather than lose the failure.
            _logger.LogError(ex, "AppealStatusMigration failed; remaining legacy rows migrate on the next start.");
        }
    }

    private async Task RunMigrationAsync(CancellationToken cancellationToken)
    {
        var batchSize = _configuration.GetValue<int?>("AppealMigration:BatchSize") ?? 100;
        var raw = _db.GetCollection<BsonDocument>(AppealRepositoryMongo.AppealsCollectionName);
        var typed = _db.GetCollection<Appeal>(AppealRepositoryMongo.AppealsCollectionName);

        var found = 0;
        var migrated = 0;
        var errors = 0;
        var failedIds = new BsonArray();

        while (!cancellationToken.IsCancellationRequested)
        {
            // A row that failed stays legacy; exclude it so the next batch
            // makes progress instead of re-reading the same failures.
            var filter = BuildLegacyStatusFilter();
            if (failedIds.Count > 0)
                filter = new BsonDocument("$and", new BsonArray
                {
                    filter,
                    new BsonDocument("_id", new BsonDocument("$nin", failedIds))
                });
            var batch = await raw.Find(filter).Limit(batchSize).ToListAsync(cancellationToken);
            if (batch.Count == 0) break;

            found += batch.Count;
            foreach (var doc in batch)
            {
                try
                {
                    if (await MigrateOneAsync(typed, doc, cancellationToken)) migrated++;
                }
                catch (Exception ex)
                {
                    errors++;
                    failedIds.Add(doc.GetValue("_id", BsonNull.Value));
                    _logger.LogError(ex,
                        "Failed to migrate appeal {AppealId} for tenant {TenantId}",
                        LogSanitizer.SafeForLog(doc.GetValue("_id", BsonNull.Value).ToString()),
                        LogSanitizer.SafeForLog(AsString(GetEither(doc, Fields.TenantId))));
                }
            }

            if (batch.Count < batchSize) break;
        }

        _logger.LogInformation(
            "AppealStatusMigration complete: found={Found} migrated={Migrated} errors={Errors}",
            found, migrated, errors);
    }


    /// <summary>
    /// A stored field's element name under the <see cref="Appeal"/> class
    /// map (<see cref="Current"/>, what the typed repository writes and
    /// queries) and its camelCase spelling (<see cref="Legacy"/>).
    /// </summary>
    internal readonly record struct FieldNames(string Current, string Legacy);

    /// <summary>Element names of the fields the migration reads and writes, derived from the class map.</summary>
    internal static class Fields
    {
        public static readonly FieldNames Status = Of(nameof(Appeal.Status));
        public static readonly FieldNames ClosureReasonCode = Of(nameof(Appeal.ClosureReasonCode));
        public static readonly FieldNames ClosedAt = Of(nameof(Appeal.ClosedAt));
        public static readonly FieldNames ClosedBy = Of(nameof(Appeal.ClosedBy));
        public static readonly FieldNames UpdatedAt = Of(nameof(Appeal.UpdatedAt));
        public static readonly FieldNames UpdatedBy = Of(nameof(Appeal.UpdatedBy));
        public static readonly FieldNames TenantId = Of(nameof(Appeal.TenantId));
        public static readonly FieldNames AppealNumber = Of(nameof(Appeal.AppealNumber));
        public static readonly FieldNames ClaimId = Of(nameof(Appeal.ClaimId));
        public static readonly FieldNames ClaimNumber = Of(nameof(Appeal.ClaimNumber));
        public static readonly FieldNames MemberId = Of(nameof(Appeal.MemberId));
        public static readonly FieldNames ProviderNPI = Of(nameof(Appeal.ProviderNPI));

        private static FieldNames Of(string memberName)
        {
            var current = BsonClassMap.LookupClassMap(typeof(Appeal)).GetMemberMap(memberName).ElementName;
            return new FieldNames(current, char.ToLowerInvariant(memberName[0]) + memberName[1..]);
        }
    }

    /// <summary>
    /// Matches records whose status — under the class-map element name or
    /// its camelCase spelling — is one of the four legacy terminal values,
    /// as a string (either case) or as an integer of the old 0-indexed enum
    /// (Draft=0, Submitted=1, InReview=2, PendingInfo=3, Approved=4,
    /// Denied=5, PartialApproval=6, Withdrawn=7).
    ///
    /// Integers 4 and 5 are matched only under the camelCase spelling. The
    /// current 1-indexed enum stores PendingInfo=4 and Closed=5 under the
    /// class-map name whenever the string-enum convention is not in force,
    /// so there they cannot be told apart from Approved / Denied;
    /// <see cref="WarnAmbiguousIntegerStatusesAsync"/> reports them instead.
    /// The current enum never produces 6 or 7.
    /// </summary>
    internal static BsonDocument BuildLegacyStatusFilter()
    {
        var strings = LegacyTerminalStatuses
            .SelectMany(s => new BsonValue[] { s, s.ToLowerInvariant() })
            .ToList();
        var current = new BsonArray(strings.Concat(new BsonValue[] { 6, 7 }));
        var legacy = new BsonArray(strings.Concat(new BsonValue[] { 4, 5, 6, 7 }));

        // A camelCase convention would make both spellings the same field;
        // then it is the current writer's field and 4/5 stay ambiguous.
        if (Fields.Status.Current == Fields.Status.Legacy)
            return new BsonDocument(Fields.Status.Current, new BsonDocument("$in", current));

        return new BsonDocument("$or", new BsonArray
        {
            new BsonDocument(Fields.Status.Current, new BsonDocument("$in", current)),
            new BsonDocument(Fields.Status.Legacy, new BsonDocument("$in", legacy))
        });
    }

    /// <summary>Returns false when the row was no longer legacy at write time (another replica migrated it).</summary>
    private async Task<bool> MigrateOneAsync(IMongoCollection<Appeal> typed, BsonDocument doc, CancellationToken ct)
    {
        var tenantId = AsString(GetEither(doc, Fields.TenantId));
        var appealId = AsString(doc.GetValue("_id", BsonNull.Value));
        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(appealId))
            throw new InvalidOperationException("Appeal document missing tenantId or _id.");

        var rawStatus = IsLegacyStatus(doc.GetValue(Fields.Status.Current, BsonNull.Value), currentSpelling: true)
            ? doc.GetValue(Fields.Status.Current)
            : doc.GetValue(Fields.Status.Legacy, BsonNull.Value);
        var legacyLabel = rawStatus.IsInt32
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

        // Typed update: the class map supplies the element names and the
        // enum representation, so the row reads back as Closed through the
        // repository. A row carrying the camelCase status also has its
        // camelCase fields rewritten, so it stops matching the legacy filter
        // (idempotency) and reads as closed under either spelling.
        var update = Builders<Appeal>.Update
            .Set(a => a.Status, AppealStatus.Closed)
            .Set(a => a.ClosureReasonCode, mappedReason)
            .Set(a => a.ClosedAt, now)
            .Set(a => a.ClosedBy, MigrationActor)
            .Set(a => a.UpdatedAt, now)
            .Set(a => a.UpdatedBy, MigrationActor);
        if (Fields.Status.Legacy != Fields.Status.Current && doc.Contains(Fields.Status.Legacy))
        {
            update = update
                .Set(Fields.Status.Legacy, AppealStatus.Closed.ToString())
                .Set(Fields.ClosureReasonCode.Legacy, mappedReason.ToString())
                .Set(Fields.ClosedAt.Legacy, now)
                .Set(Fields.ClosedBy.Legacy, MigrationActor)
                .Set(Fields.UpdatedAt.Legacy, now)
                .Set(Fields.UpdatedBy.Legacy, MigrationActor);
        }

        // Re-check the legacy status in the write so two replicas starting
        // together do not both migrate (and audit) the same row.
        var filter = new BsonDocumentFilterDefinition<Appeal>(new BsonDocument("$and", new BsonArray
        {
            new BsonDocument("_id", doc.GetValue("_id")),
            BuildLegacyStatusFilter()
        }));
        var result = await typed.UpdateOneAsync(filter, update, cancellationToken: ct);
        if (result.MatchedCount == 0) return false;

        // Build the audit event and the Kafka event from a typed snapshot of
        // the post-migration record. We populate a minimal Appeal instance
        // for envelope/headers — the payload only carries legacy/mapped data.
        var snapshot = new Appeal
        {
            TenantId = tenantId,
            Id = appealId,
            AppealNumber = AsString(GetEither(doc, Fields.AppealNumber)),
            ClaimId = AsString(GetEither(doc, Fields.ClaimId)),
            ClaimNumber = AsString(GetEither(doc, Fields.ClaimNumber)),
            MemberId = AsString(GetEither(doc, Fields.MemberId)),
            PatientName = string.Empty,
            ProviderNPI = AsString(GetEither(doc, Fields.ProviderNPI)),
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
            ActorId = MigrationActor,
            OccurredAt = now,
            Payload = new System.Text.Json.Nodes.JsonObject
            {
                ["legacyStatus"] = legacyLabel,
                ["mappedReasonCode"] = mappedReason.ToString()
            }
        };

        await _events.AppendAsync(auditEvent, ct);
        await _publisher.PublishStatusMigratedAsync(
            snapshot, legacyLabel, mappedReason, MigrationActor, correlationId: null, ct);

        _logger.LogInformation(
            "Migrated appeal {AppealId} tenant {TenantId} from legacy status {Legacy} -> Closed + {Reason}",
            LogSanitizer.SafeForLog(appealId), LogSanitizer.SafeForLog(tenantId),
            LogSanitizer.SafeForLog(legacyLabel), LogSanitizer.SafeForLog(mappedReason.ToString()));
        return true;
    }

    private static bool IsLegacyStatus(BsonValue value, bool currentSpelling) =>
        value.IsInt32
            ? value.AsInt32 is 6 or 7 || (!currentSpelling && value.AsInt32 is 4 or 5)
            : value.IsString && LegacyTerminalStatuses.Contains(value.AsString, StringComparer.OrdinalIgnoreCase);

    /// <summary>The class-map field when present, else its camelCase spelling.</summary>
    private static BsonValue GetEither(BsonDocument doc, FieldNames field) =>
        doc.TryGetValue(field.Current, out var value) && !value.IsBsonNull
            ? value
            : doc.GetValue(field.Legacy, BsonNull.Value);

    private static string AsString(BsonValue value) =>
        value.IsBsonNull ? string.Empty : value.IsString ? value.AsString : value.ToString() ?? string.Empty;

    /// <summary>
    /// Maps a legacy-status string to the new closure reason code.
    /// Case-insensitive: the filter also matches lower-case legacy values.
    /// Internal so <c>AppealStatusMigrationTests</c> can assert the mapping.
    /// </summary>
    internal static AppealClosureReasonCode MapLegacyStatus(string legacy) => legacy.ToLowerInvariant() switch
    {
        "approved" => AppealClosureReasonCode.Approved,
        "denied" => AppealClosureReasonCode.Denied,
        "partialapproval" => AppealClosureReasonCode.PartialApproval,
        "withdrawn" => AppealClosureReasonCode.Withdrawn,
        _ => AppealClosureReasonCode.Other
    };

    /// <summary>
    /// Integer statuses 4 and 5 under the class-map name are either legacy
    /// Approved / Denied (old 0-indexed enum) or current PendingInfo /
    /// Closed (1-indexed enum written without the string convention). The
    /// migration leaves them alone; this reports how many exist so an
    /// operator can resolve them.
    /// </summary>
    private async Task WarnAmbiguousIntegerStatusesAsync(IMongoCollection<BsonDocument> raw, CancellationToken ct)
    {
        try
        {
            var count = await raw.CountDocumentsAsync(
                new BsonDocument(Fields.Status.Current, new BsonDocument("$in", new BsonArray { 4, 5 })),
                cancellationToken: ct);
            if (count > 0)
            {
                _logger.LogWarning(
                    "{Count} appeal(s) store integer {Field} 4 or 5: legacy Approved/Denied under the old enum, " +
                    "PendingInfo/Closed under the current one. They were not migrated; resolve manually.",
                    count, Fields.Status.Current);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ambiguous integer-status scan failed — continuing.");
        }
    }

    private async Task WarnDuplicateAppealNumbersAsync(
        IMongoCollection<BsonDocument> raw, CancellationToken ct)
    {
        // Run a light aggregation to surface any (tenantId, appealNumber)
        // pairs that appear more than once. If any exist, the subsequent
        // unique-index build WILL FAIL and block service startup —
        // operators must resolve dupes manually and re-deploy. Groups on
        // the class-map names: those are what the unique index is built
        // over (a row missing them indexes as null).
        try
        {
            var pipeline = new[]
            {
                new BsonDocument("$group", new BsonDocument
                {
                    { "_id", new BsonDocument
                        {
                            { "tenantId", "$" + Fields.TenantId.Current },
                            { "appealNumber", "$" + Fields.AppealNumber.Current }
                        } },
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
