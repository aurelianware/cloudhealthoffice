using FhirService.Models;

namespace FhirService.Services;

public interface IBulkExportService
{
    Task<BulkExportJob> InitiateExportAsync(BulkExportRequest request, string tenantId, CancellationToken ct = default);
    Task<BulkExportJob?> GetJobStatusAsync(string jobId, string tenantId, CancellationToken ct = default);
    Task<bool> CancelJobAsync(string jobId, string tenantId, CancellationToken ct = default);
}
