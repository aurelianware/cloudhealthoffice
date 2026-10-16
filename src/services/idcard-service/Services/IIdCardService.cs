using IdCardService.Adapters;
using IdCardService.Models;
using IdCardService.Repositories;

namespace IdCardService.Services;

public interface IIdCardOrchestrator
{
    /// <param name="requestedBy">The acting user from the caller's token.</param>
    Task<IdCardOrder> CreateOrderAsync(string tenantId, string requestedBy, CreateIdCardOrderRequest request, CancellationToken ct = default);
    Task<IdCardOrder?> GetOrderAsync(string tenantId, string orderId, CancellationToken ct = default);
    Task<List<IdCardRecord>> ListForMemberAsync(string tenantId, string memberId, CancellationToken ct = default);
    Task<IdCardRecord?> GetByCardIdAsync(string tenantId, string cardId, CancellationToken ct = default);
    /// <param name="revokedBy">The acting user from the caller's token.</param>
    Task<IdCardRecord?> RevokeAsync(string tenantId, string cardId, string revokedBy, RevokeIdCardRequest request, CancellationToken ct = default);
    Task<IdCardRecord?> RecordScanAsync(string tenantId, string cardId, CancellationToken ct = default);
}

public class IdCardOrchestrator : IIdCardOrchestrator
{
    private readonly IdCardAdapterFactory _adapters;
    private readonly IIdCardOrderRepository _orders;
    private readonly IIdCardRecordRepository _records;
    private readonly ILogger<IdCardOrchestrator> _logger;

    /// <summary>What an order that failed unexpectedly reports; the exception is logged.</summary>
    public const string IssuanceFailedMessage = "The ID card could not be issued. Retry the order or contact support.";

    public IdCardOrchestrator(
        IdCardAdapterFactory adapters,
        IIdCardOrderRepository orders,
        IIdCardRecordRepository records,
        ILogger<IdCardOrchestrator> logger)
    {
        _adapters = adapters;
        _orders = orders;
        _records = records;
        _logger = logger;
    }

