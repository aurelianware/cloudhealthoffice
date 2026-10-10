using System.Security.Cryptography;
using System.Text;
using AccumulatorService.Models;
using AccumulatorService.Repositories;
using CloudHealthOffice.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace AccumulatorReversalKindBackfill;

/// <summary>
/// The accumulator-service database the tool works on (the base database: the Kafka consumer
/// that writes every reversal row and marker has no request tenant, so it always writes there),
/// plus, optionally, claims-service's database for evidence.
/// </summary>
public sealed class Target
{
    public Target(IMongoClient client, string databaseName, ClaimsEvidence? claims)
    {
        Client = client;
        DatabaseName = databaseName;
        Database = client.GetDatabase(databaseName);
        Claims = claims;
    }

    public IMongoClient Client { get; }
    public string DatabaseName { get; }
    public IMongoDatabase Database { get; }
    public ClaimsEvidence? Claims { get; }

    /// <summary>Servers of the connection string without credentials, sorted, joined by <c>;</c>.</summary>
    public string Host => string.Join(";", Client.Settings.Servers
        .Select(s => $"{s.Host}:{s.Port}".ToLowerInvariant())
        .OrderBy(s => s, StringComparer.Ordinal));

    public string Describe() =>
        $"Target: host {Host}, database {DatabaseName}; claims-service evidence: " +
        (Claims is null ? "not configured (own reversals and voids stay AMBIGUOUS)" : Claims.Describe());

    /// <exception cref="EnvironmentMismatchException">No name, or not the configured one.</exception>
    public void Confirm(string? confirmDatabase)
    {
        if (string.IsNullOrWhiteSpace(confirmDatabase))
            throw new EnvironmentMismatchException(
                $"--confirm-database is required: the configured database is '{DatabaseName}' on {Host}. Pass --confirm-database {DatabaseName} if that is the environment you mean.");
        if (!string.Equals(confirmDatabase, DatabaseName, StringComparison.Ordinal))
            throw new EnvironmentMismatchException(
                $"--confirm-database '{confirmDatabase}' is not the configured database '{DatabaseName}' on {Host}. Nothing was read or changed.");
    }

    public IMongoCollection<AccumulatorEvent> Events =>
        Database.GetCollection<AccumulatorEvent>(AccumulatorMongoIndexes.EventsCollection);

    public IMongoCollection<ProcessedClaim> Markers =>
        Database.GetCollection<ProcessedClaim>(AccumulatorMongoIndexes.ProcessedClaimsCollection);

    public IMongoCollection<AuditRecord> Audit => Database.GetCollection<AuditRecord>(Backfill.AuditCollection);

    public IMongoCollection<RunRecord> Runs => Database.GetCollection<RunRecord>(Backfill.RunsCollection);
}

/// <summary>What claims-service knows about a claim (read-only).</summary>
public sealed record ClaimFacts(int? Status, string? FrequencyCode, string? PredecessorVersionId)
{
    public const int Denied = 6;
    public const int Voided = 8;
}

/// <summary>
/// claims-service's <c>Claims</c> collection, read-only: the base database, or
/// <c>{base}_{tenant}</c> with tenant scoping (claims are written through tenant requests).
/// </summary>
public sealed class ClaimsEvidence
{
    private readonly IMongoClient _client;
    private readonly MongoDbConnectionFactory _factory;
    private readonly string _baseDatabase;
    private readonly bool _scoping;

