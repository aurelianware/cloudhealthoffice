using System.Collections.Concurrent;
using EligibilityService.Models;

namespace EligibilityService.Services;

/// <summary>
/// Persistence abstraction for BatchEligibilityJob. Kept behind an interface
/// so tests can use the in-memory store while production can swap in a
/// Cosmos/Mongo implementation without touching the service.
/// </summary>
public interface IBatchJobStore
{
    Task SaveAsync(BatchEligibilityJob job, CancellationToken ct = default);
    Task<BatchEligibilityJob?> GetAsync(string tenantId, string jobId, CancellationToken ct = default);
    Task<byte[]?> GetResultAsync(string tenantId, string jobId, CancellationToken ct = default);
    Task SaveResultAsync(string tenantId, string jobId, byte[] payload, CancellationToken ct = default);
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
