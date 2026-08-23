using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Stedi;
using CloudHealthOffice.Infrastructure.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.ReferenceData.Payers.Stedi;

/// <summary>
/// Pulls the Stedi payer directory into the canonical payer store. Failures are
/// recorded and rethrown as <see cref="PayerDirectorySyncResult"/> rather than
/// leaking <see cref="StediApiException"/> to callers.
/// </summary>
internal sealed class StediPayerDirectorySynchronizer : IPayerDirectorySynchronizer
{
    private readonly StediPayerDirectoryClient _client;
    private readonly IPayerReferenceStore _store;
    private readonly IOptions<StediGatewayOptions> _gatewayOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StediPayerDirectorySynchronizer> _logger;

    public StediPayerDirectorySynchronizer(
        StediPayerDirectoryClient client,
        IPayerReferenceStore store,
        IOptions<StediGatewayOptions> gatewayOptions,
        ILogger<StediPayerDirectorySynchronizer> logger,
        TimeProvider? timeProvider = null)
    {
        _client = client;
        _store = store;
        _gatewayOptions = gatewayOptions;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<PayerDirectorySyncResult> SynchronizeAsync(CancellationToken ct = default)
    {
        var started = _timeProvider.GetUtcNow();
        var source = StediPayerMapper.Source;

        var configErrors = _gatewayOptions.Value.Validate();
        if (configErrors.Count > 0)
        {
            return await FailAsync(
                started, source, "Stedi gateway is not configured for payer directory sync.", ct)
                .ConfigureAwait(false);
        }

        try
        {
            var dtos = await _client.ListAllAsync(ct).ConfigureAwait(false);
            var existingById = new Dictionary<string, PayerReference>(StringComparer.OrdinalIgnoreCase);
            var mapped = new List<PayerReference>();
            var malformed = 0;

            foreach (var dto in dtos)
            {
                var canonical = StediPayerMapper.ToCanonical(dto, started);
                if (canonical is null)
                {
                    malformed++;
                    continue;
                }

                mapped.Add(canonical);
            }

            var added = 0;
            var updated = 0;
            foreach (var payer in mapped)
            {
                var previous = await _store.GetByIdAsync(payer.Id, ct).ConfigureAwait(false);
                if (previous is null)
                {
                    added++;
                }
                else
                {
                    updated++;
                }

                existingById[payer.Id] = payer;
            }

            await _store.UpsertManyAsync(mapped, ct).ConfigureAwait(false);
            var disabled = await _store
                .DisableMissingFromSourceAsync(source, existingById.Keys.ToList(), started, ct)
                .ConfigureAwait(false);

            var completed = _timeProvider.GetUtcNow();
            var result = new PayerDirectorySyncResult
            {
                Succeeded = true,
                Source = source,
                StartedAt = started,
                CompletedAt = completed,
                Received = dtos.Count,
                Added = added,
                Updated = updated,
                Disabled = disabled,
                SkippedMalformed = malformed
            };

            await SaveStatusAsync(result, ct).ConfigureAwait(false);
            RecordMetrics(result);

            _logger.LogInformation(
                "Payer directory sync from {Source} received={Received} added={Added} updated={Updated} disabled={Disabled} malformed={Malformed} durationMs={DurationMs}",
                source, result.Received, result.Added, result.Updated, result.Disabled,
                result.SkippedMalformed, (int)result.Duration.TotalMilliseconds);

            return result;
        }
        catch (StediApiException ex)
        {
            _logger.LogWarning(
                "Payer directory sync failed ({Category}): {Message}", ex.Category, ex.Message);
            ChoMetrics.PayerSyncFailures.Add(1, new KeyValuePair<string, object?>("cho.category", ex.Category.ToString()));
            return await FailAsync(started, source, ex.Message, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected payer directory sync failure");
            ChoMetrics.PayerSyncFailures.Add(1, new KeyValuePair<string, object?>("cho.category", "Internal"));
            return await FailAsync(
                started, source, "Unexpected error synchronizing the payer directory.", ct)
                .ConfigureAwait(false);
        }
    }

    public Task<PayerDirectorySyncStatus?> GetStatusAsync(CancellationToken ct = default) =>
        _store.GetSyncStatusAsync(StediPayerMapper.Source, ct);

    private async Task<PayerDirectorySyncResult> FailAsync(
        DateTimeOffset started, string source, string error, CancellationToken ct)
    {
        var completed = _timeProvider.GetUtcNow();
        var result = new PayerDirectorySyncResult
        {
            Succeeded = false,
            Source = source,
            StartedAt = started,
            CompletedAt = completed,
            Error = error
        };
        await SaveStatusAsync(result, ct).ConfigureAwait(false);
        ChoMetrics.PayerSyncDuration.Record(result.Duration.TotalSeconds,
            new KeyValuePair<string, object?>("cho.outcome", "failed"));
        return result;
    }

    private async Task SaveStatusAsync(PayerDirectorySyncResult result, CancellationToken ct)
    {
        DateTimeOffset? lastSucceeded = result.CompletedAt;
        if (!result.Succeeded)
        {
            lastSucceeded = (await _store.GetSyncStatusAsync(result.Source, ct).ConfigureAwait(false))
                ?.LastSucceededAt;
        }

        await _store.SaveSyncStatusAsync(new PayerDirectorySyncStatus
        {
            Source = result.Source,
            LastAttemptedAt = result.CompletedAt,
            LastSucceededAt = lastSucceeded,
            LastSucceeded = result.Succeeded,
            LastReceived = result.Received,
            LastError = result.Error
        }, ct).ConfigureAwait(false);
    }

    private static void RecordMetrics(PayerDirectorySyncResult result)
    {
        ChoMetrics.PayerSyncDuration.Record(
            result.Duration.TotalSeconds, new KeyValuePair<string, object?>("cho.outcome", "success"));
        ChoMetrics.PayerSyncRecords.Add(result.Received, Tag("received"));
        ChoMetrics.PayerSyncRecords.Add(result.Added, Tag("added"));
        ChoMetrics.PayerSyncRecords.Add(result.Updated, Tag("updated"));
        ChoMetrics.PayerSyncRecords.Add(result.Disabled, Tag("disabled"));
    }

    private static KeyValuePair<string, object?> Tag(string outcome) =>
        new("cho.outcome", outcome);
}

/// <summary>Used when Stedi is not registered so the host can still start.</summary>
internal sealed class DisabledPayerDirectorySynchronizer : IPayerDirectorySynchronizer
{
    public Task<PayerDirectorySyncResult> SynchronizeAsync(CancellationToken ct = default) =>
        Task.FromResult(new PayerDirectorySyncResult
        {
            Succeeded = false,
            Source = StediPayerMapper.Source,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            Error = "Stedi payer directory sync is not configured."
        });

    public Task<PayerDirectorySyncStatus?> GetStatusAsync(CancellationToken ct = default) =>
        Task.FromResult<PayerDirectorySyncStatus?>(null);
}