    public ClaimsEvidence(IMongoClient client, string baseDatabase, bool useTenantScoping)
    {
        _client = client;
        _baseDatabase = baseDatabase;
        _scoping = useTenantScoping;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MongoDb:DatabaseName"] = baseDatabase,
            ["MongoDb:UseTenantScoping"] = useTenantScoping ? "true" : "false",
        }).Build();
        _factory = new MongoDbConnectionFactory(client, new HttpContextAccessor(), config);
    }

    public string Describe() =>
        $"database {_baseDatabase}{(_scoping ? "_<tenant>" : string.Empty)} on " +
        string.Join(";", _client.Settings.Servers.Select(s => $"{s.Host}:{s.Port}".ToLowerInvariant()).OrderBy(s => s, StringComparer.Ordinal));

    public async Task<ClaimFacts?> GetAsync(string tenantId, string claimId, CancellationToken ct = default)
    {
        var claims = _factory.GetDatabase(tenantId).GetCollection<BsonDocument>("Claims");
        var doc = await claims.Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("_id", claimId),
                Builders<BsonDocument>.Filter.Eq("TenantId", tenantId)))
            .FirstOrDefaultAsync(ct);
        if (doc is null) return null;
        int? status = doc.TryGetValue("Status", out var s)
            ? s.BsonType switch
            {
                BsonType.Int32 => s.AsInt32,
                BsonType.Int64 => (int)s.AsInt64,
                BsonType.String => s.AsString switch { "Denied" => ClaimFacts.Denied, "Voided" => ClaimFacts.Voided, _ => null },
                _ => null,
            }
            : null;
        string? Str(string name) => doc.TryGetValue(name, out var v) && v.IsString ? v.AsString : null;
        return new ClaimFacts(status, Str("ClaimFrequencyCode"), Str("PredecessorVersionId"));
    }
}

/// <summary>Which legacy record a plan row is.</summary>
public static class ItemTypes
{
    /// <summary>A <c>ClaimReversed</c> event row.</summary>
    public const string ClaimReversedRow = "ClaimReversedRow";

    /// <summary>A zero-delta <c>ClaimTombstoned</c> event row.</summary>
    public const string ClaimTombstonedRow = "ClaimTombstonedRow";

    /// <summary>The <c>{original}:reversal</c> processed-claim marker.</summary>
    public const string ReversalMarker = "ReversalMarker";

    /// <summary>The original's own processed-claim marker, tombstoned (<c>ReversedBeforeApply</c>).</summary>
    public const string OriginalTombstoneMarker = "OriginalTombstoneMarker";
}

/// <summary>A legacy record without a <c>ReversalKind</c>.</summary>
public sealed record LegacyItem(
    string TenantId, string ItemType, string ItemId, string OriginalClaimId, string? SourceClaimId,
    string? AggregateId, string? Outcome);

/// <summary>The proposed kind for a group of items (one original claim), and why.</summary>
public sealed record Classification(string Kind, string Evidence)
{
    public const string Ambiguous = "AMBIGUOUS";
    public bool IsAmbiguous => Kind == Ambiguous;
}

/// <summary>One plan row: an item and its proposed kind.</summary>
public sealed record PlanRow(LegacyItem Item, Classification Classification);

/// <summary>Per-row outcome of an apply run.</summary>
public enum RowOutcome
{
    WouldSet,
    Set,
    AlreadySet,
    Ambiguous,
    Refused,
    Failed,
}

public sealed record RowResult(PlanRow Row, RowOutcome Outcome, string Detail);

public sealed class ApplyOptions
{
    public required string CsvPath { get; init; }
    public bool Execute { get; init; }
    public string? ExpectedSha256 { get; init; }
    public string Operator { get; init; } = string.Empty;
    public string? ConfirmDatabase { get; init; }
    public IReadOnlyCollection<string> Tenants { get; init; } = [];
    public string? LogFile { get; init; }
}

/// <summary>The CSV's SHA-256 is not the reviewed one. Nothing is changed.</summary>
public sealed class CsvHashMismatchException(string expected, string actual)
    : Exception($"CSV SHA-256 is {actual}, not the reviewed {expected}. Nothing was changed.");

/// <summary>Wrong environment: database not confirmed, or the CSV was listed elsewhere. Nothing is changed.</summary>
public sealed class EnvironmentMismatchException(string message) : Exception(message);

