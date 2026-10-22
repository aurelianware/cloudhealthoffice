using FhirService.Models;

namespace FhirService.Services;

/// <summary>
/// Explicit capability boundary until a durable job store, member-scoped export
/// worker and authorized NDJSON download storage are implemented. Never issue
/// completed manifests or download URLs for files that have not been written.
/// </summary>
public class BulkExportService : IBulkExportService
{
    public Task<BulkExportJob> InitiateExportAsync(
        BulkExportRequest request, string tenantId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        throw new BulkExportNotImplementedException();
    }

    // No jobs are accepted by this implementation, so none can be polled or
    // cancelled. Keep these interfaces for a future durable implementation.
    public Task<BulkExportJob?> GetJobStatusAsync(
        string jobId, string tenantId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<BulkExportJob?>(null);
    }

    public Task<bool> CancelJobAsync(
        string jobId, string tenantId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }
}

public sealed class BulkExportNotImplementedException : NotSupportedException
{
    public BulkExportNotImplementedException()
        : base("Bulk export is not implemented. No export job or download files were created.") { }
}
