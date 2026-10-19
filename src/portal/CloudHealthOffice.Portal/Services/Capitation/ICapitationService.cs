namespace CloudHealthOffice.Portal.Services;

// ── Capitation Management ─────────────────────────────────────────────────

public interface ICapitationService
{
    // Contracts
    Task<List<CapitationContractSummary>> GetContractsAsync(string? npi = null, string? status = null, string? lob = null);
    Task<CapitationContractSummary?> GetContractByIdAsync(string id);
    Task<string> CreateContractAsync(CapitationContractSummary contract);
    Task UpdateContractAsync(string id, CapitationContractSummary contract);
    Task ActivateContractAsync(string id);
    Task TerminateContractAsync(string id, string reason, DateTime? terminationDate = null);

    // Runs
    Task<List<CapRunSummary>> GetRunsAsync(DateTime? from = null, DateTime? to = null, string? lineOfBusiness = null);
    Task<CapRunSummary?> GetRunByIdAsync(string id);
    Task<string> CreateRunAsync(CreateCapRunRequest request);
    Task<CapRunSummary> ExecuteRunAsync(string id);
    Task CancelRunAsync(string id);

    // Statements
    Task<List<CapStatementSummary>> GetStatementsAsync(string? npi = null, DateTime? periodFrom = null, DateTime? periodTo = null, string? status = null);
    Task<CapStatementSummary?> GetStatementByIdAsync(string id);
    Task<List<CapStatementSummary>> GetStatementsByRunAsync(string runId);
    Task<List<CapStatementSummary>> GetUnpaidStatementsAsync();
    Task ApproveStatementAsync(string id);
    Task VoidStatementAsync(string id, string reason);
    Task HoldStatementAsync(string id, string reason);
    Task<CapitationPeriodSummaryDto> GetPeriodSummaryAsync(DateTime period);

    // Disbursements
    Task<string> InitiateDisbursementAsync(string statementId, string? initiatedBy = null);
    Task<CapDisbursementBatchResult> InitiateBatchDisbursementAsync(List<string> statementIds, string? initiatedBy = null);

    /// <summary>NACHA credit files that did not reach the bank (awaiting retrieval). Never the file.</summary>
    Task<List<NachaHeldFile>> GetHeldNachaFilesAsync();

    /// <summary>payments:approve, and not the user who released it.</summary>
    Task<NachaFileResult> RetryNachaTransmissionAsync(string fileReference);
}
