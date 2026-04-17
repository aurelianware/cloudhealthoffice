using System.Collections.Concurrent;
using EligibilityService.Models;

namespace EligibilityService.Services;

/// <summary>
/// Persistence abstraction for BatchEligibilityJob. Kept behind an interface
/// so tests can use the in-memory store while production can swap in a
/// Cosmos/Mongo implementation without touching the service.
///
/// The byte[]-based Save/Get methods are the stable contract for small
/// payloads (inline path, tests). The stream-based SaveResultStream /
/// OpenResultStream methods are used for the large queued path so the
/// full row set never needs to materialize in memory. Default interface
/// implementations bridge the two so existing implementations remain
/// source-compatible.
/// </summary>
public interface IBatchJobStore
{
    Task SaveAsync(BatchEligibilityJob job, CancellationToken ct = default);
    Task<BatchEligibilityJob?> GetAsync(string tenantId, string jobId, CancellationToken ct = default);
    Task<byte[]?> GetResultAsync(string tenantId, string jobId, CancellationToken ct = default);
    Task SaveResultAsync(string tenantId, string jobId, byte[] payload, CancellationToken ct = default);

    /// <summary>
    /// Streaming write of a result (or input) payload. Default implementation
    /// buffers into a byte[] and delegates to <see cref="SaveResultAsync"/>
    /// so in-memory / dev stores keep working. Persistent stores should
    /// override to stream directly to blob storage.
    /// </summary>
    async Task SaveResultStreamAsync(
        string tenantId, string jobId, Stream source, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, ct);
        await SaveResultAsync(tenantId, jobId, buffer.ToArray(), ct);
    }

    /// <summary>
    /// Streaming read of a result (or input) payload. Default implementation
    /// wraps the byte[] from <see cref="GetResultAsync"/>.
    /// </summary>
    async Task<Stream?> OpenResultStreamAsync(
        string tenantId, string jobId, CancellationToken ct = default)
    {
        var bytes = await GetResultAsync(tenantId, jobId, ct);
        return bytes == null ? null : new MemoryStream(bytes, writable: false);
    }
}

/// <summary>
/// Default in-memory implementation. Multi-tenant safe (keyed by
/// tenantId + jobId).
///
/// Intended for tests and single-instance / dev deployments — production
/// should bind IBatchJobStore to a Cosmos, Mongo or blob-backed store so
/// state survives pod restarts and is visible across replicas.
///
/// To avoid unbounded memory growth on long-running hosts, completed jobs and
/// their result payloads are evicted after <see cref="CompletedRetention"/>
/// (default 24h) by <see cref="Evict"/>, which the hosted worker calls
/// opportunistically on every queue poll.
/// </summary>
public class InMemoryBatchJobStore : IBatchJobStore
{
    public static readonly TimeSpan CompletedRetention = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, BatchEligibilityJob> _jobs = new();
    private readonly ConcurrentDictionary<string, byte[]> _results = new();

    public Task SaveAsync(BatchEligibilityJob job, CancellationToken ct = default)
    {
        _jobs[Key(job.TenantId, job.Id)] = job;
        return Task.CompletedTask;
    }

    public Task<BatchEligibilityJob?> GetAsync(string tenantId, string jobId, CancellationToken ct = default)
    {
        _jobs.TryGetValue(Key(tenantId, jobId), out var job);
        return Task.FromResult<BatchEligibilityJob?>(job);
    }

    public Task<byte[]?> GetResultAsync(string tenantId, string jobId, CancellationToken ct = default)
    {
        _results.TryGetValue(Key(tenantId, jobId), out var data);
        return Task.FromResult<byte[]?>(data);
    }

    public Task SaveResultAsync(string tenantId, string jobId, byte[] payload, CancellationToken ct = default)
    {
        _results[Key(tenantId, jobId)] = payload;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Drops jobs whose <see cref="BatchEligibilityJob.CompletedDate"/> is older
    /// than <see cref="CompletedRetention"/>, plus their result + input payloads.
    /// Safe to call from multiple threads.
    /// </summary>
    public int Evict(DateTime? now = null)
    {
        var cutoff = (now ?? DateTime.UtcNow) - CompletedRetention;
        var removed = 0;
        foreach (var kvp in _jobs)
        {
            var job = kvp.Value;
            if (job.CompletedDate is DateTime completed && completed < cutoff)
            {
                if (_jobs.TryRemove(kvp.Key, out _))
                {
                    _results.TryRemove(kvp.Key, out _);
                    _results.TryRemove(Key(job.TenantId, BatchInputKey(job.Id)), out _);
                    removed++;
                }
            }
        }
        return removed;
    }

    private static string Key(string tenantId, string jobId) => $"{tenantId}::{jobId}";

    // Must stay in sync with BatchEligibilityService.InputKey.
    private static string BatchInputKey(string jobId) => $"INPUT::{jobId}";
}