    public async Task<IdCardOrder> CreateOrderAsync(string tenantId, string requestedBy, CreateIdCardOrderRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("Tenant is required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(requestedBy)) throw new ArgumentException("Actor is required.", nameof(requestedBy));

        if (request.Channel != IdCardDeliveryChannel.Digital)
        {
            var rejected = new IdCardOrder
            {
                TenantId = tenantId,
                MemberId = request.MemberId,
                Channel = request.Channel,
                LanguageCode = request.LanguageCode,
                RequestedBy = requestedBy,
                Status = IdCardOrderStatus.Failed,
                FailureCode = "CHANNEL_NOT_SUPPORTED",
                FailureReason = $"Delivery channel {request.Channel} is Phase 2 (digital-only in Phase 1)",
                UpdatedAt = DateTime.UtcNow
            };
            await _orders.UpsertAsync(rejected, ct);
            return rejected;
        }

        IIdCardAdapter adapter;
        Dictionary<string, string> settings;
        try
        {
            (adapter, settings) = await _adapters.GetAdapterWithSettingsAsync(tenantId, ct);
        }
        catch (TenantPlatformUnavailableException ex)
        {
            // The factory has logged the refusal. The order fails visibly rather
            // than being issued on a platform the tenant may not use.
            var failed = new IdCardOrder
            {
                TenantId = tenantId,
                MemberId = request.MemberId,
                Channel = request.Channel,
                LanguageCode = request.LanguageCode,
                RequestedBy = requestedBy,
                Status = IdCardOrderStatus.Failed,
                FailureCode = "TENANT_CONFIG_UNAVAILABLE",
                FailureReason = ex.Message,
                UpdatedAt = DateTime.UtcNow
            };
            await _orders.UpsertAsync(failed, ct);
            return failed;
        }

        var order = new IdCardOrder
        {
            TenantId = tenantId,
            MemberId = request.MemberId,
            Channel = request.Channel,
            LanguageCode = request.LanguageCode,
            RequestedBy = requestedBy,
            Platform = adapter.Platform,
            Status = IdCardOrderStatus.Rendering
        };
        await _orders.UpsertAsync(order, ct);

        try
        {
            var result = await adapter.IssueAsync(new IdCardIssueRequest
            {
                TenantId = tenantId,
                OrderId = order.Id,
                MemberId = request.MemberId,
                Channel = request.Channel,
                LanguageCode = request.LanguageCode,
                RequestedBy = requestedBy,
                PlatformSettings = settings
            }, ct);

            if (result.Success && result.Record != null)
            {
                // Stamp the platform on the record so the QNXT mirror
                // reconciliation job can filter to QNXT-issued cards only.
                result.Record.Platform = adapter.Platform;
                await _records.UpsertAsync(result.Record, ct);

                order.CardId = result.Record.CardId;
                order.DocumentId = result.Record.DocumentId;
                order.PreviewDocumentId = result.Record.PreviewDocumentId;
                order.Status = IdCardOrderStatus.Issued;
                order.IssuedAt = result.Record.IssuedAt;
            }
            else
            {
                order.Status = IdCardOrderStatus.Failed;
                order.FailureCode = result.FailureCode;
                order.FailureReason = result.FailureReason;
            }
        }
        catch (NotSupportedException nse)
        {
            order.Status = IdCardOrderStatus.Failed;
            order.FailureCode = "CHANNEL_NOT_SUPPORTED";
            order.FailureReason = nse.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ID card issuance failed for order {OrderId}", order.Id);
            order.Status = IdCardOrderStatus.Failed;
            order.FailureCode = "ISSUANCE_ERROR";
            // The order is returned to the caller: the exception text stays in the log.
            order.FailureReason = IssuanceFailedMessage;
        }
        finally
        {
            order.UpdatedAt = DateTime.UtcNow;
            await _orders.UpsertAsync(order, ct);
        }

        return order;
    }

    public Task<IdCardOrder?> GetOrderAsync(string tenantId, string orderId, CancellationToken ct = default) =>
        _orders.GetAsync(tenantId, orderId, ct);

    public Task<List<IdCardRecord>> ListForMemberAsync(string tenantId, string memberId, CancellationToken ct = default) =>
        _records.ListForMemberAsync(tenantId, memberId, ct);

    public Task<IdCardRecord?> GetByCardIdAsync(string tenantId, string cardId, CancellationToken ct = default) =>
        _records.FindByCardIdAsync(tenantId, cardId, ct);

    public async Task<IdCardRecord?> RevokeAsync(string tenantId, string cardId, string revokedBy, RevokeIdCardRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(revokedBy)) throw new ArgumentException("Actor is required.", nameof(revokedBy));

        // A concurrent write (a scan) moved the card on since it was read: re-read
        // it and apply the revocation again on top. After the last attempt the
        // conflict propagates (409).
        for (var attempt = 1; ; attempt++)
        {
            var record = await _records.FindByCardIdAsync(tenantId, cardId, ct);
            if (record == null) return null;

            record.RevokedAt = DateTime.UtcNow;
            record.RevocationReason = request.Reason;
            record.RevocationNotes = request.Notes;
            record.RevokedBy = revokedBy;
            try
            {
                await _records.UpsertAsync(record, ct);
                return record;
            }
            catch (IdCardConcurrencyException) when (attempt < MaxConcurrencyAttempts)
            {
            }
        }
    }

    /// <summary>How many times a card save is re-read and retried against concurrent writers.</summary>
    internal const int MaxConcurrencyAttempts = 5;

    public async Task<IdCardRecord?> RecordScanAsync(string tenantId, string cardId, CancellationToken ct = default)
    {
        // Read-modify-write on ScanCount, saved only against the revision read
        // (see IIdCardRecordRepository.UpsertAsync). A scan whose read is
        // overtaken (by a revocation or another scan) re-reads and counts again
        // on the fresh card, so it never writes back a stale copy and never
        // clears a revocation. A scan does not fail for this: if every attempt
        // is overtaken the increment is dropped (the counter is for analytics /
        // "is this card still in use" heuristics, not a billing or security
        // control) and the latest card is returned.
        IdCardRecord? record = null;
        for (var attempt = 1; attempt <= MaxConcurrencyAttempts; attempt++)
        {
            record = await _records.FindByCardIdAsync(tenantId, cardId, ct);
            if (record == null) return null;
            record.ScanCount += 1;
            record.LastScannedAt = DateTime.UtcNow;
            try
            {
                await _records.UpsertAsync(record, ct);
                return record;
            }
            catch (IdCardConcurrencyException)
            {
                // Overtaken: re-read and count on the fresh card.
            }
        }

        _logger.LogWarning("Scan of card {CardId} not counted: overtaken by concurrent writes {Attempts} times",
            LogSafe.Of(cardId), MaxConcurrencyAttempts);
        return await _records.FindByCardIdAsync(tenantId, cardId, ct) ?? record;
    }
}
