using CloudHealthOffice.Infrastructure.Edi.Files;
using CloudHealthOffice.Infrastructure.Edi.Interchange;
using CloudHealthOffice.Infrastructure.Logging;
using EnrollmentImportService.Models;

namespace EnrollmentImportService.Services.Runs;

/// <summary>
/// The gather (edi-operations.md §11): once completed + failed units reach the
/// number the parse produced, the run finishes exactly once (a compare-and-swap
/// on its status) and that one step completes the EDI file record. Later steps
/// of the same kind (reconciliation, the sponsor's error report) go here.
/// </summary>
public sealed class EnrollmentRunFinisher(
    IEnrollmentImportRunRepository runs,
    TimeProvider clock,
    ILogger<EnrollmentRunFinisher> logger,
    IEdiFileStore? ediFileStore = null,
    IEdiFileRecorder? ediFiles = null,
    IX12InterchangeStore? interchanges = null)
{
    public async Task<bool> TryFinishAsync(string tenantId, string runId, CancellationToken ct = default)
    {
        var run = await runs.GetAsync(tenantId, runId, ct);
        if (run is null || run.Status != EnrollmentImportRunStatus.Running || run.ExpectedUnits is not { } expected
            || run.CompletedUnits + run.FailedUnits < expected)
            return false;

        if (!await runs.TryCompleteAsync(tenantId, runId, clock.GetUtcNow().UtcDateTime, ct))
            return false;

        logger.LogInformation("Import run {RunId} finished: {Completed} unit(s) applied, {Failed} failed; {Success} accepted, {Rejected} rejected",
            LogSanitizer.Sanitize(runId), run.CompletedUnits, run.FailedUnits, run.SuccessCount, run.FailedCount);
        var finished = await runs.GetAsync(tenantId, runId, ct) ?? run;
        await CompleteEdiFileAsync(finished, new EdiFileCompletion
        {
            AcceptedRecords = finished.SuccessCount,
            RejectedRecords = finished.FailedCount,
        }, ct);
        return true;
    }

    /// <summary>
    /// The file could not be imported at all. Before anything was applied, its
    /// ISA13s are given back so the partner's corrected resend is not refused
    /// as a duplicate.
    /// </summary>
    public async Task FailAsync(EnrollmentImportRun run, string kind, string message, bool releaseControlNumbers, CancellationToken ct = default)
    {
        if (!await runs.TryFailAsync(run.TenantId, run.Id, kind, message, clock.GetUtcNow().UtcDateTime, ct))
            return;
        logger.LogWarning("Import run {RunId} failed ({Kind})", LogSanitizer.Sanitize(run.Id), kind);

        if (releaseControlNumbers && interchanges is not null)
        {
            foreach (var receipt in run.ClaimedReceipts)
            {
                try
                {
                    await interchanges.ReleaseReceiptAsync(run.TenantId, receipt.PartyKey, receipt.ControlNumber, receipt.ClaimedAt, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Could not release ISA13 {ControlNumber}; a resend will be refused as a duplicate",
                        LogSanitizer.Sanitize(receipt.ControlNumber, 64));
                }
            }
        }

        await CompleteEdiFileAsync(run, new EdiFileCompletion { Errors = [message] }, ct);
    }

    private async Task CompleteEdiFileAsync(EnrollmentImportRun run, EdiFileCompletion completion, CancellationToken ct)
    {
        if (ediFileStore is null || ediFiles is null || run.EdiFileId is null) return;
        try
        {
            var file = await ediFileStore.GetAsync(run.TenantId, run.EdiFileId, ct);
            await ediFiles.CompleteAsync(file, completion, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not record the outcome of EDI file {FileId}", LogSanitizer.Sanitize(run.EdiFileId));
        }
    }
}
