using System.Diagnostics;
using ClaimsService.Models;
using ClaimsService.Models.Migrations;
using ClaimsService.Repositories;
using CloudHealthOffice.Infrastructure.Observability;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

namespace ClaimsService.Services.Migrations;

/// <summary>
/// Capability 5.1b — Cosmos partition-key migration service. Mirrors
/// the operator-driven, idempotent, single-tenant-aware shape of
/// <c>NetworkTierBackfillService</c> (benefit-plan capability 5.5),
/// adapted for cross-tenant container-to-container copy:
/// <list type="bullet">
///   <item>The legacy <c>Claims</c> container has no canonical
///   tenant partition, so the migration walks the entire container
///   cross-partition; the target <c>ClaimsV2</c> container is
///   partitioned by <c>/tenantId</c>, so writes are partition-keyed
///   per row.</item>
///   <item>Hydration runs before each write so legacy rows
///   (pre-versioning fields) land in <c>ClaimsV2</c> with
///   <c>ClaimVersionId</c>, <c>VersionNumber</c>, and
///   <c>VersionState</c> populated by
///   <see cref="ClaimRepository.Hydrate"/>.</item>
///   <item>Idempotency is batched: every 100 reads (configurable),
///   the service queries the target for already-present <c>Id</c>s in
///   one round trip rather than paying a per-doc point-read RU. This
///   matters at scale — a million-row container otherwise burns RUs
///   re-checking already-migrated rows on each rerun.</item>
/// </list>
/// </summary>
public sealed class ClaimMigrationService : IClaimMigrationService
{
    private readonly Container _source;
    private readonly Container _target;
    private readonly IOptionsMonitor<ClaimMigrationOptions> _options;
    private readonly ILogger<ClaimMigrationService> _logger;

    private readonly object _stateLock = new();
    private bool _running;
    private ClaimMigrationResult? _lastRun;

    public ClaimMigrationService(
        IClaimMigrationContainerResolver containers,
        IOptionsMonitor<ClaimMigrationOptions> options,
        ILogger<ClaimMigrationService> logger)
    {
        ArgumentNullException.ThrowIfNull(containers);
        _source = containers.Source;
        _target = containers.Target;
        _options = options;
        _logger = logger;
    }

    public ClaimMigrationStatus GetStatus()
    {
        var opts = _options.CurrentValue;
        lock (_stateLock)
        {
            return new ClaimMigrationStatus
            {
                MigrationsEnabled = opts.MigrationsEnabled,
                SourceContainer = opts.SourceContainerName,
                TargetContainer = opts.TargetContainerName,
                BatchSize = opts.BatchSize,
                IsRunning = _running,
                LastRun = _lastRun,
            };
        }
    }

    public async Task<ClaimMigrationResult> RunAsync(ClaimMigrationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var opts = _options.CurrentValue;
        var batchSize = request.BatchSize is > 0 ? request.BatchSize.Value : opts.BatchSize;

        // One-at-a-time: a second concurrent run would double-count
        // outcomes and produce confusing telemetry. Operators get 409
        // from the controller; the service itself is the source of
        // truth for the running flag. Distinct exception type rather
        // than a plain InvalidOperationException so the controller's
        // 409 mapping doesn't depend on string-matching a message.
        lock (_stateLock)
        {
            if (_running)
            {
                throw new MigrationAlreadyRunningException();
            }
            _running = true;
        }

        var stopwatch = Stopwatch.StartNew();
        var result = new ClaimMigrationResult
        {
            DryRun = request.DryRun,
            SourceContainer = opts.SourceContainerName,
            TargetContainer = opts.TargetContainerName,
        };

        _logger.LogInformation(
            "claims cosmos migration start runId={RunId} dryRun={DryRun} source={Source} target={Target} batchSize={BatchSize} actor={Actor} correlation={Correlation}",
            result.MigrationRunId,
            request.DryRun,
            SanitizeForLog(opts.SourceContainerName),
            SanitizeForLog(opts.TargetContainerName),
            batchSize,
            SanitizeForLog(request.ActorId),
            SanitizeForLog(request.CorrelationId));

        try
        {
            await CopyAsync(result, batchSize, request.DryRun, ct);
            result.Outcome = result.DocumentsErrored == 0 ? "success" : "partial";
        }
        catch (OperationCanceledException)
        {
            result.Outcome = "failed";
            throw;
        }
        catch (Exception ex)
        {
            result.Outcome = "failed";
            _logger.LogError(ex,
                "claims cosmos migration aborted runId={RunId} read={Read} written={Written} skipped={Skipped} errored={Errored}",
                result.MigrationRunId,
                result.DocumentsRead,
                result.DocumentsWritten,
                result.DocumentsSkipped,
                result.DocumentsErrored);
            throw;
        }
        finally
        {
            stopwatch.Stop();
            result.CompletedAt = DateTime.UtcNow;
            result.DurationSeconds = stopwatch.Elapsed.TotalSeconds;

            lock (_stateLock)
            {
                _lastRun = result;
                _running = false;
            }

            ChoMetrics.ClaimsCosmosMigrationRuns.Add(
                1,
                new KeyValuePair<string, object?>("cho.outcome", result.Outcome),
                new KeyValuePair<string, object?>("cho.dry_run", request.DryRun ? "true" : "false"));
            ChoMetrics.ClaimsCosmosMigrationDuration.Record(
                result.DurationSeconds,
                new KeyValuePair<string, object?>("cho.outcome", result.Outcome),
                new KeyValuePair<string, object?>("cho.dry_run", request.DryRun ? "true" : "false"));
        }

        _logger.LogInformation(
            "claims cosmos migration complete runId={RunId} outcome={Outcome} read={Read} written={Written} skipped={Skipped} errored={Errored} hydrated={Hydrated} durationSeconds={DurationSeconds}",
            result.MigrationRunId,
            result.Outcome,
            result.DocumentsRead,
            result.DocumentsWritten,
            result.DocumentsSkipped,
            result.DocumentsErrored,
            result.DocumentsHydrated,
            result.DurationSeconds);

        return result;
    }

