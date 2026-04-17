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
/// tenantId + jobId) and suitable for tests and single-instance deployments.
/// </summary>
public class InMemoryBatchJobStore : IBatchJobStore
{
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

    private static string Key(string tenantId, string jobId) => $"{tenantId}::{jobId}";
}
