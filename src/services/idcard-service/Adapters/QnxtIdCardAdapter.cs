using IdCardService.Models;
using IdCardService.Services;

namespace IdCardService.Adapters;

// Architecture intent: CHO is the reference backend. This is a separate,
// payer-specific connector template completed from QRE discovery (version,
// configuration, licensing, hosting and solution architecture). Its unimplemented
// methods are engagement work, not proof the native workflow is missing.
// See docs/adr/027-core-adapter-engagement-boundary.md and
// docs/features/questionnaires/qre-core-admin-adapter-questions.md.
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