    private async Task CopyAsync(ClaimMigrationResult result, int batchSize, bool dryRun, CancellationToken ct)
    {
        // Cross-partition read: the legacy container's partition key is
        // /Id (runtime) or /memberId (Bicep declaration); both are
        // per-document, so a tenant-scoped read isn't an option here.
        // We accept the cross-partition cost — this is the migration's
        // one-time RU spend.
        var query = new QueryDefinition("SELECT * FROM c");
        var iterator = _source.GetItemQueryIterator<Claim>(
            query,
            requestOptions: new QueryRequestOptions { MaxItemCount = batchSize });

        while (iterator.HasMoreResults)
        {
            ct.ThrowIfCancellationRequested();
            var page = await iterator.ReadNextAsync(ct);

            var batch = page.ToList();
            if (batch.Count == 0) continue;

            result.DocumentsRead += batch.Count;

            // Batched idempotency check (Decision 7) — query the target
            // for the IDs in this batch in a single round trip per
            // partition the IDs span, instead of point-reading each.
            var existingIds = await GetExistingTargetIdsAsync(batch, ct);

            foreach (var claim in batch)
            {
                ct.ThrowIfCancellationRequested();

                if (existingIds.Contains(claim.Id))
                {
                    result.DocumentsSkipped++;
                    ChoMetrics.ClaimsCosmosMigrationDocuments.Add(
                        1,
                        new KeyValuePair<string, object?>("cho.outcome", "skipped"));
                    continue;
                }

                var hydrated = HydrateForMigration(claim, out var didHydrate);
                if (didHydrate) result.DocumentsHydrated++;

                if (string.IsNullOrEmpty(hydrated.TenantId))
                {
                    // Defensive: a row missing TenantId can't be partitioned
                    // in the new container. Surface explicitly rather than
                    // letting Cosmos reject the write with a generic 400.
                    result.DocumentsErrored++;
                    result.Issues.Add(new ClaimMigrationIssue
                    {
                        ClaimId = hydrated.Id,
                        TenantId = string.Empty,
                        Outcome = "errored",
                        Detail = "Claim is missing TenantId; cannot partition into ClaimsV2.",
                    });
                    ChoMetrics.ClaimsCosmosMigrationDocuments.Add(
                        1,
                        new KeyValuePair<string, object?>("cho.outcome", "errored"));
                    continue;
                }

                if (dryRun)
                {
                    result.DocumentsWritten++;
                    ChoMetrics.ClaimsCosmosMigrationDocuments.Add(
                        1,
                        new KeyValuePair<string, object?>("cho.outcome", "would_write"));
                    continue;
                }

                try
                {
                    await _target.CreateItemAsync(
                        hydrated,
                        new PartitionKey(hydrated.TenantId),
                        cancellationToken: ct);
                    result.DocumentsWritten++;
                    ChoMetrics.ClaimsCosmosMigrationDocuments.Add(
                        1,
                        new KeyValuePair<string, object?>("cho.outcome", "written"));
                }
                catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    // Race: another writer landed this row between the
                    // batched existence check and our write. Treat as
                    // skipped — the row exists in the target.
                    result.DocumentsSkipped++;
                    ChoMetrics.ClaimsCosmosMigrationDocuments.Add(
                        1,
                        new KeyValuePair<string, object?>("cho.outcome", "skipped"));
                }
                catch (CosmosException ex)
                {
                    result.DocumentsErrored++;
                    result.Issues.Add(new ClaimMigrationIssue
                    {
                        ClaimId = hydrated.Id,
                        TenantId = hydrated.TenantId,
                        Outcome = "errored",
                        Detail = $"{ex.StatusCode}: {ex.Message}",
                    });
                    ChoMetrics.ClaimsCosmosMigrationDocuments.Add(
                        1,
                        new KeyValuePair<string, object?>("cho.outcome", "errored"));
                }
            }
        }
    }

    /// <summary>
    /// Query the target container for the subset of <paramref name="batch"/>
    /// IDs already present. The query groups by <see cref="Claim.TenantId"/>
    /// because /tenantId is the target partition key — a per-tenant query is
    /// partition-scoped. A document with a missing TenantId can't be
    /// migrated regardless, so it is excluded from the existence check.
    /// </summary>
    private async Task<HashSet<string>> GetExistingTargetIdsAsync(IReadOnlyList<Claim> batch, CancellationToken ct)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);

        var byTenant = batch
            .Where(c => !string.IsNullOrEmpty(c.TenantId))
            .GroupBy(c => c.TenantId);

        foreach (var group in byTenant)
        {
            ct.ThrowIfCancellationRequested();
            var ids = group.Select(c => c.Id).ToArray();
            if (ids.Length == 0) continue;

            var query = new QueryDefinition("SELECT VALUE c.id FROM c WHERE ARRAY_CONTAINS(@ids, c.id)")
                .WithParameter("@ids", ids);

            var iterator = _target.GetItemQueryIterator<string>(
                query,
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(group.Key) });

            while (iterator.HasMoreResults)
            {
                var page = await iterator.ReadNextAsync(ct);
                foreach (var id in page)
                {
                    if (!string.IsNullOrEmpty(id)) found.Add(id);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Hydration mirrors <see cref="ClaimRepository.Hydrate"/> but
    /// reports whether any field was modified so the migration result
    /// can attribute legacy rows accurately. Idempotent on
    /// already-canonicalized rows.
    /// </summary>
    private static Claim HydrateForMigration(Claim claim, out bool didHydrate)
    {
        var before = (claim.ClaimVersionId, claim.VersionNumber, claim.VersionState);
        var hydrated = ClaimRepository.Hydrate(claim);
        didHydrate =
            before.ClaimVersionId != hydrated.ClaimVersionId
            || before.VersionNumber != hydrated.VersionNumber
            || before.VersionState != hydrated.VersionState;
        return hydrated;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

/// <summary>
/// DI-resolved holder for the source/target Cosmos containers consumed
/// by <see cref="ClaimMigrationService"/>. Defining a holder rather
/// than injecting two named <see cref="Container"/>s keeps the runtime
/// service code (which only ever references the production container
/// resolved by <c>CosmosDb:ContainerName</c>) decoupled from the
/// migration tooling — only the migration job sees both containers.
/// </summary>
public interface IClaimMigrationContainerResolver
{
    Container Source { get; }
    Container Target { get; }
}

/// <summary>
/// Default <see cref="IClaimMigrationContainerResolver"/>: resolves
/// both containers from the configured database via the supplied
/// <see cref="CosmosClient"/>.
/// </summary>
public sealed class CosmosClaimMigrationContainerResolver : IClaimMigrationContainerResolver
{
    public CosmosClaimMigrationContainerResolver(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        IOptionsMonitor<ClaimMigrationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(cosmosClient);
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "ClaimsDB";
        var opts = options.CurrentValue;
        Source = cosmosClient.GetContainer(databaseName, opts.SourceContainerName);
        Target = cosmosClient.GetContainer(databaseName, opts.TargetContainerName);
    }

    public Container Source { get; }
    public Container Target { get; }
}
