using System.Collections.Concurrent;
using FhirService.Models;

namespace FhirService.Services;

public class BulkExportService : IBulkExportService
{
    private readonly ConcurrentDictionary<string, BulkExportJob> _jobs = new();
    private readonly string _serverBaseUrl;
    private readonly ILogger<BulkExportService> _logger;

    private static readonly List<string> DefaultResourceTypes = new()
    {
        "Patient", "ExplanationOfBenefit", "Coverage", "Encounter",
    };

    public BulkExportService(IConfiguration configuration, ILogger<BulkExportService> logger)
    {
        _serverBaseUrl = configuration["Fhir:ServerBaseUrl"]
            ?? "https://api.cloudhealthoffice.com/fhir/r4";
        _logger = logger;
    }

    public Task<BulkExportJob> InitiateExportAsync(
        BulkExportRequest request, string tenantId, CancellationToken ct = default)
    {
        var jobId = $"export-{Guid.NewGuid().ToString("N")[..12]}";

        var resourceTypes = string.IsNullOrWhiteSpace(request.Type)
            ? DefaultResourceTypes.ToList()
            : request.Type.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

        var baseUrl = _serverBaseUrl;
        var manifest = new BulkExportManifest
        {
            TransactionTime = DateTimeOffset.UtcNow.ToString("o"),
            Request = request.GroupId != null
                ? $"Group/{request.GroupId}/$export"
                : "$export",
            Output = resourceTypes.Select(rt => new BulkExportOutput
            {
                Type = rt,
                Url = $"{baseUrl}/$export-data/{jobId}/{rt}.ndjson",
                Count = 0,
            }).ToList(),
        };

        var job = new BulkExportJob
        {
            JobId = jobId,
            Status = BulkExportStatus.Complete,
            CreatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            Request = manifest.Request,
            GroupId = request.GroupId,
            ResourceTypes = resourceTypes,
            Since = request.Since,
            Manifest = manifest,
            ProgressPercent = 100,
        };

        _jobs[$"{tenantId}:{jobId}"] = job;

        _logger.LogInformation(
            "Bulk export job {JobId} created for tenant {TenantId}, types={Types}",
            jobId, Sanitize(tenantId), Sanitize(string.Join(",", resourceTypes)));

        return Task.FromResult(job);
    }

    public Task<BulkExportJob?> GetJobStatusAsync(
        string jobId, string tenantId, CancellationToken ct = default)
    {
        _jobs.TryGetValue($"{tenantId}:{jobId}", out var job);
        return Task.FromResult(job);
    }

    public Task<bool> CancelJobAsync(
        string jobId, string tenantId, CancellationToken ct = default)
    {
        var key = $"{tenantId}:{jobId}";
        if (!_jobs.TryGetValue(key, out var job))
            return Task.FromResult(false);

        job.Status = BulkExportStatus.Cancelled;
        _logger.LogInformation("Bulk export job {JobId} cancelled for tenant {TenantId}", Sanitize(jobId), Sanitize(tenantId));
        return Task.FromResult(true);
    }

    private static string Sanitize(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty
           : value.Replace("\r", string.Empty, StringComparison.Ordinal)
                  .Replace("\n", string.Empty, StringComparison.Ordinal);
}