/// <summary>Append-only audit row: one per intended and one per completed write (never updated).</summary>
[BsonIgnoreExtraElements]
public sealed class AuditRecord
{
    [BsonId] public string Id { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty; // Intended | Completed | Raced
    public string TenantId { get; set; } = string.Empty;
    public string ItemType { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string OriginalClaimId { get; set; } = string.Empty;
    public string? SourceClaimId { get; set; }
    public string? KindBefore { get; set; }
    public string KindAfter { get; set; } = string.Empty;
    public string Evidence { get; set; } = string.Empty;
    public string Operator { get; set; } = string.Empty;
    public string CsvSha256 { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string Database { get; set; } = string.Empty;
    public DateTime At { get; set; }
}

/// <summary>Append-only run record: one when an execute run starts, one when it finishes.</summary>
[BsonIgnoreExtraElements]
public sealed class RunRecord
{
    [BsonId] public string Id { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty; // Started | Finished
    public string Operator { get; set; } = string.Empty;
    public string CsvSha256 { get; set; } = string.Empty;
    public string CsvFile { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string Database { get; set; } = string.Empty;
    public string? LogFile { get; set; }
    public Dictionary<string, int> Outcomes { get; set; } = new();
    public DateTime At { get; set; }
}

/// <summary>
/// Backfills <c>ReversalKind</c> on accumulator-service records written before the kind was
/// recorded (PR #1278 follow-up 3). Those rows fall back to the legacy rule — another claim as
/// the source means a replacement — so a frequency-8 void recorded that way makes a later, real
/// replacement of the same original look like a second replacement (skipped). See
/// docs/operations/ACCUMULATOR-REVERSAL-KIND-BACKFILL.md.
/// <para>Classification is conservative. <c>Replacement</c> (what the legacy rule already
/// assumes, so writing it changes no behaviour) needs the reversing claim to have been processed
/// as a claim itself, or claims-service to show it as a frequency-7 claim of the original.
/// <c>Void</c> — the direction that changes behaviour — needs claims-service evidence (a
/// frequency-8 claim of the original, or for the claim's own reversal, status Voided) and no
/// sign the reversing claim ever applied. <c>Denial</c> needs claims-service status Denied.
/// Anything else, or any contradiction, is AMBIGUOUS and left untouched.</para>
/// </summary>
public sealed class Backfill
{
    public const string AuditCollection = "accumulator_reversal_kind_backfill_audit";
    public const string RunsCollection = "accumulator_reversal_kind_backfill_runs";

    public static readonly string[] Columns =
    [
        "tenant_id", "item_type", "item_id", "original_claim_id", "source_claim_id", "aggregate_id", "outcome",
        "proposed_kind", "evidence", "source_database", "source_host",
    ];

    private readonly Target _target;
    private readonly Action<string> _output;

    public Backfill(Target target, Action<string> output)
    {
        _target = target;
        _output = output;
    }

    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    // ── list ────────────────────────────────────────────────────────────

    /// <summary>Every legacy item (no kind) and the kind its evidence supports. Read-only.</summary>
    public async Task<List<PlanRow>> ListAsync(IReadOnlyCollection<string> tenants, CancellationToken ct = default)
    {
        var items = await ScanAsync(tenants, ct);
        var rows = new List<PlanRow>();
        foreach (var group in items.GroupBy(i => (i.TenantId, i.OriginalClaimId)).OrderBy(g => g.Key))
        {
            var classification = await ClassifyAsync(group.Key.TenantId, group.Key.OriginalClaimId, group.ToList(), ct);
            rows.AddRange(group.OrderBy(i => i.ItemType, StringComparer.Ordinal).ThenBy(i => i.ItemId, StringComparer.Ordinal)
                .Select(i => new PlanRow(i, classification)));
        }
        return rows;
    }

    private async Task<List<LegacyItem>> ScanAsync(IReadOnlyCollection<string> tenants, CancellationToken ct)
    {
        var items = new List<LegacyItem>();

        var ev = Builders<AccumulatorEvent>.Filter;
        var eventFilter = ev.And(
            ev.In(e => e.EventType, new[] { "ClaimReversed", "ClaimTombstoned" }),
            ev.Eq(e => e.ReversalKind, null));
        if (tenants.Count > 0) eventFilter = ev.And(eventFilter, ev.In(e => e.TenantId, tenants));
        foreach (var row in await _target.Events.Find(eventFilter).ToListAsync(ct))
        {
            items.Add(new LegacyItem(
                row.TenantId,
                row.EventType == "ClaimReversed" ? ItemTypes.ClaimReversedRow : ItemTypes.ClaimTombstonedRow,
                row.Id, row.SourceClaimId ?? string.Empty, row.SourceReference, row.AggregateId, row.EventType));
        }

        var pm = Builders<ProcessedClaim>.Filter;
        var markerFilter = pm.And(
            pm.In(p => p.Outcome, new[] { "Reversed", AccumulatorService.Services.AccumulatorService.ReversedBeforeApplyOutcome }),
            pm.Eq(p => p.ReversalKind, null));
        if (tenants.Count > 0) markerFilter = pm.And(markerFilter, pm.In(p => p.TenantId, tenants));
        foreach (var marker in await _target.Markers.Find(markerFilter).ToListAsync(ct))
        {
            if (marker.ClaimId.EndsWith(":reversal", StringComparison.Ordinal))
            {
                var original = marker.ClaimId[..^":reversal".Length];
                items.Add(new LegacyItem(marker.TenantId, ItemTypes.ReversalMarker, marker.ClaimId, original,
                    await MarkerSourceAsync(marker, ct), null, marker.Outcome));
            }
            else if (marker.Outcome == AccumulatorService.Services.AccumulatorService.ReversedBeforeApplyOutcome
                     && !marker.ClaimId.Contains(':'))
            {
                items.Add(new LegacyItem(marker.TenantId, ItemTypes.OriginalTombstoneMarker, marker.ClaimId, marker.ClaimId,
                    marker.ResultingEventId, null, marker.Outcome));
            }
        }
        return items;
    }

    /// <summary>
    /// The reversing claim a reversal marker names: a tombstone names it directly; a completed
    /// reversal names its <c>ClaimReversed</c> row, whose source is the reversing claim.
    /// </summary>
    private async Task<string?> MarkerSourceAsync(ProcessedClaim marker, CancellationToken ct)
    {
        if (marker.Outcome == AccumulatorService.Services.AccumulatorService.ReversedBeforeApplyOutcome)
            return marker.ResultingEventId;
        if (string.IsNullOrEmpty(marker.ResultingEventId)) return null;
        var row = await _target.Events.Find(Builders<AccumulatorEvent>.Filter.And(
                Builders<AccumulatorEvent>.Filter.Eq(e => e.TenantId, marker.TenantId),
                Builders<AccumulatorEvent>.Filter.Eq(e => e.Id, marker.ResultingEventId)))
            .FirstOrDefaultAsync(ct);
        return row?.SourceReference;
    }

    /// <summary>What accumulator-service holds for <paramref name="claimId"/> as a claim of its own.</summary>
    private async Task<List<string>> PresenceAsync(string tenantId, string claimId, CancellationToken ct)
    {
        var found = new List<string>();
        var own = await _target.Markers.Find(Builders<ProcessedClaim>.Filter.And(
                Builders<ProcessedClaim>.Filter.Eq(p => p.TenantId, tenantId),
                Builders<ProcessedClaim>.Filter.In(p => p.ClaimId, new[] { claimId, claimId + ":reversal" })))
            .ToListAsync(ct);
        found.AddRange(own.OrderBy(m => m.ClaimId, StringComparer.Ordinal).Select(m => $"marker {m.ClaimId}={m.Outcome}"));
        var applied = await _target.Events.Find(Builders<AccumulatorEvent>.Filter.And(
                Builders<AccumulatorEvent>.Filter.Eq(e => e.TenantId, tenantId),
                Builders<AccumulatorEvent>.Filter.Eq(e => e.EventType, "ClaimApplied"),
                Builders<AccumulatorEvent>.Filter.Eq(e => e.SourceClaimId, claimId)))
            .AnyAsync(ct);
        if (applied) found.Add($"ClaimApplied row for {claimId}");
        return found;
    }

    /// <summary>The kind the evidence supports for the items of one original claim.</summary>
    internal async Task<Classification> ClassifyAsync(
        string tenantId, string originalClaimId, IReadOnlyList<LegacyItem> items, CancellationToken ct = default)
    {
        static Classification Ambiguous(string why) => new(Classification.Ambiguous, why);

        var sources = items.Select(i => i.SourceClaimId).Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal).ToList();
        if (string.IsNullOrEmpty(originalClaimId)) return Ambiguous("no original claim id recorded");
        if (sources.Count == 0) return Ambiguous("no reversing claim recorded");
        if (sources.Count > 1) return Ambiguous($"records name different reversing claims ({string.Join(", ", sources)})");
        var source = sources[0]!;
        var claims = _target.Claims;

        if (string.Equals(source, originalClaimId, StringComparison.Ordinal))
        {
            // The claim's own reversal: its own void, or a denial after it applied.
            if (claims is null)
                return Ambiguous("the claim's own reversal (a void or a denial); claims-service evidence not configured");
            var facts = await claims.GetAsync(tenantId, originalClaimId, ct);
            if (facts is null) return Ambiguous("the claim's own reversal; claim not found in claims-service");
            return facts.Status switch
            {
                ClaimFacts.Voided => new Classification(ReversalKinds.Void, "the claim's own reversal; claims-service status Voided"),
                ClaimFacts.Denied => new Classification(ReversalKinds.Denial, "the claim's own reversal; claims-service status Denied"),
                var other => Ambiguous($"the claim's own reversal; claims-service status {other?.ToString() ?? "unknown"} is neither Voided (8) nor Denied (6)"),
            };
        }

        var presence = await PresenceAsync(tenantId, source, ct);
        var sourceFacts = claims is null ? null : await claims.GetAsync(tenantId, source, ct);
        var presenceText = presence.Count == 0
            ? $"{source} was never processed as a claim of its own"
            : $"{source} was processed as a claim of its own ({string.Join("; ", presence)})";

        if (sourceFacts is not null)
        {
            var linked = string.Equals(sourceFacts.PredecessorVersionId, originalClaimId, StringComparison.Ordinal);
            var facts = $"claims-service: {source} frequency {sourceFacts.FrequencyCode ?? "none"}, predecessor {sourceFacts.PredecessorVersionId ?? "none"}";
            if (sourceFacts.FrequencyCode == "7" && linked)
                return new Classification(ReversalKinds.Replacement, $"{facts}; {presenceText}");
            if (sourceFacts.FrequencyCode == "8" && linked)
            {
                return presence.Count == 0
                    ? new Classification(ReversalKinds.Void, $"{facts}; {presenceText}")
                    : Ambiguous($"{facts}, but {presenceText} — a frequency-8 void never is");
            }
            return Ambiguous($"{facts}: not a frequency 7 or 8 claim of {originalClaimId}");
        }

        if (presence.Count > 0)
        {
            return new Classification(ReversalKinds.Replacement,
                $"{presenceText} (a frequency-8 void never is); " +
                (claims is null ? "claims-service evidence not configured" : $"{source} not found in claims-service"));
        }
        return Ambiguous($"{presenceText} — consistent with a frequency-8 void, but " +
                         (claims is null ? "claims-service evidence is not configured" : $"{source} is not in claims-service") +
                         " to confirm it");
    }

    public async Task WriteCsvAsync(TextWriter writer, IReadOnlyList<PlanRow> rows)
    {
        await writer.WriteLineAsync(Csv.Line(Columns));
        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv.Line(
            [
                r.Item.TenantId, r.Item.ItemType, r.Item.ItemId, r.Item.OriginalClaimId, r.Item.SourceClaimId ?? string.Empty,
                r.Item.AggregateId ?? string.Empty, r.Item.Outcome ?? string.Empty,
                r.Classification.Kind, r.Classification.Evidence, _target.DatabaseName, _target.Host,
            ]));
        }
    }

    // ── apply ───────────────────────────────────────────────────────────

    /// <summary>
    /// Dry-run (default) or execute of a reviewed plan. Every non-ambiguous row is re-checked
    /// against the live data — still without a kind, same original and source, and the evidence
    /// still gives the same kind — before anything is written; the write itself is conditional on
    /// the kind still being empty. Running it again changes nothing.
    /// </summary>
    public async Task<List<RowResult>> ApplyAsync(ApplyOptions options, CancellationToken ct = default)
    {
        _target.Confirm(options.ConfirmDatabase);
        var sha = Sha256Of(options.CsvPath);
        if (options.Execute)
        {
            if (string.IsNullOrWhiteSpace(options.ExpectedSha256))
                throw new ArgumentException("--execute requires --sha256 <hash of the reviewed plan>.");
            if (!string.Equals(options.ExpectedSha256.Trim(), sha, StringComparison.OrdinalIgnoreCase))
                throw new CsvHashMismatchException(options.ExpectedSha256.Trim(), sha);
            if (string.IsNullOrWhiteSpace(options.Operator))
                throw new ArgumentException("--execute requires --operator <your name>.");
        }

        var plan = ParsePlan(await File.ReadAllTextAsync(options.CsvPath, ct));
        foreach (var (rowNumber, row, database, host) in plan)
        {
            if (!string.Equals(database, _target.DatabaseName, StringComparison.Ordinal)
                || !string.Equals(host, _target.Host, StringComparison.Ordinal))
                throw new EnvironmentMismatchException(
                    $"plan row {rowNumber} was listed from {database} on {host}, not {_target.DatabaseName} on {_target.Host}. Nothing was changed.");
            if (options.Tenants.Count > 0 && !options.Tenants.Contains(row.Item.TenantId))
                throw new ArgumentException($"plan row {rowNumber} is for tenant {row.Item.TenantId}, outside --tenant. Nothing was changed.");
        }

        var runId = $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}"[..40];
        _output(_target.Describe());
        _output($"{(options.Execute ? "EXECUTE" : "DRY-RUN")} plan {options.CsvPath} sha256 {sha}, {plan.Count} row(s)" +
                (options.Execute ? $", operator {options.Operator}, run {runId}" : string.Empty));
        if (options.Execute)
        {
            await _target.Runs.InsertOneAsync(new RunRecord
            {
                Id = $"{runId}:started", RunId = runId, State = "Started", Operator = options.Operator, CsvSha256 = sha,
                CsvFile = Path.GetFileName(options.CsvPath), Host = _target.Host, Database = _target.DatabaseName,
                LogFile = options.LogFile, At = DateTime.UtcNow,
            }, cancellationToken: ct);
        }

        var groups = new Dictionary<(string, string), Classification>();
        var planGroups = plan
            .GroupBy(p => (p.Row.Item.TenantId, p.Row.Item.OriginalClaimId))
            .ToDictionary(g => g.Key, g => g.Select(p => (p.Row.Item.ItemType, p.Row.Item.ItemId)).ToHashSet());
        var results = new List<RowResult>();
        foreach (var (_, row, _, _) in plan)
        {
            var result = await ApplyRowAsync(row, options, runId, sha, groups, planGroups, ct);
            results.Add(result);
            _output($"{result.Outcome,-10} {row.Item.TenantId} {row.Item.ItemType} {row.Item.ItemId} " +
                    $"(original {row.Item.OriginalClaimId}, source {row.Item.SourceClaimId}) → {row.Classification.Kind}: {result.Detail}");
        }

        var summary = results.GroupBy(r => r.Outcome).ToDictionary(g => g.Key.ToString(), g => g.Count());
        _output("Summary: " + string.Join(", ", summary.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}")));
        if (options.Execute)
        {
            await _target.Runs.InsertOneAsync(new RunRecord
            {
                Id = $"{runId}:finished", RunId = runId, State = "Finished", Operator = options.Operator, CsvSha256 = sha,
                CsvFile = Path.GetFileName(options.CsvPath), Host = _target.Host, Database = _target.DatabaseName,
                LogFile = options.LogFile, Outcomes = summary, At = DateTime.UtcNow,
            }, cancellationToken: CancellationToken.None);
        }
        return results;
    }

    private async Task<RowResult> ApplyRowAsync(
        PlanRow row, ApplyOptions options, string runId, string sha,
        Dictionary<(string, string), Classification> groups,
        Dictionary<(string, string), HashSet<(string, string)>> planGroups, CancellationToken ct)
    {
        if (row.Classification.IsAmbiguous)
            return new RowResult(row, RowOutcome.Ambiguous, "left untouched: " + row.Classification.Evidence);
        if (row.Classification.Kind is not (ReversalKinds.Void or ReversalKinds.Replacement or ReversalKinds.Denial))
            return new RowResult(row, RowOutcome.Refused, $"unknown kind '{row.Classification.Kind}'");

        var live = await LoadAsync(row.Item, ct);
        if (live is null) return new RowResult(row, RowOutcome.Refused, "not found");
        if (live.Value.Kind is { } existing)
        {
            return string.Equals(existing, row.Classification.Kind, StringComparison.Ordinal)
                ? new RowResult(row, RowOutcome.AlreadySet, $"already {existing}")
                : new RowResult(row, RowOutcome.Refused, $"already has kind {existing}");
        }
        if (!string.Equals(live.Value.Item.OriginalClaimId, row.Item.OriginalClaimId, StringComparison.Ordinal)
            || !string.Equals(live.Value.Item.SourceClaimId ?? string.Empty, row.Item.SourceClaimId ?? string.Empty, StringComparison.Ordinal))
            return new RowResult(row, RowOutcome.Refused, "the record changed since it was listed; list again");

        var key = (row.Item.TenantId, row.Item.OriginalClaimId);
        if (!groups.TryGetValue(key, out var now))
        {
            var groupItems = (await ScanAsync([row.Item.TenantId], ct))
                .Where(i => i.OriginalClaimId == row.Item.OriginalClaimId).ToList();
            // The whole group or nothing: a plan missing one of the original's
            // legacy records (a row deleted from the CSV, or a record written
            // since the listing) is refused for that original.
            var missing = groupItems.Where(i => !planGroups[key].Contains((i.ItemType, i.ItemId))).ToList();
            now = missing.Count > 0
                ? new Classification(Classification.Ambiguous,
                    "the plan does not hold every legacy record of this original (" +
                    string.Join(", ", missing.Select(i => $"{i.ItemType} {i.ItemId}")) + "); list again")
                : groupItems.Count == 0
                    ? new Classification(Classification.Ambiguous, "no legacy records left")
                    : await ClassifyAsync(row.Item.TenantId, row.Item.OriginalClaimId, groupItems, ct);
            groups[key] = now;
        }
        if (!string.Equals(now.Kind, row.Classification.Kind, StringComparison.Ordinal))
            return new RowResult(row, RowOutcome.Refused, $"the evidence now gives {now.Kind} ({now.Evidence}); list again");

        if (!options.Execute)
            return new RowResult(row, RowOutcome.WouldSet, $"WOULD set ReversalKind := {row.Classification.Kind}");

        AuditRecord Audit(string state) => new()
        {
            Id = $"{runId}:{state}:{row.Item.TenantId}:{row.Item.ItemType}:{row.Item.ItemId}",
            RunId = runId, State = state, TenantId = row.Item.TenantId, ItemType = row.Item.ItemType,
            ItemId = row.Item.ItemId, OriginalClaimId = row.Item.OriginalClaimId, SourceClaimId = row.Item.SourceClaimId,
            KindBefore = null, KindAfter = row.Classification.Kind, Evidence = row.Classification.Evidence,
            Operator = options.Operator, CsvSha256 = sha, Host = _target.Host, Database = _target.DatabaseName,
            At = DateTime.UtcNow,
        };

        try
        {
            await _target.Audit.InsertOneAsync(Audit("Intended"), cancellationToken: ct);
            var written = await WriteKindAsync(row.Item, row.Classification.Kind, ct);
            if (written)
            {
                await _target.Audit.InsertOneAsync(Audit("Completed"), cancellationToken: CancellationToken.None);
                return new RowResult(row, RowOutcome.Set, $"ReversalKind := {row.Classification.Kind}");
            }
            // The conditional write matched nothing: someone set the kind meanwhile.
            await _target.Audit.InsertOneAsync(Audit("Raced"), cancellationToken: CancellationToken.None);
            var after = await LoadAsync(row.Item, ct);
            return after?.Kind == row.Classification.Kind
                ? new RowResult(row, RowOutcome.AlreadySet, $"set meanwhile to {after?.Kind}")
                : new RowResult(row, RowOutcome.Failed, $"the kind changed meanwhile to {after?.Kind ?? "(gone)"}; list again");
        }
        catch (MongoException ex)
        {
            return new RowResult(row, RowOutcome.Failed, ex.Message);
        }
    }

    /// <summary>The live record behind a plan row, with its current kind.</summary>
    private async Task<(LegacyItem Item, string? Kind)?> LoadAsync(LegacyItem item, CancellationToken ct)
    {
        if (item.ItemType is ItemTypes.ClaimReversedRow or ItemTypes.ClaimTombstonedRow)
        {
            var row = await _target.Events.Find(Builders<AccumulatorEvent>.Filter.And(
                    Builders<AccumulatorEvent>.Filter.Eq(e => e.TenantId, item.TenantId),
                    Builders<AccumulatorEvent>.Filter.Eq(e => e.Id, item.ItemId)))
                .FirstOrDefaultAsync(ct);
            if (row is null) return null;
            var expectedType = item.ItemType == ItemTypes.ClaimReversedRow ? "ClaimReversed" : "ClaimTombstoned";
            if (row.EventType != expectedType) return null;
            return (item with { OriginalClaimId = row.SourceClaimId ?? string.Empty, SourceClaimId = row.SourceReference },
                row.ReversalKind);
        }

        var marker = await _target.Markers.Find(Builders<ProcessedClaim>.Filter.And(
                Builders<ProcessedClaim>.Filter.Eq(p => p.TenantId, item.TenantId),
                Builders<ProcessedClaim>.Filter.Eq(p => p.ClaimId, item.ItemId)))
            .FirstOrDefaultAsync(ct);
        if (marker is null) return null;
        var source = item.ItemType == ItemTypes.ReversalMarker
            ? await MarkerSourceAsync(marker, ct)
            : marker.ResultingEventId;
        var original = item.ItemType == ItemTypes.ReversalMarker && marker.ClaimId.EndsWith(":reversal", StringComparison.Ordinal)
            ? marker.ClaimId[..^":reversal".Length]
            : marker.ClaimId;
        return (item with { OriginalClaimId = original, SourceClaimId = source }, marker.ReversalKind);
    }

    /// <summary>Sets the kind only while it is still empty (the filter is the guard).</summary>
    private async Task<bool> WriteKindAsync(LegacyItem item, string kind, CancellationToken ct)
    {
        if (item.ItemType is ItemTypes.ClaimReversedRow or ItemTypes.ClaimTombstonedRow)
        {
            var result = await _target.Events.UpdateOneAsync(
                Builders<AccumulatorEvent>.Filter.And(
                    Builders<AccumulatorEvent>.Filter.Eq(e => e.TenantId, item.TenantId),
                    Builders<AccumulatorEvent>.Filter.Eq(e => e.Id, item.ItemId),
                    Builders<AccumulatorEvent>.Filter.Eq(e => e.ReversalKind, null)),
                Builders<AccumulatorEvent>.Update.Set(e => e.ReversalKind, kind),
                cancellationToken: ct);
            return result.ModifiedCount == 1;
        }
        var marker = await _target.Markers.UpdateOneAsync(
            Builders<ProcessedClaim>.Filter.And(
                Builders<ProcessedClaim>.Filter.Eq(p => p.TenantId, item.TenantId),
                Builders<ProcessedClaim>.Filter.Eq(p => p.ClaimId, item.ItemId),
                Builders<ProcessedClaim>.Filter.Eq(p => p.ReversalKind, null)),
            Builders<ProcessedClaim>.Update.Set(p => p.ReversalKind, kind),
            cancellationToken: ct);
        return marker.ModifiedCount == 1;
    }

    /// <summary>Plan rows from the reviewed CSV (exact columns; nothing guessed).</summary>
    /// <exception cref="FormatException">The CSV is not a plan the tool wrote.</exception>
    internal static List<(int RowNumber, PlanRow Row, string Database, string Host)> ParsePlan(string text)
    {
        var parsed = Csv.ParseWithHeader(text, Columns);
        var rows = new List<(int, PlanRow, string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (rowNumber, f) in parsed)
        {
            string Get(string column) => Csv.Unguard(f[column]);
            var type = Get("item_type");
            if (type is not (ItemTypes.ClaimReversedRow or ItemTypes.ClaimTombstonedRow or ItemTypes.ReversalMarker or ItemTypes.OriginalTombstoneMarker))
                throw new FormatException($"data row {rowNumber}: unknown item_type '{type}'");
            if (!seen.Add($"{Get("tenant_id")}\u0001{type}\u0001{Get("item_id")}"))
                throw new FormatException($"data row {rowNumber}: the same record appears more than once");
            var item = new LegacyItem(Get("tenant_id"), type, Get("item_id"), Get("original_claim_id"),
                Get("source_claim_id") is { Length: > 0 } s ? s : null,
                Get("aggregate_id") is { Length: > 0 } a ? a : null,
                Get("outcome") is { Length: > 0 } o ? o : null);
            rows.Add((rowNumber, new PlanRow(item, new Classification(Get("proposed_kind"), Get("evidence"))),
                Get("source_database"), Get("source_host")));
        }
        return rows;
    }
}
