namespace CloudHealthOffice.Portal.Services;

// Payment Runs
public interface IPaymentRunService
{
    Task<List<PaymentRunSummary>> GetPaymentRunsAsync(int limit = 50);
    Task<PaymentRunDetails?> GetPaymentRunByIdAsync(string runId);
    Task<string> CreatePaymentRunAsync(CreatePaymentRunRequest request);
    Task CancelPaymentRunAsync(string runId);
    Task<Stream> DownloadEraForRunAsync(string runId);
}
