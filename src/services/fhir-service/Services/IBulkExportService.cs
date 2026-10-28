using FhirService.Models;

namespace FhirService.Services;

public interface IBulkExportService
{
    Task<BulkExportJob> InitiateExportAsync(BulkExportRequest request, string tenantId, CancellationToken ct = default);
    Task<BulkExportJob?> GetJobStatusAsync(string jobId, string tenantId, CancellationToken ct = default);
    Task<bool> CancelJobAsync(string jobId, string tenantId, CancellationToken ct = default);

    /// <summary>
    /// One file of a completed, unexpired export, or null when there is no
    /// such file to serve. The caller has already been checked against the
    /// job's <see cref="BulkExportJob.RequestedBy"/>.
    /// </summary>
    Task<Stream?> OpenFileAsync(string jobId, string fileName, string tenantId, CancellationToken ct = default)
        => Task.FromResult<Stream?>(null);
}
