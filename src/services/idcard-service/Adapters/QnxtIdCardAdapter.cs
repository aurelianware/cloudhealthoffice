using IdCardService.Models;
using IdCardService.Services;

namespace IdCardService.Adapters;

/// <summary>
/// Augment-mode adapter: issues the card through the CHO pipeline, then
/// best-effort mirrors the request to the QNXT downstream queue. QNXT
/// enqueue never blocks or fails issuance — the nightly
/// <c>QnxtMirrorReconciliationJob</c> is the backstop for dropped messages.
/// </summary>
public class QnxtIdCardAdapter : IIdCardAdapter
{
    public string Platform => "qnxt";

    private readonly ChoIdCardAdapter _cho;
    private readonly IQnxtMirrorQueue _queue;
    private readonly ILogger<QnxtIdCardAdapter> _logger;

    public QnxtIdCardAdapter(
        ChoIdCardAdapter cho,
        IQnxtMirrorQueue queue,
        ILogger<QnxtIdCardAdapter> logger)
    {
        _cho = cho;
        _queue = queue;
        _logger = logger;
    }

    public async Task<IdCardIssueResult> IssueAsync(IdCardIssueRequest request, CancellationToken ct = default)
    {
        var result = await _cho.IssueAsync(request, ct);

        if (result.Success && result.Record != null)
        {
            try
            {
                await _queue.EnqueueMirrorAsync(new QnxtMirrorMessage
                {
                    TenantId = request.TenantId,
                    MemberId = request.MemberId,
                    OrderId = request.OrderId,
                    CardId = result.Record.CardId,
                    DocumentId = result.Record.DocumentId,
                    IssuedAt = result.Record.IssuedAt
                }, ct);
            }
            catch (Exception ex)
            {
                // Fire-and-forget with warning. Reconciliation job will catch
                // anything that slips through.
                _logger.LogWarning(ex,
                    "QNXT mirror enqueue failed for card {CardId}; reconciliation will backfill", result.Record.CardId);
            }
        }

        return result;
    }
}
