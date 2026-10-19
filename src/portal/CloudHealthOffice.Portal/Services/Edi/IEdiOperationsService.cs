namespace CloudHealthOffice.Portal.Services;

// EDI Operations
public interface IEdiOperationsService
{
    Task<List<Edi834Batch>> Get834BatchesAsync(DateTime? from = null, DateTime? to = null);
    Task<List<Enrollment834Record>> Get834BatchRecordsAsync(string batchId);
    Task Resolve834RecordAsync(Edi834ResolutionRequest request);
    Task<List<ClaimAcknowledgmentSummary>> Get277CaAcknowledgmentsAsync(DateTime? from = null, DateTime? to = null);
    Task<Stream> Download277CaAsync(string claimId);
    Task<List<EraSummary>> GetErasAsync(DateTime? from = null, DateTime? to = null);
    Task<Stream> DownloadEraAsync(string paymentId);
    Task<List<EdiTransactionHistoryItem>> GetTransactionHistoryAsync(DateTime? from, DateTime? to, string? transactionType, string? partnerId, string? status, int pageNumber, int pageSize);
}
